# Done state and convergence-based polling

**Status:** approved
**Date:** 2026-06-23
**Replaces:** the perpetual-polling model from the 2026-04-27 spec

## Problem

The current state machine (`Pending` → `HasData` / `NoData`) polls items indefinitely. `HasData` items are refreshed on every daily scan, forever. `NoData` items cool down but never stop being checked. For a stable YouTube library, this means thousands of wasted SponsorBlock API calls on videos whose segment data converged long ago.

SponsorBlock data is crowdsourced and converges quickly after a video's release. A video older than ~1 month will not gain new segments. Younger videos converge within days — if the data hasn't changed for 5 consecutive daily checks, it has converged.

The current model also has a stale-segments bug (fixed separately): when SponsorBlock data disappears (downvoted/removed), the orchestrator updated state but did not delete the stale segments from Jellyfin. That fix is already in place; this spec ensures it stays covered on all paths.

## Goals

- Stop polling videos whose SponsorBlock data has converged.
- Use the YouTube publish date (`BaseItem.PremiereDate`, populated by TubeArchivist) as the primary convergence signal: videos older than 30 days → one final fetch, then freeze.
- Use a consecutive-unchanged counter as the secondary convergence signal: 5 daily fetches with identical segment data → freeze.
- Clean refactor of the daily scan task: separate "reconcile known items" from "discover untracked items."
- Drop-and-recreate the SQLite database on upgrade — no in-place migration of old rows.

## Non-goals

- Using SponsorBlock API `locked`/`votes` fields for early convergence detection.
- Per-item manual freeze/unfreeze UI.
- Configurable per-library convergence thresholds.

## State machine

### States

| Value | Name | Description |
|---|---|---|
| 0 | `Pending` | Fetched at least once, no segments yet, still inside the sanity window. |
| 1 | `HasData` | Has segments. Being polled daily until convergence. |
| 2 | `NoData` | Sanity-checked at ≥ `PendingSanityHours` and still empty. Being polled daily until convergence. |
| 3 | `Done` | Terminal. SponsorBlock data has converged. Excluded from all polling. Segments in Jellylin are frozen. Reversible only via reset endpoint. |

### Transitions

```
(no row)     ──age gate──→ Done (after one fetch)
(no row)     ──young────→ Pending / HasData (as today)
Pending      ──young────→ HasData / Pending (as today, with counter)
HasData      ──young────→ HasData / Done (counter hits threshold)
NoData       ──young────→ NoData / Done (counter hits threshold)
any state    ──age gate──→ Done (after one fetch)
Done         ──any──────→ no-op
```

### Paths to Done

1. **Age gate** — if `now - PremiereDate >= ReleaseAgeCutoffDays` (default 30) at the time of any fetch, the item is processed one final time (fetch → write/delete segments), then stored as `Done`. Applies regardless of current state: `Pending`, `HasData`, `NoData`, or no row at all. Takes precedence over the consecutive-unchanged counter.

2. **Consecutive-unchanged** — only for items younger than the age cutoff. After each fetch, compute a hash of the returned segment UUIDs (sorted, skip-action only). Compare to `last_segment_hash` stored in the row:
   - Match → `consecutive_unchanged++`
   - No match → `consecutive_unchanged = 0`, update `last_segment_hash`
   - `consecutive_unchanged >= ConsecutiveUnchangedThreshold` (default 5) → `Done`

Empty API responses (no segments) also hash to a stable value, so a video that stays empty for 5 consecutive checks also reaches `Done`.

### PlaybackStart behavior

The existing `HasData` early-return on `PlaybackStart` (skip if segments already exist in Jellyfin) extends to `Done` — both are no-ops on playback.

### Failure rule (unchanged)

Only successful API responses (HTTP 200 or HTTP 404) advance state, update `last_fetch_at`, or touch the counter/hash. Transient HTTP failures (5xx, network, timeout, 429) leave the row untouched. The counter does not increment on failure — a SponsorBlock outage does not falsely converge items.

## SegmentHasher

New static helper, lives next to `SegmentMapper.cs`:

```csharp
public static class SegmentHasher
{
    public static string Hash(IReadOnlyList<SponsorBlockSegment> segments)
    {
        var uuids = segments
            .Where(s => s.ActionType == "skip")
            .Select(s => s.UUID)
            .OrderBy(u => u, StringComparer.Ordinal)
            .ToArray();
        var input = uuids.Length == 0 ? "__empty__" : string.Join("|", uuids);
        return SHA256.HashData(Encoding.UTF8.GetBytes(input)).ToHexString();
    }
}
```

Design decisions:
- Only `skip`-action segments are hashed — matches `SegmentMapper.Map` which filters to `skip`.
- UUID-based: segment UUIDs are stable identifiers assigned by SponsorBlock on submission. Timestamp adjustments create new UUIDs, so the UUID set is the right granularity for "has the data changed."
- Empty result has a fixed hash (`"__empty__"`): a video with no segments that stays empty increments `consecutive_unchanged` → eventually `Done`.
- Ordinal sort: deterministic across locales.

## SQLite schema

Drop-and-recreate on upgrade. `PRAGMA user_version` tracks the schema version.

```sql
PRAGMA user_version = 2;

CREATE TABLE item_state (
    item_id               BLOB PRIMARY KEY,        -- Jellyfin BaseItem.Id (16 bytes)
    video_id              TEXT NOT NULL,           -- 11-char YouTube ID
    state                 INTEGER NOT NULL,        -- 0=Pending 1=HasData 2=NoData 3=Done
    first_seen_at         INTEGER NOT NULL,        -- unix seconds (UTC)
    last_fetch_at         INTEGER NOT NULL,        -- unix seconds (UTC), last successful HTTP completion
    segment_count         INTEGER NOT NULL DEFAULT 0,
    consecutive_unchanged INTEGER NOT NULL DEFAULT 0,
    last_segment_hash     TEXT NOT NULL DEFAULT ''
);
CREATE INDEX idx_state ON item_state(state);
CREATE INDEX idx_first_seen ON item_state(first_seen_at);
```

`EnsureSchema` logic: read `PRAGMA user_version`. If it doesn't match the expected version (2), `DROP TABLE IF EXISTS item_state` and recreate with the full schema, then set `PRAGMA user_version = 2`.

`ItemStateRow` record:

```csharp
public sealed record ItemStateRow(
    Guid ItemId,
    string VideoId,
    ItemState State,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastFetchAt,
    int SegmentCount,
    int ConsecutiveUnchanged,
    string LastSegmentHash);
```

### GetActiveAsync query

```sql
WHERE state IN (0, 1, 2)
```

Returns `Pending` + `HasData` + `NoData`. `Done` (3) is excluded — the daily scan never touches converged items.

The `NoData` cooldown logic (skip if within `PendingSanityHours` of `last_fetch_at`) stays in the orchestrator. The daily scan surfaces the row; the orchestrator decides whether to fetch or skip.

## Orchestrator changes

`ProcessAsync` reads `item.PremiereDate` and passes it to `ProcessLockedAsync` as a new `DateTimeOffset?` parameter.

`ProcessLockedAsync` flow:

```
ProcessLockedAsync(itemId, premiereDate, videoId, reason, config, ct):

  existing = store.Get(itemId)
  if existing?.State == Done → return (no-op)

  // ── cooldown checks (existing, unchanged) ──
  NoData cooldown, PlaybackStart poll window, etc.

  // ── fetch from API (existing, unchanged) ──
  apiSegments = api.GetSegments(videoId, categories, ct)

  // ── compute hash ──
  hash = SegmentHasher.Hash(apiSegments)
  unchanged = existing?.LastSegmentHash == hash

  // ── age gate check ──
  isMature = premiereDate != null && (now - premiereDate) >= ReleaseAgeCutoffDays

  if isMature:
      write/delete segments (existing logic)
      store.Upsert(Done, consecutive=0, hash)
      return

  // ── young item: normal flow + counter ──
  if hasSegments:
      write/delete segments (existing logic)
      consecutive = unchanged ? existing.consecutive + 1 : 0
      newState = consecutive >= threshold ? Done : HasData
      store.Upsert(newState, consecutive, hash)
  else:
      delete segments
      consecutive = unchanged ? existing.consecutive + 1 : 0
      newState = consecutive >= threshold ? Done : (sanityElapsed ? NoData : Pending)
      store.Upsert(newState, consecutive, hash)
```

If `PremiereDate` is null (defensive — shouldn't happen for TubeArchivist items), the age gate is skipped. The item stays in the young-item flow indefinitely — same behavior as today.

## Daily scan task refactor

`SponsorBlockRefreshTask.ExecuteAsync` becomes a clean two-phase dispatcher with no timing logic:

**Phase 1: Reconcile known items.**
Iterate `GetActiveAsync()` rows (`Pending` + `HasData` + `NoData`). For each row:
- Item deleted from library → delete row + delete segments (orphan cleanup).
- Item exists → orchestrator `ProcessAsync(item, DailyScan)`.

**Phase 2: Discover untracked items.**
Load all row IDs (any state, including `Done`) into a `HashSet<Guid>`. Iterate all scoped videos. For each video whose ID is not in the set → orchestrator `ProcessAsync(item, DailyScan)`.

**Removed:**
- `GetOldScopedVideosWithoutActiveRows` method and its `PendingSanityHours`/`DateCreated` cutoff.
- `TimeProvider _time` dependency (only used by the cutoff).
- `ToUtc` helper (only used by the cutoff).

The task is a pure dispatcher. All fetch/skip/cooldown/age-gate/counter logic lives in the orchestrator.

The `RequestDelayMilliseconds` throttle, progress reporting, and sequential processing remain unchanged.

## Configuration additions

```csharp
public int ReleaseAgeCutoffDays { get; set; } = 30;
public int ConsecutiveUnchangedThreshold { get; set; } = 5;
```

`configPage.html`: two numeric inputs under the existing "Advanced" disclosure. Labels: "Release age cutoff (days)" and "Consecutive unchanged threshold".

No existing config fields are removed or renamed. `PlaybackPollHours`, `PendingSanityHours`, `RequestDelayMilliseconds`, `DailyScanHour` all stay unchanged.

## Migration strategy

Drop and recreate the SQLite database on upgrade. No in-place migration.

On startup, `SqliteSponsorBlockStateStore.EnsureSchema` checks `PRAGMA user_version`. If it doesn't match the expected version, the `item_state` table is dropped and recreated with the full schema. All existing rows are lost.

Stale Jellyfin segments from the old install are cleaned up naturally: the first daily scan discovers all scoped videos as untracked (no row exists for any of them). The orchestrator fetches each one. Age gate sends old videos to `Done` after one fetch. The `DeleteOwnedAsync` + `CreateAsync` cycle (or just `DeleteOwnedAsync` for empty results) replaces/clears old segments.

## Testing

**SegmentHasher tests** (new file):
- Empty list → fixed non-empty hash.
- Single segment → stable hash.
- Multiple segments, shuffled input order → same hash as sorted.
- Segment removed → different hash.
- Segment added → different hash.
- Non-skip segments excluded from hash.

**Orchestrator tests** (extend existing file):
- `Done` state + any trigger → no-op (no API call, no store write).
- Young item, first fetch, segments found → `HasData`, `consecutive=0`, hash populated.
- Young item, second fetch, same segments → `HasData`, `consecutive=1`.
- Young item, 5th consecutive unchanged fetch → `Done`.
- Young item, segments change between fetches → counter resets to 0.
- Age gate: old `PremiereDate` + any state → one fetch → `Done`.
- Age gate: `PremiereDate` null → age gate skipped, young-item flow used.
- Young `NoData` item, empty response unchanged for 5 checks → `Done`.
- Young item, 4 unchanged then 1 changed → counter resets, not `Done`.
- Stale segments deleted on transition to `Done` when API returns empty.

**Daily scan task tests** (extend existing file):
- Phase 1: iterates `Pending` + `HasData` + `NoData` rows, skips `Done`.
- Phase 1: orphan row (item deleted) → row + segments deleted.
- Phase 2: scoped video with no row → orchestrator called.
- Phase 2: scoped video with `Done` row → not discovered (has a row).
- No `DateCreated` cutoff logic remaining.
- `_time` dependency removed from task constructor.

**State store tests** (extend existing file):
- `GetActiveAsync` returns `Pending` + `HasData` + `NoData`, excludes `Done`.
- Round-trip with new `ConsecutiveUnchanged` and `LastSegmentHash` fields.
- Schema version check → drop/recreate on version mismatch.

## Guided gates

- **GG-1:** After upgrade, confirm the SQLite database is recreated with the new schema (`consecutive_unchanged`, `last_segment_hash` columns present, `PRAGMA user_version = 2`).
- **GG-2:** Add a new YouTube video younger than 30 days. Confirm it enters `Pending` or `HasData`. Trigger 5 daily scans with no SponsorBlock data change. Confirm it transitions to `Done` and is no longer polled.
- **GG-3:** Trigger a daily scan on a library with videos older than 30 days. Confirm they are fetched once and transition directly to `Done`. Confirm `GetActiveAsync` no longer returns them on the next daily scan.
- **GG-4:** Confirm `PlaybackStart` on a `Done` item is a no-op (no API call, no state change).
- **GG-5:** Confirm the reset endpoint clears `Done` rows and segments, allowing re-convergence from scratch.
- **GG-6:** Confirm stale segments (from the old install) are deleted from Jellyfin during the first post-upgrade daily scan for videos that now return empty from SponsorBlock.
