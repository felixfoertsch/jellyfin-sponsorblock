# Playback-triggered SponsorBlock fetching

**Status:** approved
**Date:** 2026-04-27
**Replaces:** the current `IMediaSegmentProvider`-driven scan model

## Problem

The plugin currently registers `SponsorBlockSegmentProvider : IMediaSegmentProvider`. Jellyfin's media-segment scan invokes the provider for every library item, which is slow even though `Supports()` filters to YouTube filenames — the iteration itself walks the entire library.

The scan model also misses fresh data. SponsorBlock data is crowdsourced and accumulates after a video's release. A fetch-on-import strategy permanently misses any segment contributed after the first scan, unless a periodic re-scan runs over everything (slow).

## Goals

- Replace the periodic full-library scan with event-driven fetching.
- Keep segments fresh as SponsorBlock data accumulates.
- Scope behavior to a configurable subset of libraries (e.g. only the TubeArchivist library).
- Keep the SponsorBlock public API load proportional to actual usage, not library size.

## Non-goals

- Per-library category overrides.
- One-shot bulk backfill of an existing library on first install.
- A "reset state / clear cache" admin UI.
- Migration of segments previously written by the old `IMediaSegmentProvider`. Old segments will be replaced naturally on first refresh of each `HasData` item.

## Architecture

```
┌────────────────────────────────────────────────────────────────┐
│  Triggers                                                      │
│  ┌────────────────┐  ┌──────────────────┐  ┌────────────────┐  │
│  │ ItemAdded      │  │ PlaybackStart    │  │ Daily 06:00    │  │
│  │ HostedService  │  │ HostedService    │  │ ScheduledTask  │  │
│  └───────┬────────┘  └────────┬─────────┘  └────────┬───────┘  │
└──────────┼────────────────────┼─────────────────────┼──────────┘
           │                    │                     │
           ▼                    ▼                     ▼
┌────────────────────────────────────────────────────────────────┐
│  SponsorBlockOrchestrator                                      │
│  ProcessAsync(item, reason) →                                  │
│    1. LibraryScopeService.IsInScope(item)?                     │
│    2. YouTubeIdExtractor.Extract(filename)                     │
│    3. StateStore.Get(itemId) → decide if eligible              │
│    4. SponsorBlockApiClient.GetSegmentsAsync(videoId)          │
│    5. IMediaSegmentManager.DeleteSegments + CreateSegment...   │
│    6. StateStore.Upsert(itemId, newState, lastFetchAt)         │
└─────┬──────────────────────────┬──────────────────┬────────────┘
      │                          │                  │
      ▼                          ▼                  ▼
┌──────────────┐    ┌────────────────────┐   ┌──────────────────┐
│ SQLite       │    │ SponsorBlock HTTP  │   │ Jellyfin         │
│ StateStore   │    │ ApiClient (exists) │   │ MediaSegmentMgr  │
└──────────────┘    └────────────────────┘   └──────────────────┘
```

All write paths go through `SponsorBlockOrchestrator.ProcessAsync(item, reason)`. The state-machine logic exists in exactly one place; triggers decide *when* to call it, not *what* to do.

The `IMediaSegmentProvider` registration is **removed**. Segments are written via `IMediaSegmentManager` directly. Owned segments are tagged with a constant provider-name string (`"SponsorBlock"`) so writes are stable across restarts and can be reliably replaced on refresh.

Per-item concurrency is enforced with a `ConcurrentDictionary<Guid, SemaphoreSlim>` inside the orchestrator so two triggers firing for the same item don't double-fetch.

## State machine

Stored states (one row per item in SQLite):

- `Pending` — fetched at least once, no segments yet, still inside the 48h grace window
- `HasData` — has segments
- `NoData` — sanity-checked at ≥48h post-`first_seen_at` and still nothing → cooldown; rechecked after `PendingSanityHours`

`Unknown` and `Sanity` are transient computations, not stored.

### Decision table

`Orchestrator.ProcessAsync(item, reason)`:

| Current row state | Trigger | Action |
|---|---|---|
| (no row) | any | Insert `first_seen_at=now` (`DailyScan` may use Jellyfin `DateCreated` for old archive items), fetch, persist as `Pending`, `HasData`, or `NoData` |
| `Pending` | `PlaybackStart` | If `now - first_seen_at >= playbackPollHours` (default 24h): fetch; promote to `HasData` if segments returned. Otherwise no-op. |
| `Pending` | `DailyScan` | If `now - first_seen_at >= sanityHours` (default 48h): sanity scan → `HasData` or `NoData`. Else: opportunistic fetch (same code path, stays `Pending` if empty). |
| `Pending` | `ItemAdded` | Shouldn't happen (no row → row insert covers ItemAdded). Log warning, treat as new. |
| `HasData` | `DailyScan` | Refresh: `DeleteSegments` + `CreateSegment` per new segment + update `last_fetch_at` |
| `HasData` | `PlaybackStart` | No-op (data already present; daily scan handles freshness) |
| `HasData` | `ItemAdded` | Shouldn't happen — log warning |
| `NoData` | any | If `now - last_fetch_at < sanityHours`: no-op. Else fetch; promote to `HasData` if segments returned, otherwise remain `NoData` with refreshed `last_fetch_at`. |

### Rationale

- `Pending` items get one playback-triggered retry after `playbackPollHours` (default 24h), when SponsorBlock data should have converged. The daily scan also touches them, leading to the 48h sanity check.
- `HasData` items don't get hammered on every play.
- `NoData` items cool down after a sanity check, but are not permanent tombstones.
- The 48h grace covers the user's stated assumption that SponsorBlock data has converged ~24h after release with safety margin.

### Failure rule (single source of truth)

Only **successful** API responses (HTTP 200 or HTTP 404) advance state or update `last_fetch_at`. Transient HTTP failures (5xx, network, timeout, 429) leave the row untouched and emit a warning. This means a SponsorBlock outage during the daily scan does not falsely promote items to `NoData`.

## SQLite schema

Database file: `<plugin-data-dir>/sponsorblock-state.db` under `IApplicationPaths.DataPath/<plugin-id>/`.

```sql
CREATE TABLE item_state (
  item_id        BLOB PRIMARY KEY,        -- Jellyfin BaseItem.Id (16 bytes)
  video_id       TEXT NOT NULL,           -- 11-char YouTube ID
  state          INTEGER NOT NULL,        -- 0=Pending 1=HasData 2=NoData
  first_seen_at  INTEGER NOT NULL,        -- unix seconds (UTC), set on insert
  last_fetch_at  INTEGER NOT NULL,        -- unix seconds (UTC), last successful HTTP completion
  segment_count  INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX idx_state ON item_state(state);
CREATE INDEX idx_first_seen ON item_state(first_seen_at);
```

All times stored as UTC unix seconds. `DailyScanHour` from config is interpreted as **local time** (matches user expectation "06:00 in the morning").

## Components

### `ISponsorBlockStateStore` / `SqliteSponsorBlockStateStore`
- CRUD on the `item_state` table.
- Methods: `Get(itemId)`, `Upsert(row)`, `Delete(itemId)`, `GetActive()` (returns all `Pending` + `HasData` rows for the daily scan).
- Single shared connection (Microsoft.Data.Sqlite is thread-safe with WAL).
- Schema initialized on first construction.

### `LibraryScopeService` / `ILibraryScopeService`
- Reads `EnabledLibraryIds` from config on each call.
- Walks `item.GetParents()` until it finds a `CollectionFolder`; returns true iff its id is in the allowlist.
- **Empty allowlist → returns false.** Safe default: a fresh install does nothing until the admin picks libraries.

### `SponsorBlockOrchestrator`
- Single `ProcessAsync(BaseItem item, ProcessReason reason, CancellationToken ct)` entry point.
- `enum ProcessReason { ItemAdded, PlaybackStart, DailyScan }`.
- Implements the decision table above.
- Holds the per-item `SemaphoreSlim` map; releases + cleans up entries when not in use.
- Depends on `ISponsorBlockApiClient`, `ISponsorBlockStateStore`, `ILibraryScopeService`, `IMediaSegmentManager`, `TimeProvider`, `ILogger`.

### `ISponsorBlockApiClient`
- New interface extracted from existing concrete `SponsorBlockApiClient`. Same surface: `GetSegmentsAsync(videoId, categories, ct)`.
- Enables mocking in orchestrator tests.

### `ItemAddedHostedService : IHostedService`
- `StartAsync`: subscribes to `ILibraryManager.ItemAdded`.
- Handler filters to `Video` items and dispatches to orchestrator with `reason=ItemAdded` on a background task. Never blocks the library scan.

### `PlaybackStartHostedService : IHostedService`
- `StartAsync`: subscribes to `ISessionManager.PlaybackStart`.
- Handler dispatches to orchestrator with `reason=PlaybackStart` on a background task. Never blocks playback.

### `ItemRemovedHostedService : IHostedService`
- `StartAsync`: subscribes to `ILibraryManager.ItemRemoved`.
- Deletes the SQLite row and removes owned segments via `IMediaSegmentManager.DeleteSegmentsAsync`. Avoids orphan growth.

### `SponsorBlockRefreshTask : IScheduledTask`
- Default trigger: daily at `DailyScanHour:00` local time (Jellyfin's `TaskTriggerInfo.Type = "DailyTrigger"`).
- `ExecuteAsync`: `StateStore.GetActive()` → for each row, `_libraryManager.GetItemById(row.item_id)`. If null, drop row + delete owned segments. Else hand to orchestrator with `reason=DailyScan`. Then enumerate scoped library videos older than `PendingSanityHours` that were not active rows, so existing archives and cooled-down `NoData` rows can be discovered by the daily task.
- Sequential, with a `RequestDelayMilliseconds` delay between requests to avoid spiking the public SponsorBlock API.
- Reports progress through `IProgress<double>` so the Tasks page renders a progress bar.

## Configuration additions

Append to `PluginConfiguration`:

```csharp
public Guid[] EnabledLibraryIds { get; set; } = Array.Empty<Guid>();
public int DailyScanHour { get; set; } = 6;            // 0..23, local time
public int PlaybackPollHours { get; set; } = 24;       // age boundary for playback-triggered fetch on Pending items
public int PendingSanityHours { get; set; } = 48;      // age boundary for Pending → NoData sanity transition
public int RequestDelayMilliseconds { get; set; } = 200;
```

`configPage.html` additions:
- Multi-select listing all virtual folders, fetched from `/Library/VirtualFolders` on page load. Stores GUIDs.
- Hour input (0–23) for daily scan time.
- Numeric inputs for `PendingSanityHours` and `RequestDelayMilliseconds` under an "Advanced" disclosure.
- Existing category toggles and filename-pattern fields stay where they are.

## DI registrations

`PluginServiceRegistrator`:
- **Remove:** `AddSingleton<IMediaSegmentProvider, SponsorBlockSegmentProvider>()`.
- **Keep:** `AddSingleton<SponsorBlockApiClient>()` plus add `AddSingleton<ISponsorBlockApiClient>(sp => sp.GetRequiredService<SponsorBlockApiClient>())`.
- **Add:** `AddSingleton<ISponsorBlockStateStore, SqliteSponsorBlockStateStore>()`.
- **Add:** `AddSingleton<ILibraryScopeService, LibraryScopeService>()`.
- **Add:** `AddSingleton<SponsorBlockOrchestrator>()`.
- **Add:** `AddHostedService<ItemAddedHostedService>()`, `AddHostedService<PlaybackStartHostedService>()`, `AddHostedService<ItemRemovedHostedService>()`.
- The scheduled task is auto-discovered by Jellyfin via assembly scan for `IScheduledTask`.

## Error and edge-case handling

| Failure | Action |
|---|---|
| HTTP 5xx / network / timeout | Log warning, return without touching state. Next trigger retries. |
| HTTP 200 with empty array | Real "no segments" answer. Update `last_fetch_at`. State stays/becomes `Pending` (or `NoData` if past sanity). |
| HTTP 404 | Treated as empty-array (SponsorBlock returns 404 when no segments). |
| HTTP 429 | Log warning, no state change. The request-delay throttle should prevent these. |
| Other HTTP 4xx | Log warning, no state change. |
| SQLite write failure | Log error and rethrow; corrupt state is worse than a missed update. |
| `IMediaSegmentManager.CreateSegmentAsync` throws | Log error, do not advance state. Retried by next trigger. |
| State row exists but item gone (`GetItemById == null`) | Drop row + delete owned segments (daily scan cleanup path). |

Edge cases:

- **Concurrent triggers, same item.** Per-item `SemaphoreSlim` serializes them. The second one re-reads state and likely no-ops.
- **Item moves to a different library.** Scope is re-checked on every call. Removing a library from config silently stops refreshing those items; their `HasData` rows go stale but harmlessly.
- **Library removed from config.** Existing rows + owned segments stay. No automatic cleanup. (Future "reset state" UI is out of scope for v1.)
- **Plugin upgrade adds a category to config.** User toggles it on; existing `HasData` items get the new category at next daily refresh (≤24h). Acceptable.
- **Fresh install on existing library.** Items enter the system on `ItemAdded` (new downloads), `PlaybackStart` (when user plays), or the daily scan once their Jellyfin item `DateCreated` is older than `PendingSanityHours`.
- **Plugin disabled then re-enabled.** SQLite persists; `IHostedService.StartAsync` re-subscribes.
- **Server clock skew across the 48h boundary.** All stored times are UTC seconds. The 48h boundary is computed against UTC `now`.

## Testing

xUnit project `Jellyfin.Plugin.SponsorBlock.Tests` (already present).

- **`SponsorBlockOrchestratorTests`** — the bulk. Mock `ISponsorBlockApiClient`, `ISponsorBlockStateStore`, `ILibraryScopeService`, `IMediaSegmentManager`. Use `FakeTimeProvider` so the 48h boundary is deterministic. One test per row of the decision table, plus failure-mode tests (HTTP error → no transition, 404 → state advances, etc.).
- **`SqliteSponsorBlockStateStoreTests`** — real SQLite against `:memory:`. Round-trip CRUD + the `GetActive()` query.
- **`LibraryScopeServiceTests`** — fake item hierarchy, verify in/out of scope, empty allowlist → false.
- **`SponsorBlockRefreshTaskTests`** — mock `IStateStore` + `_libraryManager`. Verifies it iterates `Pending`+`HasData`, discovers old scoped videos without active rows, drops orphaned rows, throttles between requests.
- **Existing tests for `YouTubeIdExtractor`, `CategoryMapping`, `SponsorBlockSegmentProvider.MapSegments`** — keep. The mapping logic moves to the orchestrator; `MapSegments` survives as a static helper (re-home it if `SponsorBlockSegmentProvider` is deleted).

Services depend on **interfaces** (`ISponsorBlockApiClient`, `ISponsorBlockStateStore`, `ILibraryScopeService`, `TimeProvider`) — not concrete classes — to keep tests cheap.

## To verify during implementation

- Exact namespace + signature of `IMediaSegmentManager.CreateSegmentAsync` / `DeleteSegmentsAsync`. The DLL exposes the names; signatures need a quick check before writing the orchestrator.
- Whether `ISessionManager.PlaybackStart` event payload contains the `BaseItem` directly or only an item id (resolve via `_libraryManager.GetItemById` if the latter).
- Whether `IHostedService` is the correct lifecycle for event subscription in current Jellyfin (10.11.x), or if `IServerEntryPoint` is still the recommended path.
- That `Microsoft.Data.Sqlite` doesn't conflict with Jellyfin's own SQLite version (Jellyfin core uses SQLite via Emby.Server.Implementations).

## Guided gates

- **GG-1:** After deploy, add a brand-new YouTube video to the configured library. Within ~10 seconds, segments appear on the item (verified via the Jellyfin web client's media segment indicator or the `/MediaSegments/{itemId}` API).
- **GG-2:** Add a brand-new YouTube video that has no SponsorBlock data yet (e.g. just-uploaded). Confirm a row appears in SQLite with state=`Pending`. Play the video; confirm a fetch is triggered. Wait 48h+; confirm the daily scan promotes it to `NoData` if still empty.
- **GG-3:** Configure the library allowlist to only the TubeArchivist library. Add an item to a non-listed library (e.g. movies); confirm no SQLite row is created and no SponsorBlock request is made.
- **GG-4:** Confirm the new "SponsorBlock daily refresh" task appears in Jellyfin's Tasks page, is configured to run daily at the hour set in plugin config, and completes successfully when triggered manually.
- **GG-5:** During a SponsorBlock outage (simulate by pointing the API base URL to a black-hole), trigger the daily scan. Confirm no rows transition to `NoData` and warnings are logged.
