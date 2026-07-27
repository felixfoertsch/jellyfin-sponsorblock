# SponsorBlock Jellyfin 12 Compatibility Design

**Date:** 2026-07-27

## Goal

Produce and deploy a Jellyfin 12-only SponsorBlock plugin that loads against Jellyfin `12.0.0-rc3`, preserves the event-driven segment workflow, and does not reintroduce Jellyfin's core full-library media-segment scan.

## Current Failure

Jellyfin 12 adds this member to `MediaBrowser.Controller.MediaSegments.IMediaSegmentProvider`:

```csharp
Task CleanupExtractedData(Guid itemId, CancellationToken cancellationToken);
```

SponsorBlock `1.1.12.0` implements the Jellyfin 10.11 interface and lacks this member. Jellyfin therefore disables the plugin at assembly load with `ReflectionTypeLoadException` and `TypeLoadException`.

The plugin still needs an `IMediaSegmentProvider` registration. Jellyfin filters stored segments by registered provider, so removing the provider would hide SponsorBlock segments from clients. The provider must remain a stub: `Supports` returns `false`, while the plugin's orchestrator owns fetching and persistence.

## Compatibility Change

- Target `.NET 10.0` in both production and test projects.
- Pin the local SDK to `10.0.302` in `.mise.toml`.
- Pin `Jellyfin.Controller` to `12.0.0-rc3` rather than a floating range.
- Align direct Microsoft package references with .NET 10 where required by restore.
- Implement `CleanupExtractedData(Guid, CancellationToken)` as `Task.CompletedTask`.
- Keep `Name`, `Supports`, and `GetMediaSegments` behavior unchanged.

The cleanup method is intentionally a no-op. `SponsorBlockSegmentProvider` stores no extracted analysis data. SponsorBlock's state database and stored media segments are managed separately through item-removal and reset services; invoking those broader destructive paths from Jellyfin's provider-pruning callback would violate ownership and could erase valid state.

## Tests

- Compile the production project against `Jellyfin.Controller 12.0.0-rc3`; this is the primary interface-contract test.
- Add a unit test that invokes `CleanupExtractedData` and verifies immediate successful completion.
- Add a DI registration assertion for `IMediaSegmentProvider` → `SponsorBlockSegmentProvider`.
- Retain and run the full existing test suite.
- Build Release output and inspect the package to ensure it contains only the plugin DLL.

## Versioning And Distribution

- Release version: `2.0.0.0`.
- Jellyfin manifest target ABI: `12.0.0.0`.
- Requirements documentation changes from Jellyfin `10.11+` to Jellyfin `12.0.0-rc3`.
- The release manifest receives a new first entry; historical entries remain unchanged.
- The package name follows the existing script convention with the four-part plugin version.

## Deployment

1. Build and test locally.
2. Copy `/mnt/cache/appdata/jellyfin/plugins/SponsorBlock_1.1.12.0/` to a timestamped rollback directory.
3. Install the new version in a separate `SponsorBlock_2.0.0.0/` directory.
4. Restart only the `jellyfin` container.
5. Verify startup and plugin loading before testing behavior.

The old plugin directory remains untouched until verification succeeds. Rollback means stopping Jellyfin, removing the new plugin directory, restoring the old directory name if necessary, and restarting. This rollback changes only plugin files; it does not attempt to roll Jellyfin itself back from v12.

## Production Verification

- No `ReflectionTypeLoadException`, `TypeLoadException`, or SponsorBlock assembly-load failure appears.
- Jellyfin reports healthy and `/Users/Public` responds locally and publicly in under one second after startup settles.
- SponsorBlock logs initialization and retains its prior configuration and state database.
- Existing SponsorBlock segments remain present for a known item.
- One scoped YouTube playback produces the expected playback-trigger decision or fetch log.
- Jellyfin's core Media Segment Scan does not invoke SponsorBlock because `Supports` remains false.
- CPU, memory, and PID counts remain bounded during the verification window.

## Guided Gates

- **GG-1:** Open Jellyfin's Plugins dashboard and confirm SponsorBlock `2.0.0.0` is active while Chapter Segments remains independently disabled.
- **GG-2:** Play one known TubeArchivist video that has SponsorBlock data and confirm the client exposes or skips the stored segment as configured.
- **GG-3:** Play one scoped video that requires a fresh lookup and confirm its SponsorBlock log records the playback-triggered decision without starting a library-wide media-segment scan.
- **GG-4:** Confirm normal playback, seeking, and session startup remain responsive after the plugin restart.

## Out Of Scope

- Jellyfin 10.11 compatibility.
- Repairing Chapter Segments Provider.
- Changing SponsorBlock polling, category mapping, or convergence behavior.
- Repairing the separate Jellyfin 12 monitor API-token incompatibility.
- Publishing a GitHub release or updating the public plugin repository before production verification.
