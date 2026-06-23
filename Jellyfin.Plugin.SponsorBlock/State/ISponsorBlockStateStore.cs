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
