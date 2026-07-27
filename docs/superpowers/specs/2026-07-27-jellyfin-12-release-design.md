# Jellyfin 12 Release Design

> **Publication update:** The original CalVer publication was withdrawn. Remote mutation and failure-handling requirements are superseded by `2026-07-27-jellyfin-version-replacement-design.md`.

## Goal

Publish SponsorBlock `2.0.0.0` as a Jellyfin 12-only release while keeping the existing Jellyfin 10.11 build installable through the same plugin repository and through GitHub Releases.

## Compatibility Contract

The repository manifest remains the authoritative compatibility selector:

| Jellyfin version | SponsorBlock version | Target ABI |
|---|---|---|
| 12.x | `2.0.0.0` | `12.0.0.0` |
| 10.11 | `1.1.12.0` | `10.11.0.0` |

SponsorBlock `2.0.0.0` must be described as incompatible with Jellyfin 10.x and any other pre-12 release. Users on Jellyfin 10.11 must be directed to `1.1.12.0`. Existing historical versions remain in the manifest and GitHub Releases.

The README will put this matrix near the requirements and installation instructions. The new GitHub release title and the first paragraph of its notes will say `Jellyfin 12 only`. The existing `v1.1.12.0` release metadata will identify it as the Jellyfin 10.11 release without replacing its tag or asset.

## Manifest

Keep the existing `2.0.0.0` entry with `targetAbi: 12.0.0.0`. Restore a `1.1.12.0` entry immediately below it with:

- `targetAbi: 10.11.0.0`
- the existing `v1.1.12.0` GitHub asset URL
- the MD5 checksum calculated from that published asset
- the original release timestamp and changelog

This keeps a single repository URL and lets Jellyfin select a compatible package. No separate compatibility branch or manifest is required.

## History

Preserve `origin/main` and every published historical tag. Replace the 12 unpublished local Jellyfin 12 implementation commits plus the temporary release design and plan commits with one release commit directly above `origin/main`. Include the approved compatibility documentation and release metadata in that commit.

The resulting graph must be:

```text
origin/main (v1.1.12.0)
  |
  +-- one SponsorBlock 2.0.0.0 release commit (v2.0.0.0)
```

Because the original Jellyfin 12 commit was published before its version was corrected, replace it only through the approved force-with-lease pinned in the version-replacement design. Stop if remote `main` changes.

The two unrelated untracked screenshots remain untouched.

## GitHub Release

Create annotated tag `v2.0.0.0` on the squashed release commit. Create a non-prerelease GitHub release named `v2.0.0.0 (Jellyfin 12 only)`, mark it latest, and attach `jellyfin-plugin-sponsorblock-2.0.0.0.zip`.

The release notes must:

- lead with a warning that this build requires Jellyfin 12 and does not load on older Jellyfin versions
- direct Jellyfin 10.11 users to the existing `v1.1.12.0` release
- summarize the .NET 10/Jellyfin 12 retarget, cleanup contract, deterministic packaging, and preserved event-driven segment workflow
- state that upgrading Jellyfin and the plugin together preserves existing SponsorBlock configuration and state

Update only the title/body of `v1.1.12.0` to mark it as the Jellyfin 10.11 release. Do not replace its tag or ZIP.

## Verification And Failure Handling

Before rewriting history, verify the worktree and compare the full change set with `origin/main`. Build, test, and package before publication. Generate the package twice from clean checkouts and require identical MD5 checksums. Require the ZIP to contain exactly `Jellyfin.Plugin.SponsorBlock.dll`.

Validate `manifest.json` as JSON and verify both leading entries, target ABIs, URLs, and checksums. Download the published `v1.1.12.0` asset to calculate its MD5 instead of trusting undocumented local state.

After the squash, require exactly one commit above `v1.1.12.0`, rerun tests/build/package, and verify the package checksum still matches the manifest. Publish and verify the replacement tag/release before rewriting `main` with the approved force-with-lease. Delete the withdrawn release only after branch, manifest, and public asset checks pass.

Do not create the release if tests, build, deterministic packaging, manifest validation, or history checks fail. If release creation partially succeeds, keep the published tag and asset only when they match the verified commit/package; otherwise delete only the incomplete new release/tag and retry. Never modify historical release binaries.

## Guided Gates

- GG-1: Open the GitHub release page and verify the title and first paragraph clearly say Jellyfin 12 only.
- GG-2: Open the plugin repository in Jellyfin 12 and verify `2.0.0.0` is offered.
- GG-3: On Jellyfin 10.11, verify the same repository offers `1.1.12.0` rather than the Jellyfin 12 build.
