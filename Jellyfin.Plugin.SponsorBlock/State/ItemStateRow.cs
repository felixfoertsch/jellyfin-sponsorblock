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
