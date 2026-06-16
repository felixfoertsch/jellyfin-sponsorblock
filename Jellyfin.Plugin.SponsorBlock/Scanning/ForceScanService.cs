using Jellyfin.Plugin.SponsorBlock.Configuration;
using Jellyfin.Plugin.SponsorBlock.Orchestration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SponsorBlock.Scanning;

/// <summary>
/// Runs one-shot full-library SponsorBlock scans for archive backfills.
/// </summary>
public sealed class ForceScanService : IForceScanService
{
	private readonly SponsorBlockOrchestrator _orchestrator;
	private readonly Func<PluginConfiguration> _configAccessor;
	private readonly Func<Guid[], IEnumerable<Video>> _scopedVideos;
	private readonly ILogger<ForceScanService> _logger;
	private int _running;
	private DateTimeOffset? _lastStartedAt;
	private DateTimeOffset? _lastCompletedAt;
	private int _lastItemsProcessed;

	/// <summary>
	/// Production constructor — enumerates videos via <see cref="ILibraryManager.GetItemList(InternalItemsQuery)"/>.
	/// </summary>
	/// <param name="libraryManager">Jellyfin library manager.</param>
	/// <param name="orchestrator">SponsorBlock processor.</param>
	/// <param name="configAccessor">Returns the current plugin configuration.</param>
	/// <param name="logger">Logger.</param>
	public ForceScanService(
		ILibraryManager libraryManager,
		SponsorBlockOrchestrator orchestrator,
		Func<PluginConfiguration> configAccessor,
		ILogger<ForceScanService> logger)
		: this(orchestrator, configAccessor, ids => EnumerateScoped(libraryManager, ids), logger)
	{
	}

	/// <summary>
	/// Test constructor — accepts an injected video enumerator so tests don't need a real ILibraryManager.
	/// </summary>
	internal ForceScanService(
		SponsorBlockOrchestrator orchestrator,
		Func<PluginConfiguration> configAccessor,
		Func<Guid[], IEnumerable<Video>> scopedVideos,
		ILogger<ForceScanService> logger)
	{
		_orchestrator = orchestrator;
		_configAccessor = configAccessor;
		_scopedVideos = scopedVideos;
		_logger = logger;
	}

	/// <inheritdoc />
	public ForceScanStartResponse StartScanAll()
	{
		if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
		{
			return new ForceScanStartResponse(false, true, GetStatus());
		}

		_lastStartedAt = DateTimeOffset.UtcNow;
		_lastCompletedAt = null;
		_lastItemsProcessed = 0;

		_ = Task.Run(async () =>
		{
			try
			{
				_lastItemsProcessed = await ScanAllAsync(CancellationToken.None).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "SponsorBlock force scan failed.");
			}
			finally
			{
				_lastCompletedAt = DateTimeOffset.UtcNow;
				Volatile.Write(ref _running, 0);
			}
		});

		return new ForceScanStartResponse(true, false, GetStatus());
	}

	/// <inheritdoc />
	public ForceScanStatus GetStatus()
	{
		return new ForceScanStatus(
			Volatile.Read(ref _running) != 0,
			_lastStartedAt,
			_lastCompletedAt,
			_lastItemsProcessed);
	}

	internal async Task<int> ScanAllAsync(CancellationToken cancellationToken)
	{
		var config = _configAccessor();
		var enabled = config.EnabledLibraryIds;
		if (enabled.Length == 0)
		{
			_logger.LogInformation("SponsorBlock force scan requested but no libraries are enabled — nothing to do.");
			return 0;
		}

		var count = 0;
		var videos = _scopedVideos(enabled).ToList();
		_logger.LogInformation("SponsorBlock force scan starting: {Count} items in scoped libraries", videos.Count);
		foreach (var video in videos)
		{
			cancellationToken.ThrowIfCancellationRequested();
			try
			{
				await _orchestrator.ProcessAsync(video, ProcessReason.DailyScan, cancellationToken).ConfigureAwait(false);
				count++;
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "SponsorBlock force scan failed for item {ItemId}", video.Id);
			}

			if (config.RequestDelayMilliseconds > 0)
			{
				await Task.Delay(config.RequestDelayMilliseconds, cancellationToken).ConfigureAwait(false);
			}
		}

		_logger.LogInformation("SponsorBlock force scan complete: processed {Count} items in scoped libraries.", count);
		return count;
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
