# jellyfin-sponsorblock

A Jellyfin plugin that skips sponsored segments in YouTube videos using [SponsorBlock](https://sponsor.ajay.app/) data.

If you download YouTube videos and watch them in Jellyfin, you lose the SponsorBlock browser extension. This plugin brings it back: it fetches community-submitted segment data from the SponsorBlock API and feeds it into Jellyfin's native Media Segments system, so your players auto-skip sponsors, self-promotion, intros, outros, and more.

Works great with [TubeArchivist](https://www.tubearchivist.com/) (which names files by YouTube ID), but has no dependency on it. Any YouTube library works as long as the video ID is in the filename.

## Requirements

- Jellyfin 10.11+
- YouTube videos with the 11-character video ID in the filename (e.g., `dQw4w9WgXcQ.mp4`)

## Installation

1. In Jellyfin, go to **Dashboard → Plugins → Repositories**
2. Add this repository URL:
   ```
   https://raw.githubusercontent.com/felixfoertsch/jellyfin-sponsorblock/main/manifest.json
   ```
3. Go to **Catalogue**, find **SponsorBlock**, and install it
4. Restart Jellyfin

### Manual installation

1. Download `jellyfin-plugin-sponsorblock-<version>.zip` from the [latest release](https://github.com/felixfoertsch/jellyfin-sponsorblock/releases)
2. Extract it into `<jellyfin-data>/plugins/SponsorBlock_<version>/` (e.g., `SponsorBlock_1.1.7.0`)
3. Restart Jellyfin

## Setup

1. Open **Dashboard → Plugins → SponsorBlock**
2. Under **Libraries**, tick the YouTube library (or libraries) the plugin should act on. **Until you select at least one library, the plugin does nothing.**
3. Pick the categories you want to skip
4. Save

That's it for new videos — the plugin reacts to library and playback events automatically.
For an existing archive, the daily refresh discovers selected-library items older than the sanity-check window. Use **Force scan all selected libraries** on the plugin config page when you want the backfill to start immediately.

Each viewer can still tune skip vs. ask-to-skip behavior in **Settings → Playback → Media Segments** on their own device (Jellyfin stores that preference in the browser's local storage).

## How it works

The plugin is event-driven for normal operation.

| Trigger | What happens |
|---|---|
| **Item added** to a scoped library | Fetch SponsorBlock data once, immediately. |
| **Playback starts** on a scoped item | If the item still has no data after 24h (since first seen), re-fetch before playback continues. |
| **Daily refresh task** (06:00 by default) | Re-fetch every tracked item, discover selected-library items older than the sanity-check window, and recheck cooled-down `NoData` items. |
| **Force scan all** | One-shot backfill for existing archives. Walks every video in selected libraries once. |
| **Item removed** | Drop the item's state row and any segments owned by this plugin. |

Every item passes through a small state machine stored in SQLite:

- **Pending** — fetched at least once, no segments returned yet. Re-fetched on playback after the first 24h, and at the daily refresh.
- **HasData** — segments are stored. The daily refresh keeps them current.
- **NoData** — at the 48h mark, an item that has produced nothing enters a cooldown. Playback and the daily refresh recheck it after the same interval, so later community submissions can still be picked up.

This model assumes SponsorBlock data converges within ~24h of a video being released. Items uploaded mid-day still get a real shot at picking up segments by the time you actually watch them.

## File matching

The plugin needs to find the YouTube video ID in the filename. Two modes are available:

| Mode | Example | Description |
|---|---|---|
| **YouTube ID as Filename** (default) | `dQw4w9WgXcQ.mp4` | The filename without extension is the ID. This is how TubeArchivist names files. |
| **Custom Regex** | `Cool Video [dQw4w9WgXcQ].mp4` | A regex with one capture group extracts the ID. Default pattern: `\[([a-zA-Z0-9_-]{11})\]` |

## Category mapping

SponsorBlock has more category types than Jellyfin supports. The mapping:

| SponsorBlock | Jellyfin | Default |
|---|---|---|
| Sponsor | Commercial | enabled |
| Self-Promotion | Commercial | enabled |
| Interaction ("like and subscribe") | Commercial | disabled |
| Intro | Intro | disabled |
| Outro | Outro | disabled |
| Preview | Preview | disabled |
| Filler | Commercial | disabled |
| Non-Music (in music videos) | Commercial | disabled |

## Advanced configuration

Defaults are sensible — most users never need to touch these.

| Setting | Default | What it does |
|---|---|---|
| Daily scan hour | `6` | Local hour (0–23) the daily refresh task fires. |
| Playback re-fetch window (h) | `24` | How long after first seeing an item to keep re-fetching on every playback. |
| Sanity-check window (h) | `48` | After this long with no data, the item enters `NoData` cooldown and is rechecked after the same interval. |
| Daily-scan request delay (ms) | `200` | Inter-request pause during the daily refresh to be a good citizen of the public SponsorBlock API. |

## Force scan

The plugin config page has a **Force scan all selected libraries** button. Use it after enabling the plugin for an existing archive if you do not want to wait for the daily refresh. It starts a background scan over all selected-library videos, creates tracking rows, and lets the normal 48h state machine decide whether each item has SponsorBlock data.

The endpoint is `POST /Plugins/SponsorBlock/ScanAll` (admin only). The current status is available at `GET /Plugins/SponsorBlock/ScanAll`.

## Reset

The plugin config page has a **Reset** button that wipes the SponsorBlock state and removes every segment owned by this plugin for items in the configured libraries. The next playback re-fetches from scratch; the daily refresh also re-fetches once the item is older than the sanity-check window.

Use this when you want to wipe local state and force a clean re-fetch, for example after changing file matching or category settings.

The endpoint is `POST /Plugins/SponsorBlock/Reset` (admin only).

## Building from source

```bash
dotnet build
dotnet test
```

The plugin DLL is at `Jellyfin.Plugin.SponsorBlock/bin/Debug/net9.0/Jellyfin.Plugin.SponsorBlock.dll`.

## License

MIT
