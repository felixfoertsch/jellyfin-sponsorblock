using Jellyfin.Plugin.SponsorBlock;
using Jellyfin.Plugin.SponsorBlock.Configuration;
using Jellyfin.Plugin.SponsorBlock.Orchestration;
using Jellyfin.Plugin.SponsorBlock.Scanning;
using Jellyfin.Plugin.SponsorBlock.Scoping;
using Jellyfin.Plugin.SponsorBlock.State;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Jellyfin.Plugin.SponsorBlock.Tests.Scanning;

public sealed class ForceScanServiceTests
{
	private readonly ISponsorBlockApiClient _api = Substitute.For<ISponsorBlockApiClient>();
	private readonly ISponsorBlockStateStore _store = Substitute.For<ISponsorBlockStateStore>();
	private readonly ILibraryScopeService _scope = Substitute.For<ILibraryScopeService>();
	private readonly IMediaSegmentWriter _writer = Substitute.For<IMediaSegmentWriter>();

	[Fact]
	public async Task ScanAllAsync_ProcessesUntrackedVideosFromConfiguredLibraries()
	{
		var libraryId = Guid.NewGuid();
		var item = new TestVideo { Id = Guid.NewGuid(), Path = "/archive/abcdefghijk.mp4" };
		var config = new PluginConfiguration
		{
			EnabledLibraryIds = [libraryId],
			RequestDelayMilliseconds = 0,
			Sponsor = true,
		};
		var service = MakeService(config, item);

		_store.GetAsync(item.Id, Arg.Any<CancellationToken>()).Returns((ItemStateRow?)null);
		_scope.IsInScope(item).Returns(true);
		_api.GetSegmentsAsync("abcdefghijk", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
			.Returns([new SponsorBlockSegment { ActionType = "skip", Category = "sponsor", Segment = [10.0, 20.0], UUID = "uuid" }]);

		var count = await service.ScanAllAsync(CancellationToken.None);

		Assert.Equal(1, count);
		await _writer.Received(1).CreateAsync(Arg.Any<MediaSegmentDto>(), Arg.Any<CancellationToken>());
		await _store.Received(1).UpsertAsync(
			Arg.Is<ItemStateRow>(row => row.ItemId == item.Id && row.State == ItemState.HasData && row.SegmentCount == 1),
			Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task ScanAllAsync_ReturnsZero_WhenNoLibrariesEnabled()
	{
		var service = MakeService(new PluginConfiguration { EnabledLibraryIds = [] }, new TestVideo());

		var count = await service.ScanAllAsync(CancellationToken.None);

		Assert.Equal(0, count);
		await _api.DidNotReceiveWithAnyArgs().GetSegmentsAsync(default!, default!, default);
	}

	[Fact]
	public void StartScanAll_ReturnsAlreadyRunning_WhenScanIsActive()
	{
		var item = new TestVideo { Id = Guid.NewGuid(), Path = "/archive/abcdefghijk.mp4" };
		var config = new PluginConfiguration
		{
			EnabledLibraryIds = [Guid.NewGuid()],
			RequestDelayMilliseconds = 0,
			Sponsor = true,
		};
		var pending = new TaskCompletionSource<IReadOnlyList<SponsorBlockSegment>>();
		var service = MakeService(config, item);
		_scope.IsInScope(item).Returns(true);
		_api.GetSegmentsAsync("abcdefghijk", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
			.Returns(pending.Task);

		var first = service.StartScanAll();
		var second = service.StartScanAll();

		Assert.True(first.Started);
		Assert.False(second.Started);
		Assert.True(second.AlreadyRunning);
		pending.SetResult([]);
	}

	private ForceScanService MakeService(PluginConfiguration config, params Video[] scoped) => new(
		MakeOrchestrator(config),
		() => config,
		_ => scoped,
		NullLogger<ForceScanService>.Instance,
		TestLog.Create());

	private SponsorBlockOrchestrator MakeOrchestrator(PluginConfiguration config) => new(
		_api,
		_store,
		_scope,
		_writer,
		() => config,
		(_, _, _) => "abcdefghijk",
		TimeProvider.System,
		NullLogger<SponsorBlockOrchestrator>.Instance,
		TestLog.Create());

	private sealed class TestVideo : Video
	{
	}
}
