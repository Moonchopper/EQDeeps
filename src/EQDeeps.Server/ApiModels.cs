using EQDeeps.Core.Achievements;
using EQDeeps.Core.Items;
using EQDeeps.Core.Reference;
using EQDeeps.Core.Mobs;
using EQDeeps.Core.Parsing;
using EQDeeps.Core.Query;
using EQDeeps.Core.Sessions;
using EQDeeps.Server.Reference;
using EQDeeps.Server.Updates;

namespace EQDeeps.Server;

public sealed record OpenSessionRequest(string Path, DateTime? BackfillFrom = null, bool EmuMode = false);

/// <summary>How long a "no thanks" to an update should last.</summary>
public enum DeferScope
{
    /// <summary>Until the app restarts, or the user checks by hand.</summary>
    Once,

    /// <summary>Until a release newer than the one offered ships.</summary>
    Release,

    /// <summary>Until the user is running a different version than they are now.</summary>
    CurrentVersion,
}

public sealed record DeferUpdateRequest(DeferScope Scope);

/// <summary>
/// Consent to install. <paramref name="ApplyWhenReady"/> distinguishes "update
/// now" (restart as soon as the download lands) from the default, which waits
/// until the user closes the app on their own terms.
/// </summary>
public sealed record StageUpdateRequest(bool ApplyWhenReady = false);

public sealed record SetUpdateModeRequest(UpdateMode Mode);

/// <summary>Timeline scope; a record wrapper so filters (kinds, actors) can grow in later.</summary>
public sealed record TimelineRequest(QueryScope Scope);

/// <summary>
/// Which incoming swings to hand back and how many (F26).
/// </summary>
/// <param name="OwnerOnly">
/// Restrict to this log's own character. Resolved server-side against whichever
/// log is open rather than by naming them, so the setting means "me" on every
/// session instead of meaning one character everywhere — the same rule the
/// stance panels follow.
/// </param>
/// <summary>One NPC as a reference site lists it, with the page a person can read.</summary>
public sealed record NpcListing(string Name, int? Level, int Id, string Url);

/// <summary>
/// One zone a name is listed in, known from the listings' ids alone — no
/// stat block fetched (ADR-020). <see cref="Name"/> is null for a listing
/// filed under a zone id this build has no place for.
/// </summary>
/// <param name="ShortName">What a roster and a map are opened by; null with <see cref="Name"/>.</param>
/// <param name="Maps">Every map that draws the place, first-listed first.</param>
/// <param name="Levels">The levels the name is listed at there.</param>
/// <param name="Id">One listing there — the one to open for "this mob, in this zone".</param>
public sealed record NpcPlaceRow(
    string? Name,
    string? ShortName,
    IReadOnlyList<string> Maps,
    string? Era,
    IReadOnlyList<int> Levels,
    int Listings,
    int Id);

/// <summary>
/// One name in a browse: the levels it is listed at, a listing per level, and
/// the zones it stands in. A site lists the same mob once per zone it stands
/// in, so a name is one row here however many addresses it has (ADR-020).
/// </summary>
public sealed record NpcBrowseRow(
    string Name,
    int? MinLevel,
    int? MaxLevel,
    int Listings,
    IReadOnlyList<NpcListing> Levels,
    IReadOnlyList<NpcPlaceRow> Places);

/// <summary>
/// Search over the reference index; <see cref="Error"/> says why it is empty,
/// when it is. <see cref="Total"/> is how many names matched before the limit
/// — a level-band browse shows the first hundred of eight hundred and should
/// say so.
/// </summary>
public sealed record NpcSearchResult(string Source, IReadOnlyList<NpcBrowseRow> Npcs, string? Error, int Total = 0);

/// <summary>Every NPC the site lists in one zone, for the Map view (F30 × F27).</summary>
public sealed record ZoneRosterResult(string Source, ZoneRoster Roster, string? Error);

/// <summary>
/// A level band for every zone the site lists enough of, for the World view's
/// labels (F27 × F30): the middle half of the listed levels of who stands
/// there (<see cref="ZoneLevels"/>). <see cref="Known"/> is false when there
/// is no index to read — reference off, never fetched, unreachable — and
/// <see cref="Error"/> says which.
/// </summary>
public sealed record ZoneLevelsResult(string Source, bool Known, IReadOnlyList<ZoneLevelBand> Zones, string? Error);

/// <summary>One listing's full stat block.</summary>
public sealed record NpcDetailResult(string Source, string Url, NpcDetail Detail);

/// <summary>
/// A name from the log matched to a listing. <see cref="Exact"/> is false when
/// no /consider level backed the choice, so the UI can say the match is a
/// guess rather than dress it up as a measurement.
/// </summary>
public sealed record NpcLookupResult(
    string Source,
    NpcListing Listing,
    bool Exact,
    IReadOnlyList<int> ObservedLevels,
    NpcDetail? Detail);

/// <summary>The item feed's scope, the same shape as the incoming feed's (F29).</summary>
public sealed record ItemMentionsRequest(QueryScope Scope, int? Limit = null);

/// <summary>Everything the server's registry knows; <see cref="Numbered"/> is how many rows carry a game id.</summary>
public sealed record ItemReport(string Server, IReadOnlyList<ItemRecord> Items, int Numbered);

/// <summary>
/// One character's Slayer progress (F35 / ADR-023 Decision 1): whatever
/// <c>/outputfile achievements</c> last wrote to their install, read fresh on every request — the
/// app keeps no count of its own, because the game counts kills by race id and nothing outside it
/// does.
/// </summary>
/// <param name="Found">
/// False either when there is nowhere to look (<see cref="Path"/> is null) or the export has not
/// been written yet. Both are the ordinary case, not an error — <see cref="Problem"/> is null for
/// the second one, because "run the command" is not a problem, it is the next step.
/// </param>
/// <param name="Path">
/// Where the export would be, even if nothing is there yet — or null when the log is not under an
/// install's <c>Logs\</c> folder at all, so there is no install to look in.
/// </param>
/// <param name="ExportedUtc">The export file's own last-write time, so the view can say how stale the numbers are.</param>
/// <param name="Problem">
/// Set only when something is actually wrong (no install to read from, or the file could not be
/// read) — a sentence naming the trouble and, where there is one, the fix. Never set just because
/// the export has not been written yet.
/// </param>
public sealed record SlayerReport(bool Found, string? Path, DateTime? ExportedUtc, string Command, string? Problem,
    IReadOnlyList<SlayerMetaAchievement> Meta, IReadOnlyList<SlayerKillAchievement> Kills, int SkippedLines);

/// <summary>
/// Builds a <see cref="SlayerReport"/> from whatever is on disk right now. There is deliberately no
/// cache and no store here (ADR-023): the export is 64 KB and a parse costs about a millisecond, and
/// a player who just typed the command in game expects the very next refresh to show it, not the
/// next time some background job gets around to it.
/// </summary>
public static class SlayerReports
{
    // Mirrors AchievementExport.Parse's own 4 MB cap. Parse would throw a file this large away
    // anyway, so checking the length first (a FileInfo, not a read) skips reading it into a string
    // only to discard it.
    private const long MaxExportLength = 4 * 1024 * 1024;

    public static SlayerReport Build(string logPath, string character, string server)
    {
        var installRoot = LogDiscovery.InstallRootOf(logPath);
        if (installRoot is null)
        {
            return new SlayerReport(false, null, null, AchievementExport.Command,
                "This log isn't inside an EverQuest install's Logs folder, so there's no " +
                "achievements export to read. Open the log from its original install to track Slayer progress.",
                [], [], 0);
        }

        var path = AchievementExport.PathFor(installRoot, character, server);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                // Not a problem — the player just hasn't run the command yet. Command carries the
                // fix regardless of Found, so the view can offer it as the next step either way.
                return new SlayerReport(false, path, null, AchievementExport.Command, null, [], [], 0);
            }

            if (info.Length > MaxExportLength)
            {
                // Parse would hand this file back empty, and an empty report with Found set draws
                // as "0 of 0 complete" with no word of why. A real export is about 64 KB; whatever
                // this is, it is not one, so say that — and the command that replaces it.
                return new SlayerReport(false, path, null, AchievementExport.Command,
                    "The achievements export is far larger than anything the game writes, so it wasn't read. " +
                    $"Run {AchievementExport.Command} in game to write a fresh one.",
                    [], [], 0);
            }

            string text;
            // The game may hold the file open while it writes it — share everything, the posture
            // ItemStore.Changed uses for the client's own files.
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                text = reader.ReadToEnd();
            }

            var parsed = AchievementExport.Parse(text);
            var progress = Slayer.From(parsed);
            return new SlayerReport(true, path, info.LastWriteTimeUtc, AchievementExport.Command, null,
                progress.Meta, progress.Kills, parsed.SkippedLines);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
            or PathTooLongException or NotSupportedException)
        {
            return new SlayerReport(false, path, null, AchievementExport.Command,
                "Couldn't read the achievements export just now — it may be open in the game; try again in a moment.",
                [], [], 0);
        }
    }
}

public sealed record IncomingHitsRequest(
    QueryScope Scope,
    int? Limit = null,
    bool OwnerOnly = false,
    IReadOnlyList<string>? Defenders = null);

/// <param name="Install">
/// The installation the log belongs to, by folder name — "EverQuest Legends",
/// "EverQuest" — or absent when the log is not under a <c>Logs</c> folder.
/// The map settings are kept per install; see <see cref="LogDiscovery.InstallOf"/>.
/// </param>
public sealed record SessionInfo(
    string Id,
    string Path,
    string Character,
    string Server,
    bool BackfillComplete,
    int RecordCount,
    int FightCount,
    long UnrecognizedLines,
    long MalformedLines,
    /// <summary>Stance switches by this character — gates the Stances view.</summary>
    long StanceSwitches = 0,
    string? Install = null,
    /// <summary>Records this open took from the log cache instead of the parser (issue #59).</summary>
    long RestoredRecords = 0);

/// <summary>
/// Everything learned about one server's mobs (F25). The estimates are the
/// whole of it; the counts are there so the panel can say how much evidence is
/// behind what it is showing rather than presenting a first-night guess with
/// the same face as a thousand-kill average.
/// </summary>
/// <param name="Instanced">
/// Whether any of it came from an instance. On a server with no difficulty
/// tiers the tier columns are noise, so the client asks this rather than
/// inferring it from rows that happen to be on screen.
/// </param>
public sealed record MobHealthReport(
    string Server,
    List<MobHealthEstimate> Mobs,
    int Kills,
    bool Instanced);

/// <summary>
/// What this server's mobs do to the people in front of them (F26).
/// </summary>
/// <param name="Character">
/// Whose log is asking. The profiles are the server's, but which of them are
/// about <i>this</i> character is a question only the session can answer.
/// </param>
/// <param name="CharacterLevel">
/// The level the log last established for that character, or null if it never
/// did. It picks which rows the panel opens on, and its absence is reported
/// rather than guessed around — a level-58 shown a level-40's numbers would be
/// reading someone else's fight.
/// </param>
/// <param name="Landed">Hits behind the whole report, so a first-night guess does not wear the face of an evening's evidence.</param>
public sealed record MobAttackReport(
    string Server,
    string Character,
    int? CharacterLevel,
    List<MobAttackEstimate> Mobs,
    int Landed,
    bool Instanced);

/// <summary>
/// The fight list as the REST endpoint returns it: the rows, and the tracker
/// version they are a snapshot of, so a client can tell whether a live delta
/// applies on top of what it holds.
/// </summary>
public sealed record FightList(int Version, List<FightInfo> Fights);

/// <summary>
/// One live push of fights. <see cref="Full"/> means <see cref="Fights"/> is
/// the whole list; otherwise it is only the fights changed after
/// <see cref="BaseVersion"/>, to be merged by id into a list the client
/// already holds at that version or later — a raid's worth of closed fights
/// is not re-sent every time the open one takes a hit (measured: 2 MB per
/// push, once a second in combat, on an 8,000-fight log). Deltas cannot say
/// a fight is gone, and a learned-health snapshot changing moves every
/// fight's estimate, so both of those force a full push.
/// </summary>
public sealed record FightsPush(string SessionId, int Version, int BaseVersion, bool Full, List<FightInfo> Fights);

public sealed record FightInfo(
    int Id,
    string Name,
    DateTime BeginTime,
    DateTime LastDamageTime,
    bool Dead,
    bool Closed,
    long DamageTotal,
    long TankingTotal,
    int TauntCount,
    int GroupIndex,
    /// <summary>
    /// The instance difficulty this was fought at, null in the open world —
    /// which is also what a tier-0 instance reads as, since the log writes the
    /// two identically. See <see cref="InstanceZone"/>.
    /// </summary>
    int? Difficulty,
    /// <summary>
    /// Learned health for this mob at this zone and difficulty (F25), null
    /// until enough of them have been killed. Paired with
    /// <see cref="DamageTotal"/> it says whether this fight was a whole kill or
    /// a share of one.
    /// </summary>
    long? EstimatedHealth,
    /// <summary>
    /// This session's own character and their pets, out of
    /// <see cref="DamageTotal"/>. One number rather than the whole per-actor
    /// map: it keeps the fight list cheap at raid scale while giving the
    /// client a per-fight series for its own character — which is what any
    /// comparison across unequal windows has to be built from, since totals
    /// over a 36-minute set and a 2-minute one are not comparable at all.
    /// </summary>
    long CharacterDamage)
{
    /// <param name="health">
    /// Learned mob health keyed by <see cref="MobHealthStore.KeyOf"/>, or null
    /// when the store is not attached (tests, and any build that has never
    /// recorded a kill). A missing entry is normal, not an error: a mob nobody
    /// has killed enough of simply has no number yet.
    /// </param>
    /// <param name="sinceVersion">
    /// Build only fights changed after this <see cref="Fight.Version"/> — a
    /// live delta. The pull-chain grouping is still computed over every
    /// fight, because a fight's group is a fact about its neighbours; only
    /// the rows are cut down. The default builds them all.
    /// </param>
    public static List<FightInfo> Build(
        IReadOnlyList<Fight> fights,
        string character,
        IdentityRegistry identity,
        IReadOnlyDictionary<string, MobHealthEstimate>? health = null,
        int sinceVersion = -1)
    {
        var groupIndex = new Dictionary<int, int>();
        var groups = FightTracker.Group(fights);
        for (var g = 0; g < groups.Count; g++)
        {
            foreach (var fight in groups[g])
            {
                groupIndex[fight.Id] = g;
            }
        }

        return fights
            .Where(f => f.Version > sinceVersion)
            .Select(f => new FightInfo(
                f.Id, f.Name, f.BeginTime, f.LastDamageTime, f.Dead, f.Closed,
                f.DamageTotal, f.TankingTotal, f.TauntCount, groupIndex[f.Id],
                f.Zone?.Difficulty,
                HealthOf(f, health),
                OwnDamage(f, character, identity)))
            .ToList();
    }

    private static long? HealthOf(
        Fight fight, IReadOnlyDictionary<string, MobHealthEstimate>? health)
    {
        if (health is null || fight.Zone is not { BaseName.Length: > 0 } zone)
        {
            return null;
        }

        return health.TryGetValue(
            MobHealthStore.KeyOf(fight.Name, zone.BaseName, zone.Difficulty), out var estimate)
            ? estimate.Health
            : null;
    }

    /// <summary>
    /// Pets roll up to their owner here unconditionally. A pet's damage is the
    /// player's doing whatever the display toggle says, and a per-fight series
    /// that flickered as that toggle moved would compare two different things.
    /// </summary>
    private static long OwnDamage(Fight fight, string character, IdentityRegistry identity)
    {
        var total = 0L;
        foreach (var (actor, totals) in fight.DamageByActor)
        {
            if (actor.Equals(character, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(identity.OwnerOf(actor), character, StringComparison.OrdinalIgnoreCase))
            {
                total += totals.Total;
            }
        }

        return total;
    }
}
