# SponsorBlock Jellyfin 12 Compatibility Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build and deploy a Jellyfin 12-only SponsorBlock plugin that satisfies the new media-segment provider contract without enabling Jellyfin's full-library segment scan.

**Architecture:** Compile directly against `Jellyfin.Controller 12.0.0-rc3` on .NET 10. Keep `SponsorBlockSegmentProvider` as a registration-only stub and implement Jellyfin 12's cleanup callback as a no-op because the provider owns no extracted analysis cache; the existing orchestrator, state store, and media-segment writer remain unchanged.

**Tech Stack:** .NET 10.0.302, C#, Jellyfin Controller 12.0.0-rc3, xUnit, NSubstitute, Bash packaging, Unraid Docker

---

## File Map

- `.mise.toml`: pins the .NET 10 SDK used for the Jellyfin 12 build.
- `Jellyfin.Plugin.SponsorBlock/Jellyfin.Plugin.SponsorBlock.csproj`: targets .NET 10, pins Jellyfin 12 RC3, carries the four-part plugin release version.
- `Jellyfin.Plugin.SponsorBlock.Tests/Jellyfin.Plugin.SponsorBlock.Tests.csproj`: aligns tests with .NET 10 dependencies.
- `Jellyfin.Plugin.SponsorBlock/SponsorBlockSegmentProvider.cs`: implements Jellyfin 12's cleanup callback while preserving registration-only behavior.
- `Jellyfin.Plugin.SponsorBlock.Tests/SponsorBlockSegmentProviderTests.cs`: proves the cleanup callback completes without side effects.
- `Jellyfin.Plugin.SponsorBlock.Tests/PluginServiceRegistratorTests.cs`: proves the provider remains registered after the API migration.
- `scripts/package-release.sh`: packages the `net10.0` Release DLL.
- `README.md`: documents the Jellyfin 12-only requirement and net10 build path.
- `manifest.json`: advertises the Jellyfin 12 ABI and four-part plugin package.

### Task 1: Retarget And Implement The Jellyfin 12 Provider Contract

**Files:**
- Modify: `.mise.toml`
- Modify: `Jellyfin.Plugin.SponsorBlock/Jellyfin.Plugin.SponsorBlock.csproj`
- Modify: `Jellyfin.Plugin.SponsorBlock.Tests/Jellyfin.Plugin.SponsorBlock.Tests.csproj`
- Create: `Jellyfin.Plugin.SponsorBlock.Tests/SponsorBlockSegmentProviderTests.cs`
- Modify: `Jellyfin.Plugin.SponsorBlock.Tests/PluginServiceRegistratorTests.cs`
- Modify: `Jellyfin.Plugin.SponsorBlock/SponsorBlockSegmentProvider.cs`

- [ ] **Step 1: Pin the Jellyfin 12 toolchain and package contract**

Set `.mise.toml` to:

```toml
[tools]
dotnet = "10.0.302"
```

In both project files, replace `net9.0` with `net10.0`. In the production project, replace the floating Jellyfin reference and SQLite version with:

```xml
<PackageReference Include="Jellyfin.Controller" Version="12.0.0-rc3" />
<PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.10" />
```

In the test project, replace the time-provider package with:

```xml
<PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" Version="10.0.0" />
```

Run:

```bash
mise install
mise exec -- dotnet restore Jellyfin.Plugin.SponsorBlock.sln
```

Expected: the .NET `10.0.302` SDK installs and restore resolves `Jellyfin.Controller 12.0.0-rc3` without version drift.

- [ ] **Step 2: Add the failing v12 cleanup test**

Create `Jellyfin.Plugin.SponsorBlock.Tests/SponsorBlockSegmentProviderTests.cs`:

```csharp
namespace Jellyfin.Plugin.SponsorBlock.Tests;

public class SponsorBlockSegmentProviderTests
{
	[Fact]
	public async Task CleanupExtractedData_CompletesWithoutWork()
	{
		var provider = new SponsorBlockSegmentProvider();

		await provider.CleanupExtractedData(Guid.NewGuid(), CancellationToken.None);
	}
}
```

Add these imports to `PluginServiceRegistratorTests.cs`:

```csharp
using MediaBrowser.Controller.MediaSegments;
```

Add this test to `PluginServiceRegistratorTests`:

```csharp
[Fact]
public void RegisterServices_RegistersSponsorBlockSegmentProvider()
{
	var services = new ServiceCollection();

	new PluginServiceRegistrator().RegisterServices(services, Substitute.For<IServerApplicationHost>());

	Assert.Contains(services, descriptor =>
		descriptor.ServiceType == typeof(IMediaSegmentProvider)
		&& descriptor.ImplementationType == typeof(SponsorBlockSegmentProvider)
		&& descriptor.Lifetime == ServiceLifetime.Singleton);
}
```

- [ ] **Step 3: Run the focused tests to verify the contract is red**

Run:

```bash
mise exec -- dotnet test Jellyfin.Plugin.SponsorBlock.Tests/Jellyfin.Plugin.SponsorBlock.Tests.csproj --filter "FullyQualifiedName~SponsorBlockSegmentProviderTests|FullyQualifiedName~PluginServiceRegistratorTests"
```

Expected: build fails with `CS0535` because `SponsorBlockSegmentProvider` does not implement `IMediaSegmentProvider.CleanupExtractedData(Guid, CancellationToken)`; the new test also cannot resolve the missing method.

- [ ] **Step 4: Implement the minimal no-op callback**

Add to `SponsorBlockSegmentProvider` after `GetMediaSegments`:

```csharp
/// <inheritdoc />
public Task CleanupExtractedData(Guid itemId, CancellationToken cancellationToken)
	=> Task.CompletedTask;
```

Do not call `IMediaSegmentWriter`, `IResetService`, or the SponsorBlock state store from this method. The provider has no extracted cache, and Jellyfin invokes this callback while pruning its own generated analysis data.

- [ ] **Step 5: Run focused and full tests**

Run:

```bash
mise exec -- dotnet test Jellyfin.Plugin.SponsorBlock.Tests/Jellyfin.Plugin.SponsorBlock.Tests.csproj --filter "FullyQualifiedName~SponsorBlockSegmentProviderTests|FullyQualifiedName~PluginServiceRegistratorTests"
mise exec -- dotnet test Jellyfin.Plugin.SponsorBlock.sln
```

Expected: focused tests pass; the full suite reports zero failures.

- [ ] **Step 6: Commit the compatibility unit**

```bash
git add .mise.toml Jellyfin.Plugin.SponsorBlock/Jellyfin.Plugin.SponsorBlock.csproj Jellyfin.Plugin.SponsorBlock.Tests/Jellyfin.Plugin.SponsorBlock.Tests.csproj Jellyfin.Plugin.SponsorBlock/SponsorBlockSegmentProvider.cs Jellyfin.Plugin.SponsorBlock.Tests/SponsorBlockSegmentProviderTests.cs Jellyfin.Plugin.SponsorBlock.Tests/PluginServiceRegistratorTests.cs
git commit -m "target Jellyfin 12, implement segment cleanup contract"
```

### Task 2: Version, Package, And Document The Jellyfin 12 Build

**Files:**
- Modify: `Jellyfin.Plugin.SponsorBlock/Jellyfin.Plugin.SponsorBlock.csproj`
- Modify: `scripts/package-release.sh`
- Modify: `README.md`
- Modify: `manifest.json`
- Create: `artifacts/jellyfin-plugin-sponsorblock-2.0.0.0.zip` (ignored build output)

- [ ] **Step 1: Apply the Jellyfin plugin assembly version**

Set both values in `Jellyfin.Plugin.SponsorBlock.csproj`:

```xml
<AssemblyVersion>2.0.0.0</AssemblyVersion>
<FileVersion>2.0.0.0</FileVersion>
```

- [ ] **Step 2: Update the package script for net10**

Change the release DLL path in `scripts/package-release.sh` to:

```bash
local dll_path="$root/Jellyfin.Plugin.SponsorBlock/bin/Release/net10.0/Jellyfin.Plugin.SponsorBlock.dll"
```

- [ ] **Step 3: Update active documentation**

In `README.md`, replace the requirement and build output references with:

```markdown
- Jellyfin 12.0.0-rc3
```

```markdown
./scripts/package-release.sh 2.0.0.0
```

```markdown
The plugin DLL is at `Jellyfin.Plugin.SponsorBlock/bin/Release/net10.0/Jellyfin.Plugin.SponsorBlock.dll`.
```

Update the manual-install example directory to `SponsorBlock_2.0.0.0`.

- [ ] **Step 4: Build the DLL-only package and capture its checksum**

Run:

```bash
mise exec -- ./scripts/package-release.sh 2.0.0.0
zipinfo -1 artifacts/jellyfin-plugin-sponsorblock-2.0.0.0.zip
```

Expected: the script prints one 32-character MD5 checksum, and `zipinfo` prints exactly `Jellyfin.Plugin.SponsorBlock.dll`.

- [ ] **Step 5: Add the manifest entry using the emitted checksum**

Run this deterministic manifest transform from the repository root:

```bash
checksum="$(md5 -q artifacts/jellyfin-plugin-sponsorblock-2.0.0.0.zip)"
jq --arg checksum "$checksum" '.[0].versions = ([{
	version: "2.0.0.0",
	changelog: "Add Jellyfin 12 support. Retarget to .NET 10 and Jellyfin Controller 12.0.0-rc3, implement the new media-segment cleanup contract, and preserve the event-driven SponsorBlock workflow without enabling the core library segment scan.",
	targetAbi: "12.0.0.0",
	sourceUrl: "https://github.com/felixfoertsch/jellyfin-sponsorblock/releases/download/v2.0.0.0/jellyfin-plugin-sponsorblock-2.0.0.0.zip",
	checksum: $checksum,
	timestamp: "2026-07-27T00:00:00Z"
}] + .[0].versions)' manifest.json > manifest.json.tmp
mv manifest.json.tmp manifest.json
```

Expected: the first manifest entry contains the package's actual lowercase MD5; no descriptive or sentinel checksum remains.

- [ ] **Step 6: Validate release metadata and rerun all tests**

Run:

```bash
jq empty manifest.json
mise exec -- dotnet test Jellyfin.Plugin.SponsorBlock.sln
mise exec -- dotnet build Jellyfin.Plugin.SponsorBlock.sln -c Release --no-restore
git diff --check
```

Expected: valid JSON, zero test failures, successful Release build, and no whitespace errors.

- [ ] **Step 7: Commit the release unit**

```bash
git add Jellyfin.Plugin.SponsorBlock/Jellyfin.Plugin.SponsorBlock.csproj scripts/package-release.sh README.md manifest.json
git commit -m "package SponsorBlock 2.0.0.0 for Jellyfin 12"
```

Do not stage `artifacts/`, the two existing untracked screenshots, or unrelated files.

### Task 3: Review The Release Candidate

**Files:**
- Review: all changes since `2d6b6e7`

- [ ] **Step 1: Inspect repository state and complete diff**

Run:

```bash
git status --short --branch
git diff 2d6b6e7..HEAD --stat
git diff 2d6b6e7..HEAD
git log --oneline -5
```

Expected: only the compatibility, test, version, packaging, README, and manifest changes are committed; the two screenshots remain untracked.

- [ ] **Step 2: Request an independent code review**

Use `superpowers:requesting-code-review` and ask the reviewer to verify:

- The plugin compiles against Jellyfin 12 RC3 rather than a floating API.
- `CleanupExtractedData` is side-effect-free.
- The stub still returns `false` from `Supports`.
- Existing provider registration and provider ID remain unchanged.
- Packaging includes only the plugin DLL.
- No 10.11 compatibility code or unrelated behavior change was added.

Expected: no unresolved high- or medium-severity findings before deployment.

- [ ] **Step 3: Run the final local gate**

Run:

```bash
mise exec -- dotnet test Jellyfin.Plugin.SponsorBlock.sln
mise exec -- dotnet build Jellyfin.Plugin.SponsorBlock.sln -c Release --no-restore
zipinfo -1 artifacts/jellyfin-plugin-sponsorblock-2.0.0.0.zip
git diff --check 2d6b6e7..HEAD
```

Expected: zero test failures, successful build, one DLL in the package, and no whitespace errors.

### Task 4: Deploy And Verify On Jellyfin 12

**Files:**
- Deploy: `Jellyfin.Plugin.SponsorBlock/bin/Release/net10.0/Jellyfin.Plugin.SponsorBlock.dll`
- Preserve: `/mnt/cache/appdata/jellyfin/plugins/SponsorBlock_1.1.12.0/`
- Create: `/mnt/cache/appdata/jellyfin/plugin-rollbacks/2026-07-27/SponsorBlock_1.1.12.0/`
- Create: `/mnt/cache/appdata/jellyfin/plugins/SponsorBlock_2.0.0.0/`

- [ ] **Step 1: Establish the production baseline**

Run read-only checks:

```bash
ssh unraid "docker inspect jellyfin --format '{{.State.Status}} {{.State.Health.Status}} {{.Config.Image}} {{.State.StartedAt}}'"
ssh unraid "curl --silent --output /dev/null --write-out '%{http_code} %{time_total}' --max-time 15 http://127.0.0.1:8096/Users/Public"
ssh unraid "docker stats --no-stream --format '{{.Name}} {{.CPUPerc}} {{.MemUsage}} {{.PIDs}}' jellyfin"
```

Expected: `running healthy`, HTTP `200`, sub-second latency, and bounded PIDs before plugin deployment.

- [ ] **Step 2: Stop Jellyfin and preserve the incompatible plugin**

Run:

```bash
ssh unraid "docker stop jellyfin"
ssh unraid "mkdir -p /mnt/cache/appdata/jellyfin/plugin-rollbacks/2026-07-27"
ssh unraid "mv /mnt/cache/appdata/jellyfin/plugins/SponsorBlock_1.1.12.0 /mnt/cache/appdata/jellyfin/plugin-rollbacks/2026-07-27/SponsorBlock_1.1.12.0"
ssh unraid "mkdir -p /mnt/cache/appdata/jellyfin/plugins/SponsorBlock_2.0.0.0"
```

Expected: Jellyfin is stopped, the old plugin is outside the scanned plugin directory, and the new directory is empty.

- [ ] **Step 3: Copy the v12 DLL and start Jellyfin**

Run:

```bash
scp Jellyfin.Plugin.SponsorBlock/bin/Release/net10.0/Jellyfin.Plugin.SponsorBlock.dll unraid:/mnt/cache/appdata/jellyfin/plugins/SponsorBlock_2.0.0.0/Jellyfin.Plugin.SponsorBlock.dll
ssh unraid "docker start jellyfin"
```

Expected: the container starts with the new plugin as the only SponsorBlock assembly under `/config/plugins`.

- [ ] **Step 4: Wait on startup readiness rather than a fixed delay**

Poll this read-only command until it returns HTTP `200`, stopping investigation if the container exits or logs a fatal migration/startup error:

```bash
ssh unraid "curl --silent --output /dev/null --write-out '%{http_code} %{time_total}' --max-time 15 http://127.0.0.1:8096/Users/Public"
```

Expected: HTTP `200` after startup settles.

- [ ] **Step 5: Verify plugin loading and server health**

Run:

```bash
ssh unraid "docker logs --since 10m jellyfin 2>&1 | grep -E 'SponsorBlock|ReflectionTypeLoadException|TypeLoadException|Failed to load assembly|Startup complete'"
ssh unraid "docker stats --no-stream --format '{{.Name}} {{.CPUPerc}} {{.MemUsage}} {{.PIDs}}' jellyfin"
curl --silent --output /dev/null --write-out '%{http_code} %{time_total}' --max-time 15 https://netfelix.jetzt/Users/Public
```

Expected: SponsorBlock loads as `2.0.0.0`; no SponsorBlock type-load failure appears; the public endpoint returns `200`; resource counts remain bounded.

- [ ] **Step 6: Complete the guided functional gates**

Ask the user to complete GG-1 through GG-4 from the design. During GG-2 and GG-3, inspect:

```bash
ssh unraid "docker logs --since 10m jellyfin 2>&1 | grep -E 'SponsorBlock|Media Segment Scan|ReflectionTypeLoadException|TypeLoadException|SQLite Error 6|database table is locked'"
ssh unraid "docker stats --no-stream --format '{{.Name}} {{.CPUPerc}} {{.MemUsage}} {{.PIDs}}' jellyfin"
```

Expected: a scoped playback produces SponsorBlock processing or an intentional state-machine skip; no core library-wide media-segment scan, type-load error, SQLite lock, or PID fan-out occurs.

- [ ] **Step 7: Record verified status**

Update `/Users/felixfoertsch/.syncthing/dotfiles/tools/todo/todo.kdl` to state that implementation and deployment are verified while any incomplete guided gate remains explicit. Do not mark the action done until all guided gates pass and durable memory promotion receives separate scoped approval.
