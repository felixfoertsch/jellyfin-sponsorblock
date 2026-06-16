using Jellyfin.Plugin.SponsorBlock.Configuration;
using Jellyfin.Plugin.SponsorBlock.Orchestration;
using Jellyfin.Plugin.SponsorBlock.State;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SponsorBlock.Tasks;

/// <summary>
/// Daily refresh task: walks all Pending + HasData rows, then discovers old scoped videos that
/// do not have active rows yet. Drops orphan rows whose item no longer exists in the library.
/// </summary>
public sealed class SponsorBlockRefreshTask : IScheduledTask
{
	private readonly ISponsorBlockStateStore _store;
	private readonly Func<Guid, BaseItem?> _getItemById;
	private readonly SponsorBlockOrchestrator _orchestrator;
	private readonly IMediaSegmentWriter _writer;
	private readonly Func<PluginConfiguration> _configAccessor;
	private readonly Func<Guid[], IEnumerable<Video>> _scopedVideos;
	private readonly TimeProvider _time;
	private readonly ILogger<SponsorBlockRefreshTask> _logger;

	/// <summary>Initializes the scheduled task.</summary>
	/// <param name="store">Per-item state store.</param>
	/// <param name="libraryManager">Jellyfin library manager.</param>
	/// <param name="orchestrator">Orchestrator instance.</param>
	/// <param name="writer">Wrapper around Jellyfin media segment manager.</param>
	/// <param name="logger">Logger.</param>
	public SponsorBlockRefreshTask(
		ISponsorBlockStateStore store,
		ILibraryManager libraryManager,
		SponsorBlockOrchestrator orchestrator,
		IMediaSegmentWriter writer,
		ILogger<SponsorBlockRefreshTask> logger)
		: this(
			store,
			libraryManager.GetItemById,
			orchestrator,
			writer,
			() => Plugin.Instance?.Configuration ?? new PluginConfiguration(),
			ids => EnumerateScoped(libraryManager, ids),
			TimeProvider.System,
			logger)
	{
	}

	internal SponsorBlockRefreshTask(
		ISponsorBlockStateStore store,
		Func<Guid, BaseItem?> getItemById,
		SponsorBlockOrchestrator orchestrator,
		IMediaSegmentWriter writer,
		Func<PluginConfiguration> configAccessor,
		Func<Guid[], IEnumerable<Video>> scopedVideos,
		TimeProvider time,
		ILogger<SponsorBlockRefreshTask> logger)
	{
		_store = store;
		_getItemById = getItemById;
		_orchestrator = orchestrator;
		_writer = writer;
		_configAccessor = configAccessor;
		_scopedVideos = scopedVideos;
		_time = time;
		_logger = logger;
	}

	/// <inheritdoc />
	public string Name => "SponsorBlock daily refresh";

	/// <inheritdoc />
	public string Key => "SponsorBlockRefresh";

	/// <inheritdoc />
	public string Description => "Refreshes SponsorBlock segments for tracked items, discovers old scoped items, and runs the 48-hour sanity check.";

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

		var rows = new List<ItemStateRow>();
		await foreach (var row in _store.GetActiveAsync(cancellationToken).ConfigureAwait(false))
		{
			rows.Add(row);
		}

		var activeIds = rows.Select(row => row.ItemId).ToHashSet();
		var oldVideosWithoutActiveRows = GetOldScopedVideosWithoutActiveRows(config, activeIds).ToList();
		var total = rows.Count + oldVideosWithoutActiveRows.Count;
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

		foreach (var video in oldVideosWithoutActiveRows)
		{
			cancellationToken.ThrowIfCancellationRequested();
			try
			{
				await _orchestrator.ProcessAsync(video, ProcessReason.DailyScan, cancellationToken).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Daily refresh failed for old scoped item {ItemId}", video.Id);
			}

			processed++;
			progress.Report(100.0 * processed / total);
			await DelayIfConfiguredAsync(config, cancellationToken).ConfigureAwait(false);
		}
	}

	private IEnumerable<Video> GetOldScopedVideosWithoutActiveRows(PluginConfiguration config, HashSet<Guid> activeIds)
	{
		var enabled = config.EnabledLibraryIds;
		if (enabled.Length == 0)
		{
			yield break;
		}

		var cutoff = _time.GetUtcNow() - TimeSpan.FromHours(config.PendingSanityHours);
		foreach (var video in _scopedVideos(enabled))
		{
			if (activeIds.Contains(video.Id))
			{
				continue;
			}

			if (ToUtc(video.DateCreated) > cutoff)
			{
				continue;
			}

			yield return video;
		}
	}

	private static DateTimeOffset ToUtc(DateTime value)
	{
		if (value.Kind == DateTimeKind.Unspecified)
		{
			return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
		}

		return new DateTimeOffset(value.ToUniversalTime());
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
