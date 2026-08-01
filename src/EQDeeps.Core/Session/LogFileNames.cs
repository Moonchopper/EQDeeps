namespace EQDeeps.Core.Sessions;

/// <summary>
/// Parses character and server from EverQuest log filenames:
/// eqlog_&lt;Character&gt;_&lt;server&gt;.txt (optionally .gz). EMU servers and
/// hand-copied files deviate, so parsing is tolerant — a server segment may
/// contain underscores or digits; only the eqlog_ prefix and two segments are
/// required.
/// </summary>
public static class LogFileNames
{
    public static bool TryParse(string path, out string character, out string server)
    {
        character = string.Empty;
        server = string.Empty;

        var name = Path.GetFileName(path);
        if (name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^3];
        }

        var dot = name.LastIndexOf('.');
        if (dot > 0)
        {
            name = name[..dot];
        }

        const string PrefixToken = "eqlog_";
        if (!name.StartsWith(PrefixToken, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = name[PrefixToken.Length..];
        var underscore = rest.IndexOf('_');
        if (underscore <= 0 || underscore == rest.Length - 1)
        {
            return false;
        }

        character = rest[..underscore];
        server = rest[(underscore + 1)..];
        return true;
    }
}
