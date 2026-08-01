using System.Text.Json;

namespace EQDeeps.Server;

/// <summary>
/// Persistence for user-authored JSON documents (dashboards, saved queries):
/// one file per well-known key under %AppData%\EQDeeps. The server stores the
/// documents verbatim — the client owns their shape — which makes export/import
/// literally "the same JSON". Writes are atomic (temp file + move) so a crash
/// mid-write never corrupts a layout.
/// </summary>
public sealed class DocumentStore
{
    private static readonly HashSet<string> AllowedKeys = new(StringComparer.Ordinal)
    {
        "dashboards",
        "saved-queries",
        "ui-settings",
    };

    private readonly string _root;
    private readonly object _gate = new();

    public DocumentStore(string? root = null)
    {
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EQDeeps");
    }

    public static bool IsValidKey(string key) => AllowedKeys.Contains(key);

    /// <summary>Returns the stored document, or null when none exists yet.</summary>
    public JsonElement? Read(string key)
    {
        EnsureKey(key);
        var path = PathFor(key);
        lock (_gate)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                return doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                return null; // corrupt file: treat as absent rather than failing forever
            }
        }
    }

    public void Write(string key, JsonElement document)
    {
        EnsureKey(key);
        var path = PathFor(key);
        var json = JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
        lock (_gate)
        {
            Directory.CreateDirectory(_root);
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
    }

    private string PathFor(string key) => Path.Combine(_root, key + ".json");

    private static void EnsureKey(string key)
    {
        if (!IsValidKey(key))
        {
            throw new ArgumentException($"Unknown document key '{key}'.", nameof(key));
        }
    }
}
