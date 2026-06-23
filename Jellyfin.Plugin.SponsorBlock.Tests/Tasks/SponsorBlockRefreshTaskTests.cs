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

	[Fact]
	public async Task ExecuteAsync_Phase1_IteratesActiveRows_SkipsDone()
	{
		var config = NewConfig();
		var activeItem = new TestVideo { Id = Guid.NewGuid(), Path = "/archive/abcdefghijk.mp4" };
		var doneItem = new TestVideo { Id = Guid.NewGuid(), Path = "/archive/xyzabcdefgh.mp4" };
		var activeRow = NewRow(activeItem.Id, ItemState.HasData);
		var doneRow = NewRow(doneItem.Id, ItemState.Done);

		var task = MakeTask(config, activeItem, doneItem);

		_store.GetActiveAsync(Arg.Any<CancellationToken>())
			.Returns(Rows(activeRow));
		_store.GetAllItemIdsAsync(Arg.Any<CancellationToken>())
			.Returns(Ids(doneItem.Id));
		_store.GetAsync(activeItem.Id, Arg.Any<CancellationToken>()).Returns(activeRow);
		_store.GetAsync(doneItem.Id, Arg.Any<CancellationToken>()).Returns(doneRow);
		_scope.IsInScope(Arg.Any<BaseItem>()).Returns(true);
		_api.GetSegmentsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
			.Returns([new SponsorBlockSegment { ActionType = "skip", Category = "sponsor", Segment = [10.0, 20.0], UUID = "u" }]);

		await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

		await _api.Received(1).GetSegmentsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task ExecuteAsync_Phase1_OrphanRow_DeletesRowAndSegments()
	{
		var config = NewConfig();
		var orphanId = Guid.NewGuid();
		var orphanRow = NewRow(orphanId, ItemState.HasData);
		var task = MakeTask(config);

		_store.GetActiveAsync(Arg.Any<CancellationToken>())
			.Returns(Rows(orphanRow));
		_store.GetAllItemIdsAsync(Arg.Any<CancellationToken>())
			.Returns(Ids(orphanId));
		_store.GetAsync(orphanId, Arg.Any<CancellationToken>()).Returns(orphanRow);

		await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

		await _writer.Received(1).DeleteOwnedAsync(orphanId, Arg.Any<CancellationToken>());
		await _store.Received(1).DeleteAsync(orphanId, Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task ExecuteAsync_Phase2_DiscoveredUntrackedVideo_CallsOrchestrator()
	{
		var libraryId = Guid.NewGuid();
		var config = NewConfig(libraryId);
		var untracked = new TestVideo
		{
			Id = Guid.NewGuid(),
			Path = "/archive/abcdefghijk.mp4",
		};
		var task = MakeTask(config, untracked);

		_store.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(Rows());
		_store.GetAllItemIdsAsync(Arg.Any<CancellationToken>()).Returns(Ids());
		_store.GetAsync(untracked.Id, Arg.Any<CancellationToken>()).Returns((ItemStateRow?)null);
		_scope.IsInScope(untracked).Returns(true);
		_api.GetSegmentsAsync("abcdefghijk", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
			.Returns([new SponsorBlockSegment { ActionType = "skip", Category = "sponsor", Segment = [10.0, 20.0], UUID = "u" }]);

		await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

		await _store.Received(1).UpsertAsync(
			Arg.Is<ItemStateRow>(row => row.ItemId == untracked.Id && row.State != ItemState.Done),
			Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task ExecuteAsync_Phase2_VideoWithDoneRow_NotDiscovered()
	{
		var libraryId = Guid.NewGuid();
		var config = NewConfig(libraryId);
		var doneVideo = new TestVideo
		{
			Id = Guid.NewGuid(),
			Path = "/archive/abcdefghijk.mp4",
		};
		var task = MakeTask(config, doneVideo);

		_store.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(Rows());
		_store.GetAllItemIdsAsync(Arg.Any<CancellationToken>())
			.Returns(Ids(doneVideo.Id));
		_store.GetAsync(doneVideo.Id, Arg.Any<CancellationToken>())
			.Returns(NewRow(doneVideo.Id, ItemState.Done));
		_scope.IsInScope(doneVideo).Returns(true);

		await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

		await _api.DidNotReceive().GetSegmentsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
	}

	private SponsorBlockRefreshTask MakeTask(PluginConfiguration config, params Video[] scoped) => new(
		_store,
		_ => null,
		MakeOrchestrator(config),
		_writer,
		() => config,
		_ => scoped,
		NullLogger<SponsorBlockRefreshTask>.Instance,
		TestLog.Create());

	private SponsorBlockOrchestrator MakeOrchestrator(PluginConfiguration config) => new(
		_api,
		_store,
		_scope,
		_writer,
		() => config,
		(_, _, _) => "abcdefghijk",
		new FakeTimeProvider(T0),
		NullLogger<SponsorBlockOrchestrator>.Instance,
		TestLog.Create());

	private static PluginConfiguration NewConfig(Guid? libraryId = null) => new()
	{
		EnabledLibraryIds = libraryId is not null ? [libraryId.Value] : [Guid.NewGuid()],
		PendingSanityHours = 48,
		RequestDelayMilliseconds = 0,
		Sponsor = true,
	};

	private static ItemStateRow NewRow(Guid itemId, ItemState state) => new(
		itemId, "abcdefghijk", state, T0, T0, 0, 0, "");

	private static async IAsyncEnumerable<ItemStateRow> Rows(
		params ItemStateRow[] rows)
	{
		foreach (var row in rows)
		{
			await Task.CompletedTask;
			yield return row;
		}
	}

	private static async IAsyncEnumerable<Guid> Ids(
		params Guid[] ids)
	{
		foreach (var id in ids)
		{
			await Task.CompletedTask;
			yield return id;
		}
	}

	private sealed class TestVideo : Video
	{
	}
}
