using EQDeeps.Core.Reference;
using EQDeeps.Server.Reference;
using Xunit;

namespace EQDeeps.Server.Tests;

/// <summary>
/// <see cref="BundledReferenceSnapshot"/> against the REAL data embedded from <c>data/eqlbase/</c>
/// (ADR-020 Decision 1's amendment) — the only file in this project that reads that data rather than
/// a fake. It touches no network either way: everything here comes out of the assembly's own
/// manifest resources, exactly as a published build would read it.
/// </summary>
public sealed class ReferenceSnapshotTests
{
    /// <summary>Every <c>eqlbase/npcs-*.json</c> resource name, bare (no prefix) — the shard set this build actually shipped, read off the assembly rather than assumed.</summary>
    private static IReadOnlyList<string> ShardFileNames() =>
        typeof(BundledReferenceSnapshot).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("eqlbase/npcs-", StringComparison.Ordinal))
            .Select(n => n["eqlbase/".Length..])
            .ToList();

    [Fact] // S5
    public void TheRealSnapshotParsesToASubstantialIndexAndAtLeastOneRealShard()
    {
        var snapshot = new BundledReferenceSnapshot();

        Assert.NotNull(snapshot.SnapshotUtc);

        var indexText = snapshot.Read("search-index.json");
        Assert.NotNull(indexText);
        var names = NpcReferenceFormat.ParseIndex(indexText!);
        Assert.True(names.Count > 5000, $"expected > 5,000 index entries, got {names.Count}");
        Assert.NotNull(snapshot.EtagFor("search-index.json"));

        // West Karana (client zone id 12, ADR-020 Decision 6) — one of the shards checked while
        // that decision was written, and large enough that "> 100" is not a coin flip.
        var karanaText = snapshot.Read("npcs-12.json");
        Assert.NotNull(karanaText);
        var karana = NpcReferenceFormat.ParseShard(karanaText!);
        Assert.True(karana.Count > 100, $"expected > 100 listings in npcs-12.json, got {karana.Count}");
        Assert.NotNull(snapshot.EtagFor("npcs-12.json"));
    }

    [Fact] // S6, first invariant
    public void NoListingWithANullFactionCarriesAnyFactionHits()
    {
        var snapshot = new BundledReferenceSnapshot();
        var offenders = new List<string>();

        foreach (var file in ShardFileNames())
        {
            var text = snapshot.Read(file);
            Assert.NotNull(text); // every name GetManifestResourceNames() gave us must be readable
            foreach (var detail in NpcReferenceFormat.ParseShard(text!).Values)
            {
                if (detail.Faction is null && detail.FactionHits.Count > 0)
                {
                    offenders.Add($"{detail.Id} {detail.Name}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "listings with faction=null but non-empty factionHits (ADR-023 Decision 5's placeholder rule): " +
            string.Join(", ", offenders.Take(20)));
    }

    [Fact] // S6, second invariant
    public void EveryRaceLabelInTheSlayerTableOccursInTheRealData()
    {
        var raceLabels = DistinctRaceLabels();
        Assert.True(raceLabels.Count > 0, "could not read slayer-races.tsv at all — check the resource name");

        var seenRaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var snapshot = new BundledReferenceSnapshot();
        foreach (var file in ShardFileNames())
        {
            var text = snapshot.Read(file);
            if (text is null)
            {
                continue;
            }

            foreach (var detail in NpcReferenceFormat.ParseShard(text).Values)
            {
                if (detail.Race is { Length: > 0 } race)
                {
                    seenRaces.Add(race);
                }
            }
        }

        var missing = raceLabels.Where(r => !seenRaces.Contains(r)).OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToList();

        // Per the brief: if this fails, the table is not to be edited here — report which labels
        // the reference data does not carry, as a finding for the architect.
        Assert.True(missing.Count == 0,
            "slayer-races.tsv names a race no listing in the bundled data carries: " + string.Join(", ", missing));
    }

    /// <summary>
    /// Every distinct race label <c>slayer-races.tsv</c> maps some term to, read straight off the
    /// embedded resource with the table's own simple grammar (term TAB race[|race...] TAB why) —
    /// mirroring <c>EQDeeps.Core.Achievements.SlayerRaces.Parse</c> rather than depending on it,
    /// since that type's enumeration surface is internal to Core and this project only needs the
    /// set of race labels, not the term lookup.
    /// </summary>
    private static IReadOnlyList<string> DistinctRaceLabels()
    {
        var coreAssembly = typeof(NpcReferenceFormat).Assembly; // EQDeeps.Core, wherever slayer-races.tsv is embedded
        var resourceName = coreAssembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("slayer-races.tsv", StringComparison.Ordinal));
        if (resourceName is null)
        {
            return [];
        }

        using var stream = coreAssembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();

        var races = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }

            var cells = line.Split('\t');
            if (cells.Length < 2)
            {
                continue;
            }

            foreach (var race in cells[1].Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                races.Add(race);
            }
        }

        return races.ToList();
    }
}
