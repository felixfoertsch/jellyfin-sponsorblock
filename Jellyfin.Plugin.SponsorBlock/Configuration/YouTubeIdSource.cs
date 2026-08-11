namespace Jellyfin.Plugin.SponsorBlock.Configuration;

/// <summary>
/// Source used to resolve a video's YouTube ID.
/// </summary>
public enum YouTubeIdSource
{
	/// <summary>
	/// Resolve the ID from the media filename using the configured matching mode.
	/// </summary>
	Filename = 0,

	/// <summary>
	/// Resolve the ID from Jellyfin's Youtube provider metadata.
	/// </summary>
	JellyfinMetadata = 1,
}
