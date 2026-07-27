# Jellyfin 12 Release Implementation Plan

> **Superseded publication plan:** Do not execute this plan's GitHub mutation steps. Use `2026-07-27-jellyfin-version-replacement.md`, which safely replaces the withdrawn CalVer publication with `2.0.0.0`.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Publish SponsorBlock `2.0.0.0` as a clearly marked Jellyfin 12-only GitHub release while retaining catalogue and manual installation of `1.1.12.0` for Jellyfin 10.11.

**Architecture:** Keep one ABI-filtered `manifest.json`: Jellyfin 12 selects `2.0.0.0`, while Jellyfin 10.11 selects `1.1.12.0`. The replacement plan owns final history rewriting and publication sequencing.

**Tech Stack:** .NET 10, Jellyfin Controller 12, JSON plugin manifest, Bash release script, Git, GitHub CLI.

---

## File Map

- Modify: `README.md` - public compatibility matrix and version-specific installation guidance.
- Modify: `manifest.json` - restore the Jellyfin 10.11 `1.1.12.0` entry next to the Jellyfin 12 entry.
- Preserve: `artifacts/jellyfin-plugin-sponsorblock-2.0.0.0.zip` - deterministic release asset.
- Preserve: `Screenshot From 2026-06-17 06-45-40 (Edit).png` - unrelated untracked user file.
- Preserve: `Screenshot From 2026-06-17 06-52-24 (Edit).png` - unrelated untracked user file.

### Task 1: Establish Release Baseline

**Files:**
- Inspect: `manifest.json`
- Inspect: `artifacts/jellyfin-plugin-sponsorblock-2.0.0.0.zip`

- [ ] **Step 1: Refresh remote state without changing the worktree**

Run:

```bash
git fetch origin main --tags
git status --short --branch
git rev-parse origin/main
git rev-list --count origin/main..HEAD
git ls-remote --tags origin
gh release list --limit 20
```

Expected: `origin/main` remains `cd37b21`; local `main` contains only unpublished work above it; `v1.1.12.0` points to `cd37b21`; no `v2.0.0.0` tag or release exists. The two screenshots remain untracked.

- [ ] **Step 2: Re-verify the existing Jellyfin 10.11 asset**

Run from the repository root after confirming the temporary parent exists:

```bash
mkdir -p /var/folders/lw/63whhjrj1j99ztk_tdwwqz_c0000gn/T/opencode/sponsorblock-release
gh release download v1.1.12.0 --pattern jellyfin-plugin-sponsorblock-1.1.12.0.zip --dir /var/folders/lw/63whhjrj1j99ztk_tdwwqz_c0000gn/T/opencode/sponsorblock-release --clobber
md5 -q /var/folders/lw/63whhjrj1j99ztk_tdwwqz_c0000gn/T/opencode/sponsorblock-release/jellyfin-plugin-sponsorblock-1.1.12.0.zip
shasum -a 256 /var/folders/lw/63whhjrj1j99ztk_tdwwqz_c0000gn/T/opencode/sponsorblock-release/jellyfin-plugin-sponsorblock-1.1.12.0.zip
unzip -Z1 /var/folders/lw/63whhjrj1j99ztk_tdwwqz_c0000gn/T/opencode/sponsorblock-release/jellyfin-plugin-sponsorblock-1.1.12.0.zip
```

Expected MD5: `7727a9548136b7216938f70f68522480`.

Expected SHA-256: `2f2993a3f428ccc2face0f90292bb2a44702f48f19b398d2f47e100596dfb9f7`.

Expected ZIP member: exactly `Jellyfin.Plugin.SponsorBlock.dll`.

### Task 2: Add Explicit Compatibility Metadata

**Files:**
- Modify: `README.md:9-29`
- Modify: `manifest.json:8-25`

- [ ] **Step 1: Add the compatibility matrix to the README**

Replace the current single Jellyfin requirement with:

```markdown
### Jellyfin compatibility

| Jellyfin | SponsorBlock | Status |
|---|---|---|
| 12.x | `2.0.0.0` | Current release |
| 10.11 | `1.1.12.0` | Last compatible release |

SponsorBlock `2.0.0.0` requires Jellyfin 12 and is incompatible with older Jellyfin versions. Jellyfin 10.11 users must install [`v1.1.12.0`](https://github.com/felixfoertsch/jellyfin-sponsorblock/releases/tag/v1.1.12.0). The repository manifest keeps both versions available and lets Jellyfin select the matching ABI.

Additional requirements:

- YouTube videos with the 11-character video ID in the filename (e.g., `dQw4w9WgXcQ.mp4`)
- **YouTube publish date** set in Jellyfin's `PremiereDate` metadata field. This is required for convergence-based polling to work.
```

Keep the existing detailed `PremiereDate` explanation after the final bullet. Update manual installation to tell users to download the release matching the table rather than always using the latest release.

- [ ] **Step 2: Restore the Jellyfin 10.11 manifest entry**

Insert this object immediately after `2.0.0.0`:

```json
{
	"version": "1.1.12.0",
	"changelog": "Serialize SponsorBlock processing to avoid concurrent refresh and playback work for the same item. Bump the plugin version for local installation.",
	"targetAbi": "10.11.0.0",
	"sourceUrl": "https://github.com/felixfoertsch/jellyfin-sponsorblock/releases/download/v1.1.12.0/jellyfin-plugin-sponsorblock-1.1.12.0.zip",
	"checksum": "7727a9548136b7216938f70f68522480",
	"timestamp": "2026-07-05T20:34:32Z"
}
```

- [ ] **Step 3: Validate compatibility selection metadata**

Run:

```bash
jq -e '.[0].versions[0] | .version == "2.0.0.0" and .targetAbi == "12.0.0.0" and .checksum == "1f11eeee6c2ceda81bf4136a48410ee6"' manifest.json
jq -e '.[0].versions[1] | .version == "1.1.12.0" and .targetAbi == "10.11.0.0" and .checksum == "7727a9548136b7216938f70f68522480"' manifest.json
jq -e '[.[0].versions[].version] | length == (unique | length)' manifest.json
git diff --check
```

Expected: all `jq` commands print `true`; `git diff --check` exits with no output.

- [ ] **Step 4: Commit the compatibility metadata**

Inspect `git status`, `git diff`, and `git log --oneline -10`, then run:

```bash
git add README.md manifest.json
git commit -m "document version compatibility, restore Jellyfin 10.11 package"
```

Expected: only `README.md` and `manifest.json` enter this temporary commit.

### Task 3: Verify The Unsquashed Release Tree

**Files:**
- Test: `Jellyfin.Plugin.SponsorBlock.Tests/`
- Verify: `artifacts/jellyfin-plugin-sponsorblock-2.0.0.0.zip`

- [ ] **Step 1: Run all tests**

Run:

```bash
dotnet test --configuration Release
```

Expected: 86 tests pass, 0 fail, 0 skip.

- [ ] **Step 2: Build with warnings treated as errors**

Run:

```bash
dotnet build --configuration Release --no-restore
```

Expected: build succeeds with 0 warnings and 0 errors.

- [ ] **Step 3: Regenerate and inspect the Jellyfin 12 package**

Run:

```bash
./scripts/package-release.sh 2.0.0.0
md5 -q artifacts/jellyfin-plugin-sponsorblock-2.0.0.0.zip
unzip -Z1 artifacts/jellyfin-plugin-sponsorblock-2.0.0.0.zip
```

Expected MD5: `1f11eeee6c2ceda81bf4136a48410ee6`.

Expected ZIP member: exactly `Jellyfin.Plugin.SponsorBlock.dll`.

### Task 4: Squash All Unpublished Work

**Files:**
- Rewrite: local `main` commits above `origin/main`
- Preserve: all tracked content at the verified tree
- Preserve: both untracked screenshots

- [ ] **Step 1: Recheck the remote boundary and save the verified tree identity**

Run:

```bash
git fetch origin main --tags
git rev-parse origin/main
git status --short --branch
git diff --stat origin/main..HEAD
git diff --check
git rev-parse HEAD^{tree}
```

Expected: `origin/main` is still `cd37b21`; no remote release work appeared; only the two screenshots are untracked; record the tree ID from the final command.

- [ ] **Step 2: Soft-reset to the published boundary**

Run:

```bash
git reset --soft origin/main
git status --short
git diff --cached --check
```

Expected: all intended Jellyfin 12 source, tests, packaging, design, plan, README, and manifest changes are staged; screenshots remain untracked and unstaged.

- [ ] **Step 3: Create the single release commit**

Run:

```bash
git commit -m "release SponsorBlock for Jellyfin 12, preserve Jellyfin 10.11 package"
git rev-list --count origin/main..HEAD
git rev-parse HEAD^{tree}
git log --oneline --decorate --graph -5
```

Expected: exactly one commit exists above `origin/main`; its tree ID equals the pre-reset tree ID; no merge commit exists.

### Task 5: Verify The Squashed Commit Reproducibly

**Files:**
- Test: complete repository at the squashed commit
- Verify: `manifest.json`
- Verify: generated release ZIP

- [ ] **Step 1: Rerun tests, build, and package**

Run:

```bash
dotnet test --configuration Release
dotnet build --configuration Release --no-restore
./scripts/package-release.sh 2.0.0.0
md5 -q artifacts/jellyfin-plugin-sponsorblock-2.0.0.0.zip
unzip -Z1 artifacts/jellyfin-plugin-sponsorblock-2.0.0.0.zip
```

Expected: 86 tests pass; build has 0 warnings/errors; ZIP MD5 is `1f11eeee6c2ceda81bf4136a48410ee6`; ZIP contains exactly the plugin DLL.

- [ ] **Step 2: Reproduce the package in an isolated worktree**

Use the `using-git-worktrees` skill, then create a detached worktree outside the repository's normal tree:

```bash
git worktree add --detach /var/folders/lw/63whhjrj1j99ztk_tdwwqz_c0000gn/T/opencode/sponsorblock-release-verify HEAD
```

In that worktree run:

```bash
mise install
dotnet restore
dotnet test --configuration Release
./scripts/package-release.sh 2.0.0.0
md5 -q artifacts/jellyfin-plugin-sponsorblock-2.0.0.0.zip
```

Expected: 86 tests pass and MD5 is `1f11eeee6c2ceda81bf4136a48410ee6`.

Remove the verification worktree after collecting evidence:

```bash
git worktree remove /var/folders/lw/63whhjrj1j99ztk_tdwwqz_c0000gn/T/opencode/sponsorblock-release-verify
```

- [ ] **Step 3: Run final local integrity checks**

Run:

```bash
jq -e '.[0].versions[0].targetAbi == "12.0.0.0" and .[0].versions[1].targetAbi == "10.11.0.0"' manifest.json
git diff --check origin/main..HEAD
git status --short --branch
```

Expected: JSON assertion is `true`; diff check passes; branch is one commit ahead; only the screenshots are untracked.

### Task 6: Superseded Publication Steps

Do not execute this task. Follow Tasks 7 and 8 in `2026-07-27-jellyfin-version-replacement.md`, which publish and verify the replacement before rewriting `main` with the pinned force-with-lease.

### Task 7: Verify Published State

**Files:**
- Verify: GitHub branch, tags, releases, assets, and raw manifest

- [ ] **Step 1: Verify branch and tag identity**

Run:

```bash
git fetch origin main --tags
test "$(git rev-parse HEAD)" = "$(git rev-parse origin/main)"
test "$(git rev-parse HEAD)" = "$(git rev-list -n 1 v2.0.0.0)"
git rev-list --count cd37b21..origin/main
```

Expected: both identity checks pass and the final count is `1`.

- [ ] **Step 2: Verify release metadata and assets**

Run:

```bash
gh release view v2.0.0.0 --json name,tagName,isDraft,isPrerelease,body,assets,url
gh release view v1.1.12.0 --json name,tagName,body,assets,url
gh release list --limit 5
```

Expected: the Jellyfin 12 release is latest, non-draft, non-prerelease, has the explicit warning, and has one ZIP. The Jellyfin 10.11 release points to its unchanged ZIP with SHA-256 `2f2993a3f428ccc2face0f90292bb2a44702f48f19b398d2f47e100596dfb9f7`.

- [ ] **Step 3: Download and verify both public assets**

Run:

```bash
gh release download v2.0.0.0 --pattern jellyfin-plugin-sponsorblock-2.0.0.0.zip --dir /var/folders/lw/63whhjrj1j99ztk_tdwwqz_c0000gn/T/opencode/sponsorblock-release --clobber
gh release download v1.1.12.0 --pattern jellyfin-plugin-sponsorblock-1.1.12.0.zip --dir /var/folders/lw/63whhjrj1j99ztk_tdwwqz_c0000gn/T/opencode/sponsorblock-release --clobber
md5 -q /var/folders/lw/63whhjrj1j99ztk_tdwwqz_c0000gn/T/opencode/sponsorblock-release/jellyfin-plugin-sponsorblock-2.0.0.0.zip
md5 -q /var/folders/lw/63whhjrj1j99ztk_tdwwqz_c0000gn/T/opencode/sponsorblock-release/jellyfin-plugin-sponsorblock-1.1.12.0.zip
```

Expected: `1f11eeee6c2ceda81bf4136a48410ee6` and `7727a9548136b7216938f70f68522480` respectively.

- [ ] **Step 4: Verify the public manifest**

Run:

```bash
gh api 'repos/felixfoertsch/jellyfin-sponsorblock/contents/manifest.json?ref=main' --jq .content | base64 --decode | jq -e '.[0].versions[0] | .version == "2.0.0.0" and .targetAbi == "12.0.0.0" and .checksum == "1f11eeee6c2ceda81bf4136a48410ee6"'
gh api 'repos/felixfoertsch/jellyfin-sponsorblock/contents/manifest.json?ref=main' --jq .content | base64 --decode | jq -e '.[0].versions[1] | .version == "1.1.12.0" and .targetAbi == "10.11.0.0" and .checksum == "7727a9548136b7216938f70f68522480"'
```

Expected: both commands print `true`.

- [ ] **Step 5: Record guided-gate status**

Verify GG-1 directly from the published release page. Record GG-2 as supported by the already verified Jellyfin 12 production deployment plus public manifest. Leave GG-3 explicitly pending unless a Jellyfin 10.11 server is available; static ABI and asset checks do not replace that manual catalogue check.

### Task 8: Close Durable Tracking

**Files:**
- Modify after scoped approval: `/Users/felixfoertsch/.syncthing/dotfiles/knowledge/memory/project-unraid-jellyfin.md`
- Modify after successful memory gates: `/Users/felixfoertsch/.syncthing/dotfiles/tools/todo/todo.kdl`

- [ ] **Step 1: Prepare a scoped memory-promotion proposal**

Search QMD and active memory for existing SponsorBlock/Jellyfin release facts. Present the exact target, project type, published commit/tag/release URLs, compatibility contract, checksums, evidence, provenance, and TODO disposition. Request explicit scoped approval before editing memory.

- [ ] **Step 2: Promote approved facts and validate memory**

After approval, update the existing focused project memory, then run:

```bash
scripts/memory-lint.sh knowledge/memory
qmd update
qmd embed
```

Confirm QMD retrieves the new release and compatibility facts.

- [ ] **Step 3: Close only the release TODO**

Mark `squash the unpublished Jellyfin 12 SponsorBlock work, publish a GitHub release that explicitly excludes older Jellyfin versions, retain installable Jellyfin 10.11 releases` done with `done="2026-07-27"`. Leave the separate Jellyfin monitor-authentication and bounded-refresh task open.
