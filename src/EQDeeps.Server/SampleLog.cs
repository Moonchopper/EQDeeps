using System.Buffers.Binary;
using System.IO.Compression;

namespace EQDeeps.Server;

/// <summary>
/// The bundled demo log: two days of sanitized real gameplay, gzip-embedded in
/// the assembly and extracted to &lt;root&gt;\sample on demand so a first-time
/// user has something to click before they hunt down their own log file. The
/// extracted file is an ordinary log — a session over it parses, backfills,
/// and queries exactly like any other — but it is listed with source "sample"
/// and never enters the recent-logs MRU, so it can't be mistaken for the
/// player's real logs. The sample directory holds exactly one file: staleness
/// is judged against the uncompressed size recorded in the gzip trailer (a new
/// build with different content re-extracts exactly once), and leftovers from
/// older builds are swept on extraction.
/// </summary>
public sealed class SampleLog
{
    public const string FileName = "eqlog_SampleCharacter_demo.txt";
    private const string ResourceName = "EQDeeps.Server.Assets.sample-log.txt.gz";
    private readonly string _dir;
    private readonly object _gate = new();

    public SampleLog(string? root = null)
    {
        _dir = Path.Combine(
            root ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EQDeeps"),
            "sample");
    }

    public string FilePath => Path.Combine(_dir, FileName);

    /// <summary>True when the path refers to the extracted sample file.</summary>
    public bool IsSamplePath(string path)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(path), Path.GetFullPath(FilePath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false; // invalid path characters — certainly not the sample
        }
    }

    /// <summary>
    /// Extracts the sample if missing or from a different build; returns the
    /// path, or null when the resource is absent or the disk write fails —
    /// callers just omit the sample entry.
    /// </summary>
    public string? TryEnsureExtracted()
    {
        lock (_gate)
        {
            try
            {
                using var resource = typeof(SampleLog).Assembly.GetManifestResourceStream(ResourceName);
                if (resource is null)
                {
                    return null;
                }

                var expected = UncompressedLength(resource);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length == expected)
                {
                    return FilePath;
                }

                Directory.CreateDirectory(_dir);
                var temp = FilePath + ".tmp";
                using (var gunzip = new GZipStream(resource, CompressionMode.Decompress))
                using (var output = File.Create(temp))
                {
                    gunzip.CopyTo(output);
                }

                File.Move(temp, FilePath, overwrite: true);
                SweepStrayFiles();
                return FilePath;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>Gzip trailer ISIZE: uncompressed length (mod 2^32 — the sample
    /// is ~20 MB, nowhere near the wraparound).</summary>
    private static long UncompressedLength(Stream resource)
    {
        Span<byte> trailer = stackalloc byte[4];
        resource.Seek(-4, SeekOrigin.End);
        resource.ReadExactly(trailer);
        resource.Seek(0, SeekOrigin.Begin);
        return BinaryPrimitives.ReadUInt32LittleEndian(trailer);
    }

    /// <summary>The sample directory is exactly one example file — remove
    /// leftovers from older builds (renamed samples, stamp files).</summary>
    private void SweepStrayFiles()
    {
        foreach (var file in Directory.EnumerateFiles(_dir))
        {
            if (!string.Equals(Path.GetFileName(file), FileName, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
