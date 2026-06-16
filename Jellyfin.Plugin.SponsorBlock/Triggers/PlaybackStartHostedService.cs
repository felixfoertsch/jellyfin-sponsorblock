using Jellyfin.Plugin.SponsorBlock.Orchestration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SponsorBlock.Triggers;

/// <summary>
/// Subscribes to <see cref="ISessionManager.PlaybackStart"/> and dispatches the played item to the orchestrator.
/// </summary>
public sealed class PlaybackStartHostedService : IHostedService
{
	private readonly ISessionManager _sessionManager;
	private readonly SponsorBlockOrchestrator _orchestrator;
	private readonly ILogger<PlaybackStartHostedService> _logger;

	/// <summary>Initializes the hosted service.</summary>
	/// <param name="sessionManager">Jellyfin session manager.</param>
	/// <param name="orchestrator">Orchestrator instance.</param>
	/// <param name="logger">Logger.</param>
	public PlaybackStartHostedService(
		ISessionManager sessionManager,
		SponsorBlockOrchestrator orchestrator,
		ILogger<PlaybackStartHostedService> logger)
	{
		_sessionManager = sessionManager;
		_orchestrator = orchestrator;
		_logger = logger;
	}

	/// <inheritdoc />
	public Task StartAsync(CancellationToken cancellationToken)
	{
		_sessionManager.PlaybackStart += OnPlaybackStart;
		return Task.CompletedTask;
	}

	/// <inheritdoc />
	public Task StopAsync(CancellationToken cancellationToken)
	{
		_sessionManager.PlaybackStart -= OnPlaybackStart;
		return Task.CompletedTask;
	}

	private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
	{
		if (e.Item is not Video video)
		{
			_logger.LogDebug("SponsorBlock trigger: PlaybackStart for non-Video item {ItemId} ({Type}) — skipping", e.Item.Id, e.Item.GetType().Name);
			return;
		}

		try
		{
			_orchestrator.ProcessAsync(video, ProcessReason.PlaybackStart, CancellationToken.None)
				.GetAwaiter()
				.GetResult();
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "SponsorBlock orchestrator failed for played item {ItemId}", video.Id);
		}
	}
}
