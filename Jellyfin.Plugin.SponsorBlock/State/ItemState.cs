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
