using System.IO.Compression;

namespace EQDeeps.Server;

/// <summary>
/// The bundled demo log: two days of sanitized real gameplay, gzip-embedded in
/// the assembly and extracted to &lt;root&gt;\sample on demand so a first-time
/// user has something to click before they hunt down their own log file. The
/// extracted file is an ordinary log — a session over it parses, backfills,
/// and queries exactly like any other — but it is listed with source "sample"
/// and never enters the recent-logs MRU, so it can't be mistaken for the
/// player's real logs. A stamp file records the embedded resource size so a
/// new build with different sample content re-extracts exactly once.
/// </summary>
public sealed class SampleLog
{
    public const string FileName = "eqlog_Sample_demo.txt";
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

                var stampPath = FilePath + ".stamp";
                var stamp = resource.Length.ToString();
                if (File.Exists(FilePath) && File.Exists(stampPath) &&
                    File.ReadAllText(stampPath) == stamp)
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
                File.WriteAllText(stampPath, stamp);
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
}
