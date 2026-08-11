using System.Text.RegularExpressions;
using Jellyfin.Plugin.SponsorBlock.Configuration;

namespace Jellyfin.Plugin.SponsorBlock;

/// <summary>
/// Extracts YouTube video IDs from filenames.
/// </summary>
public static partial class YouTubeIdExtractor
{
	[GeneratedRegex(@"^[a-zA-Z0-9_-]{11}$")]
	private static partial Regex YouTubeIdPattern();

	/// <summary>
	/// Validates an exact YouTube video ID.
	/// </summary>
	/// <param name="value">Candidate value.</param>
	/// <returns>The value when valid; otherwise, null.</returns>
	public static string? Validate(string? value)
		=> !string.IsNullOrEmpty(value) && YouTubeIdPattern().IsMatch(value) ? value : null;

	/// <summary>
	/// Extracts a YouTube video ID from a filename.
	/// </summary>
	/// <param name="filename">The filename including extension.</param>
	/// <param name="mode">The extraction mode.</param>
	/// <param name="customPattern">Custom regex pattern (used only in CustomRegex mode).</param>
	/// <returns>The 11-character YouTube ID, or null if extraction fails.</returns>
	public static string? Extract(string filename, FileMatchingMode mode, string? customPattern)
	{
		if (string.IsNullOrEmpty(filename))
		{
			return null;
		}

		return mode switch
		{
			FileMatchingMode.YouTubeIdAsFilename => ExtractFromFilename(filename),
			FileMatchingMode.CustomRegex => ExtractWithRegex(filename, customPattern),
			_ => null,
		};
	}

	private static string? ExtractFromFilename(string filename)
	{
		var name = Path.GetFileNameWithoutExtension(filename);
		return Validate(name);
	}

	private static string? ExtractWithRegex(string filename, string? pattern)
	{
		if (string.IsNullOrEmpty(pattern))
		{
			return null;
		}

		try
		{
			var match = Regex.Match(filename, pattern);
			return match is { Success: true, Groups.Count: > 1 }
				? match.Groups[1].Value
				: null;
		}
		catch (RegexParseException)
		{
			return null;
		}
	}
}
