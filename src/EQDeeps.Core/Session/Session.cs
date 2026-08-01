using EQDeeps.Core.Events;
using EQDeeps.Core.Ingestion;
using EQDeeps.Core.Parsing;

namespace EQDeeps.Core.Sessions;

/// <summary>
/// One opened log file: owns its ingestion pipeline, parser, record store, and
/// fight tracker; shares the per-server identity registry with other sessions on
/// the same server. All state mutation happens on the single task running
/// <see cref="RunAsync"/> (the registry itself is internally synchronized).
/// </summary>
public sealed class Session
{
    private readonly LogEventParser _parser;

    public Session(
        string path,
        IdentityRegistry? identity = null,
        IngestOptions? ingestOptions = null,
        IIngestClock? clock = null,
        bool emuMode = false)
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
        _parser = new LogEventParser(new ParserOptions(Character, emuMode));
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

    public bool BackfillComplete { get; private set; }

    /// <summary>
    /// Raised after each processed batch, on the processing task — the
    /// subscribable point for realtime push (and, later, triggers).
    /// </summary>
    public event Action<LogBatch>? BatchProcessed;

    /// <summary>Runs ingestion and applies every entry to session state.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var run = Ingestion.RunAsync(cancellationToken);
        await foreach (var batch in Ingestion.Batches.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            lock (Gate)
            {
                foreach (var entry in batch.Entries)
                {
                    ProcessEntry(entry);
                }

                if (batch.Phase == IngestPhase.BackfillComplete)
                {
                    BackfillComplete = true;
                }
            }

            BatchProcessed?.Invoke(batch);
        }

        await run.ConfigureAwait(false);
    }

    private void ProcessEntry(LogEntry entry)
    {
        var evt = _parser.Parse(entry.Action, out var recognized);
        if (!recognized)
        {
            UnrecognizedLines++;
        }

        if (evt is null)
        {
            return;
        }

        ApplyIdentitySignals(evt);
        Records.Append(entry.Timestamp, evt);
        Fights.Process(entry.Timestamp, evt);
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
}
