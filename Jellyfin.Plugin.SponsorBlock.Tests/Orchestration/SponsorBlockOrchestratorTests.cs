using Jellyfin.Plugin.SponsorBlock;
using Jellyfin.Plugin.SponsorBlock.Configuration;
using Jellyfin.Plugin.SponsorBlock.Orchestration;
using Jellyfin.Plugin.SponsorBlock.Scoping;
using Jellyfin.Plugin.SponsorBlock.State;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.SponsorBlock.Tests.Orchestration;

public class SponsorBlockOrchestratorTests
{
	private static readonly DateTimeOffset T0 = new(2026, 4, 27, 12, 0, 0, TimeSpan.Zero);

	private readonly FakeTimeProvider _time = new(T0);
	private readonly ISponsorBlockApiClient _api = Substitute.For<ISponsorBlockApiClient>();
	private readonly ISponsorBlockStateStore _store = Substitute.For<ISponsorBlockStateStore>();
	private readonly ILibraryScopeService _scope = Substitute.For<ILibraryScopeService>();
	private readonly IMediaSegmentWriter _writer = Substitute.For<IMediaSegmentWriter>();
	private readonly PluginConfiguration _config = new()
	{
		EnabledLibraryIds = new[] { Guid.NewGuid() },
		PlaybackPollHours = 24,
		PendingSanityHours = 48,
		Sponsor = true,
	};

	private SponsorBlockOrchestrator MakeOrchestrator() => new(
		_api, _store, _scope, _writer,
		() => _config,
		(_, _, _) => "abcdefghijk",
		_time,
		NullLogger<SponsorBlockOrchestrator>.Instance);

	private static BaseItem FakeItem(Guid id, string path = "/library/yt/abcdefghijk.mp4") =>
		new TestItem { Id = id, Path = path };

	private sealed class TestItem : BaseItem
	{
	}

	private static SponsorBlockSegment Seg(string category = "sponsor")
		=> new() { Category = category, ActionType = "skip", Segment = [10.0, 20.0], UUID = "u" };

	[Fact]
	public async Task NoRow_FetchSucceedsWithSegments_InsertsHasData()
	{
		var item = FakeItem(Guid.NewGuid());
		_scope.IsInScope(item).Returns(true);
		_store.GetAsync(item.Id, Arg.Any<CancellationToken>()).Returns((ItemStateRow?)null);
		_api.GetSegmentsAsync("abcdefghijk", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
			.Returns(new List<SponsorBlockSegment> { Seg() });

		await MakeOrchestrator().ProcessAsync(item, ProcessReason.ItemAdded, CancellationToken.None);

		await _store.Received().UpsertAsync(
			Arg.Is<ItemStateRow>(r => r.State == ItemState.HasData && r.SegmentCount == 1 && r.FirstSeenAt == T0),
			Arg.Any<CancellationToken>());
		await _writer.Received(1).CreateAsync(Arg.Any<MediaSegmentDto>(), Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task NoRow_FetchSucceedsEmpty_InsertsPending()
	{
		var item = FakeItem(Guid.NewGuid());
		_scope.IsInScope(item).Returns(true);
		_store.GetAsync(item.Id, Arg.Any<CancellationToken>()).Returns((ItemStateRow?)null);
		_api.GetSegmentsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
			.Returns(new List<SponsorBlockSegment>());

		await MakeOrchestrator().ProcessAsync(item, ProcessReason.ItemAdded, CancellationToken.None);

		await _store.Received().UpsertAsync(
			Arg.Is<ItemStateRow>(r => r.State == ItemState.Pending && r.SegmentCount == 0),
			Arg.Any<CancellationToken>());
		await _writer.DidNotReceive().CreateAsync(Arg.Any<MediaSegmentDto>(), Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task Pending_PlaybackStart_WithinPollWindow_DoesNothing()
	{
		var item = FakeItem(Guid.NewGuid());
		_scope.IsInScope(item).Returns(true);
		var existing = NewRow(item.Id, ItemState.Pending, firstSeen: T0.AddHours(-12));
		_store.GetAsync(item.Id, Arg.Any<CancellationToken>()).Returns(existing);

		await MakeOrchestrator().ProcessAsync(item, ProcessReason.PlaybackStart, CancellationToken.None);

		await _api.DidNotReceive().GetSegmentsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
		await _store.DidNotReceive().UpsertAsync(Arg.Any<ItemStateRow>(), Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task Pending_PlaybackStart_OutsidePollWindow_RefetchesAndPromotesOnSegments()
	{
		var item = FakeItem(Guid.NewGuid());
		_scope.IsInScope(item).Returns(true);
		var existing = NewRow(item.Id, ItemState.Pending, firstSeen: T0.AddHours(-30));
		_store.GetAsync(item.Id, Arg.Any<CancellationToken>()).Returns(existing);
		_api.GetSegmentsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
			.Returns(new List<SponsorBlockSegment> { Seg() });

		await MakeOrchestrator().ProcessAsync(item, ProcessReason.PlaybackStart, CancellationToken.None);

		await _store.Received().UpsertAsync(
			Arg.Is<ItemStateRow>(r => r.State == ItemState.HasData && r.SegmentCount == 1),
			Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task Pending_DailyScan_PastSanityHours_EmptyResponse_PromotesToNoData()
	{
		var item = FakeItem(Guid.NewGuid());
		_scope.IsInScope(item).Returns(true);
		var existing = NewRow(item.Id, ItemState.Pending, firstSeen: T0.AddHours(-50));
		_store.GetAsync(item.Id, Arg.Any<CancellationToken>()).Returns(existing);
		_api.GetSegmentsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
			.Returns(new List<SponsorBlockSegment>());

		await MakeOrchestrator().ProcessAsync(item, ProcessReason.DailyScan, CancellationToken.None);

		await _store.Received().UpsertAsync(
			Arg.Is<ItemStateRow>(r => r.State == ItemState.NoData),
			Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task HasData_DailyScan_ReplacesOwnedSegments()
	{
		var item = FakeItem(Guid.NewGuid());
		_scope.IsInScope(item).Returns(true);
		var existing = NewRow(item.Id, ItemState.HasData, segmentCount: 3);
		_store.GetAsync(item.Id, Arg.Any<CancellationToken>()).Returns(existing);
		_api.GetSegmentsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
			.Returns(new List<SponsorBlockSegment> { Seg(), Seg() });

		await MakeOrchestrator().ProcessAsync(item, ProcessReason.DailyScan, CancellationToken.None);

		await _writer.Received(1).DeleteOwnedAsync(item.Id, Arg.Any<CancellationToken>());
		await _writer.Received(2).CreateAsync(Arg.Any<MediaSegmentDto>(), Arg.Any<CancellationToken>());
		await _store.Received().UpsertAsync(
			Arg.Is<ItemStateRow>(r => r.State == ItemState.HasData && r.SegmentCount == 2),
			Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task HasData_PlaybackStart_NoOps()
	{
		var item = FakeItem(Guid.NewGuid());
		_scope.IsInScope(item).Returns(true);
		_store.GetAsync(item.Id, Arg.Any<CancellationToken>())
			.Returns(NewRow(item.Id, ItemState.HasData, segmentCount: 2));
		_writer.HasAny(item.Id).Returns(true);

		await MakeOrchestrator().ProcessAsync(item, ProcessReason.PlaybackStart, CancellationToken.None);

		await _api.DidNotReceive().GetSegmentsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
		await _store.DidNotReceive().UpsertAsync(Arg.Any<ItemStateRow>(), Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task HasData_PlaybackStart_WhenJellyfinRowsWerePruned_RefetchesAndRecreatesSegments()
	{
		var item = FakeItem(Guid.NewGuid());
		_scope.IsInScope(item).Returns(true);
		_store.GetAsync(item.Id, Arg.Any<CancellationToken>())
			.Returns(NewRow(item.Id, ItemState.HasData, segmentCount: 3));
		_writer.HasAny(item.Id).Returns(false);
		_api.GetSegmentsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
			.Returns(new List<SponsorBlockSegment> { Seg(), Seg("intro"), Seg("outro") });

		await MakeOrchestrator().ProcessAsync(item, ProcessReason.PlaybackStart, CancellationToken.None);

		await _writer.Received(1).DeleteOwnedAsync(item.Id, Arg.Any<CancellationToken>());
		await _writer.Received(3).CreateAsync(Arg.Any<MediaSegmentDto>(), Arg.Any<CancellationToken>());
		await _store.Received().UpsertAsync(
			Arg.Is<ItemStateRow>(r => r.State == ItemState.HasData && r.SegmentCount == 3),
			Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task NoData_WithinSanityWindow_NoOps()
	{
		var item = FakeItem(Guid.NewGuid());
		_scope.IsInScope(item).Returns(true);
		_store.GetAsync(item.Id, Arg.Any<CancellationToken>())
			.Returns(NewRow(item.Id, ItemState.NoData));

		await MakeOrchestrator().ProcessAsync(item, ProcessReason.DailyScan, CancellationToken.None);
		await MakeOrchestrator().ProcessAsync(item, ProcessReason.PlaybackStart, CancellationToken.None);

		await _api.DidNotReceive().GetSegmentsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task NoData_PlaybackStart_AfterSanityWindow_RefetchesAndPromotesOnSegments()
	{
		var item = FakeItem(Guid.NewGuid());
		_scope.IsInScope(item).Returns(true);
		_store.GetAsync(item.Id, Arg.Any<CancellationToken>())
			.Returns(NewRow(item.Id, ItemState.NoData, firstSeen: T0.AddDays(-7), lastFetchAt: T0.AddHours(-50)));
		_api.GetSegmentsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
			.Returns(new List<SponsorBlockSegment> { Seg() });

		await MakeOrchestrator().ProcessAsync(item, ProcessReason.PlaybackStart, CancellationToken.None);

		await _writer.Received(1).CreateAsync(Arg.Any<MediaSegmentDto>(), Arg.Any<CancellationToken>());
		await _store.Received().UpsertAsync(
			Arg.Is<ItemStateRow>(r => r.State == ItemState.HasData && r.SegmentCount == 1),
			Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task OutOfScope_NoOps()
	{
		var item = FakeItem(Guid.NewGuid());
		_scope.IsInScope(item).Returns(false);

		await MakeOrchestrator().ProcessAsync(item, ProcessReason.ItemAdded, CancellationToken.None);

		await _store.DidNotReceive().GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
		await _api.DidNotReceive().GetSegmentsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
	}

	[Fact]
	public async Task TransientHttpFailure_DoesNotAdvanceState()
	{
		var item = FakeItem(Guid.NewGuid());
		_scope.IsInScope(item).Returns(true);
		_store.GetAsync(item.Id, Arg.Any<CancellationToken>())
			.Returns(NewRow(item.Id, ItemState.Pending, firstSeen: T0.AddHours(-50)));
		_api.GetSegmentsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
			.Returns<Task<IReadOnlyList<SponsorBlockSegment>>>(_ => throw new HttpRequestException("boom"));

		await MakeOrchestrator().ProcessAsync(item, ProcessReason.DailyScan, CancellationToken.None);

		await _store.DidNotReceive().UpsertAsync(Arg.Any<ItemStateRow>(), Arg.Any<CancellationToken>());
	}

	private static ItemStateRow NewRow(
		Guid itemId,
		ItemState state,
		int segmentCount = 0,
		DateTimeOffset? firstSeen = null,
		DateTimeOffset? lastFetchAt = null) =>
		new(itemId, "abcdefghijk", state, firstSeen ?? T0, lastFetchAt ?? T0, segmentCount);
}
