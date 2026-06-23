using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.SponsorBlock.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
	/// <summary>
	/// Gets or sets the file matching mode.
	/// </summary>
	public FileMatchingMode FileMatchingMode { get; set; } = FileMatchingMode.YouTubeIdAsFilename;

	/// <summary>
	/// Gets or sets the custom regex pattern for extracting YouTube IDs.
	/// </summary>
	public string CustomRegexPattern { get; set; } = @"\[([a-zA-Z0-9_-]{11})\]";

	/// <summary>
	/// Gets or sets a value indicating whether to skip sponsor segments.
	/// </summary>
	public bool Sponsor { get; set; } = true;

	/// <summary>
	/// Gets or sets a value indicating whether to skip self-promotion segments.
	/// </summary>
	public bool SelfPromo { get; set; } = true;

	/// <summary>
	/// Gets or sets a value indicating whether to skip interaction segments.
	/// </summary>
	public bool Interaction { get; set; }

	/// <summary>
	/// Gets or sets a value indicating whether to skip intro segments.
	/// </summary>
	public bool Intro { get; set; }

	/// <summary>
	/// Gets or sets a value indicating whether to skip outro segments.
	/// </summary>
	public bool Outro { get; set; }

	/// <summary>
	/// Gets or sets a value indicating whether to skip preview segments.
	/// </summary>
	public bool Preview { get; set; }

	/// <summary>
	/// Gets or sets a value indicating whether to skip filler segments.
	/// </summary>
	public bool Filler { get; set; }

	/// <summary>
	/// Gets or sets a value indicating whether to skip off-topic music segments.
	/// </summary>
	public bool MusicOfftopic { get; set; }

	/// <summary>
	/// Gets or sets the Jellyfin library (CollectionFolder) ids the plugin should act on.
	/// Empty array → plugin is inert (safe default for fresh install).
	/// </summary>
	public Guid[] EnabledLibraryIds { get; set; } = Array.Empty<Guid>();

	/// <summary>
	/// Gets or sets the local hour (0..23) at which the daily refresh task runs.
	/// </summary>
	public int DailyScanHour { get; set; } = 6;

	/// <summary>
	/// Gets or sets the age boundary (hours since first observed) within which Pending items
	/// re-fetch on every PlaybackStart trigger. After this, only the daily scan touches them.
	/// </summary>
	public int PlaybackPollHours { get; set; } = 24;

	/// <summary>
	/// Gets or sets the age boundary (hours since first observed) at which a Pending item
	/// is sanity-checked and promoted to NoData if still empty. NoData items recheck after
	/// the same cooldown.
	/// </summary>
	public int PendingSanityHours { get; set; } = 48;

	/// <summary>
	/// Gets or sets the inter-request delay (milliseconds) used by the daily refresh task
	/// to avoid spiking the public SponsorBlock API.
	/// </summary>
	public int RequestDelayMilliseconds { get; set; } = 200;

	/// <summary>
	/// Gets or sets the age (in days since YouTube premiere/publish date) at which
	/// an item is fetched one final time and frozen as Done. Videos older than this
	/// are assumed to have converged SponsorBlock data.
	/// </summary>
	public int ReleaseAgeCutoffDays { get; set; } = 30;

	/// <summary>
	/// Gets or sets how many consecutive daily fetches must return unchanged segment
	/// data before a young item is marked Done. Reset to 0 on any change.
	/// </summary>
	public int ConsecutiveUnchangedThreshold { get; set; } = 5;

	/// <summary>
	/// Returns a dictionary of category name to enabled for use with the API.
	/// </summary>
	/// <returns>Category settings dictionary.</returns>
	public Dictionary<string, bool> GetCategorySettings()
	{
		return new Dictionary<string, bool>
		{
			["sponsor"] = Sponsor,
			["selfpromo"] = SelfPromo,
			["interaction"] = Interaction,
			["intro"] = Intro,
			["outro"] = Outro,
			["preview"] = Preview,
			["filler"] = Filler,
			["music_offtopic"] = MusicOfftopic,
		};
	}
}
