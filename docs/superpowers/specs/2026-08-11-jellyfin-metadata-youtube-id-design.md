# Jellyfin Metadata YouTube ID Design

## Goal

Support libraries whose filenames do not contain the YouTube ID when Jellyfin has imported that ID from NFO metadata. Keep all current filename matching behavior unchanged and make metadata lookup explicit and opt-in.

## Scope

Add a YouTube ID source setting with two choices:

- `Filename` remains the default and continues to use the existing filename matching mode.
- `Jellyfin metadata` reads the `Youtube` entry from the item's provider IDs.

Filename mode retains its existing sub-options:

- The filename without its extension is the complete YouTube ID.
- A custom regular expression extracts the YouTube ID from the filename.

Metadata mode accepts the value Jellyfin imports from an NFO element such as:

```xml
<uniqueid type="youtube">dQw4w9WgXcQ</uniqueid>
```

The plugin validates metadata values as exact 11-character YouTube IDs before calling SponsorBlock. Filename extraction retains its current validation behavior unchanged.

## Non-Goals

- Do not parse NFO files in the plugin.
- Do not register a YouTube external ID provider with Jellyfin.
- Do not support an otherwise unrecognized `<youtubeid>` NFO element.
- Do not automatically fall back between filename and metadata sources.
- Do not expose arbitrary Jellyfin fields or provider-ID keys.

## Configuration

Add a source enum with `Filename` as its zero/default value so existing serialized configurations preserve current behavior. Keep the existing filename matching enum and custom regular expression unchanged.

The configuration page shows the existing filename-format controls only when `Filename` is selected. When `Jellyfin metadata` is selected, it explains that Jellyfin must expose a `Youtube` provider ID, normally imported from `<uniqueid type="youtube">` NFO metadata.

The new English labels follow the existing configuration page's compact patterns and use accessible labels. General configuration-page localization remains outside this focused change.

## Data Flow

`SponsorBlockOrchestrator.ProcessAsync` first applies the selected source:

1. In filename mode, derive the literal filename from `BaseItem.Path` and pass it through the existing exact or custom-regex extractor.
2. In metadata mode, read the item provider ID named `Youtube` through Jellyfin's case-insensitive provider-ID API.
3. In metadata mode, validate the resolved value against the existing YouTube ID format.
4. Continue through the current SponsorBlock state machine when valid.
5. Log a source-specific debug message and skip the item when the selected source has no valid ID.

No source fallback occurs. A user's explicit selection determines the only source consulted.

## Compatibility

Existing installations retain filename source because it is the default enum value. Existing exact-filename and custom-regex extraction remain authoritative and unchanged. The feature requires no state-store migration and does not alter segment fetching, convergence, scoping, or trigger behavior.

Metadata mode depends on Jellyfin having already imported the NFO metadata. Users may need to refresh metadata for existing library items before the provider ID becomes available.

## Error Handling

Missing, blank, or malformed metadata is not sent to SponsorBlock. The plugin logs why it skipped the item at debug level without advancing state, matching current filename-extraction failure behavior.

## Testing

Add focused tests proving:

- Existing exact-filename extraction still succeeds with the default configuration.
- Existing custom-regex extraction still succeeds.
- Filename mode does not consult metadata.
- Metadata mode accepts a valid case-insensitive `Youtube` provider ID.
- Metadata mode rejects missing, blank, and malformed provider IDs.
- Metadata mode does not fall back to a valid filename.
- The configuration default remains filename source.

Run the affected unit tests, then the complete plugin test suite and release build before release.

## Guided Gates

- `GG-1:` With the source left at its default, verify a filename-based library behaves exactly as before.
- `GG-2:` Import or refresh a Pinchflat item whose NFO contains `<uniqueid type="youtube">`, select Jellyfin metadata, and verify SponsorBlock segments appear.
- `GG-3:` Verify an item containing only `<youtubeid>` remains unsupported and is skipped because Jellyfin exposes no `Youtube` provider ID.
- `GG-4:` Switch between source modes in the configuration page and verify only the relevant filename controls appear.
