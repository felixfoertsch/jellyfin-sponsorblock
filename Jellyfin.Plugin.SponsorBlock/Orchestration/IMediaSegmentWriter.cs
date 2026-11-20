using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Model.MediaSegments;

namespace Jellyfin.Plugin.SponsorBlock.Orchestration;

/// <summary>
/// Plugin-internal abstraction over Jellyfin's <c>IMediaSegmentManager</c>.
/// The orchestrator depends on this; the production implementation forwards to
/// <c>IMediaSegmentManager.CreateSegmentAsync</c> / <c>DeleteSegmentsAsync</c>.
/// </summary>
public interface IMediaSegmentWriter
{
	/// <summary>Display name of the provider — also what <c>IMediaSegmentProvider.Name</c> returns.</summary>
	const string ProviderName = "SponsorBlock";

	/// <summary>
	/// Provider id Jellyfin uses internally as the row's <c>SegmentProviderId</c>.
	/// Mirrors Jellyfin's private <c>MediaSegmentManager.GetProviderId</c>:
	/// <c>MD5(name.ToLowerInvariant() as UTF-16 LE)</c> → <c>new Guid(bytes).ToString("N")</c>.
	/// We must pass this (not the display name) to <c>CreateSegmentAsync</c>, otherwise the
	/// segments don't match any registered provider and the public MediaSegments API hides them.
	/// </summary>
	static readonly string ProviderId = new Guid(
			MD5.HashData(Encoding.Unicode.GetBytes(ProviderName.ToLowerInvariant())))
		.ToString("N", CultureInfo.InvariantCulture);

	/// <summary>
	/// Removes all segments for an item. Note: Jellyfin 10.11's
	/// <c>IMediaSegmentManager.DeleteSegmentsAsync</c> has no per-provider filter, so this
	/// nukes any segment any provider wrote for the item. Acceptable for the SponsorBlock
	/// plugin's scoped YouTube library use case; documented as a caveat.
	/// </summary>
	/// <param name="itemId">The Jellyfin item id.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	Task DeleteOwnedAsync(Guid itemId, CancellationToken cancellationToken);

	/// <summary>Checks whether Jellyfin still has any media segments stored for an item.</summary>
	/// <param name="itemId">The Jellyfin item id.</param>
	/// <returns><c>true</c> when Jellyfin has at least one segment row for the item.</returns>
	bool HasAny(Guid itemId);

	/// <summary>Persists a single SponsorBlock segment.</summary>
	/// <param name="segment">The segment DTO to persist.</param>
	/// <param name="cancellationToken">Cancellation token (advisory; the underlying API does not accept one).</param>
	Task CreateAsync(MediaSegmentDto segment, CancellationToken cancellationToken);
}
