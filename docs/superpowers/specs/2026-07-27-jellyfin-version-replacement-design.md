# Jellyfin Version Replacement Design

## Goal

Replace the newly published CalVer-labelled Jellyfin 12 release with SponsorBlock `2.0.0.0`, following Jellyfin plugin release-generation conventions while retaining `1.1.12.0` for Jellyfin 10.11.

## Root Cause

Jellyfin parses `2026.07.27.0` successfully through `System.Version`, and the manually deployed plugin loads. The defect is ecosystem convention and future version ordering, not a parser failure. Official Jellyfin plugins use independent, monotonically increasing four-part plugin versions; compatibility is represented separately by `targetAbi`.

Keeping `2026.7.27.0` would make a conventional `2.0.0.0` release compare as a downgrade. Jellyfin would not offer it as an update to an installation carrying the CalVer version. The clean correction is therefore:

| Jellyfin | SponsorBlock | Target ABI |
|---|---|---|
| 12.x | `2.0.0.0` | `12.0.0.0` |
| 10.11 | `1.1.12.0` | `10.11.0.0` |

The plugin major version remains independent from the Jellyfin major version. The release title and compatibility matrix communicate the Jellyfin requirement; `targetAbi` enforces it.

## Repository Replacement

Replace all active `2026.07.27.0` release references with `2.0.0.0`, including:

- assembly and file versions
- manifest version, source URL, checksum, and changelog
- README compatibility and build/install examples
- release design and implementation documentation that describes the active release
- package filename and verification commands

Preserve historical dates in document filenames. Do not rename dated design or plan files merely because the packaged plugin version changes.

Rebuild the current Jellyfin 12 tree as exactly one commit above `v1.1.12.0`. The replacement commit includes the compatibility implementation, tests, deterministic packaging, all approved design/plan documentation, and `2.0.0.0` release metadata. Preserve every older tag and release and leave the two unrelated untracked screenshots untouched.

The published `main` rewrite must use `--force-with-lease` pinned to current commit `7240791`. Never use an unconditional force push. Stop if remote `main` changes.

## GitHub Replacement

Prepare and verify the replacement commit and ZIP before any remote mutation. Then:

1. Push annotated tag `v2.0.0.0` for the replacement commit.
2. Publish `v2.0.0.0 (Jellyfin 12 only)` with the deterministic DLL-only ZIP.
3. Verify the tag, release body, asset checksum, and download.
4. Replace GitHub `main` through the approved force-with-lease and verify the raw manifest.
5. Update `v1.1.12.0 (Jellyfin 10.11)` notes to link to `v2.0.0.0`.
6. Delete only the erroneous `v2026.07.27.0` release and tag after every replacement gate passes.

This order keeps the current release recoverable until the replacement exists and its public package has been verified. If replacement publication fails before step 4, retain the current branch/release unchanged. If the branch rewrite fails, leave both release tags in place while restoring or retrying the branch safely. Never alter any pre-existing `1.x` release binary.

## Production Replacement

After GitHub verification:

1. Establish a healthy Jellyfin baseline.
2. Stop only Jellyfin.
3. Move `SponsorBlock_2026.07.27.0/` to `/mnt/cache/appdata/jellyfin/plugin-rollbacks/2026-07-27/`.
4. Install the verified DLL under `SponsorBlock_2.0.0.0/`.
5. Start Jellyfin and wait for readiness.
6. Confirm `SponsorBlock 2.0.0.0` loads without type-load errors.
7. Verify configuration, state database, 7,460 stored segments across 2,924 items, one playback-triggered lookup, one client-visible stored-segment skip, endpoint latency, CPU, memory, and PID counts.

Do not reset or migrate SponsorBlock state. Assembly version is the only runtime change from the already verified Jellyfin 12 build.

## Verification

Before remote changes, require:

- 86 tests passing
- release build with zero warnings and errors
- deterministic package output from the main checkout and a clean detached worktree
- ZIP containing exactly `Jellyfin.Plugin.SponsorBlock.dll`
- manifest JSON validation with `2.0.0.0` first and `1.1.12.0` second
- exactly one replacement commit above `v1.1.12.0`
- independent final review with no unresolved Critical or Important findings

After publication, verify branch/tag identity, release ordering, explicit compatibility warnings, both public ZIP checksums, and raw manifest ABI selection. The final repository must contain no `2026.07.27.0` active release reference, GitHub release, or tag.

## Guided Gates

- GG-1: Open the `v2.0.0.0` GitHub release and verify the title and first paragraph say Jellyfin 12 only.
- GG-2: In Jellyfin 12, verify the repository offers `2.0.0.0` and playback still performs a stored-segment skip.
- GG-3: On Jellyfin 10.11, verify the same repository offers `1.1.12.0`; leave this pending when no 10.11 server is available.
