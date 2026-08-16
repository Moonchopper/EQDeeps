using System.Security.Cryptography;
using System.Text;
using EQDeeps.Core.Events;
using EQDeeps.Core.Sessions;

namespace EQDeeps.Core.Cache;

/// <summary>
/// What a checkpoint records besides the records themselves: where in the log
/// the cached records end (a line start, from
/// <see cref="Ingestion.LogBatch.ResumeOffset"/>), how many there are, and the
/// per-session counters that are not derivable from records — lines nothing
/// recognized, lines a grammar threw on, lines the scanner could not shape —
/// plus the parser's one piece of cross-line state.
/// </summary>
public sealed record CacheCheckpoint(
    long ResumeOffset,
    long RecordCount,
    long UnrecognizedLines,
    long ParserFailures,
    long MalformedLines,
    long OverlongLinesDropped,
    string? PendingEmuCritAttacker);

/// <summary>
/// The parsed records of one log file, on disk, so the next open of that log
/// can skip the parser for everything it read last time and pick up the tail
/// where it left off (issue #59; ADR-018).
///
/// <para><b>What it holds and does not hold.</b> Records only — the typed
/// events with their timestamps — never fights, never identity, never query
/// results. Those are all functions of the record stream and are rebuilt by
/// replaying it, which keeps the file's meaning independent of every layer
/// above the parser: a change to how fights close never invalidates a cache,
/// and a change to a grammar invalidates all of them, because the file is
/// stamped with the module version id of the Core assembly it was written by.
/// That stamp is deliberately blunt. A hand-maintained format version would
/// only be bumped when someone remembered to, and a grammar fix that nobody
/// remembered to bump for would leave every user reading last month's parse
/// of a line the parser now reads differently. One re-parse per upgrade is
/// the price of never having that bug.</para>
///
/// <para><b>How it knows the log is still the same log.</b> Not by name, and
/// not by size: EverQuest only ever appends, but users trim, archivers rotate,
/// and a fresh character on a reinstalled client writes a new file to the old
/// path. The header carries a SHA-256 of the 64 KB of log immediately before
/// the resume offset; if those bytes still hash the same, everything before
/// them is the content that was parsed, and the offset is still a line start
/// in it. Anything else — shorter file, different bytes — is a different
/// log, and the cache is discarded and rebuilt rather than trusted.</para>
///
/// <para><b>Layout.</b> A fixed 4 KB header region followed by the record
/// stream. Records are appended and the header rewritten in place afterwards;
/// the header's record count and data length are what a reader trusts, so a
/// crash between the two leaves a longer file with an older header, which is
/// simply the last good checkpoint. Strings that repeat — names, spells,
/// zones — are written once, on first appearance, and referenced by index
/// afterwards; the reader rebuilds the same table in the same order, and
/// interns each entry into the session's <see cref="StringPool"/> so the
/// restored records share instances with everything the live parser goes on
/// to produce. Chat text is written inline every time: it is the field that
/// never repeats.</para>
///
/// <para><b>Recomputable.</b> This is a cache in the strict sense: a corrupt
/// or missing file costs one full read of a log the user still has. Nothing
/// is ever lost by deleting the folder.</para>
///
/// <para>Not thread-safe. The session reads it once at start-up on its
/// processing task and appends to it from whichever task the host runs
/// checkpoints on, one at a time.</para>
/// </summary>
public sealed class LogCache : IDisposable
{
    /// <summary>Bumped when the byte layout changes in a way the MVID stamp would not catch (it always would; this exists for the day it doesn't).</summary>
    public const int FormatVersion = 1;

    private const int HeaderLength = 4096;
    private const int MaxLogPathBytes = 2048;
    private const int FingerprintLength = 64 * 1024;
    private static readonly byte[] Magic = "EQDCACHE"u8.ToArray();

    /// <summary>
    /// The identity of the parser that wrote a cache. Deterministic builds
    /// derive the module version id from the compiled content, so it is
    /// stable across restarts of one build and different for any change to
    /// Core — which is exactly the granularity at which cached parses stop
    /// being trustworthy.
    /// </summary>
    public static Guid CoreVersion { get; } = typeof(GameEvent).Module.ModuleVersionId;

    private readonly FileStream _file;
    private readonly string _logPath;
    private readonly bool _emuMode;
    private readonly Guid _coreVersion;

    /// <summary>Writer side: string → index, in first-appearance order.</summary>
    private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);

    private BinaryWriter? _writer;
    private long _lastTicks;
    private long _recordCount;

    private LogCache(FileStream file, string logPath, bool emuMode, Guid coreVersion, CacheCheckpoint? checkpoint)
    {
        _file = file;
        _logPath = logPath;
        _emuMode = emuMode;
        _coreVersion = coreVersion;
        Checkpoint = checkpoint;
        _recordCount = checkpoint?.RecordCount ?? 0;
    }

    /// <summary>
    /// The checkpoint the file was opened with — what <see cref="ReadAll"/>
    /// will restore — or null when the cache is empty and the log has to be
    /// read from the top.
    /// </summary>
    public CacheCheckpoint? Checkpoint { get; private set; }

    /// <summary>Records committed so far (the header's count, plus anything committed since).</summary>
    public long RecordCount => _recordCount;

    /// <summary>
    /// Opens the cache for <paramref name="logPath"/> at <paramref name="cachePath"/>,
    /// validating it against the log as it is right now. An absent, foreign,
    /// stale, or corrupt file is replaced by an empty one, so the returned
    /// cache is always writable; only its <see cref="Checkpoint"/> says whether
    /// there is anything to restore. Throws <see cref="IOException"/> when the
    /// file cannot be opened exclusively — another session on the same log
    /// owns it, and two writers would corrupt it.
    /// </summary>
    public static LogCache Open(string cachePath, string logPath, bool emuMode, Guid? coreVersion = null)
    {
        if (Encoding.UTF8.GetByteCount(logPath) > MaxLogPathBytes)
        {
            // The header region is fixed-size and the path is the one
            // variable-length thing in it.
            throw new ArgumentException($"Log path longer than {MaxLogPathBytes} bytes cannot be cached.", nameof(logPath));
        }

        var version = coreVersion ?? CoreVersion;
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        var file = new FileStream(
            cachePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
            bufferSize: 1 << 16, FileOptions.None);
        try
        {
            var checkpoint = TryReadHeader(file, logPath, emuMode, version);
            if (checkpoint is null)
            {
                // Whatever is there is not a cache of this log as it stands
                // now. Start over in place rather than deleting: the handle is
                // already ours.
                file.SetLength(0);
            }

            return new LogCache(file, logPath, emuMode, version, checkpoint);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Every cached record, in order, with its strings interned into
    /// <paramref name="pool"/>. Read whole rather than streamed on purpose: a
    /// caller that has applied half a cache and then hits a corrupt byte has no
    /// way to say where in the log the good half ended, so the file is either
    /// entirely usable or not used at all. Throws <see cref="InvalidDataException"/>
    /// (or an <see cref="IOException"/>) when it is not; the caller should then
    /// <see cref="Reset"/> and read the log from the top.
    /// </summary>
    public TimedRecord[] ReadAll(StringPool pool, Action<long, long>? progress = null)
    {
        if (Checkpoint is null)
        {
            return [];
        }

        var count = Checkpoint.RecordCount;
        if (count > int.MaxValue)
        {
            throw new InvalidDataException("Cache claims more records than a session can hold.");
        }

        var records = new TimedRecord[count];
        var table = new List<string>();
        _file.Seek(HeaderLength, SeekOrigin.Begin);
        using var reader = new BinaryReader(new BufferedStream(_file, 1 << 20), Encoding.UTF8, leaveOpen: true);
        long ticks = 0;
        var nextProgress = 0L;
        try
        {
            for (var i = 0; i < records.Length; i++)
            {
                records[i] = ReadRecord(reader, table, pool, ref ticks);
                if (i >= nextProgress && progress is not null)
                {
                    progress(i, count);
                    nextProgress = i + 250_000;
                }
            }
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentOutOfRangeException or EndOfStreamException)
        {
            // Every way BinaryReader and the constructors can object to bytes
            // that are not what the tags promised, folded into the one
            // exception the caller is told to expect.
            throw new InvalidDataException("Log cache record stream is corrupt.", ex);
        }

        // The writer continues the same table from the same point, so a name
        // that appeared before the checkpoint costs one byte after it too.
        _ids.Clear();
        for (var i = 0; i < table.Count; i++)
        {
            _ids.TryAdd(table[i], i);
        }

        _lastTicks = ticks;
        return records;
    }

    /// <summary>
    /// Appends records after those already in the file. Nothing is durable
    /// until <see cref="Commit"/>; a reader opening the file before then sees
    /// the previous checkpoint. Records must continue the sequence the file
    /// holds — the caller passes the slice of its store from the cache's
    /// <see cref="RecordCount"/> onward.
    /// </summary>
    public void Append(ReadOnlySpan<TimedRecord> records)
    {
        if (records.IsEmpty)
        {
            return;
        }

        if (_writer is null)
        {
            if (_file.Length < HeaderLength)
            {
                // A fresh file: reserve the header region so records start
                // where a reader will look for them.
                _file.SetLength(HeaderLength);
            }

            _file.Seek(0, SeekOrigin.End);
            _writer = new BinaryWriter(new BufferedStream(_file, 1 << 20), Encoding.UTF8, leaveOpen: true);
        }

        foreach (var record in records)
        {
            WriteRecord(_writer, record, ref _lastTicks);
        }

        _recordCount += records.Length;
    }

    /// <summary>
    /// Makes everything appended so far the new checkpoint. Flushes the
    /// records to disk first and the header last, so a crash in between
    /// leaves the previous checkpoint intact. <paramref name="checkpoint"/>'s
    /// record count must equal <see cref="RecordCount"/>: the caller is
    /// asserting that the records it appended are exactly the ones the log
    /// holds up to the resume offset.
    /// </summary>
    public void Commit(CacheCheckpoint checkpoint)
    {
        if (checkpoint.RecordCount != _recordCount)
        {
            throw new ArgumentException(
                $"Checkpoint says {checkpoint.RecordCount} records; the cache holds {_recordCount}.", nameof(checkpoint));
        }

        _writer?.Flush();
        if (_file.Length < HeaderLength)
        {
            _file.SetLength(HeaderLength);
        }

        _file.Flush(flushToDisk: true);

        var fingerprint = Fingerprint(_logPath, checkpoint.ResumeOffset, out var fingerprintLength);
        var header = BuildHeader(checkpoint, fingerprint, fingerprintLength, _file.Length - HeaderLength, _lastTicks);
        _file.Seek(0, SeekOrigin.Begin);
        _file.Write(header);
        _file.Flush(flushToDisk: true);
        _file.Seek(0, SeekOrigin.End);
        Checkpoint = checkpoint;
    }

    /// <summary>
    /// Empties the cache. For when the log turned out to be different content
    /// than the checkpoint described — a rotation mid-session, or a record
    /// stream that would not read back — and whatever the file held is about
    /// bytes that no longer exist. Appends after this start a new sequence
    /// from record zero of the log as it now is.
    /// </summary>
    public void Reset()
    {
        _writer?.Dispose();
        _writer = null;
        _file.SetLength(0);
        _file.Flush(flushToDisk: true);
        _ids.Clear();
        _lastTicks = 0;
        _recordCount = 0;
        Checkpoint = null;
    }

    public void Dispose()
    {
        _writer?.Dispose();
        _file.Dispose();
    }

    // ---- header ----

    private byte[] BuildHeader(CacheCheckpoint cp, byte[] fingerprint, int fingerprintLength, long dataLength, long lastTicks)
    {
        var buffer = new byte[HeaderLength];
        using var stream = new MemoryStream(buffer);
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(FormatVersion);
        w.Write(HeaderLength);
        w.Write(_coreVersion.ToByteArray());
        w.Write(_emuMode);
        w.Write(_logPath);
        w.Write(fingerprintLength);
        w.Write(fingerprint);
        w.Write(cp.ResumeOffset);
        w.Write(dataLength);
        w.Write(cp.RecordCount);
        w.Write(lastTicks);
        w.Write(cp.UnrecognizedLines);
        w.Write(cp.ParserFailures);
        w.Write(cp.MalformedLines);
        w.Write(cp.OverlongLinesDropped);
        w.Write(cp.PendingEmuCritAttacker is not null);
        w.Write(cp.PendingEmuCritAttacker ?? string.Empty);
        w.Flush();
        var payloadLength = (int)stream.Position;
        var digest = SHA256.HashData(buffer.AsSpan(0, payloadLength));
        digest.CopyTo(buffer.AsSpan(payloadLength));
        return buffer;
    }

    /// <summary>
    /// The log path a cache file was written for, read without validating
    /// anything else about it — for a sweep deciding whether the log is still
    /// there. Null when the file is not recognizably a cache at all. Opens
    /// with read sharing only, so a file a live session holds throws
    /// <see cref="IOException"/> rather than being read mid-write.
    /// </summary>
    public static string? PeekLogPath(string cachePath)
    {
        using var file = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 12);
        if (file.Length < HeaderLength)
        {
            return null;
        }

        var buffer = new byte[HeaderLength];
        file.ReadExactly(buffer);
        try
        {
            using var r = new BinaryReader(new MemoryStream(buffer), Encoding.UTF8);
            if (!r.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
            {
                return null;
            }

            _ = r.ReadInt32(); // format version
            _ = r.ReadInt32(); // header length
            _ = r.ReadBytes(16); // core version
            _ = r.ReadBoolean(); // emu mode
            return r.ReadString();
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or ArgumentException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The checkpoint in the file, or null when the file is not a valid cache
    /// of this log written by this parser: bad magic, another version, another
    /// path or mode, a header that does not verify, a log that no longer
    /// reaches the offset, or one whose bytes before it have changed.
    /// </summary>
    private static CacheCheckpoint? TryReadHeader(FileStream file, string logPath, bool emuMode, Guid coreVersion)
    {
        if (file.Length < HeaderLength)
        {
            return null;
        }

        var buffer = new byte[HeaderLength];
        file.Seek(0, SeekOrigin.Begin);
        file.ReadExactly(buffer);
        try
        {
            using var stream = new MemoryStream(buffer);
            using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (!r.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
            {
                return null;
            }

            if (r.ReadInt32() != FormatVersion || r.ReadInt32() != HeaderLength)
            {
                return null;
            }

            if (new Guid(r.ReadBytes(16)) != coreVersion)
            {
                return null;
            }

            if (r.ReadBoolean() != emuMode)
            {
                return null;
            }

            if (!string.Equals(r.ReadString(), logPath, StringComparison.Ordinal))
            {
                return null;
            }

            var fingerprintLength = r.ReadInt32();
            var fingerprint = r.ReadBytes(32);
            var resumeOffset = r.ReadInt64();
            var dataLength = r.ReadInt64();
            var recordCount = r.ReadInt64();
            _ = r.ReadInt64(); // last timestamp ticks: the reader re-derives it
            var unrecognized = r.ReadInt64();
            var failures = r.ReadInt64();
            var malformed = r.ReadInt64();
            var overlong = r.ReadInt64();
            var hasPending = r.ReadBoolean();
            var pending = r.ReadString();
            var payloadLength = (int)stream.Position;
            var digest = r.ReadBytes(32);
            if (!SHA256.HashData(buffer.AsSpan(0, payloadLength)).AsSpan().SequenceEqual(digest))
            {
                return null;
            }

            if (resumeOffset < 0 || recordCount < 0 || dataLength < 0
                || fingerprintLength < 0 || fingerprintLength > FingerprintLength
                || file.Length < HeaderLength + dataLength)
            {
                return null;
            }

            // The log itself: still at least as long as the checkpoint, and
            // still the same bytes leading up to it.
            var actual = Fingerprint(logPath, resumeOffset, out var actualLength);
            if (actualLength != fingerprintLength || !actual.AsSpan().SequenceEqual(fingerprint))
            {
                return null;
            }

            // Anything past the committed data is a torn append; drop it so
            // the next append continues from the checkpoint.
            file.SetLength(HeaderLength + dataLength);

            return new CacheCheckpoint(
                resumeOffset, recordCount, unrecognized, failures, malformed, overlong,
                hasPending ? pending : null);
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or ArgumentException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// SHA-256 of the log's bytes in <c>[offset − 64 KB, offset)</c>, and how
    /// many bytes that was (fewer near the start of the file). Zero-length
    /// with an empty digest when the log is shorter than the offset — the
    /// caller compares lengths as well as digests, so that never matches a
    /// real fingerprint.
    /// </summary>
    private static byte[] Fingerprint(string logPath, long offset, out int length)
    {
        length = 0;
        try
        {
            using var log = new FileStream(
                logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            if (log.Length < offset)
            {
                return new byte[32];
            }

            length = (int)Math.Min(FingerprintLength, offset);
            var bytes = new byte[length];
            log.Seek(offset - length, SeekOrigin.Begin);
            log.ReadExactly(bytes);
            return SHA256.HashData(bytes);
        }
        catch (IOException)
        {
            length = -1;
            return new byte[32];
        }
    }

    // ---- records ----

    private enum Tag : byte
    {
        Damage = 1,
        Heal,
        Death,
        Cast,
        WearOff,
        Ability,
        Stance,
        Taunt,
        Chat,
        Zone,
        Membership,
        Who,
        Resist,
        Experience,
        Faction,
        Loot,
        Consider,
        Level,
    }

    private void WriteRecord(BinaryWriter w, TimedRecord record, ref long lastTicks)
    {
        var ticks = record.Timestamp.Ticks;
        var evt = record.Event;
        switch (evt)
        {
            case DamageEvent d:
                w.Write((byte)Tag.Damage);
                WriteDelta(w, ticks, ref lastTicks);
                WritePooled(w, d.Attacker);
                WritePooled(w, d.Defender);
                w.Write7BitEncodedInt64(d.Amount);
                w.Write((byte)d.Kind);
                WritePooled(w, d.SubType);
                w.Write7BitEncodedInt((int)d.Modifiers);
                w.Write(d.AttackerIsSpell);
                WritePooled(w, d.AttackerOwner);
                WritePooled(w, d.DefenderOwner);
                WritePooled(w, d.School);
                break;
            case HealEvent h:
                w.Write((byte)Tag.Heal);
                WriteDelta(w, ticks, ref lastTicks);
                WritePooled(w, h.Healer);
                WritePooled(w, h.Target);
                w.Write7BitEncodedInt64(h.Landed);
                w.Write7BitEncodedInt64(h.Potential);
                w.Write(h.OverTime);
                WritePooled(w, h.Spell);
                w.Write7BitEncodedInt((int)h.Modifiers);
                WritePooled(w, h.HealerOwner);
                break;
            case DeathEvent d:
                w.Write((byte)Tag.Death);
                WriteDelta(w, ticks, ref lastTicks);
                WritePooled(w, d.Victim);
                WritePooled(w, d.Killer);
                break;
            case CastEvent c:
                w.Write((byte)Tag.Cast);
                WriteDelta(w, ticks, ref lastTicks);
                WritePooled(w, c.Caster);
                WritePooled(w, c.Spell);
                w.Write((byte)c.Kind);
                w.Write(c.Song);
                break;
            case WearOffEvent o:
                w.Write((byte)Tag.WearOff);
                WriteDelta(w, ticks, ref lastTicks);
                WritePooled(w, o.Spell);
                WritePooled(w, o.Target);
                break;
            case AbilityEvent a:
                w.Write((byte)Tag.Ability);
                WriteDelta(w, ticks, ref lastTicks);
                WritePooled(w, a.User);
                WritePooled(w, a.Ability);
                break;
            case StanceEvent s:
                w.Write((byte)Tag.Stance);
                WriteDelta(w, ticks, ref lastTicks);
                WritePooled(w, s.Player);
                WritePooled(w, s.Stance);
                break;
            case TauntEvent t:
                w.Write((byte)Tag.Taunt);
                WriteDelta(w, ticks, ref lastTicks);
                WritePooled(w, t.Taunter);
                WritePooled(w, t.Target);
                w.Write(t.Success);
                w.Write(t.Improved);
                break;
            case ChatEvent c:
                w.Write((byte)Tag.Chat);
                WriteDelta(w, ticks, ref lastTicks);
                w.Write((byte)c.Channel);
                WritePooled(w, c.Sender);
                w.Write(c.Text);
                WritePooled(w, c.Receiver);
                WritePooled(w, c.CustomChannel);
                break;
            case ZoneEvent z:
                w.Write((byte)Tag.Zone);
                WriteDelta(w, ticks, ref lastTicks);
                WritePooled(w, z.ZoneName);
                w.Write(z.Welcome);
                break;
            case MembershipEvent m:
                w.Write((byte)Tag.Membership);
                WriteDelta(w, ticks, ref lastTicks);
                WritePooled(w, m.Player);
                w.Write(m.Raid);
                w.Write(m.Joined);
                break;
            case WhoEvent o:
                w.Write((byte)Tag.Who);
                WriteDelta(w, ticks, ref lastTicks);
                WritePooled(w, o.Player);
                WriteNullable(w, o.Level);
                WritePooled(w, o.ClassText);
                break;
            case ResistEvent r:
                w.Write((byte)Tag.Resist);
                WriteDelta(w, ticks, ref lastTicks);
                WritePooled(w, r.Caster);
                WritePooled(w, r.Resister);
                WritePooled(w, r.Spell);
                break;
            case ExperienceEvent x:
                w.Write((byte)Tag.Experience);
                WriteDelta(w, ticks, ref lastTicks);
                w.Write(x.Percent.HasValue);
                w.Write(x.Percent ?? 0);
                w.Write(x.Party);
                w.Write(x.AaPoint);
                WriteNullable(w, x.AaTotal);
                break;
            case FactionEvent f:
                w.Write((byte)Tag.Faction);
                WriteDelta(w, ticks, ref lastTicks);
                WritePooled(w, f.Faction);
                WriteNullable(w, f.Delta);
                w.Write(f.Better);
                w.Write(f.Capped);
                break;
            case LootEvent l:
                w.Write((byte)Tag.Loot);
                WriteDelta(w, ticks, ref lastTicks);
                WritePooled(w, l.Looter);
                WritePooled(w, l.Item);
                WritePooled(w, l.Source);
                w.Write(l.Copper.HasValue);
                w.Write(l.Copper ?? 0);
                w.Write7BitEncodedInt(l.Quantity);
                break;
            case ConsiderEvent c:
                w.Write((byte)Tag.Consider);
                WriteDelta(w, ticks, ref lastTicks);
                WritePooled(w, c.Target);
                WritePooled(w, c.Attitude);
                WriteNullable(w, c.Level);
                break;
            case LevelEvent l:
                w.Write((byte)Tag.Level);
                WriteDelta(w, ticks, ref lastTicks);
                w.Write7BitEncodedInt(l.Level);
                break;
            default:
                // A record type this codec does not know cannot be written,
                // and silently dropping it would make the restored session
                // differ from the parsed one. Failing loudly here is what
                // makes "add a case when you add an event" enforceable.
                throw new NotSupportedException($"No cache encoding for {evt.GetType().Name}.");
        }
    }

    private static TimedRecord ReadRecord(BinaryReader r, List<string> table, StringPool pool, ref long ticks)
    {
        var tag = (Tag)r.ReadByte();
        ticks += ReadZigZag(r);
        var timestamp = new DateTime(ticks, DateTimeKind.Unspecified);
        GameEvent evt = tag switch
        {
            Tag.Damage => new DamageEvent(
                ReadPooled(r, table, pool),
                ReadPooled(r, table, pool) ?? throw Corrupt(),
                checked((uint)r.Read7BitEncodedInt64()),
                (DamageKind)r.ReadByte(),
                ReadPooled(r, table, pool),
                (HitModifiers)r.Read7BitEncodedInt(),
                r.ReadBoolean(),
                ReadPooled(r, table, pool),
                ReadPooled(r, table, pool),
                ReadPooled(r, table, pool)),
            Tag.Heal => new HealEvent(
                ReadPooled(r, table, pool),
                ReadPooled(r, table, pool) ?? throw Corrupt(),
                checked((uint)r.Read7BitEncodedInt64()),
                checked((uint)r.Read7BitEncodedInt64()),
                r.ReadBoolean(),
                ReadPooled(r, table, pool),
                (HitModifiers)r.Read7BitEncodedInt(),
                ReadPooled(r, table, pool)),
            Tag.Death => new DeathEvent(
                ReadPooled(r, table, pool) ?? throw Corrupt(),
                ReadPooled(r, table, pool)),
            Tag.Cast => new CastEvent(
                ReadPooled(r, table, pool) ?? throw Corrupt(),
                ReadPooled(r, table, pool),
                (CastKind)r.ReadByte(),
                r.ReadBoolean()),
            Tag.WearOff => new WearOffEvent(
                ReadPooled(r, table, pool) ?? throw Corrupt(),
                ReadPooled(r, table, pool) ?? throw Corrupt()),
            Tag.Ability => new AbilityEvent(
                ReadPooled(r, table, pool) ?? throw Corrupt(),
                ReadPooled(r, table, pool) ?? throw Corrupt()),
            Tag.Stance => new StanceEvent(
                ReadPooled(r, table, pool) ?? throw Corrupt(),
                ReadPooled(r, table, pool) ?? throw Corrupt()),
            Tag.Taunt => new TauntEvent(
                ReadPooled(r, table, pool) ?? throw Corrupt(),
                ReadPooled(r, table, pool) ?? throw Corrupt(),
                r.ReadBoolean(),
                r.ReadBoolean()),
            Tag.Chat => new ChatEvent(
                (ChatChannel)r.ReadByte(),
                ReadPooled(r, table, pool) ?? throw Corrupt(),
                r.ReadString(),
                ReadPooled(r, table, pool),
                ReadPooled(r, table, pool)),
            Tag.Zone => new ZoneEvent(
                ReadPooled(r, table, pool),
                r.ReadBoolean()),
            Tag.Membership => new MembershipEvent(
                ReadPooled(r, table, pool) ?? throw Corrupt(),
                r.ReadBoolean(),
                r.ReadBoolean()),
            Tag.Who => new WhoEvent(
                ReadPooled(r, table, pool) ?? throw Corrupt(),
                ReadNullable(r),
                ReadPooled(r, table, pool)),
            Tag.Resist => new ResistEvent(
                ReadPooled(r, table, pool) ?? throw Corrupt(),
                ReadPooled(r, table, pool),
                ReadPooled(r, table, pool) ?? throw Corrupt()),
            Tag.Experience => ReadExperience(r),
            Tag.Faction => new FactionEvent(
                ReadPooled(r, table, pool) ?? throw Corrupt(),
                ReadNullable(r),
                r.ReadBoolean(),
                r.ReadBoolean()),
            Tag.Loot => ReadLoot(r, table, pool),
            Tag.Consider => new ConsiderEvent(
                ReadPooled(r, table, pool) ?? throw Corrupt(),
                ReadPooled(r, table, pool) ?? throw Corrupt(),
                ReadNullable(r)),
            Tag.Level => new LevelEvent(r.Read7BitEncodedInt()),
            _ => throw Corrupt(),
        };

        return new TimedRecord(timestamp, evt);
    }

    private static ExperienceEvent ReadExperience(BinaryReader r)
    {
        var hasPercent = r.ReadBoolean();
        var percent = r.ReadDouble();
        var party = r.ReadBoolean();
        var aaPoint = r.ReadBoolean();
        var aaTotal = ReadNullable(r);
        return new ExperienceEvent(hasPercent ? percent : null, party, aaPoint, aaTotal);
    }

    private static LootEvent ReadLoot(BinaryReader r, List<string> table, StringPool pool)
    {
        var looter = ReadPooled(r, table, pool) ?? throw Corrupt();
        var item = ReadPooled(r, table, pool);
        var source = ReadPooled(r, table, pool);
        var hasCopper = r.ReadBoolean();
        var copper = r.ReadInt64();
        var quantity = r.Read7BitEncodedInt();
        return new LootEvent(looter, item, source, hasCopper ? copper : null, quantity);
    }

    private static InvalidDataException Corrupt() => new("Log cache record stream is corrupt.");

    private static void WriteDelta(BinaryWriter w, long ticks, ref long lastTicks)
    {
        WriteZigZag(w, ticks - lastTicks);
        lastTicks = ticks;
    }

    private static void WriteZigZag(BinaryWriter w, long value) =>
        w.Write7BitEncodedInt64((value << 1) ^ (value >> 63));

    private static long ReadZigZag(BinaryReader r)
    {
        var raw = r.Read7BitEncodedInt64();
        return (long)((ulong)raw >> 1) ^ -(raw & 1);
    }

    private static void WriteNullable(BinaryWriter w, int? value)
    {
        w.Write(value.HasValue);
        w.Write(value ?? 0);
    }

    private static int? ReadNullable(BinaryReader r)
    {
        var has = r.ReadBoolean();
        var value = r.ReadInt32();
        return has ? value : null;
    }

    /// <summary>
    /// A string as a table reference: 0 for null, otherwise index + 1, with
    /// the text itself following only the first time an index is issued.
    /// </summary>
    private void WritePooled(BinaryWriter w, string? value)
    {
        if (value is null)
        {
            w.Write7BitEncodedInt(0);
            return;
        }

        if (_ids.TryGetValue(value, out var id))
        {
            w.Write7BitEncodedInt(id + 1);
            return;
        }

        id = _ids.Count;
        _ids.Add(value, id);
        w.Write7BitEncodedInt(id + 1);
        w.Write(value);
    }

    private static string? ReadPooled(BinaryReader r, List<string> table, StringPool pool)
    {
        var v = r.Read7BitEncodedInt();
        if (v == 0)
        {
            return null;
        }

        var id = v - 1;
        if (id < table.Count)
        {
            return table[id];
        }

        if (id != table.Count)
        {
            throw Corrupt();
        }

        var value = pool.Intern(r.ReadString());
        table.Add(value);
        return value;
    }
}
