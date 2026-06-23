using Xunit;

namespace Jellyfin.Plugin.SponsorBlock.Tests;

public class SegmentHasherTests
{
	[Fact]
	public void Hash_EmptyList_ReturnsStableNonEmptyHash()
	{
		var h1 = SegmentHasher.Hash([]);
		var h2 = SegmentHasher.Hash([]);

		Assert.NotEmpty(h1);
		Assert.Equal(h1, h2);
	}

	[Fact]
	public void Hash_SingleSegment_ReturnsStableHash()
	{
		var seg = new SponsorBlockSegment { ActionType = "skip", Category = "sponsor", Segment = [10, 20], UUID = "abc" };

		var h1 = SegmentHasher.Hash([seg]);
		var h2 = SegmentHasher.Hash([seg]);

		Assert.Equal(h1, h2);
	}

	[Fact]
	public void Hash_MultipleSegments_ShuffledOrder_ProducesSameHash()
	{
		var a = new SponsorBlockSegment { ActionType = "skip", Category = "sponsor", Segment = [10, 20], UUID = "aaa" };
		var b = new SponsorBlockSegment { ActionType = "skip", Category = "intro", Segment = [0, 5], UUID = "bbb" };
		var c = new SponsorBlockSegment { ActionType = "skip", Category = "outro", Segment = [90, 95], UUID = "ccc" };

		var h1 = SegmentHasher.Hash([a, b, c]);
		var h2 = SegmentHasher.Hash([c, a, b]);

		Assert.Equal(h1, h2);
	}

	[Fact]
	public void Hash_SegmentRemoved_ProducesDifferentHash()
	{
		var a = new SponsorBlockSegment { ActionType = "skip", Category = "sponsor", Segment = [10, 20], UUID = "aaa" };
		var b = new SponsorBlockSegment { ActionType = "skip", Category = "intro", Segment = [0, 5], UUID = "bbb" };

		var h1 = SegmentHasher.Hash([a, b]);
		var h2 = SegmentHasher.Hash([a]);

		Assert.NotEqual(h1, h2);
	}

	[Fact]
	public void Hash_SegmentAdded_ProducesDifferentHash()
	{
		var a = new SponsorBlockSegment { ActionType = "skip", Category = "sponsor", Segment = [10, 20], UUID = "aaa" };
		var b = new SponsorBlockSegment { ActionType = "skip", Category = "intro", Segment = [0, 5], UUID = "bbb" };

		var h1 = SegmentHasher.Hash([a]);
		var h2 = SegmentHasher.Hash([a, b]);

		Assert.NotEqual(h1, h2);
	}

	[Fact]
	public void Hash_NonSkipSegmentsExcluded()
	{
		var skip = new SponsorBlockSegment { ActionType = "skip", Category = "sponsor", Segment = [10, 20], UUID = "aaa" };
		var mute = new SponsorBlockSegment { ActionType = "mute", Category = "sponsor", Segment = [30, 40], UUID = "bbb" };

		var h1 = SegmentHasher.Hash([skip]);
		var h2 = SegmentHasher.Hash([skip, mute]);

		Assert.Equal(h1, h2);
	}
}
