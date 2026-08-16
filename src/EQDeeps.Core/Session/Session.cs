using EQDeeps.Core.Spells;
using EQDeeps.Core.Cache;
using EQDeeps.Core.Events;
using EQDeeps.Core.Ingestion;
using EQDeeps.Core.Parsing;

namespace EQDeeps.Core.Sessions;

/// <summary>
/// One opened log file: owns its ingestion pipeline, parser, record store, and
/// fight tracker; shares the per-server identity registry with other sessions on
/// the same server. All state mutation happens on the single task running
/// <see cref="RunAsync"/> (the registry itself is internally synchronized).
///
/// <para>Given a <see cref="LogCache"/>, the session restores whatever the
/// cache holds before it reads the log — records straight from disk instead
/// of through the parser — and starts ingestion at the offset the cache ends
/// at. <see cref="Checkpoint"/> writes what has arrived since back into it.
/// The restored records go through exactly the path a parsed one does after
/// parsing (identity signals, the store, the fight tracker), so a resumed
/// session and a cold one hold the same state.</para>
/// </summary>
public sealed class Session : IDisposable
{
    /// <summary>
    /// Records applied per lock hold while restoring. The gate is released
    /// between chunks so a client that opened the session can read its
    /// (partial) state during a multi-second restore instead of hanging on
    /// the first request.
    /// </summary>
    private const int RestoreChunk = 50_000;

    private readonly LogEventParser _parser;
    private readonly StringPool _strings = new();
    private readonly LogCache? _cache;
    private readonly SemaphoreSlim _checkpointGate = new(1, 1);

    private long _restoredMalformed;
    private long _restoredOverlong;

    // Checkpoint bookkeeping, all written under Gate on the processing task.
    private long _resumeOffset = -1;
    private int _generation;
    private int _cacheBase;
    private int _cacheWritten;
    private bool _cacheReset;

    public Session(
        string path,
        IdentityRegistry? identity = null,
        IngestOptions? ingestOptions = null,
        IIngestClock? clock = null,
        bool emuMode = false,
        LogCache? cache = null,
        SpellBook? spells = null)
    {
        Path = path;
        if (LogFileNames.TryParse(path, out var character, out var server))
        {
            Character = character;
            Server = server;
        }
        else
        {
            Character = "Unknown";
            Server = "unknown";
        }

        Identity = identity ?? new IdentityRegistry();
        Identity.AddVerifiedPlayer(Character);
        Records = new RecordStore();
        Fights = new FightTracker(Identity);
        Ingestion = new LogFileIngestion(path, ingestOptions, clock);
        _parser = new LogEventParser(new ParserOptions(Character, emuMode) { Spells = spells ?? SpellBook.Empty });
        _cache = cache;
        BackfillFrom = ingestOptions?.BackfillFrom;
    }

    public string Path { get; }

    public string Character { get; }

    public string Server { get; }

    public IdentityRegistry Identity { get; }

    public RecordStore Records { get; }

    public FightTracker Fights { get; }

    public LogFileIngestion Ingestion { get; }

    /// <summary>
    /// Serializes state mutation against readers: batch processing takes this
    /// lock, and anything reading session state from another thread (query
    /// execution, DTO building) must too.
    /// </summary>
    public object Gate { get; } = new();

    /// <summary>Lines no grammar recognized (measured, logged, never thrown).</summary>
    public long UnrecognizedLines { get; private set; }

    /// <summary>
    /// Lines a grammar actually threw on. Should always be zero — it counts
    /// parser bugs, not log oddities — and is surfaced rather than swallowed so
    /// that "always zero" is something anyone can check instead of assume.
    /// </summary>
    public long ParserFailures { get; private set; }

    /// <summary>
    /// Non-empty lines the scanner could not shape into an entry, across the
    /// restored history and the current run both — the ingestion's own count
    /// starts at zero each run, and a resumed session did not re-scan the
    /// lines it restored.
    /// </summary>
    public long MalformedLines => _restoredMalformed + Ingestion.MalformedLines;

    public long OverlongLinesDropped => _restoredOverlong + Ingestion.OverlongLinesDropped;

    /// <summary>
    /// Stance switches by the log owner. Most servers and most characters never
    /// log one, so this is how the UI decides whether the stance breakdown is
    /// worth offering at all rather than showing a permanently empty tab.
    /// </summary>
    public long StanceSwitches { get; private set; }

    public bool BackfillComplete { get; private set; }

    /// <summary>
    /// Records that came from the cache rather than the parser this run — how
    /// much of the log the user did not have to wait for.
    /// </summary>
    public long RestoredRecords { get; private set; }

    /// <summary>The start of the historical window, if the session was opened with one.</summary>
    private DateTime? BackfillFrom { get; }

    /// <summary>
    /// Raised after each processed batch, on the processing task — the
    /// subscribable point for realtime push (and, later, triggers). Also
    /// raised, with empty backfill batches, as restore progresses.
    /// </summary>
    public event Action<LogBatch>? BatchProcessed;

    /// <summary>Runs ingestion and applies every entry to session state.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var resumeAt = Restore(cancellationToken);
        var run = Ingestion.RunAsync(cancellationToken, resumeAt);
        await foreach (var batch in Ingestion.Batches.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            lock (Gate)
            {
                if (batch.Generation != _generation)
                {
                    // The file was truncated or replaced under us and
                    // ingestion started over from the new content. Everything
                    // cached so far describes bytes that no longer exist, and
                    // the records before this point are not in the file any
                    // more either — so the cache starts again from here.
                    _generation = batch.Generation;
                    _cacheBase = Records.Count;
                    _cacheWritten = Records.Count;
                    _cacheReset = true;
                }

                foreach (var entry in batch.Entries)
                {
                    ProcessEntry(entry);
                }

                _resumeOffset = batch.ResumeOffset;

                if (batch.Phase == IngestPhase.BackfillComplete)
                {
                    BackfillComplete = true;
                }
            }

            BatchProcessed?.Invoke(batch);
        }

        await run.ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the cache's records, if it has any, and returns the log offset
    /// ingestion should begin at — null when there was nothing to restore, or
    /// the cache would not read back and the log has to be read from the top.
    /// </summary>
    private long? Restore(CancellationToken cancellationToken)
    {
        if (_cache?.Checkpoint is not { } checkpoint)
        {
            return null;
        }

        long total;
        try
        {
            total = new FileInfo(Path).Length;
        }
        catch (IOException)
        {
            total = checkpoint.ResumeOffset;
        }

        TimedRecord[] records;
        try
        {
            records = _cache.ReadAll(_strings, (done, count) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Progress in the log's own units, so the client's backfill
                // bar reads the same whether the bytes came from the cache or
                // the parser: reading the cache is the first stretch of the
                // bar, up to the resume offset.
                BatchProcessed?.Invoke(new LogBatch(
                    IngestPhase.Backfill, [],
                    checkpoint.ResumeOffset * done / Math.Max(1, count), total));
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            // Nothing has been applied, so a fresh read is still an option —
            // the only one, since a half-read cache cannot say where in the
            // log its good half ended.
            _cache.Reset();
            return null;
        }

        for (var i = 0; i < records.Length; i += RestoreChunk)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = Math.Min(records.Length, i + RestoreChunk);
            lock (Gate)
            {
                for (var j = i; j < end; j++)
                {
                    var record = records[j];
                    if (BackfillFrom is { } from && record.Timestamp < from)
                    {
                        continue;
                    }

                    Apply(record.Timestamp, record.Event);
                }
            }
        }

        lock (Gate)
        {
            UnrecognizedLines = checkpoint.UnrecognizedLines;
            ParserFailures = checkpoint.ParserFailures;
            _restoredMalformed = checkpoint.MalformedLines;
            _restoredOverlong = checkpoint.OverlongLinesDropped;
            _parser.PendingEmuCritAttacker = checkpoint.PendingEmuCritAttacker;
            RestoredRecords = records.Length;
            _resumeOffset = checkpoint.ResumeOffset;

            // The cache holds every restored record even when a backfill
            // window skipped some of them here; the writer's index counts
            // what is in the file, the store's counts what was kept. They
            // only line up when nothing was skipped, and that is the only
            // case in which appending more is meaningful — a windowed
            // session does not checkpoint.
            _cacheWritten = records.Length;
            _cacheBase = 0;
        }

        return checkpoint.ResumeOffset;
    }

    /// <summary>
    /// Writes every record that arrived since the last checkpoint to the
    /// cache and commits it against the log offset those records end at.
    /// Safe to call from any thread; serialized against itself; a no-op
    /// without a cache, before anything has been ingested, when nothing is
    /// new, or when the session was opened with a backfill window (the
    /// records it holds are not the log's from the top, so a later resume
    /// could not continue them). The record copy happens under
    /// <see cref="Gate"/>; the serialization does not.
    /// </summary>
    public void Checkpoint()
    {
        if (_cache is null || BackfillFrom is not null)
        {
            return;
        }

        _checkpointGate.Wait();
        try
        {
            TimedRecord[] slice;
            CacheCheckpoint checkpoint;
            bool reset;
            lock (Gate)
            {
                if (_resumeOffset < 0)
                {
                    return;
                }

                reset = _cacheReset;
                _cacheReset = false;
                slice = Records.CopyRange(_cacheWritten, Records.Count);
                if (slice.Length == 0 && !reset && _cache.Checkpoint is { } current
                    && current.ResumeOffset == _resumeOffset)
                {
                    return;
                }

                checkpoint = new CacheCheckpoint(
                    _resumeOffset,
                    Records.Count - _cacheBase,
                    UnrecognizedLines,
                    ParserFailures,
                    MalformedLines,
                    OverlongLinesDropped,
                    _parser.PendingEmuCritAttacker);
                _cacheWritten = Records.Count;
            }

            if (reset)
            {
                _cache.Reset();
            }

            _cache.Append(slice);
            _cache.Commit(checkpoint);
        }
        finally
        {
            _checkpointGate.Release();
        }
    }

    private void ProcessEntry(LogEntry entry)
    {
        GameEvent? evt;
        bool recognized;
        try
        {
            evt = _parser.Parse(entry.Action, out recognized);
        }
        catch (Exception)
        {
            // The parser's contract is that an unrecognized line is counted,
            // never thrown on. When a grammar breaks that contract anyway the
            // cost used to be the entire session: the exception unwound the
            // ingestion task, the channel completed, and the log simply stopped
            // being read — with no error anywhere the user could see, just a
            // parse that ended early and looked plausible. One bad line is
            // worth one bad line.
            ParserFailures++;
            return;
        }

        if (!recognized)
        {
            UnrecognizedLines++;
        }

        if (evt is null)
        {
            return;
        }

        Apply(entry.Timestamp, _strings.Canonicalize(evt));
    }

    /// <summary>
    /// The part of processing that is about the event rather than the line:
    /// identity signals, the record store, the fight tracker. Split from
    /// <see cref="ProcessEntry"/> so a record that skipped the parser — one
    /// restored from the cache — takes the same path.
    /// </summary>
    private void Apply(DateTime timestamp, GameEvent evt)
    {
        ApplyIdentitySignals(evt);
        Records.Append(timestamp, evt);
        Fights.Process(timestamp, evt);
    }

    private void ApplyIdentitySignals(GameEvent evt)
    {
        switch (evt)
        {
            case ChatEvent chat:
                ApplyChatSignals(chat);
                break;
            case MembershipEvent membership:
                Identity.AddVerifiedPlayer(membership.Player);
                break;
            case WhoEvent who:
                Identity.AddVerifiedPlayer(who.Player);
                break;
            case StanceEvent stance
                when string.Equals(stance.Player, Character, StringComparison.OrdinalIgnoreCase):
                StanceSwitches++;
                break;
        }
    }

    private void ApplyChatSignals(ChatEvent chat)
    {
        // Player-only channels verify the sender. (Say/shout/ooc/auction do not:
        // NPCs use those grammars.)
        if (chat.Channel is ChatChannel.Guild or ChatChannel.Raid or ChatChannel.Group
            or ChatChannel.Fellowship or ChatChannel.Tell)
        {
            Identity.AddVerifiedPlayer(chat.Sender);
        }

        // Pet-leader line: "<pet> says 'My leader is <Owner>'" — the definitive
        // pet→owner mapping, and the owner is a verified player.
        if (chat.Channel == ChatChannel.Say &&
            chat.Text.StartsWith("My leader is ", StringComparison.Ordinal))
        {
            var owner = chat.Text["My leader is ".Length..].TrimEnd('.', ' ');
            if (owner.Length > 0 && !owner.Contains(' '))
            {
                Identity.AddVerifiedPlayer(owner);
                Identity.MapPetToOwner(chat.Sender, owner);
            }
        }
    }

    /// <summary>Releases the cache file. Does not checkpoint; the host does that first if it wants to.</summary>
    public void Dispose()
    {
        _cache?.Dispose();
        _checkpointGate.Dispose();
    }
}
