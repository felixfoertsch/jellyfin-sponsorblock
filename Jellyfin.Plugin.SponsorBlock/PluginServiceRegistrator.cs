using Jellyfin.Plugin.SponsorBlock.Orchestration;
using Jellyfin.Plugin.SponsorBlock.Reset;
using Jellyfin.Plugin.SponsorBlock.Scanning;
using Jellyfin.Plugin.SponsorBlock.Scoping;
using Jellyfin.Plugin.SponsorBlock.State;
using Jellyfin.Plugin.SponsorBlock.Tasks;
using Jellyfin.Plugin.SponsorBlock.Triggers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SponsorBlock;

/// <summary>
/// Registers plugin services with Jellyfin's DI container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
	/// <inheritdoc />
	public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
	{
		serviceCollection.AddSingleton<SponsorBlockLog>(sp =>
			new SponsorBlockLog(
				sp.GetRequiredService<IApplicationPaths>(),
				TimeProvider.System));

		serviceCollection.AddSingleton<SponsorBlockApiClient>();
		serviceCollection.AddSingleton<ISponsorBlockApiClient>(sp => sp.GetRequiredService<SponsorBlockApiClient>());

		// Register a stub media-segment provider so Jellyfin recognizes "SponsorBlock" as a known
		// provider and serves stored segments to clients. Actual fetching happens in the orchestrator.
		serviceCollection.AddSingleton<IMediaSegmentProvider, SponsorBlockSegmentProvider>();

		serviceCollection.AddSingleton<ISponsorBlockStateStore>(sp =>
		{
			var paths = sp.GetRequiredService<IApplicationPaths>();
			var dir = Path.Combine(paths.DataPath, Plugin.PluginGuid);
			Directory.CreateDirectory(dir);
			var dbPath = Path.Combine(dir, "sponsorblock-state.db");
			var conn = new SqliteConnection($"Data Source={dbPath};Cache=Shared");
			conn.Open();
			return new SqliteSponsorBlockStateStore(conn);
		});

		serviceCollection.AddSingleton<ILibraryScopeService>(sp =>
			new LibraryScopeService(
				sp.GetRequiredService<ILibraryManager>(),
				() => Plugin.Instance!.Configuration));
		serviceCollection.AddSingleton<IMediaSegmentWriter, MediaSegmentWriter>();
		serviceCollection.AddSingleton<SponsorBlockOrchestrator>(sp =>
			new SponsorBlockOrchestrator(
				sp.GetRequiredService<ISponsorBlockApiClient>(),
				sp.GetRequiredService<ISponsorBlockStateStore>(),
				sp.GetRequiredService<ILibraryScopeService>(),
				sp.GetRequiredService<IMediaSegmentWriter>(),
				() => Plugin.Instance!.Configuration,
				TimeProvider.System,
				sp.GetRequiredService<ILogger<SponsorBlockOrchestrator>>(),
				sp.GetRequiredService<SponsorBlockLog>()));

		serviceCollection.AddSingleton<IResetService>(sp =>
			new ResetService(
				sp.GetRequiredService<ILibraryManager>(),
				sp.GetRequiredService<IMediaSegmentWriter>(),
				sp.GetRequiredService<ISponsorBlockStateStore>(),
				() => Plugin.Instance!.Configuration,
				sp.GetRequiredService<ILogger<ResetService>>()));
		serviceCollection.AddSingleton<IForceScanService>(sp =>
			new ForceScanService(
				sp.GetRequiredService<ILibraryManager>(),
				sp.GetRequiredService<SponsorBlockOrchestrator>(),
				() => Plugin.Instance!.Configuration,
				sp.GetRequiredService<ILogger<ForceScanService>>(),
				sp.GetRequiredService<SponsorBlockLog>()));

		serviceCollection.AddHostedService<ItemAddedHostedService>();
		serviceCollection.AddHostedService<PlaybackStartHostedService>();
		serviceCollection.AddHostedService<ItemRemovedHostedService>();
		serviceCollection.AddSingleton<IScheduledTask, SponsorBlockRefreshTask>();
	}
}
