using System.IO.Compression;
using System.Threading.Channels;
using EQDeeps.Core.Parsing;

namespace EQDeeps.Core.Ingestion;

/// <summary>
/// One ingestion pipeline per opened log file: backfill (fast, progress-reported)
/// then live tail, delivered as <see cref="LogBatch"/>es over a bounded channel.
///
/// Files are opened with ReadWrite+Delete sharing (EverQuest holds a write
/// handle; archivers rename and recreate at will). Tailing polls on an adaptive
/// interval — change notifications are unreliable for files held open, and a
/// 15 ms active poll is far inside the 250 ms latency budget. Truncation and
/// rotation are detected at EOF by comparing the path's stat against the open
/// handle's: a shrunken path means truncation; a path whose length diverges from
/// a quiet handle means the file was replaced. Both reopen at the start of the
/// new content — never re-emitting old entries, never crashing.
/// </summary>
public sealed class LogFileIngestion
{
    private readonly string _path;
    private readonly IngestOptions _options;
    private readonly IIngestClock _clock;
    private readonly Channel<LogBatch> _channel;
    private readonly EntryScanner _scanner;

    public LogFileIngestion(string path, IngestOptions? options = null, IIngestClock? clock = null)
    {
        _path = path;
        _options = options ?? new IngestOptions();
        _clock = clock ?? SystemClock.Instance;
        _scanner = new EntryScanner(_options.MaxLineLength);
        _channel = Channel.CreateBounded<LogBatch>(new BoundedChannelOptions(_options.ChannelCapacity)
        {
            SingleWriter = true,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public ChannelReader<LogBatch> Batches => _channel.Reader;

    /// <summary>Non-empty lines that produced no entry — the unmatched-shape counter.</summary>
    public long MalformedLines => _scanner.MalformedLines;

    public long OverlongLinesDropped => _scanner.OverlongLinesDropped;

    /// <summary>
    /// Runs the pipeline until cancellation (or end of backfill when
    /// <see cref="IngestOptions.Follow"/> is off / the file is gzip). Completes
    /// the channel on exit; a fatal error faults both the channel and this task.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            {
                await RunGzipAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await RunPlainAsync(cancellationToken).ConfigureAwait(false);
            }

            _channel.Writer.TryComplete();
        }
        catch (OperationCanceledException)
        {
            _channel.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            _channel.Writer.TryComplete(ex);
            throw;
        }
    }

    private async Task RunPlainAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[_options.ReadBufferSize];
        var entries = new List<LogEntry>();
        var stream = Open();
        try
        {
            // ---- backfill ----
            var backfillEnd = stream.Length;
            long position = 0;
            if (_options.BackfillFrom is { } from && backfillEnd > 0)
            {
                position = TimestampSeek.FindStart(stream, from);
            }

            stream.Seek(position, SeekOrigin.Begin);
            while (position < backfillEnd)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var toRead = (int)Math.Min(buffer.Length, backfillEnd - position);
                var read = stream.Read(buffer, 0, toRead);
                if (read == 0)
                {
                    break; // file shrank while backfilling
                }

                position += read;
                _scanner.Append(buffer.AsSpan(0, read), entries);
                await EmitAsync(IngestPhase.Backfill, entries, position, backfillEnd, cancellationToken)
                    .ConfigureAwait(false);
            }

            await EmitAsync(IngestPhase.BackfillComplete, entries, position, backfillEnd, cancellationToken, force: true)
                .ConfigureAwait(false);

            if (!_options.Follow)
            {
                return;
            }

            // ---- live tail ----
            var idleStreak = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read > 0)
                {
                    position += read;
                    _scanner.Append(buffer.AsSpan(0, read), entries);
                    await EmitAsync(IngestPhase.Live, entries, position, null, cancellationToken)
                        .ConfigureAwait(false);
                    idleStreak = 0;
                    continue; // drain everything available before waiting
                }

                if (NeedsReopen(stream, position))
                {
                    stream.Dispose();
                    _scanner.Reset();
                    stream = await WaitForFileAsync(cancellationToken).ConfigureAwait(false);
                    position = 0;
                    idleStreak = 0;
                    continue;
                }

                idleStreak++;
                await _clock.Delay(DelayFor(idleStreak), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            stream.Dispose();
        }
    }

    private async Task RunGzipAsync(CancellationToken cancellationToken)
    {
        using var file = Open();
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        var buffer = new byte[_options.ReadBufferSize];
        var entries = new List<LogEntry>();
        var totalBytes = file.Length;

        int read;
        while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _scanner.Append(buffer.AsSpan(0, read), entries);
            if (_options.BackfillFrom is { } from)
            {
                entries.RemoveAll(e => e.Timestamp < from);
            }

            // Progress approximated by compressed bytes consumed.
            await EmitAsync(IngestPhase.Backfill, entries, file.Position, totalBytes, cancellationToken)
                .ConfigureAwait(false);
        }

        await EmitAsync(IngestPhase.BackfillComplete, entries, totalBytes, totalBytes, cancellationToken, force: true)
            .ConfigureAwait(false);
    }

    private async Task EmitAsync(
        IngestPhase phase, List<LogEntry> entries, long bytesProcessed, long? totalBytes,
        CancellationToken cancellationToken, bool force = false)
    {
        if (entries.Count == 0 && !force)
        {
            return;
        }

        var batch = new LogBatch(phase, entries.ToArray(), bytesProcessed, totalBytes);
        entries.Clear();
        await _channel.Writer.WriteAsync(batch, cancellationToken).ConfigureAwait(false);
    }

    private FileStream Open() =>
        new(_path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1, // unbuffered: we do our own chunking, and stale
                           // internal buffers would fight the tail loop
            FileOptions.SequentialScan);

    private bool NeedsReopen(FileStream stream, long position)
    {
        var info = new FileInfo(_path);
        if (!info.Exists)
        {
            return true; // renamed/deleted; wait for the game to recreate it
        }

        long pathLength;
        try
        {
            pathLength = info.Length;
        }
        catch (IOException)
        {
            return true;
        }

        if (pathLength < position)
        {
            return true; // truncated in place, or replaced by a smaller file
        }

        // Our handle follows the original file even after a rename. If the handle
        // has no data past our position but the path's file has a different
        // length, the path now names a new file — switch to it. (If the renamed
        // original is still being written we keep draining it first; reads above
        // succeed until it goes quiet.)
        var handleLength = stream.Length;
        return handleLength <= position && pathLength != handleLength;
    }

    private async Task<FileStream> WaitForFileAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return Open();
            }
            catch (IOException)
            {
                // Not there yet (or mid-recreate); poll until it returns.
            }

            await _clock.Delay(_options.IdlePollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private TimeSpan DelayFor(int idleStreak)
    {
        var scaled = _options.ActivePollInterval * idleStreak;
        return scaled < _options.IdlePollInterval ? scaled : _options.IdlePollInterval;
    }
}
