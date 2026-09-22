using System.Text.RegularExpressions;

namespace EQDeeps.Core.Achievements;

/// <summary>
/// One faction the export itself says this player is working on. The unlock achievements
/// ("Untapped Potential: Races/Classes/Deity") enumerate them explicitly, one component per
/// faction, so there is no settings screen and no asking the player to name standings they may not
/// know the names of (F35, ADR-023 Decision 5).
/// </summary>
/// <param name="Name">The spelling as this faction was first written in the file, file order.</param>
/// <param name="Key">The <see cref="FactionNames"/> join key, for matching against the faction export and the reference layer.</param>
/// <param name="UnlockEarned">
/// Whether every component naming this faction is complete. Not the same claim as "this faction is
/// at maximum standing": a component can complete another way (a race's own faction starts
/// unlocked at character creation), and one earned by standing stays earned after the standing
/// falls — six of the owner's forty disagree this way, which is why the faction export's actual
/// standing belongs beside this rather than instead of it.
/// </param>
/// <param name="Unlocks">The titles of the achievements naming this faction, in file order, distinct.</param>
public sealed record ProtectedFaction(string Name, string Key, bool UnlockEarned, IReadOnlyList<string> Unlocks);

/// <summary>
/// Finds every "Get maximum faction with X." component in a parsed export and groups them by
/// faction. Every category is read, not only Slayer's — the unlock achievements that name
/// protected factions live under "Untapped Potential: …", and nothing in the export scopes a
/// faction unlock to any particular category.
/// </summary>
public static class ProtectedFactions
{
    // Anchored on both ends and lazy on the name, so a trailing period (present on some rows,
    // absent on others in the reference file) is optional rather than part of the captured name.
    private static readonly Regex UnlockComponent = new(
        @"^Get maximum faction with (?<name>.+?)\.?$", RegexOptions.Compiled);

    public static IReadOnlyList<ProtectedFaction> From(AchievementExportFile file)
    {
        var order = new List<string>();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var everyComponentComplete = new Dictionary<string, bool>(StringComparer.Ordinal);
        var unlocks = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var achievement in file.Achievements)
        {
            foreach (var component in achievement.Components)
            {
                var match = UnlockComponent.Match(component.Text);
                if (!match.Success)
                {
                    continue;
                }

                var name = match.Groups["name"].Value;
                var key = FactionNames.Key(name);

                if (!names.ContainsKey(key))
                {
                    names[key] = name; // first spelling wins — file order
                    everyComponentComplete[key] = true;
                    unlocks[key] = [];
                    order.Add(key);
                }

                everyComponentComplete[key] = everyComponentComplete[key] && component.Complete;

                var titles = unlocks[key];
                if (!titles.Contains(achievement.Title))
                {
                    titles.Add(achievement.Title);
                }
            }
        }

        return order
            .Select(key => new ProtectedFaction(names[key], key, everyComponentComplete[key], unlocks[key]))
            .ToList();
    }
}
