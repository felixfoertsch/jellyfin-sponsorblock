# Jellyfin Metadata YouTube ID Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an opt-in Jellyfin metadata source for YouTube IDs while preserving filename and custom-regex behavior as the default.

**Architecture:** Add a source enum to serialized plugin configuration and branch once at the orchestrator boundary. Filename mode continues through the existing extractor; metadata mode reads Jellyfin's case-insensitive `Youtube` provider ID and validates it with the existing 11-character format. The existing configuration page conditionally exposes filename controls, and no automatic fallback or NFO parsing is introduced.

**Tech Stack:** C# 13, .NET 10, Jellyfin Controller 12, xUnit, embedded Jellyfin plugin configuration HTML and JavaScript.

## Global Constraints

- `Filename` remains the zero/default source value for backward-compatible serialized configuration.
- Existing exact-filename and custom-regex extraction behavior remains unchanged.
- Metadata mode reads only Jellyfin provider ID `Youtube` and validates an exact `[a-zA-Z0-9_-]{11}` value.
- Do not parse NFO files, register an external ID provider, support `<youtubeid>`, expose arbitrary provider keys, or fall back between sources.
- Do not alter state storage, segment fetching, convergence, scoping, or trigger behavior.
- Preserve tabs in C#, HTML, and JavaScript files.

---

## File Structure

- Create `Jellyfin.Plugin.SponsorBlock/Configuration/YouTubeIdSource.cs`: serialized source choices with filename as value zero.
- Modify `Jellyfin.Plugin.SponsorBlock/Configuration/PluginConfiguration.cs`: persist the selected source.
- Modify `Jellyfin.Plugin.SponsorBlock/YouTubeIdExtractor.cs`: expose exact YouTube ID validation for metadata without changing custom-regex behavior.
- Modify `Jellyfin.Plugin.SponsorBlock/Orchestration/SponsorBlockOrchestrator.cs`: resolve the ID from only the selected source.
- Modify `Jellyfin.Plugin.SponsorBlock.Tests/YouTubeIdExtractorTests.cs`: cover the reusable validator.
- Modify `Jellyfin.Plugin.SponsorBlock.Tests/Orchestration/SponsorBlockOrchestratorTests.cs`: prove source isolation, metadata acceptance, and rejection.
- Modify `Jellyfin.Plugin.SponsorBlock/Configuration/configPage.html`: select and persist the source, conditionally show filename controls.
- Modify `README.md`: document metadata mode and its NFO/Jellyfin limitations.

---

### Task 1: Source Model and Metadata Resolution

**Files:**
- Create: `Jellyfin.Plugin.SponsorBlock/Configuration/YouTubeIdSource.cs`
- Modify: `Jellyfin.Plugin.SponsorBlock/Configuration/PluginConfiguration.cs:10-18`
- Modify: `Jellyfin.Plugin.SponsorBlock/YouTubeIdExtractor.cs:11-40`
- Modify: `Jellyfin.Plugin.SponsorBlock/Orchestration/SponsorBlockOrchestrator.cs:77-91`
- Test: `Jellyfin.Plugin.SponsorBlock.Tests/YouTubeIdExtractorTests.cs`
- Test: `Jellyfin.Plugin.SponsorBlock.Tests/Orchestration/SponsorBlockOrchestratorTests.cs`

**Interfaces:**
- Produces: `YouTubeIdSource` enum with `Filename = 0` and `JellyfinMetadata = 1`.
- Produces: `PluginConfiguration.YouTubeIdSource` defaulting to `YouTubeIdSource.Filename`.
- Produces: `YouTubeIdExtractor.Validate(string? value) -> string?`.
- Consumes: Jellyfin `IHasProviderIds.GetProviderId("Youtube") -> string?`.

- [ ] **Step 1: Write failing configuration and validator tests**

Add these tests to `YouTubeIdExtractorTests.cs`:

```csharp
[Fact]
public void Configuration_DefaultSource_IsFilename()
{
	var config = new PluginConfiguration();

	Assert.Equal(YouTubeIdSource.Filename, config.YouTubeIdSource);
}

[Theory]
[InlineData("dQw4w9WgXcQ", "dQw4w9WgXcQ")]
[InlineData("abc-_123Abc", "abc-_123Abc")]
[InlineData("short", null)]
[InlineData("toolong123456", null)]
[InlineData("invalid$id", null)]
[InlineData("", null)]
[InlineData(null, null)]
public void Validate_ReturnsOnlyExactYouTubeIds(string? value, string? expected)
{
	Assert.Equal(expected, YouTubeIdExtractor.Validate(value));
}

[Fact]
public void RegexMode_PreservesCustomCaptureWithoutExactIdValidation()
{
	var result = YouTubeIdExtractor.Extract("video-[custom-value].mp4", FileMatchingMode.CustomRegex, @"\[([^]]+)\]");

	Assert.Equal("custom-value", result);
}
```

- [ ] **Step 2: Run the validator tests and confirm the missing API failure**

Run:

```bash
dotnet test Jellyfin.Plugin.SponsorBlock.Tests/Jellyfin.Plugin.SponsorBlock.Tests.csproj --filter FullyQualifiedName~YouTubeIdExtractorTests
```

Expected: compilation fails because `YouTubeIdSource` and `YouTubeIdExtractor.Validate` do not exist.

- [ ] **Step 3: Add the source enum, configuration property, and validator**

Create `YouTubeIdSource.cs`:

```csharp
namespace Jellyfin.Plugin.SponsorBlock.Configuration;

/// <summary>
/// Source used to resolve a video's YouTube ID.
/// </summary>
public enum YouTubeIdSource
{
	/// <summary>
	/// Resolve the ID from the media filename using the configured matching mode.
	/// </summary>
	Filename = 0,

	/// <summary>
	/// Resolve the ID from Jellyfin's Youtube provider metadata.
	/// </summary>
	JellyfinMetadata = 1,
}
```

Add this property before `FileMatchingMode` in `PluginConfiguration.cs`:

```csharp
/// <summary>
/// Gets or sets the source used to resolve YouTube video IDs.
/// </summary>
public YouTubeIdSource YouTubeIdSource { get; set; } = YouTubeIdSource.Filename;
```

Expose validation in `YouTubeIdExtractor.cs` and reuse it only for exact-filename mode:

```csharp
/// <summary>
/// Validates an exact YouTube video ID.
/// </summary>
/// <param name="value">Candidate value.</param>
/// <returns>The value when valid; otherwise, null.</returns>
public static string? Validate(string? value)
	=> !string.IsNullOrEmpty(value) && YouTubeIdPattern().IsMatch(value) ? value : null;

private static string? ExtractFromFilename(string filename)
{
	var name = Path.GetFileNameWithoutExtension(filename);
	return Validate(name);
}
```

- [ ] **Step 4: Run the validator tests and confirm they pass**

Run the filtered command from Step 2.

Expected: all `YouTubeIdExtractorTests` pass, including existing filename and regex cases.

- [ ] **Step 5: Write failing orchestrator source tests**

Import `MediaBrowser.Model.Entities` in `SponsorBlockOrchestratorTests.cs`, add a helper that creates the existing orchestrator, and add focused tests equivalent to:

```csharp
[Fact]
public async Task MetadataSource_UsesCaseInsensitiveYoutubeProviderId()
{
	_config.YouTubeIdSource = YouTubeIdSource.JellyfinMetadata;
	var item = FakeItem(Guid.NewGuid(), "/archive/descriptive-title.mp4");
	item.ProviderIds["yOuTuBe"] = "dQw4w9WgXcQ";
	_scope.IsInScope(item).Returns(true);
	_store.GetAsync(item.Id, Arg.Any<CancellationToken>()).Returns((ItemStateRow?)null);
	_api.GetSegmentsAsync("dQw4w9WgXcQ", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
		.Returns(new List<SponsorBlockSegment> { Seg() });

	await MakeOrchestrator().ProcessAsync(item, ProcessReason.ItemAdded, CancellationToken.None);

	await _api.Received(1).GetSegmentsAsync("dQw4w9WgXcQ", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
}

[Theory]
[InlineData(null)]
[InlineData("")]
[InlineData("short")]
[InlineData("invalid$id")]
public async Task MetadataSource_InvalidOrMissingProviderId_Skips(string? providerId)
{
	_config.YouTubeIdSource = YouTubeIdSource.JellyfinMetadata;
	var item = FakeItem(Guid.NewGuid());
	if (providerId is not null)
	{
		item.ProviderIds["Youtube"] = providerId;
	}
	_scope.IsInScope(item).Returns(true);

	await MakeOrchestrator().ProcessAsync(item, ProcessReason.ItemAdded, CancellationToken.None);

	await _api.DidNotReceive().GetSegmentsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
}

[Fact]
public async Task MetadataSource_DoesNotFallBackToValidFilename()
{
	_config.YouTubeIdSource = YouTubeIdSource.JellyfinMetadata;
	var item = FakeItem(Guid.NewGuid(), "/archive/dQw4w9WgXcQ.mp4");
	_scope.IsInScope(item).Returns(true);

	await MakeOrchestrator().ProcessAsync(item, ProcessReason.ItemAdded, CancellationToken.None);

	await _api.DidNotReceive().GetSegmentsAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
}

[Fact]
public async Task FilenameSource_DoesNotUseMetadata()
{
	var item = FakeItem(Guid.NewGuid(), "/archive/descriptive-title.mp4");
	item.ProviderIds["Youtube"] = "dQw4w9WgXcQ";
	_scope.IsInScope(item).Returns(true);

	await MakeOrchestrator().ProcessAsync(item, ProcessReason.ItemAdded, CancellationToken.None);

	await _api.Received(1).GetSegmentsAsync("abcdefghijk", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
}
```

The final test deliberately relies on the existing injected filename extractor returning `abcdefghijk`; receiving that value proves metadata did not override filename mode.

- [ ] **Step 6: Run the orchestrator tests and confirm metadata mode fails**

Run:

```bash
dotnet test Jellyfin.Plugin.SponsorBlock.Tests/Jellyfin.Plugin.SponsorBlock.Tests.csproj --filter FullyQualifiedName~SponsorBlockOrchestratorTests
```

Expected: metadata-source tests fail because the orchestrator still always uses the filename extractor.

- [ ] **Step 7: Implement selected-source resolution in the orchestrator**

Add `using MediaBrowser.Model.Entities;`, load configuration before source-specific path handling, and replace the unconditional path extraction block with:

```csharp
var config = _config();
string? videoId;
if (config.YouTubeIdSource == YouTubeIdSource.JellyfinMetadata)
{
	videoId = YouTubeIdExtractor.Validate(item.GetProviderId("Youtube"));
	if (videoId is null)
	{
		_logger.LogDebug("SponsorBlock: skipping {ItemName} ({ItemId}) — no valid Youtube provider ID in Jellyfin metadata", item.Name, item.Id);
		return;
	}
}
else
{
	var path = item.Path;
	if (string.IsNullOrEmpty(path))
	{
		_logger.LogDebug("SponsorBlock: skipping {ItemId} — no filesystem path", item.Id);
		return;
	}

	var filename = Path.GetFileName(path);
	videoId = _extractVideoId(filename, config.FileMatchingMode, config.CustomRegexPattern);
	if (videoId is null)
	{
		_logger.LogDebug("SponsorBlock: skipping {ItemName} ({ItemId}) — could not extract YouTube ID from filename \"{Filename}\"", item.Name, item.Id, filename);
		return;
	}
}
```

Do not add a default fallback branch: unknown future enum values use filename mode, preserving the established source rather than silently enabling metadata.

- [ ] **Step 8: Run the focused extractor and orchestrator tests**

Run:

```bash
dotnet test Jellyfin.Plugin.SponsorBlock.Tests/Jellyfin.Plugin.SponsorBlock.Tests.csproj --filter "FullyQualifiedName~YouTubeIdExtractorTests|FullyQualifiedName~SponsorBlockOrchestratorTests"
```

Expected: all focused tests pass.

- [ ] **Step 9: Commit the source model and resolution behavior**

```bash
git add Jellyfin.Plugin.SponsorBlock/Configuration/YouTubeIdSource.cs Jellyfin.Plugin.SponsorBlock/Configuration/PluginConfiguration.cs Jellyfin.Plugin.SponsorBlock/YouTubeIdExtractor.cs Jellyfin.Plugin.SponsorBlock/Orchestration/SponsorBlockOrchestrator.cs Jellyfin.Plugin.SponsorBlock.Tests/YouTubeIdExtractorTests.cs Jellyfin.Plugin.SponsorBlock.Tests/Orchestration/SponsorBlockOrchestratorTests.cs
git commit -m "add opt-in jellyfin metadata youtube id source"
```

---

### Task 2: Configuration UI and User Documentation

**Files:**
- Modify: `Jellyfin.Plugin.SponsorBlock/Configuration/configPage.html:30-47,221-281,326-327`
- Modify: `README.md:20-23,82-90`

**Interfaces:**
- Consumes: serialized `PluginConfiguration.YouTubeIdSource` values `Filename` and `JellyfinMetadata`.
- Produces: configuration controls that persist the source and hide filename-only controls in metadata mode.

- [ ] **Step 1: Add the source selector and conditional filename container**

Change the `File Matching` section heading to `YouTube ID`, add this selector before the existing `FileMatchingMode` select, insert `<div id="FilenameMatchingContainer">` immediately before that existing select, and insert its closing `</div>` immediately after `CustomRegexContainer`:

```html
<div class="selectContainer">
	<label class="selectLabel" for="YouTubeIdSource">YouTube ID Source</label>
	<select id="YouTubeIdSource" is="emby-select">
		<option value="Filename">Filename</option>
		<option value="JellyfinMetadata">Jellyfin metadata</option>
	</select>
	<div class="fieldDescription">
		Metadata mode reads Jellyfin's Youtube provider ID, normally imported from
		<code>&lt;uniqueid type="youtube"&gt;</code> in an NFO file.
	</div>
</div>
<div id="FilenameMatchingContainer">
	<div class="selectContainer">
		<label class="selectLabel" for="FileMatchingMode">Filename Format</label>
		<select id="FileMatchingMode" is="emby-select">
			<option value="YouTubeIdAsFilename">YouTube ID as Filename (e.g., 4pG8_bWpmaE.mp4)</option>
			<option value="CustomRegex">Custom Regex</option>
		</select>
	</div>
	<div id="CustomRegexContainer" class="inputContainer" style="display: none;">
		<label class="inputLabel" for="CustomRegexPattern">Custom Regex Pattern</label>
		<input id="CustomRegexPattern" type="text" is="emby-input" />
		<div class="fieldDescription">
			Must contain one capture group for the YouTube video ID.
			Default: <code>\[([a-zA-Z0-9_-]{11})\]</code> — matches [videoID] in filenames.
		</div>
	</div>
</div>
```

Use the existing Jellyfin `emby-select`, `selectLabel`, and `fieldDescription` patterns. Do not add CSS or dependencies.

- [ ] **Step 2: Load, save, and react to the source value**

In `load`, set:

```javascript
document.getElementById('YouTubeIdSource').value = config.YouTubeIdSource || 'Filename';
```

In `save`, set:

```javascript
config.YouTubeIdSource = document.getElementById('YouTubeIdSource').value;
```

Replace `toggleRegexField` with a source-aware function while retaining regex visibility behavior:

```javascript
toggleSourceFields: function () {
	var source = document.getElementById('YouTubeIdSource').value;
	var filenameContainer = document.getElementById('FilenameMatchingContainer');
	filenameContainer.style.display = source === 'Filename' ? '' : 'none';
	SponsorBlockConfig.toggleRegexField();
},

toggleRegexField: function () {
	var source = document.getElementById('YouTubeIdSource').value;
	var mode = document.getElementById('FileMatchingMode').value;
	document.getElementById('CustomRegexContainer').style.display =
		source === 'Filename' && mode === 'CustomRegex' ? '' : 'none';
},
```

Call `toggleSourceFields()` after loading. Register change listeners for both source and filename mode:

```javascript
document.getElementById('YouTubeIdSource')
	.addEventListener('change', SponsorBlockConfig.toggleSourceFields);
document.getElementById('FileMatchingMode')
	.addEventListener('change', SponsorBlockConfig.toggleRegexField);
```

- [ ] **Step 3: Update README requirements and matching documentation**

Replace the filename-only requirement with text stating that each video needs either a filename-resolvable ID or a Jellyfin `Youtube` provider ID. Replace the `File matching` section with `YouTube ID source`, document both source choices, retain the exact and regex filename examples, and state explicitly:

```markdown
Jellyfin metadata mode reads the `Youtube` provider ID that Jellyfin imports from `<uniqueid type="youtube">…</uniqueid>`. The plugin does not parse NFO files itself. `<youtubeid>` is not supported because Jellyfin does not expose that element without a separate external-ID provider.
```

- [ ] **Step 4: Build the plugin to validate embedded-resource and C# integration**

Run:

```bash
dotnet build Jellyfin.Plugin.SponsorBlock.sln --configuration Release
```

Expected: build succeeds with zero warnings and zero errors.

- [ ] **Step 5: Run the complete test suite**

Run:

```bash
dotnet test Jellyfin.Plugin.SponsorBlock.sln --configuration Release --no-build
```

Expected: all tests pass with zero failures.

- [ ] **Step 6: Inspect the final diff against the design**

Verify all of the following in `git diff`:

- `Filename` is still the configuration default.
- Filename and custom-regex code paths retain prior behavior.
- Metadata mode reads only `Youtube` and validates it.
- No NFO parser, external ID provider, dependency, migration, or source fallback exists.
- The UI hides filename controls only in metadata mode.
- README accurately excludes `<youtubeid>`.

- [ ] **Step 7: Commit the UI and documentation**

```bash
git add Jellyfin.Plugin.SponsorBlock/Configuration/configPage.html README.md
git commit -m "expose youtube id source in plugin settings"
```

---

### Task 3: Final Verification

**Files:**
- Verify only; no planned source changes.

**Interfaces:**
- Consumes: Tasks 1 and 2 commits.
- Produces: evidence that the implementation meets the approved design without releasing it.

- [ ] **Step 1: Run clean release verification**

```bash
dotnet clean Jellyfin.Plugin.SponsorBlock.sln --configuration Release
dotnet build Jellyfin.Plugin.SponsorBlock.sln --configuration Release
dotnet test Jellyfin.Plugin.SponsorBlock.sln --configuration Release --no-build
```

Expected: clean, build, and all tests complete successfully with zero warnings, errors, or test failures.

- [ ] **Step 2: Inspect repository state and commit range**

```bash
git status --short --branch
git log --oneline -5
git diff origin/main...HEAD --stat
```

Expected: only the two pre-existing untracked screenshots remain outside Git; the branch contains the design, plan, core implementation, and UI/documentation commits.

- [ ] **Step 3: Leave release and deployment pending**

Do not bump the plugin version, modify `manifest.json`, publish a GitHub release, deploy to Jellyfin, or close issue 2 without explicit approval. Report the verified implementation and the remaining Guided Gates from the design.
