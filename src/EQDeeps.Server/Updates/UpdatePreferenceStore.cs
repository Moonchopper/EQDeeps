using System.Text.Json;

namespace EQDeeps.Server.Updates;

/// <summary>
/// Persists <see cref="UpdatePreferences"/> beside the other user documents in
/// %AppData%\EQDeeps. Server-side rather than in localStorage (where the old
/// dismiss flag lived) because the update loop has to honour these answers with
/// no UI attached — headless runs, and the window being closed while a download
/// is in flight. Writes are atomic (temp + move), matching RecentLogs.
/// </summary>
public sealed class UpdatePreferenceStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private UpdatePreferences? _cached;

    public UpdatePreferenceStore(string? root = null)
    {
        _path = Path.Combine(
            root ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EQDeeps"),
            "update-prefs.json");
    }

    public UpdatePreferences Read()
    {
        lock (_gate)
        {
            return _cached ??= ReadLocked();
        }
    }

    public void Write(UpdatePreferences preferences)
    {
        lock (_gate)
        {
            _cached = preferences;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(preferences, SerializerOptions));
                File.Move(temp, _path, overwrite: true);
            }
            catch (IOException)
            {
                // A read-only or full profile disk must not break updating; the
                // in-memory copy still governs this run.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Read-modify-write under the lock, returning the stored result.</summary>
    public UpdatePreferences Update(Func<UpdatePreferences, UpdatePreferences> change)
    {
        lock (_gate)
        {
            var updated = change(_cached ??= ReadLocked());
            Write(updated);
            return updated;
        }
    }

    private static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private UpdatePreferences ReadLocked()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<UpdatePreferences>(File.ReadAllText(_path), SerializerOptions)
                  ?? UpdatePreferences.Default
                : UpdatePreferences.Default;
        }
        catch (JsonException)
        {
            return UpdatePreferences.Default; // corrupt: fall back to asking
        }
        catch (IOException)
        {
            return UpdatePreferences.Default;
        }
    }
}
