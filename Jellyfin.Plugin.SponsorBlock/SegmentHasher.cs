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
