# Done State and Convergence-Based Polling — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a terminal `Done` state to the SponsorBlock orchestrator so converged videos stop being polled, gated by YouTube release age (30 days) and consecutive-unchanged fetches (5).

**Architecture:** New `Done` enum value + `consecutive_unchanged` / `last_segment_hash` columns in SQLite (drop-and-recreate migration). New `SegmentHasher` helper for change detection. Orchestrator gains an age-gate fast path to `Done` and a counter-based path. Daily scan task refactored into a clean two-phase dispatcher (reconcile known + discover untracked) with all timing logic removed from the task and centralized in the orchestrator.

**Tech Stack:** C# / .NET 9, xUnit, NSubstitute, Microsoft.Data.Sqlite, Microsoft.Extensions.TimeProvider.Testing

---

### Task 1: SegmentHasher

**Files:**
- Create: `Jellyfin.Plugin.SponsorBlock/SegmentHasher.cs`
- Create: `Jellyfin.Plugin.SponsorBlock.Tests/SegmentHasherTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `Jellyfin.Plugin.SponsorBlock.Tests/SegmentHasherTests.cs`:

```csharp
using Xunit;

namespace Jellyfin.Plugin.SponsorBlock.Tests;

public class SegmentHasherTests
{
	[Fact]
	public void Hash_EmptyList_ReturnsStableNonEmptyHash()
	{
		var h1 = SegmentHasher.Hash([]);
		var h2 = SegmentHasher.Hash([]);

		Assert.NotEmpty(h1);
		Assert.Equal(h1, h2);
	}

	[Fact]
	public void Hash_SingleSegment_ReturnsStableHash()
	{
		var seg = new SponsorBlockSegment { ActionType = "skip", Category = "sponsor", Segment = [10, 20], UUID = "abc" };

		var h1 = SegmentHasher.Hash([seg]);
		var h2 = SegmentHasher.Hash([seg]);

		Assert.Equal(h1, h2);
	}

	[Fact]
	public void Hash_MultipleSegments_ShuffledOrder_ProducesSameHash()
	{
		var a = new SponsorBlockSegment { ActionType = "skip", Category = "sponsor", Segment = [10, 20], UUID = "aaa" };
		var b = new SponsorBlockSegment { ActionType = "skip", Category = "intro", Segment = [0, 5], UUID = "bbb" };
		var c = new SponsorBlockSegment { ActionType = "skip", Category = "outro", Segment = [90, 95], UUID = "ccc" };

		var h1 = SegmentHasher.Hash([a, b, c]);
		var h2 = SegmentHasher.Hash([c, a, b]);

		Assert.Equal(h1, h2);
	}

	[Fact]
	public void Hash_SegmentRemoved_ProducesDifferentHash()
	{
		var a = new SponsorBlockSegment { ActionType = "skip", Category = "sponsor", Segment = [10, 20], UUID = "aaa" };
		var b = new SponsorBlockSegment { ActionType = "skip", Category = "intro", Segment = [0, 5], UUID = "bbb" };

		var h1 = SegmentHasher.Hash([a, b]);
		var h2 = SegmentHasher.Hash([a]);

		Assert.NotEqual(h1, h2);
	}

	[Fact]
	public void Hash_SegmentAdded_ProducesDifferentHash()
	{
		var a = new SponsorBlockSegment { ActionType = "skip", Category = "sponsor", Segment = [10, 20], UUID = "aaa" };
		var b = new SponsorBlockSegment { ActionType = "skip", Category = "intro", Segment = [0, 5], UUID = "bbb" };

		var h1 = SegmentHasher.Hash([a]);
		var h2 = SegmentHasher.Hash([a, b]);

		Assert.NotEqual(h1, h2);
	}

	[Fact]
	public void Hash_NonSkipSegmentsExcluded()
	{
		var skip = new SponsorBlockSegment { ActionType = "skip", Category = "sponsor", Segment = [10, 20], UUID = "aaa" };
		var mute = new SponsorBlockSegment { ActionType = "mute", Category = "sponsor", Segment = [30, 40], UUID = "bbb" };

		var h1 = SegmentHasher.Hash([skip]);
		var h2 = SegmentHasher.Hash([skip, mute]);

		Assert.Equal(h1, h2);
	}
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~SegmentHasherTests" --logger "console;verbosity=minimal"`
Expected: FAIL — `SegmentHasher` does not exist (compilation error).

- [ ] **Step 3: Write the implementation**

Create `Jellyfin.Plugin.SponsorBlock/SegmentHasher.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.SponsorBlock;

/// <summary>
/// Computes a stable hash of SponsorBlock segment sets for change detection.
/// </summary>
public static class SegmentHasher
{
	/// <summary>
	/// Computes a SHA-256 hex hash of the skip-segment UUID set.
	/// UUIDs are sorted ordinally so order doesn't affect the result.
	/// Empty input uses a fixed sentinel so "still empty" counts as "unchanged".
	/// </summary>
	/// <param name="segments">Segments returned by the SponsorBlock API.</param>
	/// <returns>64-character lowercase hex string.</returns>
	public static string Hash(IReadOnlyList<SponsorBlockSegment> segments)
	{
		var uuids = segments
			.Where(s => s.ActionType == "skip")
			.Select(s => s.UUID)
			.OrderBy(u => u, StringComparer.Ordinal)
			.ToArray();
		var input = uuids.Length == 0 ? "__empty__" : string.Join("|", uuids);
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
	}
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~SegmentHasherTests" --logger "console;verbosity=minimal"`
Expected: PASS — 6 tests.

- [ ] **Step 5: Commit**

```bash
git add Jellyfin.Plugin.SponsorBlock/SegmentHasher.cs Jellyfin.Plugin.SponsorBlock.Tests/SegmentHasherTests.cs
git commit -m "add SegmentHasher for sponsorblock change detection"
```

---

### Task 2: Add Done to ItemState enum and update ItemStateRow

**Files:**
- Modify: `Jellyfin.Plugin.SponsorBlock/State/ItemState.cs`
- Modify: `Jellyfin.Plugin.SponsorBlock/State/ItemStateRow.cs`

- [ ] **Step 1: Add Done to the enum**

Replace `Jellyfin.Plugin.SponsorBlock/State/ItemState.cs` with:

```csharp
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

	/// <summary>Sanity-checked at ≥ PendingSanityHours and still empty. Rechecked after the same cooldown.</summary>
	NoData = 2,

	/// <summary>Terminal: SponsorBlock data has converged. Excluded from all polling. Reversible only via reset.</summary>
	Done = 3,
}
```

- [ ] **Step 2: Add new fields to ItemStateRow**

Replace `Jellyfin.Plugin.SponsorBlock/State/ItemStateRow.cs` with:

```csharp
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
/// <param name="ConsecutiveUnchanged">Consecutive daily fetches with unchanged segment data.</param>
/// <param name="LastSegmentHash">SHA-256 hex hash of the last-seen segment UUID set.</param>
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

- [ ] **Step 3: Verify the solution still compiles (expect failures in store + tests that use the old constructor)**

Run: `dotnet build`
Expected: Build fails in `SqliteSponsorBlockStateStore`, `SponsorBlockOrchestrator`, `SponsorBlockOrchestratorTests`, `SqliteSponsorBlockStateStoreTests`, `SponsorBlockRefreshTaskTests` — all places that construct `ItemStateRow` without the two new parameters. These will be fixed in subsequent tasks.

- [ ] **Step 4: Commit (intermediate — will be fixed in next tasks)**

```bash
git add Jellyfin.Plugin.SponsorBlock/State/ItemState.cs Jellyfin.Plugin.SponsorBlock/State/ItemStateRow.cs
git commit -m "add Done state and new ItemStateRow fields"
```

---

### Task 3: Update SQLite state store — schema, CRUD, and GetActiveAsync

**Files:**
- Modify: `Jellyfin.Plugin.SponsorBlock/State/SqliteSponsorBlockStateStore.cs`
- Modify: `Jellyfin.Plugin.SponsorBlock/State/ISponsorBlockStateStore.cs`
- Modify: `Jellyfin.Plugin.SponsorBlock.Tests/State/SqliteSponsorBlockStateStoreTests.cs`

- [ ] **Step 1: Write the failing tests**

Replace `Jellyfin.Plugin.SponsorBlock.Tests/State/SqliteSponsorBlockStateStoreTests.cs` with:

```csharp
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
		var row = NewRow(state: ItemState.HasData, segmentCount: 3, consecutiveUnchanged: 2, lastSegmentHash: "abc123");
		await _store.UpsertAsync(row, CancellationToken.None);

		var fetched = await _store.GetAsync(row.ItemId, CancellationToken.None);

		Assert.NotNull(fetched);
		Assert.Equal(row.ItemId, fetched.ItemId);
		Assert.Equal(row.VideoId, fetched.VideoId);
		Assert.Equal(row.State, fetched.State);
		Assert.Equal(row.FirstSeenAt.ToUnixTimeSeconds(), fetched.FirstSeenAt.ToUnixTimeSeconds());
		Assert.Equal(row.LastFetchAt.ToUnixTimeSeconds(), fetched.LastFetchAt.ToUnixTimeSeconds());
		Assert.Equal(row.SegmentCount, fetched.SegmentCount);
		Assert.Equal(row.ConsecutiveUnchanged, fetched.ConsecutiveUnchanged);
		Assert.Equal(row.LastSegmentHash, fetched.LastSegmentHash);
	}

	[Fact]
	public async Task Upsert_Replaces_ExistingRow()
	{
		var row = NewRow(state: ItemState.Pending);
		await _store.UpsertAsync(row, CancellationToken.None);

		var updated = row with { State = ItemState.HasData, SegmentCount = 2, ConsecutiveUnchanged = 1, LastSegmentHash = "new" };
		await _store.UpsertAsync(updated, CancellationToken.None);

		var fetched = await _store.GetAsync(row.ItemId, CancellationToken.None);

		Assert.NotNull(fetched);
		Assert.Equal(ItemState.HasData, fetched.State);
		Assert.Equal(2, fetched.SegmentCount);
		Assert.Equal(1, fetched.ConsecutiveUnchanged);
		Assert.Equal("new", fetched.LastSegmentHash);
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
	public async Task GetActive_ReturnsPendingHasDataNoData_ButNotDone()
	{
		var pending = NewRow(state: ItemState.Pending);
		var hasData = NewRow(state: ItemState.HasData);
		var noData = NewRow(state: ItemState.NoData);
		var done = NewRow(state: ItemState.Done);
		await _store.UpsertAsync(pending, CancellationToken.None);
		await _store.UpsertAsync(hasData, CancellationToken.None);
		await _store.UpsertAsync(noData, CancellationToken.None);
		await _store.UpsertAsync(done, CancellationToken.None);

		var ids = new HashSet<Guid>();
		await foreach (var row in _store.GetActiveAsync(CancellationToken.None))
		{
			ids.Add(row.ItemId);
		}

		Assert.Contains(pending.ItemId, ids);
		Assert.Contains(hasData.ItemId, ids);
		Assert.Contains(noData.ItemId, ids);
		Assert.DoesNotContain(done.ItemId, ids);
	}

	[Fact]
	public async Task GetAllItemIds_ReturnsAllStatesIncludingDone()
	{
		var pending = NewRow(state: ItemState.Pending);
		var done = NewRow(state: ItemState.Done);
		await _store.UpsertAsync(pending, CancellationToken.None);
		await _store.UpsertAsync(done, CancellationToken.None);

		var ids = new HashSet<Guid>();
		await foreach (var id in _store.GetAllItemIdsAsync(CancellationToken.None))
		{
			ids.Add(id);
		}

		Assert.Contains(pending.ItemId, ids);
		Assert.Contains(done.ItemId, ids);
	}

	private static ItemStateRow NewRow(
		ItemState state = ItemState.Pending,
		int segmentCount = 0,
		int consecutiveUnchanged = 0,
		string lastSegmentHash = "")
	{
		var now = DateTimeOffset.UtcNow;
		return new ItemStateRow(
			ItemId: Guid.NewGuid(),
			VideoId: "abcdefghijk",
			State: state,
			FirstSeenAt: now,
			LastFetchAt: now,
			SegmentCount: segmentCount,
			ConsecutiveUnchanged: consecutiveUnchanged,
			LastSegmentHash: lastSegmentHash);
	}
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~SqliteSponsorBlockStateStoreTests" --logger "console;verbosity=minimal"`
Expected: FAIL — compilation errors (new `ItemStateRow` constructor, new `GetAllItemIdsAsync` method doesn't exist).

- [ ] **Step 3: Update the store interface**

Replace `Jellyfin.Plugin.SponsorBlock/State/ISponsorBlockStateStore.cs` with:

```csharp
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
	/// Returns all rows in <see cref="ItemState.Pending"/>, <see cref="ItemState.HasData"/>,
	/// or <see cref="ItemState.NoData"/> state, for use by the daily refresh task.
	/// <see cref="ItemState.Done"/> rows are excluded.
	/// </summary>
	IAsyncEnumerable<ItemStateRow> GetActiveAsync(CancellationToken cancellationToken);

	/// <summary>
	/// Returns the item IDs for ALL rows (any state, including <see cref="ItemState.Done"/>).
	/// Used by the daily scan to determine which scoped videos have no row yet.
	/// </summary>
	IAsyncEnumerable<Guid> GetAllItemIdsAsync(CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Update the SQLite implementation**

Replace `Jellyfin.Plugin.SponsorBlock/State/SqliteSponsorBlockStateStore.cs` with:

```csharp
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
	private const int SchemaVersion = 2;

	private readonly SqliteConnection _connection;
	private readonly SemaphoreSlim _writeLock = new(1, 1);

	/// <summary>
	/// Initializes the store using a caller-supplied connection. The connection is opened if not already open.
	/// </summary>
	/// <param name="connection">The SQLite connection to own.</param>
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
		using var versionCmd = _connection.CreateCommand();
		versionCmd.CommandText = "PRAGMA user_version";
		var currentVersion = (long)(versionCmd.ExecuteScalar() ?? 0);

		if (currentVersion != SchemaVersion)
		{
			using var dropCmd = _connection.CreateCommand();
			dropCmd.CommandText = "DROP TABLE IF EXISTS item_state";
			dropCmd.ExecuteNonQuery();

			using var createCmd = _connection.CreateCommand();
			createCmd.CommandText = @"
				CREATE TABLE item_state (
					item_id               BLOB PRIMARY KEY,
					video_id              TEXT NOT NULL,
					state                 INTEGER NOT NULL,
					first_seen_at         INTEGER NOT NULL,
					last_fetch_at         INTEGER NOT NULL,
					segment_count         INTEGER NOT NULL DEFAULT 0,
					consecutive_unchanged INTEGER NOT NULL DEFAULT 0,
					last_segment_hash     TEXT NOT NULL DEFAULT ''
				);
				CREATE INDEX IF NOT EXISTS idx_state ON item_state(state);
				CREATE INDEX IF NOT EXISTS idx_first_seen ON item_state(first_seen_at);
				PRAGMA user_version = " + SchemaVersion + ";";
			createCmd.ExecuteNonQuery();
		}
	}

	/// <inheritdoc />
	public async ValueTask<ItemStateRow?> GetAsync(Guid itemId, CancellationToken cancellationToken)
	{
		await using var cmd = _connection.CreateCommand();
		cmd.CommandText = "SELECT video_id, state, first_seen_at, last_fetch_at, segment_count, consecutive_unchanged, last_segment_hash FROM item_state WHERE item_id = $id";
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
			SegmentCount: reader.GetInt32(4),
			ConsecutiveUnchanged: reader.GetInt32(5),
			LastSegmentHash: reader.GetString(6));
	}

	/// <inheritdoc />
	public async ValueTask UpsertAsync(ItemStateRow row, CancellationToken cancellationToken)
	{
		await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await using var cmd = _connection.CreateCommand();
			cmd.CommandText = @"
				INSERT INTO item_state (item_id, video_id, state, first_seen_at, last_fetch_at, segment_count, consecutive_unchanged, last_segment_hash)
				VALUES ($id, $vid, $st, $fs, $lf, $sc, $cu, $lh)
				ON CONFLICT(item_id) DO UPDATE SET
					video_id              = excluded.video_id,
					state                 = excluded.state,
					first_seen_at         = excluded.first_seen_at,
					last_fetch_at         = excluded.last_fetch_at,
					segment_count         = excluded.segment_count,
					consecutive_unchanged = excluded.consecutive_unchanged,
					last_segment_hash     = excluded.last_segment_hash;";
			cmd.Parameters.AddWithValue("$id", row.ItemId.ToByteArray());
			cmd.Parameters.AddWithValue("$vid", row.VideoId);
			cmd.Parameters.AddWithValue("$st", (int)row.State);
			cmd.Parameters.AddWithValue("$fs", row.FirstSeenAt.ToUnixTimeSeconds());
			cmd.Parameters.AddWithValue("$lf", row.LastFetchAt.ToUnixTimeSeconds());
			cmd.Parameters.AddWithValue("$sc", row.SegmentCount);
			cmd.Parameters.AddWithValue("$cu", row.ConsecutiveUnchanged);
			cmd.Parameters.AddWithValue("$lh", row.LastSegmentHash);
			await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_writeLock.Release();
		}
	}

	/// <inheritdoc />
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

	/// <inheritdoc />
	public async IAsyncEnumerable<ItemStateRow> GetActiveAsync(
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await using var cmd = _connection.CreateCommand();
		cmd.CommandText = @"
			SELECT item_id, video_id, state, first_seen_at, last_fetch_at, segment_count, consecutive_unchanged, last_segment_hash
			FROM item_state
			WHERE state IN (0, 1, 2)
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
				SegmentCount: reader.GetInt32(5),
				ConsecutiveUnchanged: reader.GetInt32(6),
				LastSegmentHash: reader.GetString(7));
		}
	}

	/// <inheritdoc />
	public async IAsyncEnumerable<Guid> GetAllItemIdsAsync(
		[EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await using var cmd = _connection.CreateCommand();
		cmd.CommandText = "SELECT item_id FROM item_state";

		await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
		while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
		{
			var idBytes = (byte[])reader.GetValue(0);
			yield return new Guid(idBytes);
		}
	}

	/// <inheritdoc />
	public void Dispose()
	{
		_writeLock.Dispose();
		_connection.Dispose();
	}
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~SqliteSponsorBlockStateStoreTests" --logger "console;verbosity=minimal"`
Expected: PASS — 6 tests.

- [ ] **Step 6: Commit**

```bash
git add Jellyfin.Plugin.SponsorBlock/State/ISponsorBlockStateStore.cs Jellyfin.Plugin.SponsorBlock/State/SqliteSponsorBlockStateStore.cs Jellyfin.Plugin.SponsorBlock.Tests/State/SqliteSponsorBlockStateStoreTests.cs
git commit -m "update state store schema to v2, add GetAllItemIdsAsync, include NoData in GetActiveAsync"
```

---

### Task 4: Add configuration fields

**Files:**
- Modify: `Jellyfin.Plugin.SponsorBlock/Configuration/PluginConfiguration.cs`
- Modify: `Jellyfin.Plugin.SponsorBlock/Configuration/configPage.html`

- [ ] **Step 1: Add config fields**

In `Jellyfin.Plugin.SponsorBlock/Configuration/PluginConfiguration.cs`, add after the `RequestDelayMilliseconds` property (after line 88):

```csharp
	/// <summary>
	/// Gets or sets the age (in days since YouTube premiere/publish date) at which
	/// an item is fetched one final time and frozen as Done. Videos older than this
	/// are assumed to have converged SponsorBlock data.
	/// </summary>
	public int ReleaseAgeCutoffDays { get; set; } = 30;

	/// <summary>
	/// Gets or sets how many consecutive daily fetches must return unchanged segment
	/// data before a young item is marked Done. Reset to 0 on any change.
	/// </summary>
	public int ConsecutiveUnchangedThreshold { get; set; } = 5;
```

- [ ] **Step 2: Add config page inputs**

In `Jellyfin.Plugin.SponsorBlock/Configuration/configPage.html`, add inside the `<details>` Advanced section, after the `RequestDelayMilliseconds` inputContainer (after line 125, before `</details>`):

```html
					<div class="inputContainer">
						<label class="inputLabel" for="ReleaseAgeCutoffDays">Release age cutoff (days)</label>
						<input id="ReleaseAgeCutoffDays" type="number" min="1" is="emby-input" />
						<div class="fieldDescription">Videos whose YouTube publish date is older than this are fetched once and frozen. Default: 30.</div>
					</div>
					<div class="inputContainer">
						<label class="inputLabel" for="ConsecutiveUnchangedThreshold">Consecutive unchanged threshold</label>
						<input id="ConsecutiveUnchangedThreshold" type="number" min="1" is="emby-input" />
						<div class="fieldDescription">Number of daily fetches with unchanged data before an item is marked done. Default: 5.</div>
					</div>
```

- [ ] **Step 3: Add JS load/save for the new fields**

In `configPage.html`, in the `load` function, add after the `RequestDelayMilliseconds` line (after line 224):

```javascript
						document.getElementById('ReleaseAgeCutoffDays').value = config.ReleaseAgeCutoffDays;
						document.getElementById('ConsecutiveUnchangedThreshold').value = config.ConsecutiveUnchangedThreshold;
```

In the `save` function, add after the `RequestDelayMilliseconds` line (after line 247):

```javascript
						config.ReleaseAgeCutoffDays = parseInt(document.getElementById('ReleaseAgeCutoffDays').value, 10);
						config.ConsecutiveUnchangedThreshold = parseInt(document.getElementById('ConsecutiveUnchangedThreshold').value, 10);
```

- [ ] **Step 4: Verify build**

Run: `dotnet build`
Expected: PASS (config page is embedded resource, no compilation impact).

- [ ] **Step 5: Commit**

```bash
git add Jellyfin.Plugin.SponsorBlock/Configuration/PluginConfiguration.cs Jellyfin.Plugin.SponsorBlock/Configuration/configPage.html
git commit -m "add ReleaseAgeCutoffDays and ConsecutiveUnchangedThreshold config fields"
```

---

### Task 5: Update orchestrator — Done state, age gate, and consecutive counter

**Files:**
- Modify: `Jellyfin.Plugin.SponsorBlock/Orchestration/SponsorBlockOrchestrator.cs`
- Modify: `Jellyfin.Plugin.SponsorBlock.Tests/Orchestration/SponsorBlockOrchestratorTests.cs`

- [ ] **Step 1: Write the failing tests**

In `Jellyfin.Plugin.SponsorBlock.Tests/Orchestration/SponsorBlockOrchestratorTests.cs`, update the `NewRow` helper and add new tests. First, replace the `NewRow` helper at the bottom of the file:

```csharp
	private static ItemStateRow NewRow(
		Guid itemId,
		ItemState state,
		int segmentCount = 0,
		DateTimeOffset? firstSeen = null,
		DateTimeOffset? lastFetchAt = null,
		int consecutiveUnchanged = 0,
		string lastSegmentHash = "") =>
		new(itemId, "abcdefghijk", state, firstSeen ?? T0, lastFetchAt ?? T0, segmentCount, consecutiveUnchanged, lastSegmentHash);
```

Then add these new tests at the end of the class (before the closing `}`):

```csharp
	[Fact]
	public async Task DoneState_AnyTrigger_NoOps()
	{
		var item = FakeItem(Guid.NewGuid());
		_scope.IsInScope(item).Returns(true);
		_store.GetAsync(item.Id, Arg.Any<CancellationToken>())
			.Returns(NewRow(item.Id, ItemState.Done, segmentCount: 2));

		await MakeOrchestrator().ProcessAsync(item, ProcessReason.DailyScan, CancellationToken.None);
		await MakeOrchestrator().ProcessAsync(item, ProcessReason.PlaybackStart, CancellationToken.None);

		await _api.DidNotReceive().GetSegmentsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
		await _store.DidNotReceive().UpsertAsync(Arg.Any<ItemStateRow>(), Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task YoungItem_FirstFetch_SegmentsFound_HasDataWithHash()
	{
		var item = FakeItem(Guid.NewGuid());
		item.PremiereDate = T0.DateTime;
		_scope.IsInScope(item).Returns(true);
		_store.GetAsync(item.Id, Arg.Any<CancellationToken>()).Returns((ItemStateRow?)null);
		_api.GetSegmentsAsync("abcdefghijk", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
			.Returns(new List<SponsorBlockSegment> { Seg() });

		await MakeOrchestrator().ProcessAsync(item, ProcessReason.ItemAdded, CancellationToken.None);

		await _store.Received().UpsertAsync(
			Arg.Is<ItemStateRow>(r => r.State == ItemState.HasData && r.ConsecutiveUnchanged == 0 && !string.IsNullOrEmpty(r.LastSegmentHash)),
			Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task YoungItem_SecondFetch_SameSegments_IncrementsCounter()
	{
		var item = FakeItem(Guid.NewGuid());
		item.PremiereDate = T0.DateTime;
		_scope.IsInScope(item).Returns(true);
		var hash = SegmentHasher.Hash(new List<SponsorBlockSegment> { Seg() });
		_store.GetAsync(item.Id, Arg.Any<CancellationToken>())
			.Returns(NewRow(item.Id, ItemState.HasData, segmentCount: 1, consecutiveUnchanged: 0, lastSegmentHash: hash));
		_api.GetSegmentsAsync("abcdefghijk", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
			.Returns(new List<SponsorBlockSegment> { Seg() });

		await MakeOrchestrator().ProcessAsync(item, ProcessReason.DailyScan, CancellationToken.None);

		await _store.Received().UpsertAsync(
			Arg.Is<ItemStateRow>(r => r.State == ItemState.HasData && r.ConsecutiveUnchanged == 1),
			Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task YoungItem_FifthConsecutiveUnchanged_TransitionsToDone()
	{
		var item = FakeItem(Guid.NewGuid());
		item.PremiereDate = T0.DateTime;
		_scope.IsInScope(item).Returns(true);
		var hash = SegmentHasher.Hash(new List<SponsorBlockSegment> { Seg() });
		_store.GetAsync(item.Id, Arg.Any<CancellationToken>())
			.Returns(NewRow(item.Id, ItemState.HasData, segmentCount: 1, consecutiveUnchanged: 4, lastSegmentHash: hash));
		_api.GetSegmentsAsync("abcdefghijk", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
			.Returns(new List<SponsorBlockSegment> { Seg() });

		await MakeOrchestrator().ProcessAsync(item, ProcessReason.DailyScan, CancellationToken.None);

		await _store.Received().UpsertAsync(
			Arg.Is<ItemStateRow>(r => r.State == ItemState.Done && r.ConsecutiveUnchanged == 5),
			Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task YoungItem_SegmentsChange_CounterResetsToZero()
	{
		var item = FakeItem(Guid.NewGuid());
		item.PremiereDate = T0.DateTime;
		_scope.IsInScope(item).Returns(true);
		_store.GetAsync(item.Id, Arg.Any<CancellationToken>())
			.Returns(NewRow(item.Id, ItemState.HasData, segmentCount: 1, consecutiveUnchanged: 3, lastSegmentHash: "stale-hash"));
		_api.GetSegmentsAsync("abcdefghijk", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
			.Returns(new List<SponsorBlockSegment> { Seg() });

		await MakeOrchestrator().ProcessAsync(item, ProcessReason.DailyScan, CancellationToken.None);

		await _store.Received().UpsertAsync(
			Arg.Is<ItemStateRow>(r => r.State == ItemState.HasData && r.ConsecutiveUnchanged == 0),
			Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task AgeGate_OldPremiereDate_AnyState_TransitionsToDone()
	{
		var item = FakeItem(Guid.NewGuid());
		item.PremiereDate = T0.AddDays(-35).DateTime;
		_scope.IsInScope(item).Returns(true);
		_store.GetAsync(item.Id, Arg.Any<CancellationToken>())
			.Returns(NewRow(item.Id, ItemState.HasData, segmentCount: 1, consecutiveUnchanged: 2));
		_api.GetSegmentsAsync("abcdefghijk", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
			.Returns(new List<SponsorBlockSegment> { Seg() });

		await MakeOrchestrator().ProcessAsync(item, ProcessReason.DailyScan, CancellationToken.None);

		await _store.Received().UpsertAsync(
			Arg.Is<ItemStateRow>(r => r.State == ItemState.Done),
			Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task AgeGate_NoPremiereDate_SkipsAgeGate_UsesYoungItemFlow()
	{
		var item = FakeItem(Guid.NewGuid());
		item.PremiereDate = null;
		_scope.IsInScope(item).Returns(true);
		_store.GetAsync(item.Id, Arg.Any<CancellationToken>()).Returns((ItemStateRow?)null);
		_api.GetSegmentsAsync("abcdefghijk", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
			.Returns(new List<SponsorBlockSegment> { Seg() });

		await MakeOrchestrator().ProcessAsync(item, ProcessReason.ItemAdded, CancellationToken.None);

		await _store.Received().UpsertAsync(
			Arg.Is<ItemStateRow>(r => r.State == ItemState.HasData),
			Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task YoungNoData_EmptyResponseUnchanged_FiveTimes_TransitionsToDone()
	{
		var item = FakeItem(Guid.NewGuid());
		item.PremiereDate = T0.DateTime;
		_scope.IsInScope(item).Returns(true);
		var emptyHash = SegmentHasher.Hash(new List<SponsorBlockSegment>());
		_store.GetAsync(item.Id, Arg.Any<CancellationToken>())
			.Returns(NewRow(item.Id, ItemState.NoData, consecutiveUnchanged: 4, lastSegmentHash: emptyHash, firstSeen: T0.AddHours(-50)));
		_api.GetSegmentsAsync("abcdefghijk", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
			.Returns(new List<SponsorBlockSegment>());

		await MakeOrchestrator().ProcessAsync(item, ProcessReason.DailyScan, CancellationToken.None);

		await _store.Received().UpsertAsync(
			Arg.Is<ItemStateRow>(r => r.State == ItemState.Done && r.ConsecutiveUnchanged == 5),
			Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task YoungItem_FourUnchangedThenOneChanged_CounterResets_NotDone()
	{
		var item = FakeItem(Guid.NewGuid());
		item.PremiereDate = T0.DateTime;
		_scope.IsInScope(item).Returns(true);
		_store.GetAsync(item.Id, Arg.Any<CancellationToken>())
			.Returns(NewRow(item.Id, ItemState.HasData, segmentCount: 1, consecutiveUnchanged: 4, lastSegmentHash: "old-hash"));
		_api.GetSegmentsAsync("abcdefghijk", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
			.Returns(new List<SponsorBlockSegment> { Seg() });

		await MakeOrchestrator().ProcessAsync(item, ProcessReason.DailyScan, CancellationToken.None);

		await _store.Received().UpsertAsync(
			Arg.Is<ItemStateRow>(r => r.State == ItemState.HasData && r.ConsecutiveUnchanged == 0),
			Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task DoneTransition_EmptyApi_DeletesStaleSegments()
	{
		var item = FakeItem(Guid.NewGuid());
		item.PremiereDate = T0.AddDays(-35).DateTime;
		_scope.IsInScope(item).Returns(true);
		_store.GetAsync(item.Id, Arg.Any<CancellationToken>())
			.Returns(NewRow(item.Id, ItemState.HasData, segmentCount: 2));
		_api.GetSegmentsAsync("abcdefghijk", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
			.Returns(new List<SponsorBlockSegment>());

		await MakeOrchestrator().ProcessAsync(item, ProcessReason.DailyScan, CancellationToken.None);

		await _writer.Received(1).DeleteOwnedAsync(item.Id, Arg.Any<CancellationToken>());
		await _writer.DidNotReceive().CreateAsync(Arg.Any<MediaSegmentDto>(), Arg.Any<CancellationToken>());
		await _store.Received().UpsertAsync(
			Arg.Is<ItemStateRow>(r => r.State == ItemState.Done && r.SegmentCount == 0),
			Arg.Any<CancellationToken>());
	}
```

Also update the `Seg` helper to set a stable UUID:

```csharp
	private static SponsorBlockSegment Seg(string category = "sponsor")
		=> new() { Category = category, ActionType = "skip", Segment = [10.0, 20.0], UUID = "uuid-1" };
```

And update the `TestItem` class to allow setting `PremiereDate`:

```csharp
	private sealed class TestItem : BaseItem
	{
		public override DateTime? PremiereDate { get; set; }
	}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~SponsorBlockOrchestratorTests" --logger "console;verbosity=minimal"`
Expected: FAIL — compilation errors (orchestrator doesn't pass `PremiereDate`, doesn't compute hash, doesn't handle `Done`).

- [ ] **Step 3: Implement the orchestrator changes**

Replace `Jellyfin.Plugin.SponsorBlock/Orchestration/SponsorBlockOrchestrator.cs` with:

```csharp
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
	private readonly SponsorBlockLog _log;
	private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _itemLocks = new();

	/// <summary>Production constructor (uses static <see cref="YouTubeIdExtractor"/>).</summary>
	public SponsorBlockOrchestrator(
		ISponsorBlockApiClient api,
		ISponsorBlockStateStore store,
		ILibraryScopeService scope,
		IMediaSegmentWriter writer,
		Func<PluginConfiguration> config,
		TimeProvider time,
		ILogger<SponsorBlockOrchestrator> logger,
		SponsorBlockLog log)
		: this(api, store, scope, writer, config,
			(filename, mode, pattern) => YouTubeIdExtractor.Extract(filename, mode, pattern),
			time, logger, log)
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
		ILogger<SponsorBlockOrchestrator> logger,
		SponsorBlockLog log)
	{
		_api = api;
		_store = store;
		_scope = scope;
		_writer = writer;
		_config = config;
		_extractVideoId = extractVideoId;
		_time = time;
		_logger = logger;
		_log = log;
	}

	/// <summary>
	/// Process one item under the given trigger. Implements the decision table from the spec.
	/// Swallows transient HTTP failures (logs warning, leaves state untouched).
	/// </summary>
	public async Task ProcessAsync(BaseItem item, ProcessReason reason, CancellationToken cancellationToken)
	{
		if (!_scope.IsInScope(item))
		{
			_logger.LogDebug("SponsorBlock: skipping {ItemName} ({ItemId}) — not in configured library scope", item.Name, item.Id);
			return;
		}

		var path = item.Path;
		if (string.IsNullOrEmpty(path))
		{
			_logger.LogDebug("SponsorBlock: skipping {ItemId} — no filesystem path", item.Id);
			return;
		}

		var config = _config();
		var filename = Path.GetFileName(path);
		var videoId = _extractVideoId(filename, config.FileMatchingMode, config.CustomRegexPattern);
		if (videoId is null)
		{
			_logger.LogDebug("SponsorBlock: skipping {ItemName} ({ItemId}) — could not extract YouTube ID from filename \"{Filename}\"", item.Name, item.Id, filename);
			return;
		}

		var sem = _itemLocks.GetOrAdd(item.Id, _ => new SemaphoreSlim(1, 1));
		await sem.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await ProcessLockedAsync(item.Id, item.PremiereDate, item.DateCreated, videoId, reason, config, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			sem.Release();
		}
	}

	private async Task ProcessLockedAsync(
		Guid itemId,
		DateTime? premiereDate,
		DateTime itemDateCreated,
		string videoId,
		ProcessReason reason,
		PluginConfiguration config,
		CancellationToken ct)
	{
		var existing = await _store.GetAsync(itemId, ct).ConfigureAwait(false);
		var now = _time.GetUtcNow();

		if (existing is { State: ItemState.Done })
		{
			_logger.LogDebug("SponsorBlock {VideoId}: Done — skipping {Reason}", videoId, reason);
			return;
		}

		if (existing is { State: ItemState.NoData })
		{
			var ageHours = (now - existing.LastFetchAt).TotalHours;
			if (ageHours < config.PendingSanityHours)
			{
				_logger.LogDebug("SponsorBlock {VideoId}: NoData cooldown ({Age:F1}h of {Window}h) — skipping {Reason}", videoId, ageHours, config.PendingSanityHours, reason);
				return;
			}

			_logger.LogInformation("SponsorBlock {VideoId}: NoData cooldown elapsed ({Age:F1}h) — rechecking ({Reason})", videoId, ageHours, reason);
			_log.Information($"SponsorBlock {videoId}: NoData cooldown elapsed ({ageHours:F1}h) — rechecking ({reason})");
		}

		if (existing is { State: ItemState.HasData or ItemState.Done } && reason == ProcessReason.PlaybackStart && _writer.HasAny(itemId))
		{
			_logger.LogDebug("SponsorBlock {VideoId}: segments already exist in Jellyfin — skipping {Reason}", videoId, reason);
			return;
		}

		if (existing is { State: ItemState.Pending } && reason == ProcessReason.PlaybackStart)
		{
			var ageHours = (now - existing.FirstSeenAt).TotalHours;
			if (ageHours < config.PlaybackPollHours)
			{
				_logger.LogDebug("SponsorBlock {VideoId}: Pending poll window ({Age:F1}h of {Window}h) — skipping {Reason}", videoId, ageHours, config.PlaybackPollHours, reason);
				return;
			}

			_logger.LogInformation("SponsorBlock {VideoId}: Pending poll window elapsed ({Age:F1}h) — rechecking ({Reason})", videoId, ageHours, reason);
			_log.Information($"SponsorBlock {videoId}: Pending poll window elapsed ({ageHours:F1}h) — rechecking ({reason})");
		}

		IReadOnlyList<SponsorBlockSegment> apiSegments;
		try
		{
			var categories = CategoryMapping.GetEnabledCategories(config.GetCategorySettings());
			if (categories.Count == 0)
			{
				_logger.LogWarning("SponsorBlock {VideoId}: all categories disabled — skipping {Reason}", videoId, reason);
				_log.Warning($"SponsorBlock {videoId}: all categories disabled — skipping {reason}");
				return;
			}

			apiSegments = await _api.GetSegmentsAsync(videoId, categories, ct).ConfigureAwait(false);
		}
		catch (HttpRequestException ex)
		{
			_logger.LogWarning(ex, "SponsorBlock fetch failure for {VideoId}; state unchanged", videoId);
			_log.Warning($"SponsorBlock fetch failure for {videoId}: {ex.Message}");
			return;
		}
		catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
		{
			_logger.LogWarning(ex, "SponsorBlock fetch timeout for {VideoId}; state unchanged", videoId);
			_log.Warning($"SponsorBlock fetch timeout for {videoId}");
			return;
		}

		var firstSeen = existing?.FirstSeenAt ?? GetInitialFirstSeen(reason, itemDateCreated, now);
		var hasSegments = apiSegments.Any(s => s.ActionType == "skip");
		var hash = SegmentHasher.Hash(apiSegments);
		var unchanged = existing is not null && existing.LastSegmentHash == hash;
		var consecutive = unchanged ? existing!.ConsecutiveUnchanged + 1 : 0;

		var premiereOffset = premiereDate is not null
			? now - ToUtc(premiereDate.Value)
			: (TimeSpan?)null;
		var isMature = premiereOffset is not null && premiereOffset.Value.TotalDays >= config.ReleaseAgeCutoffDays;

		if (isMature)
		{
			if (hasSegments)
			{
				var dtos = SegmentMapper.Map(apiSegments, itemId);
				await _writer.DeleteOwnedAsync(itemId, ct).ConfigureAwait(false);
				foreach (var dto in dtos)
				{
					await _writer.CreateAsync(dto, ct).ConfigureAwait(false);
				}
			}
			else
			{
				await _writer.DeleteOwnedAsync(itemId, ct).ConfigureAwait(false);
			}

			await _store.UpsertAsync(
				new ItemStateRow(itemId, videoId, ItemState.Done, firstSeen, now, hasSegments ? apiSegments.Count(s => s.ActionType == "skip") : 0, 0, hash),
				ct).ConfigureAwait(false);
			_logger.LogInformation("SponsorBlock {VideoId}: age gate → Done ({Reason})", videoId, reason);
			_log.Information($"SponsorBlock {videoId}: age gate → Done ({reason})");
			return;
		}

		if (hasSegments)
		{
			var dtos = SegmentMapper.Map(apiSegments, itemId);
			await _writer.DeleteOwnedAsync(itemId, ct).ConfigureAwait(false);
			foreach (var dto in dtos)
			{
				await _writer.CreateAsync(dto, ct).ConfigureAwait(false);
			}

			var newState = consecutive >= config.ConsecutiveUnchangedThreshold ? ItemState.Done : ItemState.HasData;
			await _store.UpsertAsync(
				new ItemStateRow(itemId, videoId, newState, firstSeen, now, dtos.Count, consecutive, hash),
				ct).ConfigureAwait(false);
			_logger.LogInformation("SponsorBlock {VideoId}: wrote {Count} segments → {State} ({Reason})", videoId, dtos.Count, newState, reason);
			_log.Information($"SponsorBlock {videoId}: wrote {dtos.Count} segments → {newState} ({reason})");
			return;
		}

		var sanityElapsed = (now - firstSeen).TotalHours >= config.PendingSanityHours;
		var noDataState = sanityElapsed ? ItemState.NoData : ItemState.Pending;
		var finalState = consecutive >= config.ConsecutiveUnchangedThreshold ? ItemState.Done : noDataState;

		await _writer.DeleteOwnedAsync(itemId, ct).ConfigureAwait(false);
		await _store.UpsertAsync(
			new ItemStateRow(itemId, videoId, finalState, firstSeen, now, 0, consecutive, hash),
			ct).ConfigureAwait(false);
		_logger.LogInformation("SponsorBlock {VideoId}: no segments found → {State} ({Reason})", videoId, finalState, reason);
		_log.Information($"SponsorBlock {videoId}: no segments found → {finalState} ({reason})");
	}

	private static DateTimeOffset GetInitialFirstSeen(ProcessReason reason, DateTime itemDateCreated, DateTimeOffset now)
	{
		if (reason != ProcessReason.DailyScan)
		{
			return now;
		}

		var created = ToUtc(itemDateCreated);
		return created > now ? now : created;
	}

	private static DateTimeOffset ToUtc(DateTime value)
	{
		if (value.Kind == DateTimeKind.Unspecified)
		{
			return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
		}

		return new DateTimeOffset(value.ToUniversalTime());
	}
}
```

- [ ] **Step 4: Run all orchestrator tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~SponsorBlockOrchestratorTests" --logger "console;verbosity=minimal"`
Expected: PASS — all orchestrator tests (old + new).

- [ ] **Step 5: Commit**

```bash
git add Jellyfin.Plugin.SponsorBlock/Orchestration/SponsorBlockOrchestrator.cs Jellyfin.Plugin.SponsorBlock.Tests/Orchestration/SponsorBlockOrchestratorTests.cs
git commit -m "add done state, age gate, and consecutive-unchanged counter to orchestrator"
```

---

### Task 6: Refactor daily scan task — clean two-phase dispatcher

**Files:**
- Modify: `Jellyfin.Plugin.SponsorBlock/Tasks/SponsorBlockRefreshTask.cs`
- Modify: `Jellyfin.Plugin.SponsorBlock.Tests/Tasks/SponsorBlockRefreshTaskTests.cs`

- [ ] **Step 1: Write the failing tests**

Replace `Jellyfin.Plugin.SponsorBlock.Tests/Tasks/SponsorBlockRefreshTaskTests.cs` with:

```csharp
using System.Runtime.CompilerServices;
using Jellyfin.Plugin.SponsorBlock;
using Jellyfin.Plugin.SponsorBlock.Configuration;
using Jellyfin.Plugin.SponsorBlock.Orchestration;
using Jellyfin.Plugin.SponsorBlock.Scoping;
using Jellyfin.Plugin.SponsorBlock.State;
using Jellyfin.Plugin.SponsorBlock.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Jellyfin.Plugin.SponsorBlock.Tests.Tasks;

public sealed class SponsorBlockRefreshTaskTests
{
	private static readonly DateTimeOffset T0 = new(2026, 5, 25, 6, 0, 0, TimeSpan.Zero);

	private readonly ISponsorBlockStateStore _store = Substitute.For<ISponsorBlockStateStore>();
	private readonly ISponsorBlockApiClient _api = Substitute.For<ISponsorBlockApiClient>();
	private readonly ILibraryScopeService _scope = Substitute.For<ILibraryScopeService>();
	private readonly IMediaSegmentWriter _writer = Substitute.For<IMediaSegmentWriter>();

	[Fact]
	public async Task ExecuteAsync_Phase1_IteratesActiveRows_SkipsDone()
	{
		var config = NewConfig();
		var activeItem = new TestVideo { Id = Guid.NewGuid(), Path = "/archive/abcdefghijk.mp4" };
		var doneItem = new TestVideo { Id = Guid.NewGuid(), Path = "/archive/xyzabcdefgh.mp4" };
		var activeRow = NewRow(activeItem.Id, ItemState.HasData);
		var doneRow = NewRow(doneItem.Id, ItemState.Done);

		var task = MakeTask(config, activeItem, doneItem);

		_store.GetActiveAsync(Arg.Any<CancellationToken>())
			.Returns(Rows(activeRow));
		_store.GetAllItemIdsAsync(Arg.Any<CancellationToken>())
			.Returns(Ids(doneItem.Id));
		_store.GetAsync(activeItem.Id, Arg.Any<CancellationToken>()).Returns(activeRow);
		_store.GetAsync(doneItem.Id, Arg.Any<CancellationToken>()).Returns(doneRow);
		_scope.IsInScope(Arg.Any<BaseItem>()).Returns(true);
		_api.GetSegmentsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
			.Returns([new SponsorBlockSegment { ActionType = "skip", Category = "sponsor", Segment = [10.0, 20.0], UUID = "u" }]);

		await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

		await _api.Received(1).GetSegmentsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task ExecuteAsync_Phase1_OrphanRow_DeletesRowAndSegments()
	{
		var config = NewConfig();
		var orphanId = Guid.NewGuid();
		var orphanRow = NewRow(orphanId, ItemState.HasData);
		var task = MakeTask(config);

		_store.GetActiveAsync(Arg.Any<CancellationToken>())
			.Returns(Rows(orphanRow));
		_store.GetAllItemIdsAsync(Arg.Any<CancellationToken>())
			.Returns(Ids(orphanId));
		_store.GetAsync(orphanId, Arg.Any<CancellationToken>()).Returns(orphanRow);
		// _getItemById returns null for orphan

		await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

		await _writer.Received(1).DeleteOwnedAsync(orphanId, Arg.Any<CancellationToken>());
		await _store.Received(1).DeleteAsync(orphanId, Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task ExecuteAsync_Phase2_DiscoveredUntrackedVideo_CallsOrchestrator()
	{
		var libraryId = Guid.NewGuid();
		var config = NewConfig(libraryId);
		var untracked = new TestVideo
		{
			Id = Guid.NewGuid(),
			Path = "/archive/abcdefghijk.mp4",
		};
		var task = MakeTask(config, untracked);

		_store.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(Rows());
		_store.GetAllItemIdsAsync(Arg.Any<CancellationToken>()).Returns(Ids());
		_store.GetAsync(untracked.Id, Arg.Any<CancellationToken>()).Returns((ItemStateRow?)null);
		_scope.IsInScope(untracked).Returns(true);
		_api.GetSegmentsAsync("abcdefghijk", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
			.Returns([new SponsorBlockSegment { ActionType = "skip", Category = "sponsor", Segment = [10.0, 20.0], UUID = "u" }]);

		await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

		await _store.Received(1).UpsertAsync(
			Arg.Is<ItemStateRow>(row => row.ItemId == untracked.Id && row.State != ItemState.Done),
			Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task ExecuteAsync_Phase2_VideoWithDoneRow_NotDiscovered()
	{
		var libraryId = Guid.NewGuid();
		var config = NewConfig(libraryId);
		var doneVideo = new TestVideo
		{
			Id = Guid.NewGuid(),
			Path = "/archive/abcdefghijk.mp4",
		};
		var task = MakeTask(config, doneVideo);

		_store.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(Rows());
		_store.GetAllItemIdsAsync(Arg.Any<CancellationToken>())
			.Returns(Ids(doneVideo.Id));
		_store.GetAsync(doneVideo.Id, Arg.Any<CancellationToken>())
			.Returns(NewRow(doneVideo.Id, ItemState.Done));
		_scope.IsInScope(doneVideo).Returns(true);

		await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

		await _api.DidNotReceive().GetSegmentsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
	}

	private SponsorBlockRefreshTask MakeTask(PluginConfiguration config, params Video[] scoped) => new(
		_store,
		_ => null,
		MakeOrchestrator(config),
		_writer,
		() => config,
		_ => scoped,
		NullLogger<SponsorBlockRefreshTask>.Instance,
		TestLog.Create());

	private SponsorBlockOrchestrator MakeOrchestrator(PluginConfiguration config) => new(
		_api,
		_store,
		_scope,
		_writer,
		() => config,
		(_, _, _) => "abcdefghijk",
		new FakeTimeProvider(T0),
		NullLogger<SponsorBlockOrchestrator>.Instance,
		TestLog.Create());

	private static PluginConfiguration NewConfig(Guid? libraryId = null) => new()
	{
		EnabledLibraryIds = libraryId is not null ? [libraryId.Value] : [Guid.NewGuid()],
		PendingSanityHours = 48,
		RequestDelayMilliseconds = 0,
		Sponsor = true,
	};

	private static ItemStateRow NewRow(Guid itemId, ItemState state) => new(
		itemId, "abcdefghijk", state, T0, T0, 0, 0, "");

	private static async IAsyncEnumerable<ItemStateRow> Rows(
		params ItemStateRow[] rows)
	{
		foreach (var row in rows)
		{
			await Task.CompletedTask;
			yield return row;
		}
	}

	private static async IAsyncEnumerable<Guid> Ids(
		params Guid[] ids)
	{
		foreach (var id in ids)
		{
			await Task.CompletedTask;
			yield return id;
		}
	}

	private sealed class TestVideo : Video
	{
	}
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~SponsorBlockRefreshTaskTests" --logger "console;verbosity=minimal"`
Expected: FAIL — `SponsorBlockRefreshTask` constructor doesn't match (no `TimeProvider`, `GetAllItemIdsAsync` not used yet).

- [ ] **Step 3: Implement the refactored task**

Replace `Jellyfin.Plugin.SponsorBlock/Tasks/SponsorBlockRefreshTask.cs` with:

```csharp
using Jellyfin.Plugin.SponsorBlock.Configuration;
using Jellyfin.Plugin.SponsorBlock.Orchestration;
using Jellyfin.Plugin.SponsorBlock.State;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SponsorBlock.Tasks;

/// <summary>
/// Daily refresh task: reconciles known items (Phase 1) and discovers untracked scoped videos (Phase 2).
/// All fetch/skip/cooldown/age-gate logic lives in <see cref="SponsorBlockOrchestrator"/>; this task is a pure dispatcher.
/// </summary>
public sealed class SponsorBlockRefreshTask : IScheduledTask
{
	private readonly ISponsorBlockStateStore _store;
	private readonly Func<Guid, BaseItem?> _getItemById;
	private readonly SponsorBlockOrchestrator _orchestrator;
	private readonly IMediaSegmentWriter _writer;
	private readonly Func<PluginConfiguration> _configAccessor;
	private readonly Func<Guid[], IEnumerable<Video>> _scopedVideos;
	private readonly ILogger<SponsorBlockRefreshTask> _logger;
	private readonly SponsorBlockLog _log;

	/// <summary>Initializes the scheduled task.</summary>
	public SponsorBlockRefreshTask(
		ISponsorBlockStateStore store,
		ILibraryManager libraryManager,
		SponsorBlockOrchestrator orchestrator,
		IMediaSegmentWriter writer,
		ILogger<SponsorBlockRefreshTask> logger,
		SponsorBlockLog log)
		: this(
			store,
			libraryManager.GetItemById,
			orchestrator,
			writer,
			() => Plugin.Instance?.Configuration ?? new PluginConfiguration(),
			ids => EnumerateScoped(libraryManager, ids),
			logger,
			log)
	{
	}

	internal SponsorBlockRefreshTask(
		ISponsorBlockStateStore store,
		Func<Guid, BaseItem?> getItemById,
		SponsorBlockOrchestrator orchestrator,
		IMediaSegmentWriter writer,
		Func<PluginConfiguration> configAccessor,
		Func<Guid[], IEnumerable<Video>> scopedVideos,
		ILogger<SponsorBlockRefreshTask> logger,
		SponsorBlockLog log)
	{
		_store = store;
		_getItemById = getItemById;
		_orchestrator = orchestrator;
		_writer = writer;
		_configAccessor = configAccessor;
		_scopedVideos = scopedVideos;
		_logger = logger;
		_log = log;
	}

	/// <inheritdoc />
	public string Name => "SponsorBlock daily refresh";

	/// <inheritdoc />
	public string Key => "SponsorBlockRefresh";

	/// <inheritdoc />
	public string Description => "Refreshes SponsorBlock segments for tracked items and discovers untracked scoped videos.";

	/// <inheritdoc />
	public string Category => "SponsorBlock";

	/// <inheritdoc />
	public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
	{
		var hour = _configAccessor().DailyScanHour;
		return new[]
		{
			new TaskTriggerInfo
			{
				Type = TaskTriggerInfoType.DailyTrigger,
				TimeOfDayTicks = TimeSpan.FromHours(hour).Ticks,
			},
		};
	}

	/// <inheritdoc />
	public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
	{
		var config = _configAccessor();

		// ── Phase 1: Reconcile known items (Pending + HasData + NoData) ──
		var rows = new List<ItemStateRow>();
		await foreach (var row in _store.GetActiveAsync(cancellationToken).ConfigureAwait(false))
		{
			rows.Add(row);
		}

		// Load ALL row IDs for Phase 2 filtering
		var knownIds = new HashSet<Guid>();
		await foreach (var id in _store.GetAllItemIdsAsync(cancellationToken).ConfigureAwait(false))
		{
			knownIds.Add(id);
		}

		var oldVideos = GetUntrackedScopedVideos(config, knownIds);
		var total = rows.Count + oldVideos.Count;
		_logger.LogInformation("SponsorBlock daily refresh: {ActiveRows} active rows, {Untracked} untracked videos — {Total} total", rows.Count, oldVideos.Count, total);
		_log.Information($"Daily refresh: {rows.Count} active rows, {oldVideos.Count} untracked videos — {total} total");
		if (total == 0)
		{
			progress.Report(100);
			return;
		}

		var processed = 0;
		foreach (var row in rows)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var item = _getItemById(row.ItemId);
			if (item is null)
			{
				_logger.LogInformation("Dropping orphan SponsorBlock state for missing item {ItemId}", row.ItemId);
				try
				{
					await _writer.DeleteOwnedAsync(row.ItemId, cancellationToken).ConfigureAwait(false);
				}
				catch (Exception ex)
				{
					_logger.LogWarning(ex, "Failed to delete owned segments for orphan {ItemId}", row.ItemId);
				}

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

			processed++;
			progress.Report(100.0 * processed / total);
			await DelayIfConfiguredAsync(config, cancellationToken).ConfigureAwait(false);
		}

		// ── Phase 2: Discover untracked scoped videos ──
		foreach (var video in oldVideos)
		{
			cancellationToken.ThrowIfCancellationRequested();
			try
			{
				await _orchestrator.ProcessAsync(video, ProcessReason.DailyScan, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Daily refresh failed for untracked item {ItemId}", video.Id);
			}

			processed++;
			progress.Report(100.0 * processed / total);
			await DelayIfConfiguredAsync(config, cancellationToken).ConfigureAwait(false);
		}

		_logger.LogInformation("SponsorBlock daily refresh complete: {Total} items processed", total);
		_log.Information($"Daily refresh complete: {total} items processed");
	}

	private List<Video> GetUntrackedScopedVideos(PluginConfiguration config, HashSet<Guid> knownIds)
	{
		var enabled = config.EnabledLibraryIds;
		if (enabled.Length == 0)
		{
			return [];
		}

		return _scopedVideos(enabled)
			.Where(v => !knownIds.Contains(v.Id))
			.ToList();
	}

	private static async Task DelayIfConfiguredAsync(PluginConfiguration config, CancellationToken cancellationToken)
	{
		if (config.RequestDelayMilliseconds > 0)
		{
			await Task.Delay(config.RequestDelayMilliseconds, cancellationToken).ConfigureAwait(false);
		}
	}

	private static IEnumerable<Video> EnumerateScoped(ILibraryManager libraryManager, Guid[] enabled)
	{
		var query = new InternalItemsQuery
		{
			AncestorIds = enabled,
			Recursive = true,
		};
		foreach (var item in libraryManager.GetItemList(query))
		{
			if (item is Video video)
			{
				yield return video;
			}
		}
	}
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~SponsorBlockRefreshTaskTests" --logger "console;verbosity=minimal"`
Expected: PASS — 4 tests.

- [ ] **Step 5: Commit**

```bash
git add Jellyfin.Plugin.SponsorBlock/Tasks/SponsorBlockRefreshTask.cs Jellyfin.Plugin.SponsorBlock.Tests/Tasks/SponsorBlockRefreshTaskTests.cs
git commit -m "refactor daily scan to two-phase dispatcher, remove timing logic from task"
```

---

### Task 7: Fix remaining compilation errors and run full test suite

**Files:**
- Modify: `Jellyfin.Plugin.SponsorBlock/PluginServiceRegistrator.cs` (remove `TimeProvider` from orchestrator DI if needed)
- Verify all test files compile

- [ ] **Step 1: Check for remaining compilation errors**

Run: `dotnet build`
Expected: May have errors in `PluginServiceRegistrator` (if `SponsorBlockRefreshTask` constructor signature changed — it did: `TimeProvider` removed) or other files that construct `ItemStateRow`.

- [ ] **Step 2: Fix PluginServiceRegistrator if needed**

The `SponsorBlockRefreshTask` production constructor no longer takes `TimeProvider` — it was removed. Check `PluginServiceRegistrator.cs` line 85: `serviceCollection.AddSingleton<IScheduledTask, SponsorBlockRefreshTask>()` — this should still work since DI auto-resolves constructor params. But the task's production constructor now takes `(store, libraryManager, orchestrator, writer, logger, log)` — no `TimeProvider`. Verify no other DI registration passes `TimeProvider` to the task.

Also check `ResetServiceTests.cs` and any other test files that construct `ItemStateRow`:

Run: `dotnet build 2>&1 | grep -i error`

Fix any `ItemStateRow` constructor calls that are missing the new `ConsecutiveUnchanged` and `LastSegmentHash` parameters.

- [ ] **Step 3: Run the full test suite**

Run: `dotnet test --logger "console;verbosity=minimal"`
Expected: PASS — all tests green.

- [ ] **Step 4: Commit any remaining fixes**

```bash
git add -A
git commit -m "fix remaining compilation errors from state row and task constructor changes"
```

---

### Task 8: Update reset service tests for new ItemStateRow constructor

**Files:**
- Modify: `Jellyfin.Plugin.SponsorBlock.Tests/Reset/ResetServiceTests.cs`

- [ ] **Step 1: Check if reset tests compile**

Run: `dotnet test --filter "FullyQualifiedName~ResetServiceTests" --logger "console;verbosity=minimal" 2>&1 | head -20`

If they pass, skip this task. If they fail due to `ItemStateRow` constructor changes, update the `NewRow` helper in `ResetServiceTests.cs` to include the new parameters:

```csharp
private static ItemStateRow NewRow(Guid itemId) => new(
    itemId, "abcdefghijk", ItemState.Pending, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, 0, "");
```

- [ ] **Step 2: Run reset tests to verify**

Run: `dotnet test --filter "FullyQualifiedName~ResetServiceTests" --logger "console;verbosity=minimal"`
Expected: PASS

- [ ] **Step 3: Commit if changed**

```bash
git add Jellyfin.Plugin.SponsorBlock.Tests/Reset/ResetServiceTests.cs
git commit -m "update reset tests for new ItemStateRow constructor"
```

---

### Task 9: Final full test run and lint

- [ ] **Step 1: Run the complete test suite**

Run: `dotnet test --logger "console;verbosity=minimal"`
Expected: PASS — all tests green, no warnings beyond existing.

- [ ] **Step 2: Verify no files with tabs/spaces issues**

Check that all new/modified `.cs` files use tabs (per project convention):

Run: `rg -L '^\t' Jellyfin.Plugin.SponsorBlock/SegmentHasher.cs Jellyfin.Plugin.SponsorBlock/Orchestration/SponsorBlockOrchestrator.cs Jellyfin.Plugin.SponsorBlock/Tasks/SponsorBlockRefreshTask.cs Jellyfin.Plugin.SponsorBlock/State/SqliteSponsorBlockStateStore.cs 2>/dev/null || echo "All files use tabs"`

- [ ] **Step 3: Commit final state**

If there are any remaining uncommitted changes:

```bash
git add -A
git commit -m "final cleanup, all tests passing"
```
