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
