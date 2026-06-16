using Jellyfin.Plugin.SponsorBlock.Orchestration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SponsorBlock.Triggers;

/// <summary>
/// Subscribes to <see cref="ILibraryManager.ItemAdded"/> and dispatches Video items to the orchestrator.
/// </summary>
public sealed class ItemAddedHostedService : IHostedService
{
	private readonly ILibraryManager _libraryManager;
	private readonly SponsorBlockOrchestrator _orchestrator;
	private readonly ILogger<ItemAddedHostedService> _logger;

	/// <summary>Initializes the hosted service.</summary>
	/// <param name="libraryManager">Jellyfin library manager.</param>
	/// <param name="orchestrator">Orchestrator instance.</param>
	/// <param name="logger">Logger.</param>
	public ItemAddedHostedService(
		ILibraryManager libraryManager,
		SponsorBlockOrchestrator orchestrator,
		ILogger<ItemAddedHostedService> logger)
	{
		_libraryManager = libraryManager;
		_orchestrator = orchestrator;
		_logger = logger;
	}

	/// <inheritdoc />
	public Task StartAsync(CancellationToken cancellationToken)
	{
		_libraryManager.ItemAdded += OnItemAdded;
		return Task.CompletedTask;
	}

	/// <inheritdoc />
	public Task StopAsync(CancellationToken cancellationToken)
	{
		_libraryManager.ItemAdded -= OnItemAdded;
		return Task.CompletedTask;
	}

	private void OnItemAdded(object? sender, ItemChangeEventArgs e)
	{
		if (e.Item is not Video video)
		{
			return;
		}

		_logger.LogDebug("SponsorBlock trigger: ItemAdded {ItemName} ({ItemId})", video.Name, video.Id);
		_ = Task.Run(async () =>
		{
			try
			{
				await _orchestrator.ProcessAsync(video, ProcessReason.ItemAdded, CancellationToken.None).ConfigureAwait(false);
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "SponsorBlock orchestrator failed for added item {ItemId}", video.Id);
			}
		});
	}
}
