namespace Jellyfin.Plugin.SponsorBlock.Tests;

public class SponsorBlockSegmentProviderTests
{
	[Fact]
	public async Task CleanupExtractedData_CompletesWithoutWork()
	{
		await new SponsorBlockSegmentProvider().CleanupExtractedData(Guid.NewGuid(), CancellationToken.None);
	}
}
