using System.Text.Json;

namespace EQDeeps.Server.Reference;

/// <summary>
/// The reference data's own shipped copy, as <see cref="NpcReferenceStore"/> sees it (ADR-020
/// Decision 1, reversed 2026-09-21 — see the amendment there before anything else here). Reading it
/// is never a request: this is a second local source alongside the on-disk cache, not a stand-in for
/// the network, which is why the store's load order tries this only after memory and a cache file
/// actually newer than <see cref="SnapshotUtc"/>. See <c>data/eqlbase/README.md</c> for what the
/// snapshot is, and its own takedown path.
/// </summary>
public interface IReferenceSnapshot
{
    /// <summary>When the snapshot was taken; null means there is none — the data folder was deleted
    /// for this build (the README's takedown path), or a caller deliberately wants none (tests, and
    /// any test that swaps <c>IReferenceSource</c> for a fake, per <see cref="ServerApp"/>'s comment
    /// on its own default).</summary>
    DateTime? SnapshotUtc { get; }

    /// <summary>The named file's bytes as shipped ("search-index.json", "npcs-12.json"), or null when
    /// the snapshot has no such file.</summary>
    string? Read(string fileName);

    /// <summary>The ETag the snapshot recorded for this file at fetch time — what Refresh's
    /// conditional GET carries when the snapshot's copy, not a later cache file, is the one in
    /// use.</summary>
    string? EtagFor(string fileName);
}

/// <summary>
/// Everything null: with this, <see cref="NpcReferenceStore"/> behaves exactly as it did before the
/// snapshot existed — fetch on demand, cache locally, revalidate on a schedule (ADR-020 Decisions
/// 1-2, before the amendment). The store's own default when nobody supplies a snapshot, and what a
/// test gets when it swaps in a fake <see cref="IReferenceSource"/> — see the comment on
/// <c>ServerApp.Build</c>'s default for why that pairing is deliberate.
/// </summary>
public sealed class NoReferenceSnapshot : IReferenceSnapshot
{
    public DateTime? SnapshotUtc => null;

    public string? Read(string fileName) => null;

    public string? EtagFor(string fileName) => null;
}

/// <summary>
/// The snapshot embedded into this assembly from <c>data/eqlbase/</c> (see the conditional
/// <c>EmbeddedResource</c> group in <c>EQDeeps.Server.csproj</c>), read once and kept in memory —
/// eighty-odd small JSON files and a manifest, worth holding rather than re-parsing the manifest on
/// every call. A missing or malformed manifest (a build published with the data folder deleted, the
/// README's takedown path, or a corrupt resource) leaves every member answering null rather than
/// throwing: this data is never load-bearing (ADR-020 Decision 3), and an install without it is
/// simply an install with no snapshot, not a broken one.
/// </summary>
public sealed class BundledReferenceSnapshot : IReferenceSnapshot
{
    private const string ResourcePrefix = "eqlbase/";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly Lazy<Manifest?> _manifest = new(Load, isThreadSafe: true);

    public DateTime? SnapshotUtc => _manifest.Value?.SnapshotUtc;

    public string? Read(string fileName)
    {
        if (_manifest.Value is not { } manifest || !manifest.Files.ContainsKey(fileName))
        {
            return null;
        }

        try
        {
            using var resource = typeof(BundledReferenceSnapshot).Assembly.GetManifestResourceStream(ResourcePrefix + fileName);
            if (resource is null)
            {
                return null;
            }

            using var reader = new StreamReader(resource);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return null;
        }
    }

    public string? EtagFor(string fileName) =>
        _manifest.Value is { } manifest && manifest.Files.TryGetValue(fileName, out var file) ? file.Etag : null;

    private static Manifest? Load()
    {
        try
        {
            using var resource = typeof(BundledReferenceSnapshot).Assembly.GetManifestResourceStream(ResourcePrefix + "manifest.json");
            if (resource is null)
            {
                return null; // the takedown path: data/eqlbase/ was not there to embed at build time
            }

            using var reader = new StreamReader(resource);
            var dto = JsonSerializer.Deserialize<ManifestDto>(reader.ReadToEnd(), Json);
            return dto?.Files is null ? null : new Manifest(dto.SnapshotUtc, dto.Files);
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

    private sealed record Manifest(DateTime? SnapshotUtc, Dictionary<string, ManifestFileDto> Files);

    // Only the two fields the store needs; scripts/snapshot-eqlbase.mjs also writes `path`,
    // `fetchedUtc` and `bytes`/`rows`, which System.Text.Json ignores here without complaint.
    private sealed record ManifestFileDto(string? Etag);

    private sealed record ManifestDto(DateTime? SnapshotUtc, Dictionary<string, ManifestFileDto>? Files);
}
