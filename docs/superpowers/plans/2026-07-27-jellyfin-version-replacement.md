# Jellyfin Version Replacement Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the mistaken CalVer-labelled Jellyfin 12 release with conventional SponsorBlock version `2.0.0.0` while retaining `1.1.12.0` for Jellyfin 10.11.

**Architecture:** Change only release identity and documentation; runtime behavior remains the already verified Jellyfin 12 implementation. Build one replacement commit above `v1.1.12.0`, publish and verify `v2.0.0.0` before rewriting `main`, then remove the CalVer release only after every replacement gate passes.

**Tech Stack:** .NET 10, Jellyfin Controller 12, JSON plugin manifest, Bash packaging, Git, GitHub CLI, Unraid Docker.

---

## File Map

- Modify: `Jellyfin.Plugin.SponsorBlock/Jellyfin.Plugin.SponsorBlock.csproj` - assembly/file version `2.0.0.0`.
- Modify: `manifest.json` - Jellyfin 12 version, URL, checksum, and compatibility ordering.
- Modify: `README.md` - public compatibility, installation, and build examples.
- Modify: `docs/superpowers/specs/2026-07-27-jellyfin-12-compatibility-design.md` - active release version references.
- Modify: `docs/superpowers/specs/2026-07-27-jellyfin-12-release-design.md` - active release version references.
- Modify: `docs/superpowers/plans/2026-07-27-jellyfin-12-compatibility.md` - active release/deployment commands.
- Modify: `docs/superpowers/plans/2026-07-27-jellyfin-12-release.md` - active release/publication commands.
- Preserve: `docs/superpowers/specs/2026-07-27-jellyfin-version-replacement-design.md` - root-cause record intentionally referencing the withdrawn version.
- Preserve: `docs/superpowers/plans/2026-07-27-jellyfin-version-replacement.md` - replacement execution record.
- Preserve: both unrelated untracked screenshots.

### Task 1: Lock The Published Baseline

**Files:**
- Inspect: Git branch, tags, releases, assets, and manifest

- [ ] **Step 1: Refresh and verify the exact rewrite lease**

Run:

```bash
git fetch origin main --tags
git rev-parse origin/main
git rev-parse v2026.07.27.0^{commit}
git rev-parse v1.1.12.0^{commit}
git status --short --branch
```

Expected: `origin/main` and `v2026.07.27.0` are `724079136719949ca2c88c981b5644db17042665`; `v1.1.12.0` is `cd37b21059a3f61ead8996353c0c84ed1b23a3a8`; only the two screenshots are untracked apart from temporary approved design/plan commits.

- [ ] **Step 2: Capture current GitHub recovery evidence**

Run:

```bash
gh release view v2026.07.27.0 --json name,tagName,body,assets,url
gh release view v1.1.12.0 --json name,tagName,body,assets,url
gh release download v2026.07.27.0 --pattern jellyfin-plugin-sponsorblock-2026.07.27.0.zip --dir /var/folders/lw/63whhjrj1j99ztk_tdwwqz_c0000gn/T/opencode/sponsorblock-release-plan --clobber
md5 -q /var/folders/lw/63whhjrj1j99ztk_tdwwqz_c0000gn/T/opencode/sponsorblock-release-plan/jellyfin-plugin-sponsorblock-2026.07.27.0.zip
```

Expected CalVer asset MD5: `464008e249edddb842e4cb2291c86f35`. Do not delete it yet.

### Task 2: Prove And Correct Version Metadata

**Files:**
- Modify: `Jellyfin.Plugin.SponsorBlock/Jellyfin.Plugin.SponsorBlock.csproj:12-13`
- Modify: `manifest.json:8-25`
- Modify: `README.md:9-40,136-141`

- [ ] **Step 1: Run the metadata gate before correction**

Run:

```bash
grep -q '<AssemblyVersion>2.0.0.0</AssemblyVersion>' Jellyfin.Plugin.SponsorBlock/Jellyfin.Plugin.SponsorBlock.csproj
```

Expected: FAIL because the assembly still carries `2026.7.27.0`.

- [ ] **Step 2: Change assembly and file versions**

Set the project properties to:

```xml
<AssemblyVersion>2.0.0.0</AssemblyVersion>
<FileVersion>2.0.0.0</FileVersion>
```

- [ ] **Step 3: Replace the first manifest entry**

Use this complete Jellyfin 12 entry:

```json
{
	"version": "2.0.0.0",
	"changelog": "Add Jellyfin 12 support. Retarget to .NET 10 and Jellyfin Controller 12.0.0-rc3, implement the new media-segment cleanup contract, and preserve the event-driven SponsorBlock workflow without enabling the core library segment scan.",
	"targetAbi": "12.0.0.0",
	"sourceUrl": "https://github.com/felixfoertsch/jellyfin-sponsorblock/releases/download/v2.0.0.0/jellyfin-plugin-sponsorblock-2.0.0.0.zip",
	"checksum": "1f11eeee6c2ceda81bf4136a48410ee6",
	"timestamp": "2026-07-27T00:00:00Z"
}
```

Keep `1.1.12.0` immediately below it with ABI `10.11.0.0` and checksum `7727a9548136b7216938f70f68522480`.

- [ ] **Step 4: Update public README guidance**

Use this compatibility matrix and warning:

```markdown
| Jellyfin | SponsorBlock | Status |
|---|---|---|
| 12.x | `2.0.0.0` | Current release |
| 10.11 | `1.1.12.0` | Last compatible release |

SponsorBlock `2.0.0.0` requires Jellyfin 12 and is incompatible with older Jellyfin versions. Jellyfin 10.11 users must install [`v1.1.12.0`](https://github.com/felixfoertsch/jellyfin-sponsorblock/releases/tag/v1.1.12.0). The repository manifest keeps both versions available and lets Jellyfin select the matching ABI.
```

Change the manual directory example to `SponsorBlock_2.0.0.0` and the build command to `./scripts/package-release.sh 2.0.0.0`.

- [ ] **Step 5: Verify corrected metadata**

Run:

```bash
grep -q '<AssemblyVersion>2.0.0.0</AssemblyVersion>' Jellyfin.Plugin.SponsorBlock/Jellyfin.Plugin.SponsorBlock.csproj
grep -q '<FileVersion>2.0.0.0</FileVersion>' Jellyfin.Plugin.SponsorBlock/Jellyfin.Plugin.SponsorBlock.csproj
jq -e '.[0].versions[0] | .version == "2.0.0.0" and .targetAbi == "12.0.0.0" and .checksum == "1f11eeee6c2ceda81bf4136a48410ee6"' manifest.json
jq -e '.[0].versions[1] | .version == "1.1.12.0" and .targetAbi == "10.11.0.0" and .checksum == "7727a9548136b7216938f70f68522480"' manifest.json
```

Expected: all commands pass and both `jq` commands print `true`.

### Task 3: Update Active Release Documentation

**Files:**
- Modify: four compatibility/release design and plan files listed in File Map
- Preserve: replacement design and plan historical references

- [ ] **Step 1: Replace active release identity**

In the four older compatibility/release documents, replace:

```text
2026.07.27.0 -> 2.0.0.0
2026.7.27.0  -> 2.0.0.0
v2026.07.27.0 -> v2.0.0.0
jellyfin-plugin-sponsorblock-2026.07.27.0.zip -> jellyfin-plugin-sponsorblock-2.0.0.0.zip
SponsorBlock_2026.07.27.0 -> SponsorBlock_2.0.0.0
464008e249edddb842e4cb2291c86f35 -> 1f11eeee6c2ceda81bf4136a48410ee6
```

Do not replace the withdrawn-version explanations or rollback paths in the replacement design/plan.

- [ ] **Step 2: Verify active documentation**

Run a scoped search over `README.md`, `manifest.json`, the project file, and the four older compatibility/release documents.

Expected: no CalVer release identity remains in those active files; replacement design/plan references remain as intentional historical and cleanup instructions.

- [ ] **Step 3: Commit the temporary correction**

Inspect `git status`, `git diff`, and `git log --oneline -10`, stage only intended tracked files, then run:

```bash
git commit -m "replace CalVer with Jellyfin plugin version"
```

Expected: screenshots remain unstaged.

### Task 4: Build And Test The Replacement Tree

**Files:**
- Test: complete solution
- Create: ignored `artifacts/jellyfin-plugin-sponsorblock-2.0.0.0.zip`

- [ ] **Step 1: Run tests and build**

Run:

```bash
mise exec -- dotnet test --configuration Release
mise exec -- dotnet build --configuration Release --no-restore
```

Expected: 86 tests pass; build has 0 warnings and 0 errors.

- [ ] **Step 2: Package and verify the replacement**

Run:

```bash
mise exec -- ./scripts/package-release.sh 2.0.0.0
md5 -q artifacts/jellyfin-plugin-sponsorblock-2.0.0.0.zip
shasum -a 256 artifacts/jellyfin-plugin-sponsorblock-2.0.0.0.zip
unzip -Z1 artifacts/jellyfin-plugin-sponsorblock-2.0.0.0.zip
```

Expected MD5: `1f11eeee6c2ceda81bf4136a48410ee6`.

Expected SHA-256: `238fa5f6a4cc50bf38c59cbb9464c511faa46d738e23af1317dd1398459e62af`.

Expected ZIP member: exactly `Jellyfin.Plugin.SponsorBlock.dll`.

### Task 5: Rebuild The Single Release Commit

**Files:**
- Rewrite: local `main` above `v1.1.12.0`
- Preserve: verified tracked tree and screenshots

- [ ] **Step 1: Recheck the remote lease and record the tree**

Run:

```bash
git fetch origin main --tags
git rev-parse origin/main
git rev-parse HEAD^{tree}
git diff --check v1.1.12.0..HEAD
git status --short --branch
```

Expected remote main: `724079136719949ca2c88c981b5644db17042665`. Record the tree ID. Stop if remote main differs.

- [ ] **Step 2: Soft-reset to the last Jellyfin 10.11 release**

Run:

```bash
git reset --soft v1.1.12.0
git diff --cached --check
git status --short
```

Expected: all intended Jellyfin 12 implementation, tests, docs, and `2.0.0.0` metadata are staged; screenshots remain untracked.

- [ ] **Step 3: Create one replacement commit**

Run:

```bash
git commit -m "release SponsorBlock 2.0.0.0 for Jellyfin 12, preserve Jellyfin 10.11 package"
git rev-list --count v1.1.12.0..HEAD
git rev-parse HEAD^{tree}
```

Expected: count `1`; tree ID matches the pre-reset tree.

### Task 6: Verify Reproducibility And Review

**Files:**
- Verify: replacement commit in main checkout and detached worktree

- [ ] **Step 1: Rerun local release gates**

Run tests, build, package, JSON assertions, ZIP inspection, `git diff --check`, and one-commit history checks again.

Expected: all Task 4 checks pass unchanged.

- [ ] **Step 2: Reproduce in a detached worktree**

Run from the main checkout:

```bash
git worktree add --detach /var/folders/lw/63whhjrj1j99ztk_tdwwqz_c0000gn/T/opencode/sponsorblock-2-release-verify HEAD
```

Run inside the detached worktree:

```bash
mise install
mise exec -- dotnet restore
mise exec -- dotnet test --configuration Release
mise exec -- ./scripts/package-release.sh 2.0.0.0
md5 -q artifacts/jellyfin-plugin-sponsorblock-2.0.0.0.zip
```

Expected: 86 tests pass and ZIP MD5 is `1f11eeee6c2ceda81bf4136a48410ee6`. Remove the worktree afterward.

```bash
git worktree remove /var/folders/lw/63whhjrj1j99ztk_tdwwqz_c0000gn/T/opencode/sponsorblock-2-release-verify
```

- [ ] **Step 3: Request independent final review**

Review `v1.1.12.0..HEAD` against the replacement spec. Resolve all Critical and Important findings before publication.

### Task 7: Publish Replacement Before Withdrawing CalVer

**Files:**
- Create: tag/release `v2.0.0.0`
- Rewrite: GitHub `main` with force-with-lease
- Modify: `v1.1.12.0` release notes
- Delete after verification: `v2026.07.27.0` release/tag

- [ ] **Step 1: Push and publish the replacement tag**

Run:

```bash
git tag -a v2.0.0.0 -m "SponsorBlock 2.0.0.0 for Jellyfin 12"
git push origin v2.0.0.0
gh release create v2.0.0.0 artifacts/jellyfin-plugin-sponsorblock-2.0.0.0.zip --verify-tag --latest --title "v2.0.0.0 (Jellyfin 12 only)" --notes $'## Compatibility\n\n**Jellyfin 12 only. This build is incompatible with Jellyfin 10.x and all other older Jellyfin versions.**\n\nJellyfin 10.11 users must install [SponsorBlock v1.1.12.0](https://github.com/felixfoertsch/jellyfin-sponsorblock/releases/tag/v1.1.12.0) instead. The plugin repository manifest retains both builds and selects the matching ABI.\n\n## Changes\n\n- Retarget the plugin to .NET 10 and Jellyfin 12.\n- Implement Jellyfin 12\'s media-segment cleanup contract.\n- Preserve the event-driven SponsorBlock workflow without enabling Jellyfin\'s full-library media-segment scan.\n- Produce a deterministic DLL-only release package.\n- Preserve existing SponsorBlock configuration, state, and stored segments when Jellyfin and the plugin are upgraded together.'
```

Expected: public replacement release exists with one ZIP.

- [ ] **Step 2: Verify the replacement release before rewriting main**

Run:

```bash
gh release download v2.0.0.0 --pattern jellyfin-plugin-sponsorblock-2.0.0.0.zip --dir /var/folders/lw/63whhjrj1j99ztk_tdwwqz_c0000gn/T/opencode/sponsorblock-release-plan --clobber
md5 -q /var/folders/lw/63whhjrj1j99ztk_tdwwqz_c0000gn/T/opencode/sponsorblock-release-plan/jellyfin-plugin-sponsorblock-2.0.0.0.zip
shasum -a 256 /var/folders/lw/63whhjrj1j99ztk_tdwwqz_c0000gn/T/opencode/sponsorblock-release-plan/jellyfin-plugin-sponsorblock-2.0.0.0.zip
unzip -Z1 /var/folders/lw/63whhjrj1j99ztk_tdwwqz_c0000gn/T/opencode/sponsorblock-release-plan/jellyfin-plugin-sponsorblock-2.0.0.0.zip
```

Expected: MD5 `1f11eeee6c2ceda81bf4136a48410ee6`, SHA-256 `238fa5f6a4cc50bf38c59cbb9464c511faa46d738e23af1317dd1398459e62af`, and exactly `Jellyfin.Plugin.SponsorBlock.dll`.

- [ ] **Step 3: Rewrite main with the exact lease**

Run:

```bash
git push --force-with-lease=refs/heads/main:724079136719949ca2c88c981b5644db17042665 origin HEAD:main
```

Expected: remote `main` moves to the verified replacement commit. No unconditional force is used.

- [ ] **Step 4: Verify public manifest, then update old release notes**

Run:

```bash
gh api 'repos/felixfoertsch/jellyfin-sponsorblock/contents/manifest.json?ref=main' --jq .content | base64 --decode | jq -e '.[0].versions[0] | .version == "2.0.0.0" and .targetAbi == "12.0.0.0" and .checksum == "1f11eeee6c2ceda81bf4136a48410ee6"'
gh api 'repos/felixfoertsch/jellyfin-sponsorblock/contents/manifest.json?ref=main' --jq .content | base64 --decode | jq -e '.[0].versions[1] | .version == "1.1.12.0" and .targetAbi == "10.11.0.0" and .checksum == "7727a9548136b7216938f70f68522480"'
gh release edit v1.1.12.0 --title "v1.1.12.0 (Jellyfin 10.11)" --notes $'## Compatibility\n\n**This is the last SponsorBlock release for Jellyfin 10.11. Do not install the Jellyfin 12 build on Jellyfin 10.11.**\n\nSerialize SponsorBlock processing to avoid concurrent refresh and playback work for the same item. Bumps the plugin version for local install.\n\nJellyfin 12 users must install [SponsorBlock v2.0.0.0](https://github.com/felixfoertsch/jellyfin-sponsorblock/releases/tag/v2.0.0.0).'
```

Expected: both JSON assertions print `true`; the historical release retains its existing ZIP and links to `v2.0.0.0`.

- [ ] **Step 5: Remove only the mistaken release and tag**

Run:

```bash
gh release delete v2026.07.27.0 --cleanup-tag --yes
git tag -d v2026.07.27.0
```

Expected: `v2.0.0.0` is latest; `v1.1.12.0` remains; no remote/local CalVer tag or GitHub release remains.

### Task 8: Replace Production Plugin

**Files:**
- Move: `/mnt/cache/appdata/jellyfin/plugins/SponsorBlock_2026.07.27.0/`
- Create: `/mnt/cache/appdata/jellyfin/plugins/SponsorBlock_2.0.0.0/`
- Preserve: configuration and SponsorBlock state database

- [ ] **Step 1: Establish healthy baseline**

Run:

```bash
ssh unraid "docker inspect --format '{{.State.Status}} {{.State.Health.Status}}' jellyfin"
ssh unraid "curl --silent --output /dev/null --write-out '%{http_code} %{time_total}' --max-time 15 http://127.0.0.1:8096/Users/Public"
curl --silent --output /dev/null --write-out '%{http_code} %{time_total}' --max-time 15 https://netfelix.jetzt/Users/Public
ssh unraid "docker stats --no-stream --format '{{.CPUPerc}} {{.MemUsage}} {{.PIDs}}' jellyfin"
```

Expected: running/healthy, both endpoints return `200`, and resource counts remain near the established healthy baseline.

- [ ] **Step 2: Stop Jellyfin and archive the CalVer plugin**

Run:

```bash
ssh unraid "test -d /mnt/cache/appdata/jellyfin/plugins/SponsorBlock_2026.07.27.0"
ssh unraid "test ! -e /mnt/cache/appdata/jellyfin/plugin-rollbacks/2026-07-27/SponsorBlock_2026.07.27.0"
ssh unraid "docker stop jellyfin"
ssh unraid "mv /mnt/cache/appdata/jellyfin/plugins/SponsorBlock_2026.07.27.0 /mnt/cache/appdata/jellyfin/plugin-rollbacks/2026-07-27/SponsorBlock_2026.07.27.0"
```

Expected: only the CalVer plugin directory moves; `SponsorBlock_1.1.12.0/` remains in the rollback directory.

- [ ] **Step 3: Install and start `2.0.0.0`**

Run:

```bash
ssh unraid "mkdir -p /mnt/cache/appdata/jellyfin/plugins/SponsorBlock_2.0.0.0"
scp Jellyfin.Plugin.SponsorBlock/bin/Release/net10.0/Jellyfin.Plugin.SponsorBlock.dll unraid:/mnt/cache/appdata/jellyfin/plugins/SponsorBlock_2.0.0.0/Jellyfin.Plugin.SponsorBlock.dll
ssh unraid "docker start jellyfin"
```

Poll local `/Users/Public` with a 15-second request timeout until it returns `200`; do not use a fixed readiness assumption.

- [ ] **Step 4: Verify production state and behavior**

Run:

```bash
ssh unraid "docker logs --since 5m jellyfin 2>&1 | grep -E 'Loaded plugin: SponsorBlock|SponsorBlock.*(TypeLoadException|ReflectionTypeLoadException|Failed to load assembly)'"
ssh unraid "ls -lh /mnt/cache/appdata/jellyfin/plugins/configurations/Jellyfin.Plugin.SponsorBlock.xml /mnt/cache/appdata/jellyfin/data/c0e51a88-71a0-4f5c-82dc-81b8ae1a3e0f/sponsorblock-state.db"
ssh unraid "sqlite3 /mnt/cache/appdata/jellyfin/data/jellyfin.db \"SELECT COUNT(*), COUNT(DISTINCT ItemId) FROM MediaSegments WHERE SegmentProviderId = '4bc99c625103c30a9a5dbcaa3ace155c';\""
md5 -q Jellyfin.Plugin.SponsorBlock/bin/Release/net10.0/Jellyfin.Plugin.SponsorBlock.dll
ssh unraid "md5sum /mnt/cache/appdata/jellyfin/plugins/SponsorBlock_2.0.0.0/Jellyfin.Plugin.SponsorBlock.dll"
```

Expected: `Loaded plugin: SponsorBlock 2.0.0.0`; no SponsorBlock type-load error; config/state files remain; query returns `7460|2924`; local and remote DLL MD5 values match. Confirm no broad SponsorBlock processing appears in startup logs.

Ask the user to play one eligible pending YouTube item for the bounded lookup and `Vaccines and Autism: A Measured Response` for the known stored-segment skip. Inspect logs/state after each confirmation, then rerun the baseline endpoint and resource commands.

### Task 9: Verify Final State And Close Tracking

**Files:**
- Verify: repository, GitHub, Unraid
- Modify after separate scoped approval: existing Jellyfin project memory and durable TODO

- [ ] **Step 1: Run final release verification**

Require 86 tests, zero-warning build, exact local/public ZIP hashes, branch/tag identity, one commit above `v1.1.12.0`, clean tracked worktree, preserved screenshots, public compatibility matrix, and no active CalVer tag/release/manifest entry.

- [ ] **Step 2: Request scoped memory-promotion approval**

Propose updating the existing Jellyfin project memory with `2.0.0.0`, the replacement commit/tag/release, corrected compatibility contract/checksums, withdrawn CalVer release, and production deployment evidence. Include TODO disposition and request explicit approval.

- [ ] **Step 3: Validate approved memory and close only the replacement TODO**

After approval, run memory lint, `qmd update`, `qmd embed`, and exact retrieval. Mark only the version-replacement outcome done; leave monitor authentication and Jellyfin 10.11 GG-3 open.
