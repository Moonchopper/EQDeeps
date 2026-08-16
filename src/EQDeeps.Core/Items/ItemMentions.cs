using EQDeeps.Core.Events;
using EQDeeps.Core.Query;
using EQDeeps.Core.Sessions;

namespace EQDeeps.Core.Items;

/// <summary>How an item came up in the log.</summary>
public enum ItemMentionKind
{
    /// <summary>Named in chat — by whoever, in whatever channel.</summary>
    Chat,
    /// <summary>Looted, by the character or someone in the group.</summary>
    Looted,
    /// <summary>Sold to a merchant.</summary>
    Sold,
    /// <summary>Bought from a merchant.</summary>
    Bought,
}

/// <summary>
/// One time an item was named. <see cref="Who"/> is the looter, the seller,
/// the buyer or the chat sender; <see cref="Where"/> is the corpse, the
/// merchant, or the chat channel; <see cref="Text"/> is the chat line for a
/// chat mention and null otherwise. <see cref="Id"/> is filled from the
/// registry when a client file has numbered the item.
/// </summary>
public sealed record ItemMention(
    DateTime At,
    ItemMentionKind Kind,
    string Item,
    int? Id,
    string Who,
    string? Where,
    string? Text,
    int Quantity);

public sealed record ItemMentionsResult(
    List<ItemMention> Mentions,
    /// <summary>How many the scope held before the limit; the client shows "n of m".</summary>
    int Total,
    /// <summary>How many item names the scanner knew — zero explains an empty chat column.</summary>
    int KnownNames);

/// <summary>
/// The item feed (F29): every time an item was looted, sold, bought or
/// named in chat inside a scope, newest first. Like the incoming feed it is
/// deliberately not a QuerySpec — the point is the list, with who and when —
/// and like it, it reads the record stream under the session gate and caps
/// the answer.
/// </summary>
public static class ItemMentionsBuilder
{
    public const int DefaultLimit = 200;
    public const int MaxLimit = 2000;

    public static ItemMentionsResult Build(
        RecordStore records,
        FightTracker fights,
        ItemRegistry registry,
        ItemMentionScanner scanner,
        QueryScope scope,
        int limit = DefaultLimit)
    {
        limit = Math.Clamp(limit, 1, MaxLimit);
        var union = IncomingHitsBuilder.ResolveScope(records, fights, scope);
        var found = new List<ItemMention>();
        foreach (var segment in union.Segments)
        {
            foreach (var (at, evt) in records.Range(segment.Begin, segment.End))
            {
                switch (evt)
                {
                    case LootEvent { Item: { } item } loot:
                        found.Add(new ItemMention(at, ItemMentionKind.Looted, ItemNames.Strip(item), IdOf(registry, item),
                            loot.Looter, loot.Source, null, loot.Quantity));
                        break;
                    case MerchantEvent m:
                        found.Add(new ItemMention(at, m.Sold ? ItemMentionKind.Sold : ItemMentionKind.Bought,
                            ItemNames.Strip(m.Item), IdOf(registry, m.Item), "You", m.Merchant, null, m.Quantity));
                        break;
                    case ChatEvent chat when !scanner.IsEmpty:
                        foreach (var name in scanner.Find(chat.Text))
                        {
                            found.Add(new ItemMention(at, ItemMentionKind.Chat, name, IdOf(registry, name),
                                chat.Sender, ChannelLabel(chat), chat.Text, 1));
                        }

                        break;
                }
            }
        }

        var total = found.Count;
        // Newest first: the reason to open the feed is what just happened.
        found.Reverse();
        if (found.Count > limit)
        {
            found.RemoveRange(limit, found.Count - limit);
        }

        return new ItemMentionsResult(found, total, registry.Count);
    }

    private static int? IdOf(ItemRegistry registry, string name) => registry.Find(name)?.Id;

    private static string ChannelLabel(ChatEvent chat) => chat.Channel switch
    {
        ChatChannel.Custom => chat.CustomChannel ?? "channel",
        ChatChannel.Tell => chat.Receiver is { } to ? $"tell to {to}" : "tell",
        var c => c.ToString().ToLowerInvariant(),
    };
}
