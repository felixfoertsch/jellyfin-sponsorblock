# Playback-Triggered SponsorBlock Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the slow `IMediaSegmentProvider` library scan with an event-driven orchestrator that fetches SponsorBlock data on item-add, playback-start, and a daily refresh task — backed by a SQLite per-item state machine and scoped to configured Jellyfin libraries.

**Architecture:** A single `SponsorBlockOrchestrator` enforces the state machine (`Pending`/`HasData`/`NoData`) and is the only writer to Jellyfin's `IMediaSegmentManager`. Three trigger services (`IHostedService`s for ItemAdded/PlaybackStart/ItemRemoved + an `IScheduledTask` for the daily run) all funnel into it. State is persisted in `Microsoft.Data.Sqlite`. Library scope is enforced by walking parent `CollectionFolder`s against an admin-configured allowlist.

**Tech Stack:** .NET 9, Jellyfin 10.11 (`Jellyfin.Controller` package), Microsoft.Data.Sqlite, xUnit, Microsoft.Extensions.TimeProvider.Testing.

**Spec:** `docs/superpowers/specs/2026-04-27-playback-triggered-sponsorblock-design.md`

---

## Conventions used in this plan

- Indentation in C# files is **tabs** (project-wide convention).
- All new types live in `Jellyfin.Plugin.SponsorBlock` or a sub-namespace.
- All new tests live in `Jellyfin.Plugin.SponsorBlock.Tests`, mirroring the source layout.
- After every task that compiles, run `dotnet build` from the repo root and confirm zero warnings (project has `TreatWarningsAsErrors`).
- After every task with tests, run `dotnet test` from the repo root and confirm all green.

---

## Task 1: Verify Jellyfin MediaSegmentManager API surface

This is a spike. Before designing types around it, learn the exact signatures of `IMediaSegmentManager.CreateSegmentAsync` and `DeleteSegmentsAsync` and the `ISessionManager.PlaybackStart` event payload.

**Files:**
- Create (temporary, deleted at end of task): `Jellyfin.Plugin.SponsorBlock/_Probe.cs`

- [ ] **Step 1: Write a probe file that forces the compiler to print the signatures**

```csharp
// Jellyfin.Plugin.SponsorBlock/_Probe.cs
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.MediaSegments;

namespace Jellyfin.Plugin.SponsorBlock;

internal static class _Probe
{
	public static void Inspect(IMediaSegmentManager mgr, ISessionManager sm)
	{
		// Trigger an overload error so dotnet build prints candidate signatures.
		mgr.CreateSegmentAsync(default(int), default(int));
		mgr.DeleteSegmentsAsync(default(int), default(int), default(int));
		sm.PlaybackStart += (_, _) => { };
	}
}
```

- [ ] **Step 2: Build and capture the compiler errors**

Run: `dotnet build Jellyfin.Plugin.SponsorBlock 2>&1 | grep -E 'CS1503|CS7036|CS0123|CS0029|CS0234|delegate' | head -40`

The output will list candidate methods + the actual `EventHandler<T>` delegate type for `PlaybackStart`. Record exact signatures here in this plan as a comment for later tasks:

```
CreateSegmentAsync(MediaSegmentDto mediaSegment, string providerName) → Task<MediaSegmentDto>   // NOTE: no CancellationToken
DeleteSegmentsAsync(Guid itemId, CancellationToken cancellationToken) → Task                    // NOTE: deletes ALL segments for itemId — no provider filter
PlaybackStart event payload type → EventHandler<PlaybackProgressEventArgs>                       // namespace: MediaBrowser.Controller.Library
ItemAdded event payload type (ILibraryManager) → EventHandler<ItemChangeEventArgs>               // namespace: MediaBrowser.Controller.Library
ItemRemoved event payload type (ILibraryManager) → EventHandler<ItemChangeEventArgs>             // namespace: MediaBrowser.Controller.Library
```

**Important caveat:** `DeleteSegmentsAsync` in 10.11 has no per-provider filter — it nukes every segment for the given item, regardless of which plugin wrote it. For the scoped YouTube library this is fine in practice (no other segment provider acts on those items), but if a user later installs another segment plugin that targets the same library, our daily refresh would clobber its segments. Document this in the README before release. The `IMediaSegmentWriter.DeleteOwnedAsync` method should still be the orchestrator's only delete path — its docstring needs to reflect this "delete-all" reality.

If `ILibraryManager.ItemAdded`/`ItemRemoved` payload types are uncertain, repeat the probe trick for them too:

```csharp
using MediaBrowser.Controller.Library;
public static void InspectLib(ILibraryManager lib)
{
	lib.ItemAdded += (_, _) => { };
	lib.ItemRemoved += (_, _) => { };
}
```

- [ ] **Step 3: Update this plan in place**

Edit Task 1 of this plan and replace the `?` placeholders with actual signatures discovered in Step 2. Subsequent tasks reference these signatures.

- [ ] **Step 4: Delete the probe and confirm clean build**

```bash
rm Jellyfin.Plugin.SponsorBlock/_Probe.cs
dotnet build
```

Expected: clean build with no errors or warnings.

- [ ] **Step 5: Commit the plan update**

```bash
git add docs/superpowers/plans/2026-04-27-playback-triggered-sponsorblock.md
git commit -m "record verified jellyfin mediasegment + event api signatures in impl plan"
```

---

## Task 2: Add `ISponsorBlockApiClient` interface

Extract the API client behind an interface so the orchestrator can be unit-tested without HTTP.

**Files:**
- Modify: `Jellyfin.Plugin.SponsorBlock/SponsorBlockApiClient.cs`
- Create: `Jellyfin.Plugin.SponsorBlock/ISponsorBlockApiClient.cs`

- [ ] **Step 1: Create the interface**

```csharp
// Jellyfin.Plugin.SponsorBlock/ISponsorBlockApiClient.cs
namespace Jellyfin.Plugin.SponsorBlock;

/// <summary>
/// Fetches skip segments from the SponsorBlock public API.
/// </summary>
public interface ISponsorBlockApiClient
{
	/// <summary>
	/// Fetches skip segments for a YouTube video.
	/// </summary>
	/// <param name="videoId">The 11-character YouTube video ID.</param>
	/// <param name="categories">SponsorBlock category names to include.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>List of segments. Empty list if SponsorBlock has no data (HTTP 404 or 200 [] both map to empty).</returns>
	/// <exception cref="HttpRequestException">Network or transport error. Caller treats as transient.</exception>
	Task<IReadOnlyList<SponsorBlockSegment>> GetSegmentsAsync(
		string videoId,
		IReadOnlyList<string> categories,
		CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Make `SponsorBlockApiClient` implement the interface and propagate failures**

The current implementation **swallows** `HttpRequestException` and returns an empty list. The orchestrator's failure rule needs to distinguish "real empty answer" (404 / `[]`) from "transient failure" — so we must rethrow transient errors.

Modify `Jellyfin.Plugin.SponsorBlock/SponsorBlockApiClient.cs`:
- Change `public class SponsorBlockApiClient` → `public class SponsorBlockApiClient : ISponsorBlockApiClient`.
- In `GetSegmentsAsync`, **delete the `catch (HttpRequestException ex) { ... return []; }` block** so transient failures propagate. Keep the `404 → []` behavior — that is a real "no segments" answer per SponsorBlock's contract.

- [ ] **Step 3: Build and run existing tests**

Run: `dotnet build && dotnet test`
Expected: all green.

- [ ] **Step 4: Commit**

```bash
git add Jellyfin.Plugin.SponsorBlock/ISponsorBlockApiClient.cs Jellyfin.Plugin.SponsorBlock/SponsorBlockApiClient.cs
git commit -m "extract ISponsorBlockApiClient, propagate transient http errors"
```

---

## Task 3: Move `MapSegments` to a standalone helper

Currently lives on `SponsorBlockSegmentProvider`, which we are about to delete. Move it to a static helper so other classes (the orchestrator) can use it and so the existing tests keep passing.

**Files:**
- Create: `Jellyfin.Plugin.SponsorBlock/SegmentMapper.cs`
- Modify: `Jellyfin.Plugin.SponsorBlock/SponsorBlockSegmentProvider.cs` (forward to new helper)
- Create: `Jellyfin.Plugin.SponsorBlock.Tests/SegmentMapperTests.cs`

- [ ] **Step 1: Create the helper**

```csharp
// Jellyfin.Plugin.SponsorBlock/SegmentMapper.cs
using MediaBrowser.Model.MediaSegments;

namespace Jellyfin.Plugin.SponsorBlock;

/// <summary>
/// Converts SponsorBlock API segments into Jellyfin <see cref="MediaSegmentDto"/> instances.
/// </summary>
public static class SegmentMapper
{
	/// <summary>
	/// Maps SponsorBlock API segments to Jellyfin segment DTOs.
	/// Skips segments that aren't `skip` actions, have invalid timing, or use unsupported categories.
	/// </summary>
	/// <param name="apiSegments">Segments returned by the SponsorBlock API.</param>
	/// <param name="itemId">The Jellyfin item id the segments are for.</param>
	/// <returns>Mapped DTOs in input order.</returns>
	public static IReadOnlyList<MediaSegmentDto> Map(
		IReadOnlyList<SponsorBlockSegment> apiSegments,
		Guid itemId)
	{
		var result = new List<MediaSegmentDto>();
		foreach (var seg in apiSegments)
		{
			if (seg.ActionType != "skip" || seg.Segment.Length < 2)
			{
				continue;
			}

			var segmentType = CategoryMapping.ToSegmentType(seg.Category);
			if (segmentType is null)
			{
				continue;
			}

			result.Add(new MediaSegmentDto
			{
				Id = Guid.NewGuid(),
				ItemId = itemId,
				Type = segmentType.Value,
				StartTicks = (long)(seg.Segment[0] * TimeSpan.TicksPerSecond),
				EndTicks = (long)(seg.Segment[1] * TimeSpan.TicksPerSecond),
			});
		}

		return result;
	}
}
```

- [ ] **Step 2: Replace `SponsorBlockSegmentProvider.MapSegments` body to delegate**

In `SponsorBlockSegmentProvider.cs`, replace the existing `MapSegments` method body with a one-liner:

```csharp
public static IReadOnlyList<MediaSegmentDto> MapSegments(
	IReadOnlyList<SponsorBlockSegment> apiSegments,
	Guid itemId) => SegmentMapper.Map(apiSegments, itemId);
```

- [ ] **Step 3: Add tests for `SegmentMapper`**

Create `Jellyfin.Plugin.SponsorBlock.Tests/SegmentMapperTests.cs` containing the same three test methods currently in `SponsorBlockSegmentProviderTests.cs`, but calling `SegmentMapper.Map` instead of `SponsorBlockSegmentProvider.MapSegments`. Copy the test bodies verbatim, only renaming the call site. (We delete the old file in a later task.)

- [ ] **Step 4: Build and test**

Run: `dotnet build && dotnet test`
Expected: all green; both old and new tests pass.

- [ ] **Step 5: Commit**

```bash
git add Jellyfin.Plugin.SponsorBlock/SegmentMapper.cs Jellyfin.Plugin.SponsorBlock/SponsorBlockSegmentProvider.cs Jellyfin.Plugin.SponsorBlock.Tests/SegmentMapperTests.cs
git commit -m "extract SegmentMapper helper, prepare for provider removal"
```

---

## Task 4: Extend `PluginConfiguration` with new fields

**Files:**
- Modify: `Jellyfin.Plugin.SponsorBlock/Configuration/PluginConfiguration.cs`

- [ ] **Step 1: Add the four new properties**

Append to `PluginConfiguration` class, after the `MusicOfftopic` property and before `GetCategorySettings`:

```csharp
	/// <summary>
	/// Gets or sets the Jellyfin library (CollectionFolder) ids the plugin should act on.
	/// Empty array → plugin is inert (safe default for fresh install).
	/// </summary>
	public Guid[] EnabledLibraryIds { get; set; } = Array.Empty<Guid>();

	/// <summary>
	/// Gets or sets the local hour (0..23) at which the daily refresh task runs.
	/// </summary>
	public int DailyScanHour { get; set; } = 6;

	/// <summary>
	/// Gets or sets the age boundary (hours since first_seen_at) within which Pending items
	/// re-fetch on every PlaybackStart trigger. After this, only the daily scan touches them.
	/// </summary>
	public int PlaybackPollHours { get; set; } = 24;

	/// <summary>
	/// Gets or sets the age boundary (hours since first_seen_at) at which a Pending item
	/// is sanity-checked one last time and promoted to NoData if still empty.
	/// </summary>
	public int PendingSanityHours { get; set; } = 48;

	/// <summary>
	/// Gets or sets the inter-request delay (milliseconds) used by the daily refresh task
	/// to avoid spiking the public SponsorBlock API.
	/// </summary>
	public int RequestDelayMilliseconds { get; set; } = 200;
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: clean build.

- [ ] **Step 3: Commit**

```bash
git add Jellyfin.Plugin.SponsorBlock/Configuration/PluginConfiguration.cs
git commit -m "add library scope, daily-scan hour, poll/sanity windows, throttle to PluginConfiguration"
```

---

## Task 5: Add `Microsoft.Data.Sqlite` dependency + `ItemState` model + `ISponsorBlockStateStore` interface

**Files:**
- Modify: `Jellyfin.Plugin.SponsorBlock/Jellyfin.Plugin.SponsorBlock.csproj`
- Create: `Jellyfin.Plugin.SponsorBlock/State/ItemState.cs`
- Create: `Jellyfin.Plugin.SponsorBlock/State/ItemStateRow.cs`
- Create: `Jellyfin.Plugin.SponsorBlock/State/ISponsorBlockStateStore.cs`

- [ ] **Step 1: Add the SQLite package reference**

Edit `Jellyfin.Plugin.SponsorBlock/Jellyfin.Plugin.SponsorBlock.csproj` and add inside the existing `<ItemGroup>` that has `<PackageReference Include="Jellyfin.Controller" ... />`:

```xml
<PackageReference Include="Microsoft.Data.Sqlite" Version="9.0.0" />
```

- [ ] **Step 2: Restore packages and confirm version is available**

Run: `dotnet restore Jellyfin.Plugin.SponsorBlock 2>&1 | tail -5`
Expected: success. If `9.0.0` is not present, use the latest 9.x.

- [ ] **Step 3: Create the state enum**

```csharp
// Jellyfin.Plugin.SponsorBlock/State/ItemState.cs
namespace Jellyfin.Plugin.SponsorBlock.State;

/// <summary>
/// Per-item lifecycle state for SponsorBlock fetching.
/// </summary>
public enum ItemState
{
	/// <summary>Fetched at least once, no segments yet, still inside the sanity window.</summary>
	Pending = 0,

	/// <summary>Has at least one segment from a successful fetch.</summary>
	HasData = 1,

	/// <summary>Sanity-checked at ≥ PendingSanityHours and still empty. Permanently skipped.</summary>
	NoData = 2,
}
```

- [ ] **Step 4: Create the row record**

```csharp
// Jellyfin.Plugin.SponsorBlock/State/ItemStateRow.cs
namespace Jellyfin.Plugin.SponsorBlock.State;

/// <summary>
/// One row in the SQLite item_state table.
/// </summary>
/// <param name="ItemId">Jellyfin BaseItem.Id (primary key).</param>
/// <param name="VideoId">11-character YouTube video id.</param>
/// <param name="State">Current lifecycle state.</param>
/// <param name="FirstSeenAt">UTC timestamp of first observation.</param>
/// <param name="LastFetchAt">UTC timestamp of last successful API response (200 or 404).</param>
/// <param name="SegmentCount">Number of segments persisted for this item.</param>
public sealed record ItemStateRow(
	Guid ItemId,
	string VideoId,
	ItemState State,
	DateTimeOffset FirstSeenAt,
	DateTimeOffset LastFetchAt,
	int SegmentCount);
```

- [ ] **Step 5: Create the interface**

```csharp
// Jellyfin.Plugin.SponsorBlock/State/ISponsorBlockStateStore.cs
namespace Jellyfin.Plugin.SponsorBlock.State;

/// <summary>
/// Persistent per-item state store backing the SponsorBlock state machine.
/// </summary>
public interface ISponsorBlockStateStore
{
	/// <summary>Returns the row for an item, or null if absent.</summary>
	ValueTask<ItemStateRow?> GetAsync(Guid itemId, CancellationToken cancellationToken);

	/// <summary>Inserts or replaces the row for an item.</summary>
	ValueTask UpsertAsync(ItemStateRow row, CancellationToken cancellationToken);

	/// <summary>Deletes the row for an item if present.</summary>
	ValueTask DeleteAsync(Guid itemId, CancellationToken cancellationToken);

	/// <summary>
	/// Returns all rows in <see cref="ItemState.Pending"/> or <see cref="ItemState.HasData"/> state,
	/// for use by the daily refresh task. <see cref="ItemState.NoData"/> rows are excluded.
	/// </summary>
	IAsyncEnumerable<ItemStateRow> GetActiveAsync(CancellationToken cancellationToken);
}
```

- [ ] **Step 6: Build**

Run: `dotnet build`
Expected: clean build.

- [ ] **Step 7: Commit**

```bash
git add Jellyfin.Plugin.SponsorBlock/Jellyfin.Plugin.SponsorBlock.csproj Jellyfin.Plugin.SponsorBlock/State/
git commit -m "add Microsoft.Data.Sqlite, define ItemState model and store interface"
```

---

## Task 6: Implement `SqliteSponsorBlockStateStore` (TDD)

**Files:**
- Create: `Jellyfin.Plugin.SponsorBlock.Tests/State/SqliteSponsorBlockStateStoreTests.cs`
- Create: `Jellyfin.Plugin.SponsorBlock/State/SqliteSponsorBlockStateStore.cs`

- [ ] **Step 1: Add Microsoft.Data.Sqlite test reference (test project doesn't yet reference it directly)**

The test project already references the main project, so it can use `Microsoft.Data.Sqlite` transitively. No csproj change needed.

- [ ] **Step 2: Write the failing tests**

```csharp
// Jellyfin.Plugin.SponsorBlock.Tests/State/SqliteSponsorBlockStateStoreTests.cs
using Jellyfin.Plugin.SponsorBlock.State;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jellyfin.Plugin.SponsorBlock.Tests.State;

public class SqliteSponsorBlockStateStoreTests : IAsyncLifetime
{
	private SqliteConnection _connection = null!;
	private SqliteSponsorBlockStateStore _store = null!;

	public Task InitializeAsync()
	{
		_connection = new SqliteConnection("Data Source=:memory:;Cache=Shared");
		_connection.Open();
		_store = new SqliteSponsorBlockStateStore(_connection);
		return Task.CompletedTask;
	}

	public Task DisposeAsync()
	{
		_connection.Dispose();
		return Task.CompletedTask;
	}

	[Fact]
	public async Task Get_ReturnsNull_WhenItemAbsent()
	{
		var result = await _store.GetAsync(Guid.NewGuid(), CancellationToken.None);
		Assert.Null(result);
	}

	[Fact]
	public async Task Upsert_ThenGet_RoundTrips()
	{
		var row = NewRow(state: ItemState.HasData, segmentCount: 3);
		await _store.UpsertAsync(row, CancellationToken.None);

		var fetched = await _store.GetAsync(row.ItemId, CancellationToken.None);

		Assert.NotNull(fetched);
		Assert.Equal(row.ItemId, fetched.ItemId);
		Assert.Equal(row.VideoId, fetched.VideoId);
		Assert.Equal(row.State, fetched.State);
		Assert.Equal(row.FirstSeenAt.ToUnixTimeSeconds(), fetched.FirstSeenAt.ToUnixTimeSeconds());
		Assert.Equal(row.LastFetchAt.ToUnixTimeSeconds(), fetched.LastFetchAt.ToUnixTimeSeconds());
		Assert.Equal(row.SegmentCount, fetched.SegmentCount);
	}

	[Fact]
	public async Task Upsert_Replaces_ExistingRow()
	{
		var row = NewRow(state: ItemState.Pending);
		await _store.UpsertAsync(row, CancellationToken.None);

		var updated = row with { State = ItemState.HasData, SegmentCount = 2 };
		await _store.UpsertAsync(updated, CancellationToken.None);

		var fetched = await _store.GetAsync(row.ItemId, CancellationToken.None);

		Assert.NotNull(fetched);
		Assert.Equal(ItemState.HasData, fetched.State);
		Assert.Equal(2, fetched.SegmentCount);
	}

	[Fact]
	public async Task Delete_RemovesRow()
	{
		var row = NewRow();
		await _store.UpsertAsync(row, CancellationToken.None);
		await _store.DeleteAsync(row.ItemId, CancellationToken.None);

		var fetched = await _store.GetAsync(row.ItemId, CancellationToken.None);
		Assert.Null(fetched);
	}

	[Fact]
	public async Task GetActive_ReturnsPendingAndHasData_ButNotNoData()
	{
		var pending = NewRow(state: ItemState.Pending);
		var hasData = NewRow(state: ItemState.HasData);
		var noData = NewRow(state: ItemState.NoData);
		await _store.UpsertAsync(pending, CancellationToken.None);
		await _store.UpsertAsync(hasData, CancellationToken.None);
		await _store.UpsertAsync(noData, CancellationToken.None);

		var ids = new HashSet<Guid>();
		await foreach (var row in _store.GetActiveAsync(CancellationToken.None))
		{
			ids.Add(row.ItemId);
		}

		Assert.Contains(pending.ItemId, ids);
		Assert.Contains(hasData.ItemId, ids);
		Assert.DoesNotContain(noData.ItemId, ids);
	}

	private static ItemStateRow NewRow(
		ItemState state = ItemState.Pending,
		int segmentCount = 0)
	{
		var now = DateTimeOffset.UtcNow;
		return new ItemStateRow(
			ItemId: Guid.NewGuid(),
			VideoId: "abcdefghijk",
			State: state,
			FirstSeenAt: now,
			LastFetchAt: now,
			SegmentCount: segmentCount);
	}
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test --filter SqliteSponsorBlockStateStoreTests 2>&1 | tail -10`
Expected: 5 tests fail with "type or namespace 'SqliteSponsorBlockStateStore' could not be found".

- [ ] **Step 4: Implement the store**

```csharp
// Jellyfin.Plugin.SponsorBlock/State/SqliteSponsorBlockStateStore.cs
using System.Data;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.SponsorBlock.State;

/// <summary>
/// SQLite-backed implementation of <see cref="ISponsorBlockStateStore"/>.
/// Owns its connection; intended to be registered as a singleton.
/// </summary>
public sealed class SqliteSponsorBlockStateStore : ISponsorBlockStateStore, IDisposable
{
	private readonly SqliteConnection _connection;
	private readonly SemaphoreSlim _writeLock = new(1, 1);

	/// <summary>
	/// Initializes the store using a caller-supplied connection. The connection is opened if not already open.
	/// </summary>
	public SqliteSponsorBlockStateStore(SqliteConnection connection)
	{
		_connection = connection;
		if (_connection.State != ConnectionState.Open)
		{
			_connection.Open();
		}

		EnsureSchema();
	}

	private void EnsureSchema()
	{
		using var cmd = _connection.CreateCommand();
		cmd.CommandText = @"
			CREATE TABLE IF NOT EXISTS item_state (
				item_id        BLOB PRIMARY KEY,
				video_id       TEXT NOT NULL,
				state          INTEGER NOT NULL,
				first_seen_at  INTEGER NOT NULL,
				last_fetch_at  INTEGER NOT NULL,
				segment_count  INTEGER NOT NULL DEFAULT 0
			);
			CREATE INDEX IF NOT EXISTS idx_state ON item_state(state);
			CREATE INDEX IF NOT EXISTS idx_first_seen ON item_state(first_seen_at);";
		cmd.ExecuteNonQuery();
	}

	public async ValueTask<ItemStateRow?> GetAsync(Guid itemId, CancellationToken cancellationToken)
	{
		await using var cmd = _connection.CreateCommand();
		cmd.CommandText = "SELECT video_id, state, first_seen_at, last_fetch_at, segment_count FROM item_state WHERE item_id = $id";
		cmd.Parameters.AddWithValue("$id", itemId.ToByteArray());

		await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			return null;
		}

		return new ItemStateRow(
			ItemId: itemId,
			VideoId: reader.GetString(0),
			State: (ItemState)reader.GetInt32(1),
			FirstSeenAt: DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)),
			LastFetchAt: DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3)),
			SegmentCount: reader.GetInt32(4));
	}

	public async ValueTask UpsertAsync(ItemStateRow row, CancellationToken cancellationToken)
	{
		await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await using var cmd = _connection.CreateCommand();
			cmd.CommandText = @"
				INSERT INTO item_state (item_id, video_id, state, first_seen_at, last_fetch_at, segment_count)
				VALUES ($id, $vid, $st, $fs, $lf, $sc)
				ON CONFLICT(item_id) DO UPDATE SET
					video_id      = excluded.video_id,
					state         = excluded.state,
					first_seen_at = excluded.first_seen_at,
					last_fetch_at = excluded.last_fetch_at,
					segment_count = excluded.segment_count;";
			cmd.Parameters.AddWithValue("$id", row.ItemId.ToByteArray());
			cmd.Parameters.AddWithValue("$vid", row.VideoId);
			cmd.Parameters.AddWithValue("$st", (int)row.State);
			cmd.Parameters.AddWithValue("$fs", row.FirstSeenAt.ToUnixTimeSeconds());
			cmd.Parameters.AddWithValue("$lf", row.LastFetchAt.ToUnixTimeSeconds());
			cmd.Parameters.AddWithValue("$sc", row.SegmentCount);
			await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_writeLock.Release();
		}
	}

	public async ValueTask DeleteAsync(Guid itemId, CancellationToken cancellationToken)
	{
		await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await using var cmd = _connection.CreateCommand();
			cmd.CommandText = "DELETE FROM item_state WHERE item_id = $id";
			cmd.Parameters.AddWithValue("$id", itemId.ToByteArray());
			await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_writeLock.Release();
		}
	}

	public async IAsyncEnumerable<ItemStateRow> GetActiveAsync(
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await using var cmd = _connection.CreateCommand();
		cmd.CommandText = @"
			SELECT item_id, video_id, state, first_seen_at, last_fetch_at, segment_count
			FROM item_state
			WHERE state IN (0, 1)
			ORDER BY first_seen_at ASC";

		await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			var idBytes = (byte[])reader.GetValue(0);
			yield return new ItemStateRow(
				ItemId: new Guid(idBytes),
				VideoId: reader.GetString(1),
				State: (ItemState)reader.GetInt32(2),
				FirstSeenAt: DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3)),
				LastFetchAt: DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4)),
				SegmentCount: reader.GetInt32(5));
		}
	}

	public void Dispose()
	{
		_writeLock.Dispose();
		_connection.Dispose();
	}
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test --filter SqliteSponsorBlockStateStoreTests`
Expected: all 5 tests pass.

- [ ] **Step 6: Commit**

```bash
git add Jellyfin.Plugin.SponsorBlock/State/SqliteSponsorBlockStateStore.cs Jellyfin.Plugin.SponsorBlock.Tests/State/SqliteSponsorBlockStateStoreTests.cs
git commit -m "implement SqliteSponsorBlockStateStore with crud + active-rows enumeration"
```

---

## Task 7: Implement `LibraryScopeService` (TDD)

**Files:**
- Create: `Jellyfin.Plugin.SponsorBlock.Tests/Scoping/LibraryScopeServiceTests.cs`
- Create: `Jellyfin.Plugin.SponsorBlock/Scoping/ILibraryScopeService.cs`
- Create: `Jellyfin.Plugin.SponsorBlock/Scoping/LibraryScopeService.cs`

- [ ] **Step 1: Create the interface**

```csharp
// Jellyfin.Plugin.SponsorBlock/Scoping/ILibraryScopeService.cs
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.SponsorBlock.Scoping;

/// <summary>
/// Decides whether a library item is in scope for SponsorBlock processing.
/// </summary>
public interface ILibraryScopeService
{
	/// <summary>
	/// Returns true iff the item belongs to a CollectionFolder whose id is in the configured
	/// allowlist. Empty allowlist → always false (safe default for fresh installs).
	/// </summary>
	bool IsInScope(BaseItem item);
}
```

- [ ] **Step 2: Write failing tests using a fake item hierarchy**

The trick: `BaseItem` is abstract and hard to mock. Test against the **scope policy** directly by injecting a delegate that returns ancestor ids — keeps the test independent of Jellyfin's parent walk.

```csharp
// Jellyfin.Plugin.SponsorBlock.Tests/Scoping/LibraryScopeServiceTests.cs
using Jellyfin.Plugin.SponsorBlock.Configuration;
using Jellyfin.Plugin.SponsorBlock.Scoping;
using Xunit;

namespace Jellyfin.Plugin.SponsorBlock.Tests.Scoping;

public class LibraryScopeServiceTests
{
	[Fact]
	public void IsInScope_ReturnsFalse_WhenAllowlistEmpty()
	{
		var libId = Guid.NewGuid();
		var config = new PluginConfiguration { EnabledLibraryIds = Array.Empty<Guid>() };
		var service = new LibraryScopeService(() => config, _ => new[] { libId });

		Assert.False(service.IsInScope(item: null!));
	}

	[Fact]
	public void IsInScope_ReturnsTrue_WhenItemAncestorIsAllowlisted()
	{
		var libId = Guid.NewGuid();
		var config = new PluginConfiguration { EnabledLibraryIds = new[] { libId } };
		var service = new LibraryScopeService(() => config, _ => new[] { Guid.NewGuid(), libId });

		Assert.True(service.IsInScope(item: null!));
	}

	[Fact]
	public void IsInScope_ReturnsFalse_WhenItemHasNoAllowlistedAncestor()
	{
		var libId = Guid.NewGuid();
		var config = new PluginConfiguration { EnabledLibraryIds = new[] { libId } };
		var service = new LibraryScopeService(() => config, _ => new[] { Guid.NewGuid(), Guid.NewGuid() });

		Assert.False(service.IsInScope(item: null!));
	}
}
```

- [ ] **Step 3: Run tests, expect compilation failure**

Run: `dotnet test --filter LibraryScopeServiceTests 2>&1 | tail -5`
Expected: fails — types don't exist yet.

- [ ] **Step 4: Implement the service**

```csharp
// Jellyfin.Plugin.SponsorBlock/Scoping/LibraryScopeService.cs
using Jellyfin.Plugin.SponsorBlock.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.SponsorBlock.Scoping;

/// <summary>
/// Default implementation of <see cref="ILibraryScopeService"/>.
/// </summary>
public sealed class LibraryScopeService : ILibraryScopeService
{
	private readonly Func<PluginConfiguration> _configAccessor;
	private readonly Func<BaseItem, IEnumerable<Guid>> _ancestorIds;

	/// <summary>
	/// Production constructor — pulls ancestor ids by asking <see cref="ILibraryManager.GetCollectionFolders"/>.
	/// </summary>
	public LibraryScopeService(ILibraryManager libraryManager, Func<PluginConfiguration> configAccessor)
		: this(configAccessor, item => GetCollectionFolderIds(item, libraryManager))
	{
	}

	/// <summary>
	/// Test constructor — accepts an injected ancestor-id source so tests don't need a real BaseItem.
	/// </summary>
	internal LibraryScopeService(
		Func<PluginConfiguration> configAccessor,
		Func<BaseItem, IEnumerable<Guid>> ancestorIds)
	{
		_configAccessor = configAccessor;
		_ancestorIds = ancestorIds;
	}

	public bool IsInScope(BaseItem item)
	{
		var allow = _configAccessor().EnabledLibraryIds;
		if (allow.Length == 0)
		{
			return false;
		}

		var allowSet = new HashSet<Guid>(allow);
		foreach (var id in _ancestorIds(item))
		{
			if (allowSet.Contains(id))
			{
				return true;
			}
		}

		return false;
	}

	private static IEnumerable<Guid> GetCollectionFolderIds(BaseItem item, ILibraryManager libraryManager)
	{
		foreach (var folder in libraryManager.GetCollectionFolders(item))
		{
			yield return folder.Id;
		}
	}
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test --filter LibraryScopeServiceTests`
Expected: 3 tests pass.

- [ ] **Step 6: Commit**

```bash
git add Jellyfin.Plugin.SponsorBlock/Scoping/ Jellyfin.Plugin.SponsorBlock.Tests/Scoping/
git commit -m "add LibraryScopeService with empty-allowlist-blocks-everything default"
```

---

## Task 8: Implement `SponsorBlockOrchestrator` (TDD)

The decision-table heart. The single class with non-trivial logic — gets thorough tests.

**Files:**
- Create: `Jellyfin.Plugin.SponsorBlock/Orchestration/ProcessReason.cs`
- Create: `Jellyfin.Plugin.SponsorBlock/Orchestration/IMediaSegmentWriter.cs`
- Create: `Jellyfin.Plugin.SponsorBlock/Orchestration/SponsorBlockOrchestrator.cs`
- Create: `Jellyfin.Plugin.SponsorBlock.Tests/Orchestration/SponsorBlockOrchestratorTests.cs`
- Modify: `Jellyfin.Plugin.SponsorBlock.Tests/Jellyfin.Plugin.SponsorBlock.Tests.csproj` (add `Microsoft.Extensions.TimeProvider.Testing` + `NSubstitute`)

- [ ] **Step 1: Add test deps**

Edit `Jellyfin.Plugin.SponsorBlock.Tests/Jellyfin.Plugin.SponsorBlock.Tests.csproj` and add to the existing `<ItemGroup>` containing test packages:

```xml
<PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" Version="9.0.0" />
<PackageReference Include="NSubstitute" Version="5.3.0" />
```

Run: `dotnet restore Jellyfin.Plugin.SponsorBlock.Tests 2>&1 | tail -5`
Expected: success.

- [ ] **Step 2: Define `ProcessReason`**

```csharp
// Jellyfin.Plugin.SponsorBlock/Orchestration/ProcessReason.cs
namespace Jellyfin.Plugin.SponsorBlock.Orchestration;

/// <summary>
/// Why <see cref="SponsorBlockOrchestrator.ProcessAsync"/> was invoked.
/// Drives the decision-table branches inside the orchestrator.
/// </summary>
public enum ProcessReason
{
	/// <summary>Library item was just added (ILibraryManager.ItemAdded).</summary>
	ItemAdded,

	/// <summary>A session started playback (ISessionManager.PlaybackStart).</summary>
	PlaybackStart,

	/// <summary>The daily refresh task is running.</summary>
	DailyScan,
}
```

- [ ] **Step 3: Define `IMediaSegmentWriter` (thin shim around IMediaSegmentManager)**

The Jellyfin `IMediaSegmentManager` may be hard to mock directly (sealed types, missing virtual methods). A trivial shim makes the orchestrator testable without depending on the concrete manager API.

```csharp
// Jellyfin.Plugin.SponsorBlock/Orchestration/IMediaSegmentWriter.cs
using MediaBrowser.Model.MediaSegments;

namespace Jellyfin.Plugin.SponsorBlock.Orchestration;

/// <summary>
/// Plugin-internal abstraction over Jellyfin's <c>IMediaSegmentManager</c>.
/// The orchestrator depends on this; the production implementation forwards to
/// <c>IMediaSegmentManager.CreateSegmentAsync</c> / <c>DeleteSegmentsAsync</c>.
/// </summary>
public interface IMediaSegmentWriter
{
	/// <summary>Constant provider name that owns SponsorBlock-written segments.</summary>
	const string ProviderName = "SponsorBlock";

	/// <summary>Removes all segments owned by SponsorBlock for an item.</summary>
	Task DeleteOwnedAsync(Guid itemId, CancellationToken cancellationToken);

	/// <summary>Persists a single SponsorBlock segment.</summary>
	Task CreateAsync(MediaSegmentDto segment, CancellationToken cancellationToken);
}
```

(The production impl that wraps `IMediaSegmentManager` is built in Task 9 once we know the exact signature — see Task 1.)

- [ ] **Step 4: Write the failing orchestrator tests covering the decision table**

```csharp
// Jellyfin.Plugin.SponsorBlock.Tests/Orchestration/SponsorBlockOrchestratorTests.cs
using Jellyfin.Plugin.SponsorBlock;
using Jellyfin.Plugin.SponsorBlock.Configuration;
using Jellyfin.Plugin.SponsorBlock.Orchestration;
using Jellyfin.Plugin.SponsorBlock.Scoping;
using Jellyfin.Plugin.SponsorBlock.State;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.SponsorBlock.Tests.Orchestration;

public class SponsorBlockOrchestratorTests
{
	private static readonly DateTimeOffset T0 = new(2026, 4, 27, 12, 0, 0, TimeSpan.Zero);

	private readonly FakeTimeProvider _time = new(T0);
	private readonly ISponsorBlockApiClient _api = Substitute.For<ISponsorBlockApiClient>();
	private readonly ISponsorBlockStateStore _store = Substitute.For<ISponsorBlockStateStore>();
	private readonly ILibraryScopeService _scope = Substitute.For<ILibraryScopeService>();
	private readonly IMediaSegmentWriter _writer = Substitute.For<IMediaSegmentWriter>();
	private readonly PluginConfiguration _config = new()
	{
		EnabledLibraryIds = new[] { Guid.NewGuid() },
		PlaybackPollHours = 24,
		PendingSanityHours = 48,
		Sponsor = true,
	};

	private SponsorBlockOrchestrator MakeOrchestrator() => new(
		_api, _store, _scope, _writer,
		() => _config,
		(_, _, _) => "abcdefghijk",
		_time,
		NullLogger<SponsorBlockOrchestrator>.Instance);

	private static BaseItem FakeItem(Guid id, string path = "/library/yt/abcdefghijk.mp4")
	{
		var item = Substitute.For<BaseItem>();
		item.Id.Returns(id);
		item.Path.Returns(path);
		return item;
	}

	private static SponsorBlockSegment Seg(string category = "sponsor")
		=> new() { Category = category, ActionType = "skip", Segment = [10.0, 20.0], UUID = "u" };

	[Fact]
	public async Task NoRow_FetchSucceedsWithSegments_InsertsHasData()
	{
		var item = FakeItem(Guid.NewGuid());
		_scope.IsInScope(item).Returns(true);
		_store.GetAsync(item.Id, Arg.Any<CancellationToken>()).Returns((ItemStateRow?)null);
		_api.GetSegmentsAsync("abcdefghijk", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
			.Returns(new List<SponsorBlockSegment> { Seg() });

		await MakeOrchestrator().ProcessAsync(item, ProcessReason.ItemAdded, CancellationToken.None);

		await _store.Received().UpsertAsync(
			Arg.Is<ItemStateRow>(r => r.State == ItemState.HasData && r.SegmentCount == 1 && r.FirstSeenAt == T0),
			Arg.Any<CancellationToken>());
		await _writer.Received(1).CreateAsync(Arg.Any<MediaSegmentDto>(), Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task NoRow_FetchSucceedsEmpty_InsertsPending()
	{
		var item = FakeItem(Guid.NewGuid());
		_scope.IsInScope(item).Returns(true);
		_store.GetAsync(item.Id, Arg.Any<CancellationToken>()).Returns((ItemStateRow?)null);
		_api.GetSegmentsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
			.Returns(new List<SponsorBlockSegment>());

		await MakeOrchestrator().ProcessAsync(item, ProcessReason.ItemAdded, CancellationToken.None);

		await _store.Received().UpsertAsync(
			Arg.Is<ItemStateRow>(r => r.State == ItemState.Pending && r.SegmentCount == 0),
			Arg.Any<CancellationToken>());
		await _writer.DidNotReceive().CreateAsync(Arg.Any<MediaSegmentDto>(), Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task Pending_PlaybackStart_WithinPollWindow_RefetchesAndPromotesOnSegments()
	{
		var item = FakeItem(Guid.NewGuid());
		_scope.IsInScope(item).Returns(true);
		var existing = NewRow(item.Id, ItemState.Pending, firstSeen: T0.AddHours(-12));
		_store.GetAsync(item.Id, Arg.Any<CancellationToken>()).Returns(existing);
		_api.GetSegmentsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
			.Returns(new List<SponsorBlockSegment> { Seg() });

		await MakeOrchestrator().ProcessAsync(item, ProcessReason.PlaybackStart, CancellationToken.None);

		await _store.Received().UpsertAsync(
			Arg.Is<ItemStateRow>(r => r.State == ItemState.HasData && r.SegmentCount == 1),
			Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task Pending_PlaybackStart_OutsidePollWindow_DoesNothing()
	{
		var item = FakeItem(Guid.NewGuid());
		_scope.IsInScope(item).Returns(true);
		var existing = NewRow(item.Id, ItemState.Pending, firstSeen: T0.AddHours(-30)); // > 24h
		_store.GetAsync(item.Id, Arg.Any<CancellationToken>()).Returns(existing);

		await MakeOrchestrator().ProcessAsync(item, ProcessReason.PlaybackStart, CancellationToken.None);

		await _api.DidNotReceive().GetSegmentsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
		await _store.DidNotReceive().UpsertAsync(Arg.Any<ItemStateRow>(), Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task Pending_DailyScan_PastSanityHours_EmptyResponse_PromotesToNoData()
	{
		var item = FakeItem(Guid.NewGuid());
		_scope.IsInScope(item).Returns(true);
		var existing = NewRow(item.Id, ItemState.Pending, firstSeen: T0.AddHours(-50));
		_store.GetAsync(item.Id, Arg.Any<CancellationToken>()).Returns(existing);
		_api.GetSegmentsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
			.Returns(new List<SponsorBlockSegment>());

		await MakeOrchestrator().ProcessAsync(item, ProcessReason.DailyScan, CancellationToken.None);

		await _store.Received().UpsertAsync(
			Arg.Is<ItemStateRow>(r => r.State == ItemState.NoData),
			Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task HasData_DailyScan_ReplacesOwnedSegments()
	{
		var item = FakeItem(Guid.NewGuid());
		_scope.IsInScope(item).Returns(true);
		var existing = NewRow(item.Id, ItemState.HasData, segmentCount: 3);
		_store.GetAsync(item.Id, Arg.Any<CancellationToken>()).Returns(existing);
		_api.GetSegmentsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
			.Returns(new List<SponsorBlockSegment> { Seg(), Seg() });

		await MakeOrchestrator().ProcessAsync(item, ProcessReason.DailyScan, CancellationToken.None);

		await _writer.Received(1).DeleteOwnedAsync(item.Id, Arg.Any<CancellationToken>());
		await _writer.Received(2).CreateAsync(Arg.Any<MediaSegmentDto>(), Arg.Any<CancellationToken>());
		await _store.Received().UpsertAsync(
			Arg.Is<ItemStateRow>(r => r.State == ItemState.HasData && r.SegmentCount == 2),
			Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task HasData_PlaybackStart_NoOps()
	{
		var item = FakeItem(Guid.NewGuid());
		_scope.IsInScope(item).Returns(true);
		_store.GetAsync(item.Id, Arg.Any<CancellationToken>())
			.Returns(NewRow(item.Id, ItemState.HasData, segmentCount: 2));

		await MakeOrchestrator().ProcessAsync(item, ProcessReason.PlaybackStart, CancellationToken.None);

		await _api.DidNotReceive().GetSegmentsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
		await _store.DidNotReceive().UpsertAsync(Arg.Any<ItemStateRow>(), Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task NoData_AnyTrigger_NoOps()
	{
		var item = FakeItem(Guid.NewGuid());
		_scope.IsInScope(item).Returns(true);
		_store.GetAsync(item.Id, Arg.Any<CancellationToken>())
			.Returns(NewRow(item.Id, ItemState.NoData));

		await MakeOrchestrator().ProcessAsync(item, ProcessReason.DailyScan, CancellationToken.None);
		await MakeOrchestrator().ProcessAsync(item, ProcessReason.PlaybackStart, CancellationToken.None);

		await _api.DidNotReceive().GetSegmentsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task OutOfScope_NoOps()
	{
		var item = FakeItem(Guid.NewGuid());
		_scope.IsInScope(item).Returns(false);

		await MakeOrchestrator().ProcessAsync(item, ProcessReason.ItemAdded, CancellationToken.None);

		await _store.DidNotReceive().GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
		await _api.DidNotReceive().GetSegmentsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task TransientHttpFailure_DoesNotAdvanceState()
	{
		var item = FakeItem(Guid.NewGuid());
		_scope.IsInScope(item).Returns(true);
		_store.GetAsync(item.Id, Arg.Any<CancellationToken>())
			.Returns(NewRow(item.Id, ItemState.Pending, firstSeen: T0.AddHours(-50)));
		_api.GetSegmentsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
			.Returns<Task<IReadOnlyList<SponsorBlockSegment>>>(_ => throw new HttpRequestException("boom"));

		await MakeOrchestrator().ProcessAsync(item, ProcessReason.DailyScan, CancellationToken.None);

		await _store.DidNotReceive().UpsertAsync(Arg.Any<ItemStateRow>(), Arg.Any<CancellationToken>());
	}

	private static ItemStateRow NewRow(
		Guid itemId,
		ItemState state,
		int segmentCount = 0,
		DateTimeOffset? firstSeen = null) =>
		new(itemId, "abcdefghijk", state, firstSeen ?? T0, T0, segmentCount);
}
```

- [ ] **Step 5: Run tests, expect compile failures**

Run: `dotnet test --filter SponsorBlockOrchestratorTests 2>&1 | tail -10`
Expected: fails — `SponsorBlockOrchestrator` doesn't exist.

- [ ] **Step 6: Implement the orchestrator**

```csharp
// Jellyfin.Plugin.SponsorBlock/Orchestration/SponsorBlockOrchestrator.cs
using System.Collections.Concurrent;
using Jellyfin.Plugin.SponsorBlock.Configuration;
using Jellyfin.Plugin.SponsorBlock.Scoping;
using Jellyfin.Plugin.SponsorBlock.State;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SponsorBlock.Orchestration;

/// <summary>
/// Single writer for SponsorBlock state and segments. All triggers funnel through ProcessAsync.
/// </summary>
public sealed class SponsorBlockOrchestrator
{
	private readonly ISponsorBlockApiClient _api;
	private readonly ISponsorBlockStateStore _store;
	private readonly ILibraryScopeService _scope;
	private readonly IMediaSegmentWriter _writer;
	private readonly Func<PluginConfiguration> _config;
	private readonly Func<string, FileMatchingMode, string?, string?> _extractVideoId;
	private readonly TimeProvider _time;
	private readonly ILogger<SponsorBlockOrchestrator> _logger;
	private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _itemLocks = new();

	/// <summary>Production constructor (uses static <see cref="YouTubeIdExtractor"/>).</summary>
	public SponsorBlockOrchestrator(
		ISponsorBlockApiClient api,
		ISponsorBlockStateStore store,
		ILibraryScopeService scope,
		IMediaSegmentWriter writer,
		Func<PluginConfiguration> config,
		TimeProvider time,
		ILogger<SponsorBlockOrchestrator> logger)
		: this(api, store, scope, writer, config,
			(filename, mode, pattern) => YouTubeIdExtractor.Extract(filename, mode, pattern),
			time, logger)
	{
	}

	internal SponsorBlockOrchestrator(
		ISponsorBlockApiClient api,
		ISponsorBlockStateStore store,
		ILibraryScopeService scope,
		IMediaSegmentWriter writer,
		Func<PluginConfiguration> config,
		Func<string, FileMatchingMode, string?, string?> extractVideoId,
		TimeProvider time,
		ILogger<SponsorBlockOrchestrator> logger)
	{
		_api = api;
		_store = store;
		_scope = scope;
		_writer = writer;
		_config = config;
		_extractVideoId = extractVideoId;
		_time = time;
		_logger = logger;
	}

	/// <summary>
	/// Process one item under the given trigger. Implements the decision table from the spec.
	/// Swallows transient HTTP failures (logs warning, leaves state untouched).
	/// </summary>
	public async Task ProcessAsync(BaseItem item, ProcessReason reason, CancellationToken cancellationToken)
	{
		if (!_scope.IsInScope(item))
		{
			return;
		}

		var path = item.Path;
		if (string.IsNullOrEmpty(path))
		{
			return;
		}

		var config = _config();
		var videoId = _extractVideoId(Path.GetFileName(path), config.FileMatchingMode, config.CustomRegexPattern);
		if (videoId is null)
		{
			return;
		}

		var sem = _itemLocks.GetOrAdd(item.Id, _ => new SemaphoreSlim(1, 1));
		await sem.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await ProcessLockedAsync(item.Id, videoId, reason, config, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			sem.Release();
		}
	}

	private async Task ProcessLockedAsync(
		Guid itemId,
		string videoId,
		ProcessReason reason,
		PluginConfiguration config,
		CancellationToken ct)
	{
		var existing = await _store.GetAsync(itemId, ct).ConfigureAwait(false);

		if (existing is { State: ItemState.NoData })
		{
			return;
		}

		if (existing is { State: ItemState.HasData } && reason == ProcessReason.PlaybackStart)
		{
			return;
		}

		var now = _time.GetUtcNow();

		if (existing is { State: ItemState.Pending } && reason == ProcessReason.PlaybackStart)
		{
			var ageHours = (now - existing.FirstSeenAt).TotalHours;
			if (ageHours >= config.PlaybackPollHours)
			{
				return;
			}
		}

		IReadOnlyList<SponsorBlockSegment> apiSegments;
		try
		{
			var categories = CategoryMapping.GetEnabledCategories(config.GetCategorySettings());
			if (categories.Count == 0)
			{
				return;
			}

			apiSegments = await _api.GetSegmentsAsync(videoId, categories, ct).ConfigureAwait(false);
		}
		catch (HttpRequestException ex)
		{
			_logger.LogWarning(ex, "Transient SponsorBlock fetch failure for {VideoId}; state unchanged", videoId);
			return;
		}
		catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
		{
			_logger.LogWarning(ex, "SponsorBlock fetch timeout for {VideoId}; state unchanged", videoId);
			return;
		}

		var firstSeen = existing?.FirstSeenAt ?? now;
		var hasSegments = apiSegments.Any(s => s.ActionType == "skip");

		if (hasSegments)
		{
			var dtos = SegmentMapper.Map(apiSegments, itemId);
			await _writer.DeleteOwnedAsync(itemId, ct).ConfigureAwait(false);
			foreach (var dto in dtos)
			{
				await _writer.CreateAsync(dto, ct).ConfigureAwait(false);
			}

			await _store.UpsertAsync(
				new ItemStateRow(itemId, videoId, ItemState.HasData, firstSeen, now, dtos.Count),
				ct).ConfigureAwait(false);
			return;
		}

		var sanityElapsed = (now - firstSeen).TotalHours >= config.PendingSanityHours;
		var newState = sanityElapsed ? ItemState.NoData : ItemState.Pending;

		await _store.UpsertAsync(
			new ItemStateRow(itemId, videoId, newState, firstSeen, now, 0),
			ct).ConfigureAwait(false);
	}
}
```

- [ ] **Step 7: Run tests**

Run: `dotnet test --filter SponsorBlockOrchestratorTests`
Expected: all 11 tests pass.

- [ ] **Step 8: Commit**

```bash
git add Jellyfin.Plugin.SponsorBlock/Orchestration/ Jellyfin.Plugin.SponsorBlock.Tests/Orchestration/ Jellyfin.Plugin.SponsorBlock.Tests/Jellyfin.Plugin.SponsorBlock.Tests.csproj
git commit -m "implement SponsorBlockOrchestrator with decision-table behavior + tests"
```

---

## Task 9: Implement `MediaSegmentWriter` (production wrapper around `IMediaSegmentManager`)

This task **depends on Task 1** for the verified API signatures. Use them directly.

**Files:**
- Create: `Jellyfin.Plugin.SponsorBlock/Orchestration/MediaSegmentWriter.cs`

- [ ] **Step 1: Implement the wrapper**

Replace the `<<verified-from-task-1>>` placeholders with the actual signatures recorded in Task 1.

```csharp
// Jellyfin.Plugin.SponsorBlock/Orchestration/MediaSegmentWriter.cs
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.MediaSegments;

namespace Jellyfin.Plugin.SponsorBlock.Orchestration;

/// <summary>
/// Production implementation that forwards to Jellyfin's <see cref="IMediaSegmentManager"/>.
/// </summary>
public sealed class MediaSegmentWriter : IMediaSegmentWriter
{
	private readonly IMediaSegmentManager _manager;

	public MediaSegmentWriter(IMediaSegmentManager manager)
	{
		_manager = manager;
	}

	public Task DeleteOwnedAsync(Guid itemId, CancellationToken cancellationToken)
	{
		// Use the signature recorded in Task 1.
		// Likely shape: _manager.DeleteSegmentsAsync(itemId, providerId: IMediaSegmentWriter.ProviderName, cancellationToken)
		return _manager.DeleteSegmentsAsync(itemId, /* args from task 1 */ default!, cancellationToken);
	}

	public Task CreateAsync(MediaSegmentDto segment, CancellationToken cancellationToken)
	{
		// Likely shape: _manager.CreateSegmentAsync(segment, IMediaSegmentWriter.ProviderName, cancellationToken)
		return _manager.CreateSegmentAsync(segment, IMediaSegmentWriter.ProviderName, cancellationToken).AsTask();
	}
}
```

- [ ] **Step 2: Build and fix the placeholders against the real signatures**

Run: `dotnet build 2>&1 | grep -E 'CS|error' | head -20`

If the build fails, the compiler tells you exactly what arguments are missing. Update the file to match. The `IMediaSegmentManager` is real and concrete — not testable in unit tests; we test the orchestrator-side behavior via `IMediaSegmentWriter` mocks.

- [ ] **Step 3: Commit**

```bash
git add Jellyfin.Plugin.SponsorBlock/Orchestration/MediaSegmentWriter.cs
git commit -m "wire MediaSegmentWriter to Jellyfin IMediaSegmentManager"
```

---

## Task 10: `ItemAddedHostedService`

**Files:**
- Create: `Jellyfin.Plugin.SponsorBlock/Triggers/ItemAddedHostedService.cs`

- [ ] **Step 1: Implement**

Use the `ItemAdded` event signature recorded in Task 1.

```csharp
// Jellyfin.Plugin.SponsorBlock/Triggers/ItemAddedHostedService.cs
using Jellyfin.Plugin.SponsorBlock.Orchestration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SponsorBlock.Triggers;

/// <summary>
/// Subscribes to <see cref="ILibraryManager.ItemAdded"/> and dispatches Video items to the orchestrator.
/// </summary>
public sealed class ItemAddedHostedService : IHostedService
{
	private readonly ILibraryManager _libraryManager;
	private readonly SponsorBlockOrchestrator _orchestrator;
	private readonly ILogger<ItemAddedHostedService> _logger;

	public ItemAddedHostedService(
		ILibraryManager libraryManager,
		SponsorBlockOrchestrator orchestrator,
		ILogger<ItemAddedHostedService> logger)
	{
		_libraryManager = libraryManager;
		_orchestrator = orchestrator;
		_logger = logger;
	}

	public Task StartAsync(CancellationToken cancellationToken)
	{
		_libraryManager.ItemAdded += OnItemAdded;
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken cancellationToken)
	{
		_libraryManager.ItemAdded -= OnItemAdded;
		return Task.CompletedTask;
	}

	private void OnItemAdded(object? sender, ItemChangeEventArgs e)
	{
		if (e.Item is not Video video)
		{
			return;
		}

		_ = Task.Run(async () =>
		{
			try
			{
				await _orchestrator.ProcessAsync(video, ProcessReason.ItemAdded, CancellationToken.None).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "SponsorBlock orchestrator failed for added item {ItemId}", video.Id);
			}
		});
	}
}
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: clean. If the event signature is different from what Task 1 recorded, fix the handler.

- [ ] **Step 3: Commit**

```bash
git add Jellyfin.Plugin.SponsorBlock/Triggers/ItemAddedHostedService.cs
git commit -m "add ItemAddedHostedService trigger"
```

---

## Task 11: `PlaybackStartHostedService`

**Files:**
- Create: `Jellyfin.Plugin.SponsorBlock/Triggers/PlaybackStartHostedService.cs`

- [ ] **Step 1: Implement**

Use the `PlaybackStart` event signature recorded in Task 1.

```csharp
// Jellyfin.Plugin.SponsorBlock/Triggers/PlaybackStartHostedService.cs
using Jellyfin.Plugin.SponsorBlock.Orchestration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SponsorBlock.Triggers;

/// <summary>
/// Subscribes to <see cref="ISessionManager.PlaybackStart"/> and dispatches the played item to the orchestrator.
/// </summary>
public sealed class PlaybackStartHostedService : IHostedService
{
	private readonly ISessionManager _sessionManager;
	private readonly SponsorBlockOrchestrator _orchestrator;
	private readonly ILogger<PlaybackStartHostedService> _logger;

	public PlaybackStartHostedService(
		ISessionManager sessionManager,
		SponsorBlockOrchestrator orchestrator,
		ILogger<PlaybackStartHostedService> logger)
	{
		_sessionManager = sessionManager;
		_orchestrator = orchestrator;
		_logger = logger;
	}

	public Task StartAsync(CancellationToken cancellationToken)
	{
		_sessionManager.PlaybackStart += OnPlaybackStart;
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken cancellationToken)
	{
		_sessionManager.PlaybackStart -= OnPlaybackStart;
		return Task.CompletedTask;
	}

	private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
	{
		if (e.Item is not Video video)
		{
			return;
		}

		_ = Task.Run(async () =>
		{
			try
			{
				await _orchestrator.ProcessAsync(video, ProcessReason.PlaybackStart, CancellationToken.None).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "SponsorBlock orchestrator failed for played item {ItemId}", video.Id);
			}
		});
	}
}
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: clean.

- [ ] **Step 3: Commit**

```bash
git add Jellyfin.Plugin.SponsorBlock/Triggers/PlaybackStartHostedService.cs
git commit -m "add PlaybackStartHostedService trigger"
```

---

## Task 12: `ItemRemovedHostedService` (cleanup)

**Files:**
- Create: `Jellyfin.Plugin.SponsorBlock/Triggers/ItemRemovedHostedService.cs`

- [ ] **Step 1: Implement**

```csharp
// Jellyfin.Plugin.SponsorBlock/Triggers/ItemRemovedHostedService.cs
using Jellyfin.Plugin.SponsorBlock.Orchestration;
using Jellyfin.Plugin.SponsorBlock.State;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SponsorBlock.Triggers;

/// <summary>
/// Drops the per-item SQLite row and owned segments when a library item is removed.
/// </summary>
public sealed class ItemRemovedHostedService : IHostedService
{
	private readonly ILibraryManager _libraryManager;
	private readonly ISponsorBlockStateStore _store;
	private readonly IMediaSegmentWriter _writer;
	private readonly ILogger<ItemRemovedHostedService> _logger;

	public ItemRemovedHostedService(
		ILibraryManager libraryManager,
		ISponsorBlockStateStore store,
		IMediaSegmentWriter writer,
		ILogger<ItemRemovedHostedService> logger)
	{
		_libraryManager = libraryManager;
		_store = store;
		_writer = writer;
		_logger = logger;
	}

	public Task StartAsync(CancellationToken cancellationToken)
	{
		_libraryManager.ItemRemoved += OnItemRemoved;
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken cancellationToken)
	{
		_libraryManager.ItemRemoved -= OnItemRemoved;
		return Task.CompletedTask;
	}

	private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
	{
		var itemId = e.Item.Id;
		_ = Task.Run(async () =>
		{
			try
			{
				await _writer.DeleteOwnedAsync(itemId, CancellationToken.None).ConfigureAwait(false);
				await _store.DeleteAsync(itemId, CancellationToken.None).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "SponsorBlock cleanup failed for removed item {ItemId}", itemId);
			}
		});
	}
}
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: clean.

- [ ] **Step 3: Commit**

```bash
git add Jellyfin.Plugin.SponsorBlock/Triggers/ItemRemovedHostedService.cs
git commit -m "add ItemRemovedHostedService cleanup trigger"
```

---

## Task 13: `SponsorBlockRefreshTask` (daily IScheduledTask)

**Files:**
- Create: `Jellyfin.Plugin.SponsorBlock/Tasks/SponsorBlockRefreshTask.cs`

The scheduled task is integration-style (depends on `ILibraryManager` to resolve items). The orchestrator-dispatch behavior is already covered by orchestrator tests; the task itself is mostly glue.

- [ ] **Step 1: Implement**

```csharp
// Jellyfin.Plugin.SponsorBlock/Tasks/SponsorBlockRefreshTask.cs
using Jellyfin.Plugin.SponsorBlock.Configuration;
using Jellyfin.Plugin.SponsorBlock.Orchestration;
using Jellyfin.Plugin.SponsorBlock.State;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SponsorBlock.Tasks;

/// <summary>
/// Daily refresh task: walks all Pending + HasData rows, runs the orchestrator with DailyScan reason.
/// Drops orphan rows whose item no longer exists in the library.
/// </summary>
public sealed class SponsorBlockRefreshTask : IScheduledTask
{
	private readonly ISponsorBlockStateStore _store;
	private readonly ILibraryManager _libraryManager;
	private readonly SponsorBlockOrchestrator _orchestrator;
	private readonly IMediaSegmentWriter _writer;
	private readonly ILogger<SponsorBlockRefreshTask> _logger;

	public SponsorBlockRefreshTask(
		ISponsorBlockStateStore store,
		ILibraryManager libraryManager,
		SponsorBlockOrchestrator orchestrator,
		IMediaSegmentWriter writer,
		ILogger<SponsorBlockRefreshTask> logger)
	{
		_store = store;
		_libraryManager = libraryManager;
		_orchestrator = orchestrator;
		_writer = writer;
		_logger = logger;
	}

	public string Name => "SponsorBlock daily refresh";
	public string Key => "SponsorBlockRefresh";
	public string Description => "Refreshes SponsorBlock segments for tracked items and runs the 48-hour sanity check on items with no data.";
	public string Category => "SponsorBlock";

	public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
	{
		var hour = Plugin.Instance?.Configuration.DailyScanHour ?? 6;
		return new[]
		{
			new TaskTriggerInfo
			{
				Type = TaskTriggerInfo.TriggerDaily,
				TimeOfDayTicks = TimeSpan.FromHours(hour).Ticks,
			},
		};
	}

	public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
	{
		var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();

		var rows = new List<ItemStateRow>();
		await foreach (var row in _store.GetActiveAsync(cancellationToken).ConfigureAwait(false))
		{
			rows.Add(row);
		}

		if (rows.Count == 0)
		{
			progress.Report(100);
			return;
		}

		for (var i = 0; i < rows.Count; i++)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var row = rows[i];

			var item = _libraryManager.GetItemById(row.ItemId);
			if (item is null)
			{
				_logger.LogInformation("Dropping orphan SponsorBlock state for missing item {ItemId}", row.ItemId);
				try { await _writer.DeleteOwnedAsync(row.ItemId, cancellationToken).ConfigureAwait(false); }
				catch (Exception ex) { _logger.LogWarning(ex, "Failed to delete owned segments for orphan {ItemId}", row.ItemId); }
				await _store.DeleteAsync(row.ItemId, cancellationToken).ConfigureAwait(false);
			}
			else
			{
				try
				{
					await _orchestrator.ProcessAsync(item, ProcessReason.DailyScan, cancellationToken).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					_logger.LogError(ex, "Daily refresh failed for item {ItemId}", row.ItemId);
				}
			}

			progress.Report(100.0 * (i + 1) / rows.Count);

			if (config.RequestDelayMilliseconds > 0)
			{
				await Task.Delay(config.RequestDelayMilliseconds, cancellationToken).ConfigureAwait(false);
			}
		}
	}
}
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: clean. The `TaskTriggerInfo.TriggerDaily` constant + `TimeOfDayTicks` property come from `MediaBrowser.Model.Tasks` — verify the actual property name if the build complains.

- [ ] **Step 3: Commit**

```bash
git add Jellyfin.Plugin.SponsorBlock/Tasks/SponsorBlockRefreshTask.cs
git commit -m "add SponsorBlockRefreshTask daily scheduled task"
```

---

## Task 14: Wire DI, drop the old `IMediaSegmentProvider` registration, file location for SQLite

**Files:**
- Modify: `Jellyfin.Plugin.SponsorBlock/PluginServiceRegistrator.cs`

- [ ] **Step 1: Replace registrations**

```csharp
// Jellyfin.Plugin.SponsorBlock/PluginServiceRegistrator.cs
using Jellyfin.Plugin.SponsorBlock.Orchestration;
using Jellyfin.Plugin.SponsorBlock.Scoping;
using Jellyfin.Plugin.SponsorBlock.State;
using Jellyfin.Plugin.SponsorBlock.Triggers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SponsorBlock;

/// <summary>
/// Registers plugin services with Jellyfin's DI container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
	/// <inheritdoc />
	public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
	{
		// API client
		serviceCollection.AddSingleton<SponsorBlockApiClient>();
		serviceCollection.AddSingleton<ISponsorBlockApiClient>(sp => sp.GetRequiredService<SponsorBlockApiClient>());

		// Persistence
		serviceCollection.AddSingleton<ISponsorBlockStateStore>(sp =>
		{
			var paths = sp.GetRequiredService<IApplicationPaths>();
			var dir = Path.Combine(paths.DataPath, Plugin.PluginGuid);
			Directory.CreateDirectory(dir);
			var dbPath = Path.Combine(dir, "sponsorblock-state.db");
			var conn = new SqliteConnection($"Data Source={dbPath};Cache=Shared");
			conn.Open();
			return new SqliteSponsorBlockStateStore(conn);
		});

		// Scoping + segments + orchestrator
		serviceCollection.AddSingleton<ILibraryScopeService>(sp =>
			new LibraryScopeService(
				sp.GetRequiredService<ILibraryManager>(),
				() => Plugin.Instance!.Configuration));
		serviceCollection.AddSingleton<IMediaSegmentWriter, MediaSegmentWriter>();
		serviceCollection.AddSingleton<SponsorBlockOrchestrator>(sp =>
			new SponsorBlockOrchestrator(
				sp.GetRequiredService<ISponsorBlockApiClient>(),
				sp.GetRequiredService<ISponsorBlockStateStore>(),
				sp.GetRequiredService<ILibraryScopeService>(),
				sp.GetRequiredService<IMediaSegmentWriter>(),
				() => Plugin.Instance!.Configuration,
				TimeProvider.System,
				sp.GetRequiredService<ILogger<SponsorBlockOrchestrator>>()));

		// Triggers
		serviceCollection.AddHostedService<ItemAddedHostedService>();
		serviceCollection.AddHostedService<PlaybackStartHostedService>();
		serviceCollection.AddHostedService<ItemRemovedHostedService>();

		// IMediaSegmentProvider registration removed deliberately — segments are written directly by the orchestrator.
	}
}
```

- [ ] **Step 2: Build**

Run: `dotnet build`
Expected: clean.

- [ ] **Step 3: Commit**

```bash
git add Jellyfin.Plugin.SponsorBlock/PluginServiceRegistrator.cs
git commit -m "swap DI: drop IMediaSegmentProvider, register orchestrator + triggers + sqlite store"
```

---

## Task 15: Delete the old `SponsorBlockSegmentProvider` and its tests

The provider is no longer referenced. `SegmentMapper` (Task 3) carries the only logic worth keeping.

**Files:**
- Delete: `Jellyfin.Plugin.SponsorBlock/SponsorBlockSegmentProvider.cs`
- Delete: `Jellyfin.Plugin.SponsorBlock.Tests/SponsorBlockSegmentProviderTests.cs`

- [ ] **Step 1: Delete files**

```bash
rm Jellyfin.Plugin.SponsorBlock/SponsorBlockSegmentProvider.cs Jellyfin.Plugin.SponsorBlock.Tests/SponsorBlockSegmentProviderTests.cs
```

- [ ] **Step 2: Build and test**

Run: `dotnet build && dotnet test`
Expected: all green. (`SegmentMapperTests` from Task 3 covers the mapping logic.)

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "remove obsolete SponsorBlockSegmentProvider (replaced by orchestrator + SegmentMapper)"
```

---

## Task 16: Update `configPage.html` with library multi-select and advanced settings

**Files:**
- Modify: `Jellyfin.Plugin.SponsorBlock/Configuration/configPage.html`

- [ ] **Step 1: Add library picker, daily-scan hour, and advanced section**

Insert a new section **before** the `<h3>File Matching</h3>` section in the body markup:

```html
<div class="verticalSection">
	<h3>Libraries</h3>
	<div class="fieldDescription" style="margin-bottom: 0.5em;">
		Pick the libraries this plugin should fetch SponsorBlock data for. With nothing selected the plugin is inactive.
	</div>
	<div id="LibraryListContainer" class="checkboxList"></div>
</div>

<div class="verticalSection">
	<h3>Refresh Schedule</h3>
	<div class="inputContainer">
		<label class="inputLabel" for="DailyScanHour">Daily scan hour (local, 0–23)</label>
		<input id="DailyScanHour" type="number" min="0" max="23" is="emby-input" />
		<div class="fieldDescription">The "SponsorBlock daily refresh" task triggers once per day at this hour.</div>
	</div>
</div>
```

Insert an "Advanced" section **after** the "Categories to Skip" block:

```html
<details class="verticalSection">
	<summary><h3 style="display: inline-block; margin-bottom: 0.5em;">Advanced</h3></summary>
	<div class="inputContainer">
		<label class="inputLabel" for="PlaybackPollHours">Playback re-fetch window (hours since first observed)</label>
		<input id="PlaybackPollHours" type="number" min="0" is="emby-input" />
		<div class="fieldDescription">Items with no data yet keep re-fetching on every playback within this window.</div>
	</div>
	<div class="inputContainer">
		<label class="inputLabel" for="PendingSanityHours">Sanity-check window (hours)</label>
		<input id="PendingSanityHours" type="number" min="0" is="emby-input" />
		<div class="fieldDescription">After this many hours, an item with no SponsorBlock data is permanently skipped.</div>
	</div>
	<div class="inputContainer">
		<label class="inputLabel" for="RequestDelayMilliseconds">Daily-scan request delay (ms)</label>
		<input id="RequestDelayMilliseconds" type="number" min="0" is="emby-input" />
		<div class="fieldDescription">Delay between SponsorBlock API requests during the daily scan to avoid rate-limiting.</div>
	</div>
</details>
```

- [ ] **Step 2: Update the JavaScript `load()` and `save()` to handle the new fields**

Replace the entire `<script>` block with the version below. **Note:** library checkboxes are built using safe DOM methods (`createElement` + `textContent`), no innerHTML — virtual-folder names are admin-controlled but treating them as untrusted is good hygiene.

```html
<script type="text/javascript">
	var SponsorBlockConfig = {
		pluginUniqueId: 'c0e51a88-71a0-4f5c-82dc-81b8ae1a3e0f',

		clearChildren: function (node) {
			while (node.firstChild) { node.removeChild(node.firstChild); }
		},

		renderLibraryList: function (config, virtualFolders) {
			var container = document.getElementById('LibraryListContainer');
			SponsorBlockConfig.clearChildren(container);
			var enabled = new Set(config.EnabledLibraryIds || []);
			virtualFolders.forEach(function (vf) {
				var label = document.createElement('label');
				label.className = 'emby-checkbox-label';
				label.style.display = 'block';

				var input = document.createElement('input');
				input.type = 'checkbox';
				input.setAttribute('is', 'emby-checkbox');
				input.value = vf.ItemId;
				input.checked = enabled.has(vf.ItemId);
				input.dataset.libraryId = vf.ItemId;
				input.className = 'libraryCheckbox';

				var span = document.createElement('span');
				span.textContent = vf.Name + ' (' + (vf.CollectionType || 'unknown') + ')';

				label.appendChild(input);
				label.appendChild(span);
				container.appendChild(label);
			});
		},

		collectSelectedLibraryIds: function () {
			return Array.prototype.map.call(
				document.querySelectorAll('.libraryCheckbox:checked'),
				function (cb) { return cb.value; });
		},

		load: function () {
			Dashboard.showLoadingMsg();
			Promise.all([
				ApiClient.getPluginConfiguration(this.pluginUniqueId),
				ApiClient.getVirtualFolders()
			]).then(function (results) {
				var config = results[0];
				var virtualFolders = results[1];
				document.getElementById('FileMatchingMode').value = config.FileMatchingMode;
				document.getElementById('CustomRegexPattern').value = config.CustomRegexPattern;
				document.getElementById('DailyScanHour').value = config.DailyScanHour;
				document.getElementById('PlaybackPollHours').value = config.PlaybackPollHours;
				document.getElementById('PendingSanityHours').value = config.PendingSanityHours;
				document.getElementById('RequestDelayMilliseconds').value = config.RequestDelayMilliseconds;
				document.getElementById('Sponsor').checked = config.Sponsor;
				document.getElementById('SelfPromo').checked = config.SelfPromo;
				document.getElementById('Interaction').checked = config.Interaction;
				document.getElementById('Intro').checked = config.Intro;
				document.getElementById('Outro').checked = config.Outro;
				document.getElementById('Preview').checked = config.Preview;
				document.getElementById('Filler').checked = config.Filler;
				document.getElementById('MusicOfftopic').checked = config.MusicOfftopic;
				SponsorBlockConfig.renderLibraryList(config, virtualFolders);
				SponsorBlockConfig.toggleRegexField();
				Dashboard.hideLoadingMsg();
			});
		},

		save: function () {
			Dashboard.showLoadingMsg();
			ApiClient.getPluginConfiguration(this.pluginUniqueId).then(function (config) {
				config.FileMatchingMode = document.getElementById('FileMatchingMode').value;
				config.CustomRegexPattern = document.getElementById('CustomRegexPattern').value;
				config.DailyScanHour = parseInt(document.getElementById('DailyScanHour').value, 10);
				config.PlaybackPollHours = parseInt(document.getElementById('PlaybackPollHours').value, 10);
				config.PendingSanityHours = parseInt(document.getElementById('PendingSanityHours').value, 10);
				config.RequestDelayMilliseconds = parseInt(document.getElementById('RequestDelayMilliseconds').value, 10);
				config.EnabledLibraryIds = SponsorBlockConfig.collectSelectedLibraryIds();
				config.Sponsor = document.getElementById('Sponsor').checked;
				config.SelfPromo = document.getElementById('SelfPromo').checked;
				config.Interaction = document.getElementById('Interaction').checked;
				config.Intro = document.getElementById('Intro').checked;
				config.Outro = document.getElementById('Outro').checked;
				config.Preview = document.getElementById('Preview').checked;
				config.Filler = document.getElementById('Filler').checked;
				config.MusicOfftopic = document.getElementById('MusicOfftopic').checked;
				ApiClient.updatePluginConfiguration(SponsorBlockConfig.pluginUniqueId, config).then(function () {
					Dashboard.processPluginConfigurationUpdateResult();
				});
			});
		},

		toggleRegexField: function () {
			var mode = document.getElementById('FileMatchingMode').value;
			document.getElementById('CustomRegexContainer').style.display =
				mode === 'CustomRegex' ? '' : 'none';
		}
	};

	document.getElementById('FileMatchingMode')
		.addEventListener('change', SponsorBlockConfig.toggleRegexField);

	document.querySelector('#SponsorBlockConfigPage')
		.addEventListener('pageshow', function () { SponsorBlockConfig.load(); });
</script>
```

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: clean. The HTML is embedded as a resource so the build will fail if the file is missing/broken.

- [ ] **Step 4: Commit**

```bash
git add Jellyfin.Plugin.SponsorBlock/Configuration/configPage.html
git commit -m "extend config page with library picker, daily-scan hour, and advanced settings"
```

---

## Task 17: Bump CalVer and update manifest

**Files:**
- Modify: `manifest.json`

- [ ] **Step 1: Pick a CalVer**

Today is 2026-04-27. Use `2026.04.27`. If multiple builds happen the same day, append `.1`, `.2`, etc.

- [ ] **Step 2: Update manifest.json**

Inspect the current entries first to match shape:

Run: `cat manifest.json | head -40`

Add a new top entry to the `versions` array mirroring the existing pattern, with `"version": "2026.04.27"` and a changelog summary:
- Replace media-segment scan with event-driven orchestrator
- Add daily refresh task at configurable hour
- Add per-library scoping (allowlist) — empty by default
- SQLite per-item state (Pending/HasData/NoData) with 48h sanity check

- [ ] **Step 3: Build a release artifact (optional sanity check)**

Run: `dotnet publish Jellyfin.Plugin.SponsorBlock -c Release -o /tmp/sponsorblock-publish 2>&1 | tail -5`
Expected: publish succeeds; `Jellyfin.Plugin.SponsorBlock.dll` and `Microsoft.Data.Sqlite.dll` present in the output dir.

- [ ] **Step 4: Commit**

```bash
git add manifest.json
git commit -m "release v2026.04.27 — playback-triggered sponsorblock"
```

---

## Final verification (no commits)

- [ ] **Run all tests one more time:** `dotnet test`. Expect all green.
- [ ] **Run the build with warnings as errors:** `dotnet build -warnaserror`. Already on by csproj — must be clean.
- [ ] **Skim the diff:** `git log --oneline main..HEAD` should show the 17 commits of this plan, in order, each commit message describing intent.

## Out of plan (deliberately deferred)

- One-shot library backfill UI button (would re-introduce the slow scan).
- "Reset state / clear cache" admin action.
- Per-library category overrides.
- Migration of segments previously written by the deleted `IMediaSegmentProvider`. They will be replaced naturally on first daily-scan refresh of each `HasData` item that the new system fetches.
