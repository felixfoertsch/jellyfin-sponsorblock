namespace Jellyfin.Plugin.SponsorBlock.Tests;

public static class TestLog
{
	public static SponsorBlockLog Create()
	{
		var dir = Path.Combine(Path.GetTempPath(), $"sponsorblock-test-{Guid.NewGuid():N}");
		Directory.CreateDirectory(dir);
		return new SponsorBlockLog(dir, TimeProvider.System);
	}
}
