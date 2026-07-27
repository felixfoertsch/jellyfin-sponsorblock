using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model;
using MediaBrowser.Model.MediaSegments;

namespace Jellyfin.Plugin.SponsorBlock;

/// <summary>
/// Stub <see cref="IMediaSegmentProvider"/>. Exists solely so Jellyfin recognizes "SponsorBlock"
/// as a registered provider and serves stored segments to clients via the MediaSegments API.
/// All actual segment fetching is done by <see cref="Orchestration.SponsorBlockOrchestrator"/>
/// from event triggers — the dedicated Media Segment Scan task should not call into us.
/// </summary>
public sealed class SponsorBlockSegmentProvider : IMediaSegmentProvider
{
	/// <inheritdoc />
	public string Name => "SponsorBlock";

	/// <inheritdoc />
	public ValueTask<bool> Supports(BaseItem item) => ValueTask.FromResult(false);

	/// <inheritdoc />
	public Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(MediaSegmentGenerationRequest request, CancellationToken cancellationToken)
		=> Task.FromResult<IReadOnlyList<MediaSegmentDto>>(Array.Empty<MediaSegmentDto>());

	/// <inheritdoc />
	public Task CleanupExtractedData(Guid itemId, CancellationToken cancellationToken) => Task.CompletedTask;
}
