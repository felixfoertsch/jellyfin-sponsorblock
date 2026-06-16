using System.Runtime.CompilerServices;
using Jellyfin.Plugin.SponsorBlock;
using Jellyfin.Plugin.SponsorBlock.Configuration;
using Jellyfin.Plugin.SponsorBlock.Orchestration;
using Jellyfin.Plugin.SponsorBlock.Scoping;
using Jellyfin.Plugin.SponsorBlock.State;
using Jellyfin.Plugin.SponsorBlock.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Jellyfin.Plugin.SponsorBlock.Tests.Tasks;

public sealed class SponsorBlockRefreshTaskTests
{
	private static readonly DateTimeOffset T0 = new(2026, 5, 25, 6, 0, 0, TimeSpan.Zero);

	private readonly ISponsorBlockStateStore _store = Substitute.For<ISponsorBlockStateStore>();
	private readonly ISponsorBlockApiClient _api = Substitute.For<ISponsorBlockApiClient>();
	private readonly ILibraryScopeService _scope = Substitute.For<ILibraryScopeService>();
	private readonly IMediaSegmentWriter _writer = Substitute.For<IMediaSegmentWriter>();
	private readonly FakeTimeProvider _time = new(T0);

	[Fact]
	public async Task ExecuteAsync_ProcessesUntrackedScopedVideosOlderThanSanityWindow()
	{
		var libraryId = Guid.NewGuid();
		var item = new TestVideo
		{
			Id = Guid.NewGuid(),
			Path = "/archive/abcdefghijk.mp4",
			DateCreated = T0.AddHours(-49).UtcDateTime,
		};
		var config = new PluginConfiguration
		{
			EnabledLibraryIds = [libraryId],
			PendingSanityHours = 48,
			RequestDelayMilliseconds = 0,
			Sponsor = true,
		};
		var task = MakeTask(config, item);

		_store.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(Rows());
		_store.GetAsync(item.Id, Arg.Any<CancellationToken>()).Returns((ItemStateRow?)null);
		_scope.IsInScope(item).Returns(true);
		_api.GetSegmentsAsync("abcdefghijk", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
			.Returns([new SponsorBlockSegment { ActionType = "skip", Category = "sponsor", Segment = [10.0, 20.0], UUID = "uuid" }]);

		await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

		await _writer.Received(1).CreateAsync(Arg.Any<MediaSegmentDto>(), Arg.Any<CancellationToken>());
		await _store.Received(1).UpsertAsync(
			Arg.Is<ItemStateRow>(row => row.ItemId == item.Id && row.State == ItemState.HasData && row.SegmentCount == 1),
			Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task ExecuteAsync_UsesItemAgeForUntrackedOldVideoSanityCheck()
	{
		var libraryId = Guid.NewGuid();
		var item = new TestVideo
		{
			Id = Guid.NewGuid(),
			Path = "/archive/abcdefghijk.mp4",
			DateCreated = T0.AddHours(-49).UtcDateTime,
		};
		var config = new PluginConfiguration
		{
			EnabledLibraryIds = [libraryId],
			PendingSanityHours = 48,
			RequestDelayMilliseconds = 0,
			Sponsor = true,
		};
		var task = MakeTask(config, item);

		_store.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(Rows());
		_store.GetAsync(item.Id, Arg.Any<CancellationToken>()).Returns((ItemStateRow?)null);
		_scope.IsInScope(item).Returns(true);
		_api.GetSegmentsAsync("abcdefghijk", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
			.Returns([]);

		await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

		await _store.Received(1).UpsertAsync(
			Arg.Is<ItemStateRow>(row => row.ItemId == item.Id && row.State == ItemState.NoData && row.SegmentCount == 0),
			Arg.Any<CancellationToken>());
	}

	private SponsorBlockRefreshTask MakeTask(PluginConfiguration config, params Video[] scoped) => new(
		_store,
		_ => null,
		MakeOrchestrator(config),
		_writer,
		() => config,
		_ => scoped,
		_time,
		NullLogger<SponsorBlockRefreshTask>.Instance,
		TestLog.Create());

	private SponsorBlockOrchestrator MakeOrchestrator(PluginConfiguration config) => new(
		_api,
		_store,
		_scope,
		_writer,
		() => config,
		(_, _, _) => "abcdefghijk",
		_time,
		NullLogger<SponsorBlockOrchestrator>.Instance,
		TestLog.Create());

	private static async IAsyncEnumerable<ItemStateRow> Rows(
		[EnumeratorCancellation] CancellationToken cancellationToken = default)
	{
		await Task.CompletedTask;
		yield break;
	}

	private sealed class TestVideo : Video
	{
	}
}
