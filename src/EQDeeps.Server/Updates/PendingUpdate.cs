using System.Text.Json;

namespace EQDeeps.Server.Updates;

/// <summary>A verified installer sitting on disk, waiting to be run.</summary>
public sealed record PendingUpdate(string Version, string InstallerPath, DateTimeOffset StagedUtc);

/// <summary>
/// Remembers the staged installer across runs. Needed because staging and
/// applying are deliberately separated (ADR-010): the download happens while
/// the user is parsing, the install happens once they've closed the app. If
/// EQDeeps is killed in between, this file is what lets the next run finish
/// the job without re-downloading.
/// </summary>
public sealed class PendingUpdateStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public PendingUpdateStore(string? root = null)
    {
        _path = Path.Combine(
            root ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EQDeeps"),
            "pending-update.json");
    }

    /// <summary>The staged update, or null when there is none or it no longer exists on disk.</summary>
    public PendingUpdate? Read()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return null;
                }

                var pending = JsonSerializer.Deserialize<PendingUpdate>(File.ReadAllText(_path));
                // The installer lives in temp, which Windows and cleanup tools
                // are free to empty; a marker pointing at nothing is just stale.
                return pending is not null && File.Exists(pending.InstallerPath) ? pending : null;
            }
            catch (JsonException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    public void Write(PendingUpdate pending)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(pending));
                File.Move(temp, _path, overwrite: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            try
            {
                File.Delete(_path);
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
