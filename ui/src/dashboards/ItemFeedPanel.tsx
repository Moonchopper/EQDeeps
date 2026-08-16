import { useEffect, useMemo, useState } from "react";
import { api, type ItemMention, type ItemMentionKind } from "../api";
import { fmtClock, fmtNum } from "../format";
import { frameScope, type TimeFrame } from "../timeFrame";
import { fuzzyMatch, type FuzzyHit } from "../fuzzy";
import { LookupLink } from "../lookup/LookupLink";
import { Highlight, TableSearch } from "./tableTools";

/** Newest first, and this many at most: the reason to open the feed is what just happened. */
const FEED_LIMIT = 300;

const KIND_LABEL: Record<ItemMentionKind, string> = {
  looted: "looted",
  sold: "sold",
  bought: "bought",
  chat: "chat",
};

/**
 * The item feed (F29): every time an item was looted, sold, bought or named
 * in chat inside the time frame, newest first, each with a lookup door — the
 * "linked in chat or looted" half of issue #62. Like the incoming feed it is
 * not a QuerySpec: the list is the information, with who and when, and no
 * aggregation keeps that.
 *
 * <p>Chat mentions are a dictionary match against what the server's registry
 * already knows (Legends writes no link markup, see ADR-019), so a name
 * nobody on this server has looted, sold, bought or filtered is invisible
 * here. The empty state says how many names it was looking for, so an empty
 * chat column reads as "nothing known yet" and not "nothing said".</p>
 */
export function ItemFeedPanel({ sessionId, frame }: { sessionId: string; frame: TimeFrame }) {
  const [feed, setFeed] = useState<ItemMention[] | null>(null);
  const [total, setTotal] = useState(0);
  const [known, setKnown] = useState(0);
  const [query, setQuery] = useState("");

  const scope = useMemo(() => frameScope(frame), [frame]);

  // Follows the time frame and the live tail on the same beat as the charts;
  // nothing is pushed for it, the server's tick banks items and the list is
  // cheap to re-read.
  useEffect(() => {
    if (!sessionId) return;
    let cancelled = false;
    const load = () =>
      api
        .itemMentions(sessionId, scope, { limit: FEED_LIMIT })
        .then((result) => {
          if (cancelled) return;
          setFeed(result.mentions);
          setTotal(result.total);
          setKnown(result.knownNames);
        })
        .catch(() => undefined);
    load();
    const timer = window.setInterval(load, 3000);
    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, [sessionId, scope]);

  const rows = useMemo(() => {
    const all = feed ?? [];
    if (!query.trim()) return all.map((m) => ({ m, hit: undefined as FuzzyHit | undefined }));
    // The item is the thing being searched for; who and where are fallbacks,
    // and a hit on them highlights nothing (the positions would be theirs).
    const out: { m: ItemMention; hit: FuzzyHit | undefined }[] = [];
    for (const m of all) {
      const onItem = fuzzyMatch(m.item, query);
      if (onItem) {
        out.push({ m, hit: onItem });
      } else if (fuzzyMatch(m.who, query) || fuzzyMatch(m.where ?? "", query) || fuzzyMatch(m.text ?? "", query)) {
        out.push({ m, hit: undefined });
      }
    }
    return out;
  }, [feed, query]);

  if (feed === null) return <div className="empty">Loading…</div>;

  return (
    <div className="table-panel">
      <TableSearch
        value={query}
        onChange={setQuery}
        placeholder="Filter by item, who or where…"
        shown={rows.length}
        total={feed.length}
      />
      <div className="table-scroll">
        {feed.length === 0 ? (
          <div className="empty">
            No items looted, sold, bought or named in chat in this time frame.
            {known === 0 && (
              <>
                {" "}
                Chat mentions need names the app already knows — this server has
                none yet; they arrive with the first loot.
              </>
            )}
          </div>
        ) : (
          <table className="item-feed">
            <thead>
              <tr>
                <th>Time</th>
                <th>What</th>
                <th>Item</th>
                <th>Who</th>
                <th>Where</th>
              </tr>
            </thead>
            <tbody>
              {rows.map(({ m, hit }, i) => (
                <tr key={`${m.at}|${m.item}|${i}`} className={`item-${m.kind}`} title={m.text ?? undefined}>
                  <td className="subtle">{fmtClock(m.at)}</td>
                  <td>
                    <span className={`item-kind item-kind-${m.kind}`}>{KIND_LABEL[m.kind]}</span>
                    {m.quantity > 1 && <span className="subtle"> ×{m.quantity}</span>}
                  </td>
                  <td className="mob-name">
                    <Highlight text={m.item} hit={hit} />
                    <LookupLink kind="item" name={m.item} id={m.id} />
                  </td>
                  <td className="subtle">{m.who}</td>
                  <td className="subtle item-where">
                    {m.where}
                    {/* The corpse a thing dropped from is a mob; give it its door too. */}
                    {m.kind === "looted" && m.where && <LookupLink kind="npc" name={m.where} />}
                    {m.kind === "chat" && m.text && <span className="item-text"> — {m.text}</span>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
      {total > feed.length && (
        <div className="subtle item-feed-note">newest {fmtNum(feed.length)} of {fmtNum(total)} in the time frame</div>
      )}
    </div>
  );
}
