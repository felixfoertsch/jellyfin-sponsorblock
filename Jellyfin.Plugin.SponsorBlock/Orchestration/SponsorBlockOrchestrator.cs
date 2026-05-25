using System.Collections.Concurrent;
using Jellyfin.Plugin.SponsorBlock.Configuration;
using Jellyfin.Plugin.SponsorBlock.Scoping;
using Jellyfin.Plugin.SponsorBlock.State;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SponsorBlock.Orchestration;

/// <summary>
/// Single writer for SponsorBlock state and segments. All triggers funnel through ProcessAsync.
/// </summary>
public sealed class SponsorBlockOrchestrator
{
	private readonly ISponsorBlockApiClient _api;
	private readonly ISponsorBlockStateStore _store;
	private readonly ILibraryScopeService _scope;
	private readonly IMediaSegmentWriter _writer;
	private readonly Func<PluginConfiguration> _config;
	private readonly Func<string, FileMatchingMode, string?, string?> _extractVideoId;
	private readonly TimeProvider _time;
	private readonly ILogger<SponsorBlockOrchestrator> _logger;
	private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _itemLocks = new();

	/// <summary>Production constructor (uses static <see cref="YouTubeIdExtractor"/>).</summary>
	/// <param name="api">SponsorBlock API client.</param>
	/// <param name="store">Per-item state store.</param>
	/// <param name="scope">Library scope policy.</param>
	/// <param name="writer">Wrapper around Jellyfin media segment manager.</param>
	/// <param name="config">Returns the current plugin configuration.</param>
	/// <param name="time">Time provider (use <see cref="TimeProvider.System"/> in production).</param>
	/// <param name="logger">Logger.</param>
	public SponsorBlockOrchestrator(
		ISponsorBlockApiClient api,
		ISponsorBlockStateStore store,
		ILibraryScopeService scope,
		IMediaSegmentWriter writer,
		Func<PluginConfiguration> config,
		TimeProvider time,
		ILogger<SponsorBlockOrchestrator> logger)
		: this(api, store, scope, writer, config,
			(filename, mode, pattern) => YouTubeIdExtractor.Extract(filename, mode, pattern),
			time, logger)
	{
	}

	internal SponsorBlockOrchestrator(
		ISponsorBlockApiClient api,
		ISponsorBlockStateStore store,
		ILibraryScopeService scope,
		IMediaSegmentWriter writer,
		Func<PluginConfiguration> config,
		Func<string, FileMatchingMode, string?, string?> extractVideoId,
		TimeProvider time,
		ILogger<SponsorBlockOrchestrator> logger)
	{
		_api = api;
		_store = store;
		_scope = scope;
		_writer = writer;
		_config = config;
		_extractVideoId = extractVideoId;
		_time = time;
		_logger = logger;
	}

	/// <summary>
	/// Process one item under the given trigger. Implements the decision table from the spec.
	/// Swallows transient HTTP failures (logs warning, leaves state untouched).
	/// </summary>
	/// <param name="item">Item to process.</param>
	/// <param name="reason">Why this call is being made.</param>
	/// <param name="cancellationToken">Cancellation token.</param>
	public async Task ProcessAsync(BaseItem item, ProcessReason reason, CancellationToken cancellationToken)
	{
		if (!_scope.IsInScope(item))
		{
			return;
		}

		var path = item.Path;
		if (string.IsNullOrEmpty(path))
		{
			return;
		}

		var config = _config();
		var videoId = _extractVideoId(Path.GetFileName(path), config.FileMatchingMode, config.CustomRegexPattern);
		if (videoId is null)
		{
			return;
		}

		var sem = _itemLocks.GetOrAdd(item.Id, _ => new SemaphoreSlim(1, 1));
		await sem.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await ProcessLockedAsync(item.Id, item.DateCreated, videoId, reason, config, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			sem.Release();
		}
	}

	private async Task ProcessLockedAsync(
		Guid itemId,
		DateTime itemDateCreated,
		string videoId,
		ProcessReason reason,
		PluginConfiguration config,
		CancellationToken ct)
	{
		var existing = await _store.GetAsync(itemId, ct).ConfigureAwait(false);
		var now = _time.GetUtcNow();

		if (existing is { State: ItemState.NoData })
		{
			var ageHours = (now - existing.LastFetchAt).TotalHours;
			if (ageHours < config.PendingSanityHours)
			{
				return;
			}
		}

		if (existing is { State: ItemState.HasData } && reason == ProcessReason.PlaybackStart && _writer.HasAny(itemId))
		{
			return;
		}

		if (existing is { State: ItemState.Pending } && reason == ProcessReason.PlaybackStart)
		{
			var ageHours = (now - existing.FirstSeenAt).TotalHours;
			if (ageHours < config.PlaybackPollHours)
			{
				return;
			}
		}

		IReadOnlyList<SponsorBlockSegment> apiSegments;
		try
		{
			var categories = CategoryMapping.GetEnabledCategories(config.GetCategorySettings());
			if (categories.Count == 0)
			{
				return;
			}

			apiSegments = await _api.GetSegmentsAsync(videoId, categories, ct).ConfigureAwait(false);
		}
		catch (HttpRequestException ex)
		{
			_logger.LogWarning(ex, "Transient SponsorBlock fetch failure for {VideoId}; state unchanged", videoId);
			return;
		}
		catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
		{
			_logger.LogWarning(ex, "SponsorBlock fetch timeout for {VideoId}; state unchanged", videoId);
			return;
		}

		var firstSeen = existing?.FirstSeenAt ?? GetInitialFirstSeen(reason, itemDateCreated, now);
		var hasSegments = apiSegments.Any(s => s.ActionType == "skip");

		if (hasSegments)
		{
			var dtos = SegmentMapper.Map(apiSegments, itemId);
			await _writer.DeleteOwnedAsync(itemId, ct).ConfigureAwait(false);
			foreach (var dto in dtos)
			{
				await _writer.CreateAsync(dto, ct).ConfigureAwait(false);
			}

			await _store.UpsertAsync(
				new ItemStateRow(itemId, videoId, ItemState.HasData, firstSeen, now, dtos.Count),
				ct).ConfigureAwait(false);
			return;
		}

		var sanityElapsed = (now - firstSeen).TotalHours >= config.PendingSanityHours;
		var newState = sanityElapsed ? ItemState.NoData : ItemState.Pending;

		await _store.UpsertAsync(
			new ItemStateRow(itemId, videoId, newState, firstSeen, now, 0),
			ct).ConfigureAwait(false);
	}

	private static DateTimeOffset GetInitialFirstSeen(ProcessReason reason, DateTime itemDateCreated, DateTimeOffset now)
	{
		if (reason != ProcessReason.DailyScan)
		{
			return now;
		}

		var created = ToUtc(itemDateCreated);
		return created > now ? now : created;
	}

	private static DateTimeOffset ToUtc(DateTime value)
	{
		if (value.Kind == DateTimeKind.Unspecified)
		{
			return new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
		}

		return new DateTimeOffset(value.ToUniversalTime());
	}
}
