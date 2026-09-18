# jellyfin-sponsorblock

A Jellyfin plugin that provides sponsored-segment markers for YouTube videos using [SponsorBlock](https://sponsor.ajay.app/) data.

If you download YouTube videos and watch them in Jellyfin, you lose the SponsorBlock browser extension. This plugin brings its segment data into Jellyfin's native Media Segments system, so supported players can show skip buttons or automatically skip sponsors, self-promotion, intros, outros, and more. **The plugin supplies the markers; each viewer's playback settings determine whether the player shows a button, skips automatically, or does nothing.**

> [!IMPORTANT]
> Installing and enabling the plugin does not automatically enable commercial skip buttons in Jellyfin Web. Each viewer needs to set **Commercial → Ask to Skip** in **Settings → Playback → Media Segment Actions** on the browser/device they use. See [Enable skip buttons in your player](#2-enable-skip-buttons-in-your-player) below for screenshots and instructions.

Works great with [TubeArchivist](https://www.tubearchivist.com/) (which names files by YouTube ID and populates the publish date), but has no dependency on it. Any YouTube library works as long as the video ID is in the filename.

## Requirements

### Jellyfin compatibility

| Jellyfin | SponsorBlock | Status |
|---|---|---|
| 12.x | `2.0.1.0` | Current release |
| 10.11 | `1.1.12.0` | Last compatible release |

SponsorBlock `2.0.1.0` requires Jellyfin 12 and is incompatible with older Jellyfin versions. Jellyfin 10.11 users must install [`v1.1.12.0`](https://github.com/felixfoertsch/jellyfin-sponsorblock/releases/tag/v1.1.12.0). The repository manifest keeps both versions available and lets Jellyfin select the matching ABI.

Additional requirements:

- YouTube videos whose 11-character video ID is available in the filename or Jellyfin's `Youtube` provider metadata
- **YouTube publish date** set in Jellyfin's `PremiereDate` metadata field. This is required for convergence-based polling to work — the plugin uses the publish date to determine when SponsorBlock data has converged and a video no longer needs to be polled. [TubeArchivist](https://www.tubearchivist.com/) populates this automatically. If your import tool does not set `PremiereDate`, the age-gate is skipped and videos are polled indefinitely via the consecutive-unchanged counter only.

## Installation

1. In Jellyfin, go to **Dashboard → Plugins → Repositories**
2. Add this repository URL:
   ```
   https://raw.githubusercontent.com/felixfoertsch/jellyfin-sponsorblock/main/manifest.json
   ```
3. Go to **Catalogue**, find **SponsorBlock**, and install it
4. Restart Jellyfin

### Manual installation

1. Download `jellyfin-plugin-sponsorblock-<version>.zip` from the [release matching your Jellyfin version](https://github.com/felixfoertsch/jellyfin-sponsorblock/releases)
2. Extract it into `<jellyfin-data>/plugins/SponsorBlock_<version>/` (e.g., `SponsorBlock_2.0.1.0`)
3. Restart Jellyfin

## Setup

There are two separate steps: an administrator enables segment collection on the server, and each viewer enables the desired playback behavior in their player.

### 1. Configure the server plugin

1. Open **Dashboard → Plugins → SponsorBlock**
2. Under **Libraries**, tick the YouTube library (or libraries) the plugin should act on. **Until you select at least one library, the plugin does nothing.**
3. Choose where the plugin should read YouTube IDs from
4. Pick the categories whose segments the plugin should collect
5. Save

The plugin now reacts to library and playback events automatically.
For an existing archive, the daily refresh discovers selected-library items and fetches their segments. Use **Force scan all selected libraries** on the plugin config page when you want the backfill to start immediately.

### 2. Enable skip buttons in your player

Do this while signed in as the **user who will watch the videos**, not just as the administrator who installed the plugin. These are the viewer's playback settings, not the SponsorBlock configuration page in the dashboard.

1. Open your user menu/profile settings and go to **Settings → Playback**.
2. Scroll to **Media Segment Actions** (the Media Segments section).
3. Set **Commercial** to **Ask to Skip** to show a button during sponsored segments. Choose **Skip** instead if you want automatic skipping without a prompt.
4. Set any other segment types you use, such as **Intro**, **Outro**, or **Preview**, to the behavior you prefer.
5. **Save**, then start the video again. Jellyfin Web reads these preferences when playback starts.

The following screenshots are from the [setup explanation in issue #1](https://github.com/felixfoertsch/jellyfin-sponsorblock/issues/1#issuecomment-4726185405). Labels and layout may vary with the Jellyfin version and language.

<img src="https://github.com/user-attachments/assets/989a3f17-7b53-4a1d-8bf0-2fa2fcb443f7" alt="Jellyfin user settings for configuring playback behavior" width="760" />

<img src="https://github.com/user-attachments/assets/e810e600-c8f8-41fb-a5eb-23084954ab93" alt="Jellyfin playback settings showing the media segment action choices" width="640" />

| Player action | What happens when a matching segment is reached |
|---|---|
| **None** | No skip button and no automatic skip, even if the plugin successfully stored the segment. |
| **Ask to Skip** | The player offers a skip button. Playback continues unless you choose to skip. |
| **Skip** | The player skips the segment automatically. |

**Why Commercial?** SponsorBlock's **Sponsor** and **Self-Promotion** categories are both stored as Jellyfin **Commercial** segments. Enabling those categories in the plugin controls which markers are collected; it does not enable the player's Commercial action. See [Category mapping](#category-mapping) for the other mappings.

**Repeat this for each browser/device and viewer using Jellyfin Web.** The web client stores segment-action preferences locally for the user, rather than synchronizing them through the server. Configuring your administrator account does not configure another viewer's account. Other Jellyfin clients have their own settings and segment-support capabilities.

Existing saved choices remain in effect, including **None**. Updating the plugin, rescanning the library, or changing a future client default does not replace a stored playback preference. You do not need to reset SponsorBlock merely to switch between asking and automatic skipping.

### Segments are detected, but there is no skip button

First check **Commercial → Ask to Skip** in the settings of the actual player and account being used, save, and restart playback. A log entry saying that segments were written confirms the server-side step; it does not confirm that the viewer enabled prompts.

If that setting is already correct, check that the video's library is selected in the plugin, the YouTube ID is being resolved, and SponsorBlock returned data for the enabled categories. The button appears when the relevant segment is reached, not continuously throughout the video. Also verify that the client supports Jellyfin media-segment prompts. Jellyfin Web suppresses prompts for segments shorter than three seconds.

## How it works

The plugin is event-driven for normal operation.

| Trigger | What happens |
|---|---|
| **Item added** to a scoped library | Fetch SponsorBlock data once, immediately. |
| **Playback starts** on a scoped item | If the item still has no data after 24h (since first seen), re-fetch before playback continues. |
| **Daily refresh task** (06:00 by default) | Re-fetch every tracked item, discover untracked scoped videos, and recheck cooled-down `NoData` items. Items that have converged are frozen as `Done` and excluded from future scans. |
| **Force scan all** | One-shot backfill for existing archives. Walks every video in selected libraries once. |
| **Item removed** | Drop the item's state row and any segments owned by this plugin. |

Every item passes through a state machine stored in SQLite:

- **Pending** — fetched at least once, no segments returned yet. Re-fetched on playback after the first 24h, and at the daily refresh.
- **HasData** — segments are stored. The daily refresh keeps them current until convergence.
- **NoData** — at the 48h mark, an item that has produced nothing enters a cooldown. The daily refresh rechecks it after the same interval, so later community submissions can still be picked up.
- **Done** — terminal state. SponsorBlock data has converged; the item is no longer polled. Segments in Jellyfin are frozen. Reversible only via the Reset button.

### Convergence: when does an item stop being polled?

Two signals determine convergence:

1. **Release age gate** — if the video's YouTube publish date (`PremiereDate`) is older than 30 days, the item is fetched one final time and frozen as `Done`. SponsorBlock data is crowdsourced and converges within weeks of release; a month-old video will not gain new segments.

2. **Consecutive-unchanged counter** — for videos younger than 30 days, every daily fetch compares the current segment set against the previous one (by segment UUID hash). If the data hasn't changed for 5 consecutive daily checks, the item is marked `Done`. Any change resets the counter to 0.

If `PremiereDate` is not set (e.g. your import tool doesn't populate it), the age gate is skipped. The item still converges via the consecutive-unchanged counter, but the first 5 days of polling always happen regardless of video age.

## YouTube ID source

The plugin needs the original 11-character YouTube video ID. Choose its source in the plugin settings:

| Source | Description |
|---|---|
| **Filename** (default) | Extract the ID from the media filename using one of the modes below. This preserves the behavior used by existing installations. |
| **Jellyfin metadata** | Read Jellyfin's `Youtube` provider ID. Use this for tools such as Pinchflat that store the ID in NFO metadata instead of the filename. |

Filename source supports two formats:

| Mode | Example | Description |
|---|---|---|
| **YouTube ID as Filename** (default) | `dQw4w9WgXcQ.mp4` | The filename without extension is the ID. This is how TubeArchivist names files. |
| **Custom Regex** | `Cool Video [dQw4w9WgXcQ].mp4` | A regex with one capture group extracts the ID. Default pattern: `\[([a-zA-Z0-9_-]{11})\]` |

Jellyfin metadata mode reads the `Youtube` provider ID that Jellyfin imports from `<uniqueid type="youtube">…</uniqueid>`. The plugin does not parse NFO files itself. `<youtubeid>` is not supported because Jellyfin does not expose that element without a separate external-ID provider.

## Category mapping

SponsorBlock has more category types than Jellyfin supports. The mapping:

| SponsorBlock | Jellyfin | Plugin collection default |
|---|---|---|
| Sponsor | Commercial | enabled |
| Self-Promotion | Commercial | enabled |
| Interaction ("like and subscribe") | Commercial | disabled |
| Intro | Intro | disabled |
| Outro | Outro | disabled |
| Preview | Preview | disabled |
| Filler | Commercial | disabled |
| Non-Music (in music videos) | Commercial | disabled |

These defaults enable collection of segment markers, not skip buttons or automatic skipping. Configure the corresponding Jellyfin segment action in [your player's playback settings](#2-enable-skip-buttons-in-your-player).

## Advanced configuration

Defaults are sensible — most users never need to touch these.

| Setting | Default | What it does |
|---|---|---|
| Daily scan hour | `6` | Local hour (0–23) the daily refresh task fires. |
| Playback re-fetch window (h) | `24` | How long after first seeing an item to keep re-fetching on every playback. |
| Sanity-check window (h) | `48` | After this long with no data, the item enters `NoData` cooldown and is rechecked after the same interval. |
| Daily-scan request delay (ms) | `200` | Inter-request pause during the daily refresh to be a good citizen of the public SponsorBlock API. |
| Release age cutoff (days) | `30` | Videos whose YouTube publish date is older than this are fetched once and frozen as `Done`. |
| Consecutive unchanged threshold | `5` | Number of daily fetches with unchanged segment data before an item is marked `Done`. |

## Force scan

The plugin config page has a **Force scan all selected libraries** button. Use it after enabling the plugin for an existing archive if you do not want to wait for the daily refresh. It starts a background scan over all selected-library videos, creates tracking rows, and lets the state machine decide convergence.

The endpoint is `POST /Plugins/SponsorBlock/ScanAll` (admin only). The current status is available at `GET /Plugins/SponsorBlock/ScanAll`.

## Reset

The plugin config page has a **Reset** button that wipes the SponsorBlock state and removes every segment owned by this plugin for items in the configured libraries. All items return to an untracked state — the next playback or daily scan re-fetches from scratch, and convergence starts over.

Use this when you want to wipe local state and force a clean re-fetch, for example after changing file matching or category settings.

The endpoint is `POST /Plugins/SponsorBlock/Reset` (admin only).

## Building from source

```bash
dotnet build
dotnet test
./scripts/package-release.sh 2.0.1.0
```

The plugin DLL is at `Jellyfin.Plugin.SponsorBlock/bin/Release/net10.0/Jellyfin.Plugin.SponsorBlock.dll`.
Release zips are written to `artifacts/` and intentionally contain only `Jellyfin.Plugin.SponsorBlock.dll`; Jellyfin provides the framework dependencies at runtime.

## License

MIT
