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
