using Jellyfin.Plugin.SponsorBlock.Configuration;
using Xunit;

namespace Jellyfin.Plugin.SponsorBlock.Tests;

public class YouTubeIdExtractorTests
{
	[Fact]
	public void Configuration_DefaultSource_IsFilename()
	{
		var config = new PluginConfiguration();

		Assert.Equal(YouTubeIdSource.Filename, config.YouTubeIdSource);
	}

	[Theory]
	[InlineData("dQw4w9WgXcQ", "dQw4w9WgXcQ")]
	[InlineData("abc-_123Abc", "abc-_123Abc")]
	[InlineData("short", null)]
	[InlineData("toolong123456", null)]
	[InlineData("invalid$id", null)]
	[InlineData("", null)]
	[InlineData(null, null)]
	public void Validate_ReturnsOnlyExactYouTubeIds(string? value, string? expected)
	{
		Assert.Equal(expected, YouTubeIdExtractor.Validate(value));
	}

	[Theory]
	[InlineData("4pG8_bWpmaE.mp4", "4pG8_bWpmaE")]
	[InlineData("dQw4w9WgXcQ.mkv", "dQw4w9WgXcQ")]
	[InlineData("abc-_123Abc.webm", "abc-_123Abc")]
	public void FilenameMode_ExtractsId(string filename, string expectedId)
	{
		var result = YouTubeIdExtractor.Extract(filename, FileMatchingMode.YouTubeIdAsFilename, null);
		Assert.Equal(expectedId, result);
	}

	[Theory]
	[InlineData("not-a-video-id.mp4")]
	[InlineData("toolong12345678.mp4")]
	[InlineData("short.mp4")]
	[InlineData("")]
	public void FilenameMode_ReturnsNull_ForInvalidIds(string filename)
	{
		var result = YouTubeIdExtractor.Extract(filename, FileMatchingMode.YouTubeIdAsFilename, null);
		Assert.Null(result);
	}

	[Theory]
	[InlineData("2024-01-15 Cool Video [dQw4w9WgXcQ].mp4", @"\[([a-zA-Z0-9_-]{11})\]", "dQw4w9WgXcQ")]
	[InlineData("Video Title [abc-_123Abc].mkv", @"\[([a-zA-Z0-9_-]{11})\]", "abc-_123Abc")]
	public void RegexMode_ExtractsId(string filename, string pattern, string expectedId)
	{
		var result = YouTubeIdExtractor.Extract(filename, FileMatchingMode.CustomRegex, pattern);
		Assert.Equal(expectedId, result);
	}

	[Fact]
	public void RegexMode_PreservesCustomCaptureWithoutExactIdValidation()
	{
		var result = YouTubeIdExtractor.Extract("video-[custom-value].mp4", FileMatchingMode.CustomRegex, @"\[([^]]+)\]");

		Assert.Equal("custom-value", result);
	}

	[Theory]
	[InlineData("no-brackets.mp4", @"\[([a-zA-Z0-9_-]{11})\]")]
	[InlineData("file.mp4", @"(invalid")]
	[InlineData("file.mp4", "no-capture-group")]
	[InlineData("file.mp4", null)]
	public void RegexMode_ReturnsNull_WhenNoMatch(string filename, string? pattern)
	{
		var result = YouTubeIdExtractor.Extract(filename, FileMatchingMode.CustomRegex, pattern);
		Assert.Null(result);
	}
}
