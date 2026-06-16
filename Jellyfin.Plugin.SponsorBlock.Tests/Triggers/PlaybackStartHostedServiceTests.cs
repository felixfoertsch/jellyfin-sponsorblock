using Jellyfin.Plugin.SponsorBlock;
using Jellyfin.Plugin.SponsorBlock.Configuration;
using Jellyfin.Plugin.SponsorBlock.Orchestration;
using Jellyfin.Plugin.SponsorBlock.Scoping;
using Jellyfin.Plugin.SponsorBlock.State;
using Jellyfin.Plugin.SponsorBlock.Triggers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Jellyfin.Plugin.SponsorBlock.Tests.Triggers;

public class PlaybackStartHostedServiceTests
{
	[Fact]
	public async Task PlaybackStart_WaitsForSponsorBlockProcessingBeforeReturning()
	{
		var item = new Video
		{
			Id = Guid.NewGuid(),
			Path = "/library/yt/abcdefghijk.mp4",
		};
		var apiResult = new TaskCompletionSource<IReadOnlyList<SponsorBlockSegment>>(TaskCreationOptions.RunContinuationsAsynchronously);
		var sessionManager = Substitute.For<ISessionManager>();
		var api = Substitute.For<ISponsorBlockApiClient>();
		var store = Substitute.For<ISponsorBlockStateStore>();
		var scope = Substitute.For<ILibraryScopeService>();
		var writer = Substitute.For<IMediaSegmentWriter>();
		var config = new PluginConfiguration
		{
			EnabledLibraryIds = [Guid.NewGuid()],
			Sponsor = true,
		};
		var orchestrator = new SponsorBlockOrchestrator(
			api,
			store,
			scope,
			writer,
			() => config,
			(_, _, _) => "abcdefghijk",
			TimeProvider.System,
			NullLogger<SponsorBlockOrchestrator>.Instance,
			TestLog.Create());

		scope.IsInScope(item).Returns(true);
		store.GetAsync(item.Id, Arg.Any<CancellationToken>()).Returns((ItemStateRow?)null);
		api.GetSegmentsAsync("abcdefghijk", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
			.Returns(apiResult.Task);

		var service = new PlaybackStartHostedService(
			sessionManager,
			orchestrator,
			NullLogger<PlaybackStartHostedService>.Instance);
		await service.StartAsync(CancellationToken.None);

		var playbackStartReturned = Task.Run(() =>
		{
			sessionManager.PlaybackStart += Raise.Event<EventHandler<PlaybackProgressEventArgs>>(
				this,
				new PlaybackProgressEventArgs { Item = item });
		});

		await Task.Delay(50);
		Assert.False(playbackStartReturned.IsCompleted);

		apiResult.SetResult([new SponsorBlockSegment
		{
			Category = "sponsor",
			ActionType = "skip",
			Segment = [10.0, 20.0],
			UUID = "u",
		}]);

		await playbackStartReturned;
		await writer.Received(1).CreateAsync(Arg.Any<MediaBrowser.Model.MediaSegments.MediaSegmentDto>(), Arg.Any<CancellationToken>());
	}
}
