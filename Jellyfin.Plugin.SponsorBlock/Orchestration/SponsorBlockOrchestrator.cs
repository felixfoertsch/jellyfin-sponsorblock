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
	private readonly SponsorBlockLog _log;
	private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _itemLocks = new();

	/// <summary>Production constructor (uses static <see cref="YouTubeIdExtractor"/>).</summary>
	public SponsorBlockOrchestrator(
		ISponsorBlockApiClient api,
		ISponsorBlockStateStore store,
		ILibraryScopeService scope,
		IMediaSegmentWriter writer,
		Func<PluginConfiguration> config,
		TimeProvider time,
		ILogger<SponsorBlockOrchestrator> logger,
		SponsorBlockLog log)
		: this(api, store, scope, writer, config,
			(filename, mode, pattern) => YouTubeIdExtractor.Extract(filename, mode, pattern),
			time, logger, log)
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
		ILogger<SponsorBlockOrchestrator> logger,
		SponsorBlockLog log)
	{
		_api = api;
		_store = store;
		_scope = scope;
		_writer = writer;
		_config = config;
		_extractVideoId = extractVideoId;
		_time = time;
		_logger = logger;
		_log = log;
	}

	/// <summary>
	/// Process one item under the given trigger. Implements the decision table from the spec.
	/// Swallows transient HTTP failures (logs warning, leaves state untouched).
	/// </summary>
	public async Task ProcessAsync(BaseItem item, ProcessReason reason, CancellationToken cancellationToken)
	{
		if (!_scope.IsInScope(item))
		{
			_logger.LogDebug("SponsorBlock: skipping {ItemName} ({ItemId}) — not in configured library scope", item.Name, item.Id);
			return;
		}

		var path = item.Path;
		if (string.IsNullOrEmpty(path))
		{
			_logger.LogDebug("SponsorBlock: skipping {ItemId} — no filesystem path", item.Id);
			return;
		}

		var config = _config();
		var filename = Path.GetFileName(path);
		var videoId = _extractVideoId(filename, config.FileMatchingMode, config.CustomRegexPattern);
		if (videoId is null)
		{
			_logger.LogDebug("SponsorBlock: skipping {ItemName} ({ItemId}) — could not extract YouTube ID from filename \"{Filename}\"", item.Name, item.Id, filename);
			return;
		}

		var sem = _itemLocks.GetOrAdd(item.Id, _ => new SemaphoreSlim(1, 1));
		await sem.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			await ProcessLockedAsync(item.Id, item.PremiereDate, item.DateCreated, videoId, reason, config, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			sem.Release();
		}
	}

	private async Task ProcessLockedAsync(
		Guid itemId,
		DateTime? premiereDate,
		DateTime itemDateCreated,
		string videoId,
		ProcessReason reason,
		PluginConfiguration config,
		CancellationToken ct)
	{
		var existing = await _store.GetAsync(itemId, ct).ConfigureAwait(false);
		var now = _time.GetUtcNow();

		if (existing is { State: ItemState.Done })
		{
			_logger.LogDebug("SponsorBlock {VideoId}: Done — skipping {Reason}", videoId, reason);
			return;
		}

		if (existing is { State: ItemState.NoData })
		{
			var ageHours = (now - existing.LastFetchAt).TotalHours;
			if (ageHours < config.PendingSanityHours)
			{
				_logger.LogDebug("SponsorBlock {VideoId}: NoData cooldown ({Age:F1}h of {Window}h) — skipping {Reason}", videoId, ageHours, config.PendingSanityHours, reason);
				return;
			}

			_logger.LogInformation("SponsorBlock {VideoId}: NoData cooldown elapsed ({Age:F1}h) — rechecking ({Reason})", videoId, ageHours, reason);
			_log.Information($"SponsorBlock {videoId}: NoData cooldown elapsed ({ageHours:F1}h) — rechecking ({reason})");
		}

		if (existing is { State: ItemState.HasData } && reason == ProcessReason.PlaybackStart && _writer.HasAny(itemId))
		{
			_logger.LogDebug("SponsorBlock {VideoId}: segments already exist in Jellyfin — skipping {Reason}", videoId, reason);
			return;
		}

		if (existing is { State: ItemState.Pending } && reason == ProcessReason.PlaybackStart)
		{
			var ageHours = (now - existing.FirstSeenAt).TotalHours;
			if (ageHours < config.PlaybackPollHours)
			{
				_logger.LogDebug("SponsorBlock {VideoId}: Pending poll window ({Age:F1}h of {Window}h) — skipping {Reason}", videoId, ageHours, config.PlaybackPollHours, reason);
				return;
			}

			_logger.LogInformation("SponsorBlock {VideoId}: Pending poll window elapsed ({Age:F1}h) — rechecking ({Reason})", videoId, ageHours, reason);
			_log.Information($"SponsorBlock {videoId}: Pending poll window elapsed ({ageHours:F1}h) — rechecking ({reason})");
		}

		IReadOnlyList<SponsorBlockSegment> apiSegments;
		try
		{
			var categories = CategoryMapping.GetEnabledCategories(config.GetCategorySettings());
			if (categories.Count == 0)
			{
				_logger.LogWarning("SponsorBlock {VideoId}: all categories disabled — skipping {Reason}", videoId, reason);
				_log.Warning($"SponsorBlock {videoId}: all categories disabled — skipping {reason}");
				return;
			}

			apiSegments = await _api.GetSegmentsAsync(videoId, categories, ct).ConfigureAwait(false);
		}
		catch (HttpRequestException ex)
		{
			_logger.LogWarning(ex, "SponsorBlock fetch failure for {VideoId}; state unchanged", videoId);
			_log.Warning($"SponsorBlock fetch failure for {videoId}: {ex.Message}");
			return;
		}
		catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
		{
			_logger.LogWarning(ex, "SponsorBlock fetch timeout for {VideoId}; state unchanged", videoId);
			_log.Warning($"SponsorBlock fetch timeout for {videoId}");
			return;
		}

		var firstSeen = existing?.FirstSeenAt ?? GetInitialFirstSeen(reason, itemDateCreated, now);
		var hasSegments = apiSegments.Any(s => s.ActionType == "skip");
		var hash = SegmentHasher.Hash(apiSegments);
		var unchanged = existing is not null && existing.LastSegmentHash == hash;
		var consecutive = unchanged ? existing!.ConsecutiveUnchanged + 1 : 0;

		var premiereOffset = premiereDate is not null
			? now - ToUtc(premiereDate.Value)
			: (TimeSpan?)null;
		var isMature = premiereOffset is not null && premiereOffset.Value.TotalDays >= config.ReleaseAgeCutoffDays;

		if (isMature)
		{
			if (hasSegments)
			{
				var dtos = SegmentMapper.Map(apiSegments, itemId);
				await _writer.DeleteOwnedAsync(itemId, ct).ConfigureAwait(false);
				foreach (var dto in dtos)
				{
					await _writer.CreateAsync(dto, ct).ConfigureAwait(false);
				}
			}
			else
			{
				await _writer.DeleteOwnedAsync(itemId, ct).ConfigureAwait(false);
			}

			var skipCount = apiSegments.Count(s => s.ActionType == "skip");
			await _store.UpsertAsync(
				new ItemStateRow(itemId, videoId, ItemState.Done, firstSeen, now, hasSegments ? skipCount : 0, 0, hash),
				ct).ConfigureAwait(false);
			_logger.LogInformation("SponsorBlock {VideoId}: age gate → Done ({Reason})", videoId, reason);
			_log.Information($"SponsorBlock {videoId}: age gate → Done ({reason})");
			return;
		}

		if (hasSegments)
		{
			var dtos = SegmentMapper.Map(apiSegments, itemId);
			await _writer.DeleteOwnedAsync(itemId, ct).ConfigureAwait(false);
			foreach (var dto in dtos)
			{
				await _writer.CreateAsync(dto, ct).ConfigureAwait(false);
			}

			var newState = consecutive >= config.ConsecutiveUnchangedThreshold ? ItemState.Done : ItemState.HasData;
			await _store.UpsertAsync(
				new ItemStateRow(itemId, videoId, newState, firstSeen, now, dtos.Count, consecutive, hash),
				ct).ConfigureAwait(false);
			_logger.LogInformation("SponsorBlock {VideoId}: wrote {Count} segments → {State} ({Reason})", videoId, dtos.Count, newState, reason);
			_log.Information($"SponsorBlock {videoId}: wrote {dtos.Count} segments → {newState} ({reason})");
			return;
		}

		var sanityElapsed = (now - firstSeen).TotalHours >= config.PendingSanityHours;
		var noDataState = sanityElapsed ? ItemState.NoData : ItemState.Pending;
		var finalState = consecutive >= config.ConsecutiveUnchangedThreshold ? ItemState.Done : noDataState;

		await _writer.DeleteOwnedAsync(itemId, ct).ConfigureAwait(false);
		await _store.UpsertAsync(
			new ItemStateRow(itemId, videoId, finalState, firstSeen, now, 0, consecutive, hash),
			ct).ConfigureAwait(false);
		_logger.LogInformation("SponsorBlock {VideoId}: no segments found → {State} ({Reason})", videoId, finalState, reason);
		_log.Information($"SponsorBlock {videoId}: no segments found → {finalState} ({reason})");
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
