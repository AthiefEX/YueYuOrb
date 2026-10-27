# Real-time Translation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a unified real-time translation feature with selected-text, screen-region OCR, and fixed-region subtitle modes, backed by MyMemory by default and configurable third-party providers.

**Architecture:** A provider-independent translation center in `FloatDesk.Core` handles language resolution, text chunking, caching, deduplication, rate limiting, and typed results. Provider adapters live in `FloatDesk.Infrastructure`; Windows OCR, DPAPI, and capture APIs live in `FloatDesk.Windows`; WPF route controllers and the shared translation overlay live in `FloatDesk.App`.

**Tech Stack:** .NET 10, WPF, CommunityToolkit.Mvvm, xUnit, `HttpClient`, Windows.Media.Ocr, Win32 clipboard/SendInput/display-affinity APIs, Windows DPAPI (`ProtectedData`).

**Spec:** `docs/superpowers/specs/2026-10-26-realtime-translation-design.md`

## Global Constraints

- Windows 10 22H2 x64 and Windows 11 x64 are the supported release targets.
- Do not add a speech engine, OCR executable, Python runtime, bundled translation model, service, driver, or administrator requirement.
- MyMemory is the default provider and must work without a key or account.
- Custom provider endpoints must use HTTPS; HTTP is rejected, including loopback.
- Screenshots, clipboard snapshots, selected text, recognized text, translations, API keys, and request bodies are never written to logs, settings, export files, or telemetry.
- API keys are protected with `ProtectedData` at `DataProtectionScope.CurrentUser`; there is no plaintext fallback.
- `AppSettings` keeps `SchemaVersion = 1`; all translation fields are additive init-only properties.
- Default source language is `auto`; default target language is `zh-Hans`.
- Default subtitle sampling interval is `450 ms`; allowed range is `300-1500 ms`.
- Default subtitle overlay opacity is `0.92`; allowed range is `0.60-1.00`.
- Default subtitle font size is `20 DIP`; allowed range is `14-36 DIP`.
- MyMemory requests are chunked to at most 450 UTF-8 bytes, below its documented 500-byte per-request limit.
- Interactive translation has priority over subtitle translation.
- The translation overlay must not take focus and must follow the existing app theme.
- Subtitle overlays must be excluded from capture or moved outside the selected region.
- Existing saved wheel choices are not rewritten; translation actions are defaults only for new settings.
- New hotkeys are backfilled for old settings only when the action is absent.
- The source tree currently contains unrelated uncommitted orb-image/transparent-clock changes. Never use `git add -A`; stage only the exact files listed by each task. If those unrelated changes cannot be isolated when a shared file must be committed, stop and ask the user to commit or stash them first.
- Use `scripts/test.ps1` for final full verification. New package or target-framework changes require a normal restore before returning to locked-mode verification.

## Review Focus

- Hotkey modifiers still held during selected-text extraction can turn Ctrl+C into another application shortcut; the selection route must wait for physical modifier release before injecting input. Covered by `SelectedTextReaderTests`.
- Clipboard restoration can silently destroy images or file lists if only text is saved; the selected-text route must snapshot and restore all accessible formats. Covered by `SelectedTextReaderTests`.
- A visible subtitle overlay inside the captured region creates an OCR feedback loop; subtitle mode must use display-affinity exclusion or a verified non-overlapping position. Covered by `WindowCaptureExclusionTests` and `SubtitleTranslationControllerTests`.
- Slow translation can let an older subtitle result overwrite a newer caption; subtitle work must remain serial and latest-wins. Covered by `SubtitleTranslationControllerTests`.
- A key, selected text, OCR text, or translation can leak through settings, exceptions, or diagnostics; each boundary must expose only fixed status metadata. Covered by provider, credential, applier, and diagnostics tests.

---

## File Structure Map

### Core

- `src/FloatDesk.Core/Models/TranslationModels.cs`: settings, enums, language/OCR options, route results.
- `src/FloatDesk.Core/Translation/TranslationContracts.cs`: provider, result, factory, credential contracts.
- `src/FloatDesk.Core/Translation/TranslationLanguages.cs`: canonical tags and display names.
- `src/FloatDesk.Core/Translation/TranslationLanguageDetector.cs`: best-effort local detection.
- `src/FloatDesk.Core/Translation/TranslationTextProcessor.cs`: normalization, chunking, joining, near-duplicate checks.
- `src/FloatDesk.Core/Translation/TranslationCenter.cs`: cache, dedupe, rate gate, provider calls.
- `src/FloatDesk.Core/Translation/TranslationEndpointPolicy.cs`: shared HTTPS/endpoint validation.
- `src/FloatDesk.Core/Translation/TranslationCredentialKey.cs`: endpoint-bound credential ID.
- `src/FloatDesk.Core/Translation/SubtitleFrameChangeDetector.cs`: downscaled frame signatures and difference.
- `src/FloatDesk.Core/Models/AppSettings.cs`: new init-only translation settings and defaults.
- `src/FloatDesk.Core/Models/LauncherModels.cs`: wheel action kinds.

### Infrastructure

- `src/FloatDesk.Infrastructure/Settings/SettingsValidator.cs`: call shared translation validation.
- `src/FloatDesk.Infrastructure/Settings/SettingsMigrator.cs`: backfill missing translation hotkeys.
- `src/FloatDesk.Infrastructure/Translation/TranslationProviderFactory.cs`: provider switch and shared HTTP client.
- `src/FloatDesk.Infrastructure/Translation/TranslationLanguageMapper.cs`: canonical-to-provider language codes.
- `src/FloatDesk.Infrastructure/Translation/MyMemoryTranslationProvider.cs`: default free provider.
- `src/FloatDesk.Infrastructure/Translation/AzureTranslatorProvider.cs`: Azure Translator adapter.
- `src/FloatDesk.Infrastructure/Translation/DeepLTranslationProvider.cs`: DeepL adapter.
- `src/FloatDesk.Infrastructure/Translation/LibreTranslateProvider.cs`: LibreTranslate adapter.
- `src/FloatDesk.Infrastructure/Translation/OpenAiCompatibleTranslationProvider.cs`: compatible chat-completions adapter.

### Windows

- `src/FloatDesk.Windows/Translation/DpapiTranslationCredentialStore.cs`: encrypted credential file.
- `src/FloatDesk.Windows/Translation/ITranslationDataProtector.cs`: testable DPAPI boundary.
- `src/FloatDesk.Windows/Translation/WindowsScreenOcrService.cs`: Windows.Media.Ocr adapter.
- `src/FloatDesk.Windows/Translation/WindowCaptureExclusion.cs`: `WDA_EXCLUDEFROMCAPTURE` interop.

### App

- `src/FloatDesk.App/Coordination/RegionSelectionCoordinator.cs`: reusable frozen-desktop selection.
- `src/FloatDesk.App/Coordination/CaptureSessionGate.cs`: one capture/selection session at a time.
- `src/FloatDesk.App/Coordination/WindowCoordinator.cs`: transient capture-hidden windows.
- `src/FloatDesk.App/Translation/TranslationRuntime.cs`: provider lifecycle, shared route entry points, overlay updates.
- `src/FloatDesk.App/Translation/TranslationRouteHost.cs`: narrow route-to-runtime contract.
- `src/FloatDesk.App/Translation/SelectedTextReader.cs`: clipboard snapshot, modifier wait, Ctrl+C injection.
- `src/FloatDesk.App/Translation/SelectedTextTranslationController.cs`: selected-text route.
- `src/FloatDesk.App/Translation/TranslationRegionCaptureService.cs`: local region selection/capture.
- `src/FloatDesk.App/Translation/OcrTranslationController.cs`: region OCR route.
- `src/FloatDesk.App/Translation/SubtitleTranslationController.cs`: fixed-region loop and controls.
- `src/FloatDesk.App/Translation/TranslationSettingsApplier.cs`: settings/key application ordering.
- `src/FloatDesk.App/Views/TranslationOverlayWindow.xaml`: shared non-activating output window.
- `src/FloatDesk.App/Views/TranslationOverlayWindow.xaml.cs`: overlay state/placement/actions.
- `src/FloatDesk.App/ViewModels/SettingsViewModel.cs`: translation settings and hotkey/wheel merge.
- `src/FloatDesk.App/Views/SettingsWindow.xaml`: translation settings page and hotkey rows.
- `src/FloatDesk.App/Views/SettingsWindow.xaml.cs`: provider-dependent fields, test connection, key workflow.
- `src/FloatDesk.App/Views/QuickPanelWindow.xaml`: three translation wheel actions.
- `src/FloatDesk.App/Views/QuickPanelWindow.xaml.cs`: three wheel events and visibility.
- `src/FloatDesk.App/AppRuntime.cs`: lifecycle, hotkeys, routes, capture/session coordination.

### Tests

- `tests/FloatDesk.Infrastructure.Tests/TranslationSettingsValidationTests.cs`
- `tests/FloatDesk.Core.Tests/TranslationLanguageTests.cs`
- `tests/FloatDesk.Core.Tests/TranslationTextProcessorTests.cs`
- `tests/FloatDesk.Core.Tests/TranslationCenterTests.cs`
- `tests/FloatDesk.Core.Tests/SubtitleFrameChangeDetectorTests.cs`
- `tests/FloatDesk.Infrastructure.Tests/MyMemoryTranslationProviderTests.cs`
- `tests/FloatDesk.Infrastructure.Tests/CustomTranslationProviderTests.cs`
- `tests/FloatDesk.Infrastructure.Tests/TranslationSettingsMigrationTests.cs`
- `tests/FloatDesk.Windows.Tests/DpapiTranslationCredentialStoreTests.cs`
- `tests/FloatDesk.Windows.Tests/WindowsScreenOcrServiceTests.cs`
- `tests/FloatDesk.Windows.Tests/WindowCaptureExclusionTests.cs`
- `tests/FloatDesk.Ui.Tests/SettingsViewModelTranslationTests.cs`
- `tests/FloatDesk.Ui.Tests/TranslationSettingsApplierTests.cs`
- `tests/FloatDesk.Ui.Tests/SelectedTextReaderTests.cs`
- `tests/FloatDesk.Ui.Tests/TranslationOverlayWindowTests.cs`
- `tests/FloatDesk.Ui.Tests/OcrTranslationControllerTests.cs`
- `tests/FloatDesk.Ui.Tests/SubtitleTranslationControllerTests.cs`
- Existing test files updated for navigation count, hotkeys, wheel actions, and compatibility.

---

### Task 1: Translation Domain Model and Settings Compatibility

**Files:**
- Create: `src/FloatDesk.Core/Models/TranslationModels.cs`
- Create: `src/FloatDesk.Core/Translation/TranslationLanguages.cs`
- Create: `src/FloatDesk.Core/Translation/TranslationEndpointPolicy.cs`
- Modify: `src/FloatDesk.Core/Models/AppSettings.cs`
- Modify: `src/FloatDesk.Core/Models/LauncherModels.cs`
- Modify: `src/FloatDesk.Infrastructure/Settings/SettingsValidator.cs`
- Modify: `src/FloatDesk.Infrastructure/Settings/SettingsMigrator.cs`
- Test: `tests/FloatDesk.Infrastructure.Tests/TranslationSettingsValidationTests.cs`
- Test: `tests/FloatDesk.Infrastructure.Tests/TranslationSettingsMigrationTests.cs`
- Modify: `tests/FloatDesk.Infrastructure.Tests/SettingsStoreTests.cs`

**Interfaces:**
- Consumes: existing positional `AppSettings`, `SettingsValidator`, `SettingsMigrator`.
- Produces:
  - `TranslationProviderKind { MyMemory, AzureTranslator, DeepL, LibreTranslate, OpenAiCompatible }`
  - `TranslationSettings(TranslationProviderKind Provider, string SourceLanguage, string TargetLanguage, string Endpoint, string Model, string AzureRegion)` with init-only `SubtitleRefreshIntervalMs`, `SubtitleOverlayOpacity`, `SubtitleFontSizeDip`, `OcrLanguage`, `SubtitleRegion`, and `SubtitleOverlayPlacement`
  - `TranslationCaptureRegion(string MonitorId, double RelativeX, double RelativeY, double RelativeWidth, double RelativeHeight)`
  - `TranslationOverlayPlacement(string MonitorId, double RelativeX, double RelativeY)`
  - `TranslationSettings.CreateDefault()`
  - `TranslationLanguages.Auto`, `.SimplifiedChinese`, `.IsKnown(string)`
  - `TranslationEndpointPolicy.TryNormalizeHttps(string?, out string?, out string?)`
  - `HotkeyAction.TranslateSelection`, `.TranslateRegion`, `.ToggleSubtitles`
  - `WheelActionKind.TranslateSelection`, `.TranslateRegion`, `.TranslateSubtitles`

- [ ] **Step 1: Write failing settings and migration tests**

```csharp
[Fact]
public void DefaultTranslationSettingsUseFreeProviderAndChineseTarget()
{
    var settings = AppSettings.CreateDefault();
    Assert.Equal(TranslationProviderKind.MyMemory, settings.Translation.Provider);
    Assert.Equal("auto", settings.Translation.SourceLanguage);
    Assert.Equal("zh-Hans", settings.Translation.TargetLanguage);
    Assert.Equal(450, settings.Translation.SubtitleRefreshIntervalMs);
    Assert.Equal(0.92, settings.Translation.SubtitleOverlayOpacity);
    Assert.Equal(20, settings.Translation.SubtitleFontSizeDip);
}

[Fact]
public void LegacySettingsWithoutTranslationUseDefaults()
{
    // Serialize defaults, remove "translation", then deserialize through SettingsMigrator.
    Assert.Equal(
        TranslationSettings.CreateDefault(),
        migrated.Translation);
}

[Fact]
public void LegacyHotkeysBackfillOnlyMissingTranslationActions()
{
    // Remove TranslateSelection and keep a custom TranslateRegion value.
    Assert.Equal(
        new HotkeyChord(ShortcutModifiers.Control | ShortcutModifiers.Alt, 0x54),
        migrated.Hotkeys[HotkeyAction.TranslateSelection]);
    Assert.Equal(
        new HotkeyChord(ShortcutModifiers.Control | ShortcutModifiers.Alt, 0x51),
        migrated.Hotkeys[HotkeyAction.TranslateRegion]);
}

[Fact]
public void ValidatorRejectsInsecureOrMalformedCustomEndpoint()
{
    var settings = AppSettings.CreateDefault() with
    {
        Translation = AppSettings.CreateDefault().Translation with
        {
            Provider = TranslationProviderKind.OpenAiCompatible,
            Endpoint = "http://example.com/v1/chat/completions",
            Model = "test"
        }
    };
    Assert.Contains(
        SettingsValidator.Validate(settings),
        error => error.Contains("HTTPS", StringComparison.Ordinal));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FloatDesk.Infrastructure.Tests/FloatDesk.Infrastructure.Tests.csproj --filter "FullyQualifiedName~TranslationSettingsValidationTests" --verbosity minimal`

Expected: FAIL because translation types and settings properties do not exist.

- [ ] **Step 3: Implement the domain model and additive defaults**

Create the exact records/enums from the Interfaces block. The positional six-field `TranslationSettings` constructor and its init-only subtitle/OCR/region/placement properties are fixed by the signature above. Add this to `AppSettings` without changing its constructor:

```csharp
public TranslationSettings Translation { get; init; } =
    TranslationSettings.CreateDefault();
```

Add the three default hotkeys `Ctrl+Alt+T`, `Ctrl+Alt+O`, and `Ctrl+Alt+L`, and add the three translation wheel actions to `DefaultWheelActions`. Add shared validation for enum values, known language tags, subtitle ranges, endpoint rules, and relative coordinates.

Update `SettingsMigrator.Deserialize` to copy the hotkey dictionary and add only absent translation hotkeys. Do not change an existing chord and do not modify existing saved wheel actions.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FloatDesk.Infrastructure.Tests/FloatDesk.Infrastructure.Tests.csproj --filter "FullyQualifiedName~TranslationSettingsValidationTests" --verbosity minimal`

Run: `dotnet test tests/FloatDesk.Infrastructure.Tests/FloatDesk.Infrastructure.Tests.csproj --filter "FullyQualifiedName~SettingsStoreTests|FullyQualifiedName~TranslationSettingsMigrationTests" --verbosity minimal`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/FloatDesk.Core/Models/TranslationModels.cs src/FloatDesk.Core/Translation/TranslationLanguages.cs src/FloatDesk.Core/Translation/TranslationEndpointPolicy.cs src/FloatDesk.Core/Models/AppSettings.cs src/FloatDesk.Core/Models/LauncherModels.cs src/FloatDesk.Infrastructure/Settings/SettingsValidator.cs src/FloatDesk.Infrastructure/Settings/SettingsMigrator.cs tests/FloatDesk.Infrastructure.Tests/TranslationSettingsValidationTests.cs tests/FloatDesk.Infrastructure.Tests/TranslationSettingsMigrationTests.cs tests/FloatDesk.Infrastructure.Tests/SettingsStoreTests.cs
git commit -m "feat: add translation settings domain"
```

---

### Task 2: Language Detection and Text Processing

**Files:**
- Create: `src/FloatDesk.Core/Translation/TranslationLanguageDetector.cs`
- Create: `src/FloatDesk.Core/Translation/TranslationTextProcessor.cs`
- Test: `tests/FloatDesk.Core.Tests/TranslationLanguageTests.cs`
- Test: `tests/FloatDesk.Core.Tests/TranslationTextProcessorTests.cs`

**Interfaces:**
- Consumes: `TranslationLanguages` from Task 1.
- Produces:
  - `TranslationLanguageDetection(string LanguageTag, double Confidence)`
  - `TranslationLanguageDetector.Detect(string text)`
  - `TranslationTextChunk(string Text, string SeparatorAfter)`
  - `TranslationTextProcessor.Normalize(string)`
  - `TranslationTextProcessor.Chunk(string text, int maxUtf8Bytes, int maxCharacters)`
  - `TranslationTextProcessor.Join(IReadOnlyList<string>, string targetLanguage)`
  - `TranslationTextProcessor.IsNearDuplicate(string previous, string current, double threshold = 0.92)`

- [ ] **Step 1: Write failing language and text tests**

```csharp
[Theory]
[InlineData("这是中文文本。", "zh-Hans")]
[InlineData("これは日本語です。", "ja")]
[InlineData("안녕하세요 반갑습니다.", "ko")]
public void DetectsScriptedLanguages(string text, string expected)
{
    Assert.Equal(expected, TranslationLanguageDetector.Detect(text).LanguageTag);
}

[Fact]
public void ChunkingPreservesOrderAndRespectsUtf8Limit()
{
    var chunks = TranslationTextProcessor.Chunk(
        string.Concat(Enumerable.Repeat("这是需要翻译的句子。", 80)),
        maxUtf8Bytes: 450,
        maxCharacters: 5000);
    Assert.True(chunks.Count > 1);
    Assert.All(chunks, chunk =>
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(chunk.Text) <= 450));
    Assert.Equal(
        TranslationTextProcessor.Normalize(string.Concat(chunks.Select(c => c.Text + c.SeparatorAfter))),
        TranslationTextProcessor.Normalize(string.Concat(Enumerable.Repeat("这是需要翻译的句子。", 80))));
}

[Fact]
public void NearDuplicateDetectsSubtitleRepaintNoise()
{
    Assert.True(TranslationTextProcessor.IsNearDuplicate(
        "欢迎使用实时字幕功能",
        "欢迎使用实时字幕功能。"));
    Assert.False(TranslationTextProcessor.IsNearDuplicate(
        "欢迎使用实时字幕功能",
        "这是一个完全不同的字幕"));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FloatDesk.Core.Tests/FloatDesk.Core.Tests.csproj --filter "FullyQualifiedName~TranslationLanguageTests|FullyQualifiedName~TranslationTextProcessorTests" --verbosity minimal`

Expected: FAIL because the detector and processor do not exist.

- [ ] **Step 3: Implement deterministic detection and text helpers**

Detect scripts before Latin scoring: Kana => `ja`, Hangul => `ko`, Cyrillic => `ru`, Arabic => `ar`, Hebrew => `he`, Thai => `th`, Devanagari => `hi`, Han-only => `zh-Hans` unless the Traditional-character signal is strong. For Latin text, score a fixed small stopword table and fall back to `en` with low confidence.

Normalize CRLF, trailing whitespace, and excess blank lines. Chunk first at paragraph boundaries, then sentence boundaries, then code-point boundaries. Use a separator of `""` for `zh-Hans`, `zh-Hant`, `ja`, and `ko` when joining chunks, otherwise one ASCII space. Implement near-duplicate as normalized Levenshtein similarity.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FloatDesk.Core.Tests/FloatDesk.Core.Tests.csproj --filter "FullyQualifiedName~TranslationLanguageTests|FullyQualifiedName~TranslationTextProcessorTests" --verbosity minimal`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/FloatDesk.Core/Translation/TranslationLanguageDetector.cs src/FloatDesk.Core/Translation/TranslationTextProcessor.cs tests/FloatDesk.Core.Tests/TranslationLanguageTests.cs tests/FloatDesk.Core.Tests/TranslationTextProcessorTests.cs
git commit -m "feat: add translation language and text processing"
```

---

### Task 3: Translation Contracts and Translation Center

**Files:**
- Create: `src/FloatDesk.Core/Translation/TranslationContracts.cs`
- Create: `src/FloatDesk.Core/Translation/ScreenOcrContracts.cs`
- Create: `src/FloatDesk.Core/Translation/TranslationCenter.cs`
- Create: `src/FloatDesk.Core/Translation/TranslationCredentialKey.cs`
- Modify: `src/FloatDesk.Core/Models/TranslationModels.cs`
- Test: `tests/FloatDesk.Core.Tests/TranslationCenterTests.cs`

**Interfaces:**
- Consumes: language detection and text processor from Task 2; `IClock` from `ServiceContracts.cs`.
- Produces:
  - `TranslationPurpose { Interactive, Subtitle }`
  - `TranslationFailureKind { None, EmptyText, NoTranslationNeeded, UnsupportedLanguage, Authentication, Quota, Timeout, Network, InvalidRequest, ProviderError, Cancelled }`
  - `TranslationRequest(string Text, string SourceLanguage, string TargetLanguage, TranslationPurpose Purpose)`
  - `TranslationProviderCapabilities(bool SupportsAutoDetect, int MaxCharacters, int MaxUtf8BytesPerRequest, int MaxChunks, int MaxConcurrentRequests, TimeSpan MinimumRequestInterval)`
  - `TranslationProviderResult(string? TranslatedText, string? DetectedLanguage, TranslationFailureKind FailureKind, string? ErrorCode, string? Message)` with static `Ok(...)` and `Fail(...)`
  - `TranslationResponse(string OriginalText, string? TranslatedText, string SourceLanguage, string ProviderName, TranslationFailureKind FailureKind, string? ErrorCode, string? Message)`
  - `ITranslationProvider { string Id { get; } string DisplayName { get; } TranslationProviderCapabilities Capabilities { get; } Task<TranslationProviderResult> TranslateAsync(TranslationRequest, CancellationToken); }`
  - `ITranslationProviderFactory { ITranslationProvider Create(TranslationSettings, string? apiKey); }`
  - `ITranslationCredentialStore { Task<bool> HasAsync(TranslationProviderKind, string, CancellationToken); Task<string?> GetAsync(TranslationProviderKind, string, CancellationToken); Task SaveAsync(TranslationProviderKind, string, string, CancellationToken); Task DeleteAsync(TranslationProviderKind, string, CancellationToken); }`
  - `TranslationProviderConfigurationException`
  - `TranslationResponse.Ok(...)` and `TranslationResponse.Fail(...)`
  - `OcrLanguageOption(string LanguageTag, string DisplayName)`
  - `ScreenOcrResult(string? Text, string? ErrorCode, string? Message)`
  - `IScreenOcrService { IReadOnlyList<OcrLanguageOption> AvailableLanguages { get; } Task<ScreenOcrResult> RecognizeAsync(CaptureFrame, string?, CancellationToken); }`
  - `TranslationCredentialKey.For(TranslationProviderKind, string endpoint)`
  - `TranslationCenter(ITranslationProvider, IClock)` and `TranslateAsync(...)`

- [ ] **Step 1: Write failing center tests**

```csharp
[Fact]
public async Task IdenticalRequestsShareOneProviderCallAndCacheResult()
{
    var provider = new FakeProvider("你好", minimumInterval: TimeSpan.Zero);
    var center = new TranslationCenter(provider, new ManualClock());

    var first = center.TranslateAsync(
        "Hello", "auto", "zh-Hans", TranslationPurpose.Interactive, default);
    var second = center.TranslateAsync(
        "Hello", "auto", "zh-Hans", TranslationPurpose.Interactive, default);
    await Task.WhenAll(first, second);
    var third = await center.TranslateAsync(
        "Hello", "auto", "zh-Hans", TranslationPurpose.Interactive, default);

    Assert.Equal(1, provider.CallCount);
    Assert.Equal("你好", third.TranslatedText);
}

[Fact]
public async Task NonAutoProviderReceivesLocallyResolvedLanguage()
{
    var provider = new FakeProvider("你好", minimumInterval: TimeSpan.Zero)
    {
        SupportsAutoDetect = false
    };
    var center = new TranslationCenter(provider, new ManualClock());

    var result = await center.TranslateAsync(
        "Hello world", "auto", "zh-Hans", TranslationPurpose.Interactive, default);

    Assert.True(result.Success, result.ErrorMessage);
    Assert.Equal("en", Assert.Single(provider.Requests).SourceLanguage);
}

[Fact]
public async Task TextAlreadyInTargetLanguageDoesNotCallProvider()
{
    var provider = new FakeProvider("unused", TimeSpan.Zero);
    var center = new TranslationCenter(provider, new ManualClock());

    var result = await center.TranslateAsync(
        "这是中文", "zh-Hans", "zh-Hans", TranslationPurpose.Interactive, default);

    Assert.Equal(TranslationFailureKind.NoTranslationNeeded, result.FailureKind);
    Assert.Equal(0, provider.CallCount);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FloatDesk.Core.Tests/FloatDesk.Core.Tests.csproj --filter "FullyQualifiedName~TranslationCenterTests" --verbosity minimal`

Expected: FAIL because the contracts and center do not exist.

- [ ] **Step 3: Implement contracts, credential ID, and the center**

Define typed results and a fixed provider contract. `TranslationResponse.Ok` and `.Fail` create success/failure responses without requiring UI types. Define the OCR contracts here so the settings page can list OCR languages before the Windows implementation is added. Implement a bounded in-memory cache (128 entries, 10-minute TTL), in-flight request deduplication, concurrency gate, and minimum-interval delay using `IClock`. Resolve `auto` locally when `SupportsAutoDetect` is false; pass `auto` through when true. Normalize and chunk the request according to `TranslationProviderCapabilities`. Preserve chunk order and join with `TranslationTextProcessor.Join`. Map provider failures without adding request text to messages.

Implement `TranslationCredentialKey.For` as lowercase hex SHA-256 of
`providerKind + "\n" + normalizedEndpoint`, with `"no-endpoint"` for an empty endpoint.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FloatDesk.Core.Tests/FloatDesk.Core.Tests.csproj --filter "FullyQualifiedName~TranslationCenterTests" --verbosity minimal`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/FloatDesk.Core/Translation/TranslationContracts.cs src/FloatDesk.Core/Translation/ScreenOcrContracts.cs src/FloatDesk.Core/Translation/TranslationCenter.cs src/FloatDesk.Core/Translation/TranslationCredentialKey.cs src/FloatDesk.Core/Models/TranslationModels.cs tests/FloatDesk.Core.Tests/TranslationCenterTests.cs
git commit -m "feat: add unified translation center"
```

---

### Task 4: MyMemory Provider and Provider Factory

**Files:**
- Create: `src/FloatDesk.Infrastructure/Translation/TranslationLanguageMapper.cs`
- Create: `src/FloatDesk.Infrastructure/Translation/MyMemoryTranslationProvider.cs`
- Create: `src/FloatDesk.Infrastructure/Translation/TranslationProviderFactory.cs`
- Modify: `src/FloatDesk.Core/Translation/TranslationLanguages.cs`
- Test: `tests/FloatDesk.Infrastructure.Tests/MyMemoryTranslationProviderTests.cs`

**Interfaces:**
- Consumes: `ITranslationProvider` and result types from Task 3.
- Produces:
  - `TranslationLanguageMapper.ToMyMemory(string)`
  - `MyMemoryTranslationProvider(HttpClient httpClient, Uri? endpoint = null)`
  - `TranslationProviderFactory(HttpClient? httpClient = null)` implementing `ITranslationProviderFactory` and `IDisposable`
  - `TranslationEndpointDefaults.For(TranslationProviderKind)`

- [ ] **Step 1: Write failing MyMemory tests**

```csharp
[Fact]
public async Task MyMemorySendsLangPairAndDecodesEntities()
{
    Uri? requested = null;
    var handler = new StubHandler(request =>
    {
        requested = request.RequestUri;
        return Json("""{"responseData":{"translatedText":"Tom &amp; Jerry"},"responseStatus":200}""");
    });
    using var http = new HttpClient(handler);
    var provider = new MyMemoryTranslationProvider(http);

    var result = await provider.TranslateAsync(
        new TranslationRequest("Tom and Jerry", "en", "zh-Hans", TranslationPurpose.Interactive),
        CancellationToken.None);

    Assert.True(result.Success);
    Assert.Equal("Tom & Jerry", result.TranslatedText);
    Assert.Contains("langpair=en%7Czh-CN", requested!.Query, StringComparison.OrdinalIgnoreCase);
    Assert.Equal(1, handler.CallCount);
}

[Fact]
public async Task MyMemoryQuotaWarningBecomesQuotaFailure()
{
    var handler = new StubHandler(_ => Json(
        """{"responseData":{"translatedText":"MYMEMORY WARNING: YOU USED ALL AVAILABLE FREE TRANSLATIONS FOR TODAY"},"responseStatus":429}"""));
    using var http = new HttpClient(handler);
    var provider = new MyMemoryTranslationProvider(http);

    var result = await provider.TranslateAsync(
        new TranslationRequest("Hello", "en", "zh-Hans", TranslationPurpose.Interactive),
        CancellationToken.None);

    Assert.Equal(TranslationFailureKind.Quota, result.FailureKind);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FloatDesk.Infrastructure.Tests/FloatDesk.Infrastructure.Tests.csproj --filter "FullyQualifiedName~MyMemoryTranslationProviderTests" --verbosity minimal`

Expected: FAIL because MyMemory provider and mapper do not exist.

- [ ] **Step 3: Implement MyMemory and the factory shell**

Use the fixed HTTPS endpoint `https://api.mymemory.translated.net/get`. Set capabilities to `SupportsAutoDetect=false`, max characters `5000`, max UTF-8 bytes `450`, max chunks `10`, max concurrency `1`, and minimum interval one second. URL-encode `q` and `langpair`. Decode HTML entities. Treat HTTP 429, quota text, warning payloads, malformed JSON, and missing translated text as typed failures.

Implement the factory for MyMemory now and throw `TranslationProviderConfigurationException` for the not-yet-implemented provider kinds. Use one injected `HttpClient` when supplied; otherwise create and own one with no request/response body logging.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FloatDesk.Infrastructure.Tests/FloatDesk.Infrastructure.Tests.csproj --filter "FullyQualifiedName~MyMemoryTranslationProviderTests" --verbosity minimal`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/FloatDesk.Infrastructure/Translation/TranslationLanguageMapper.cs src/FloatDesk.Infrastructure/Translation/MyMemoryTranslationProvider.cs src/FloatDesk.Infrastructure/Translation/TranslationProviderFactory.cs src/FloatDesk.Core/Translation/TranslationLanguages.cs tests/FloatDesk.Infrastructure.Tests/MyMemoryTranslationProviderTests.cs
git commit -m "feat: add MyMemory translation provider"
```

---

### Task 5: Azure, DeepL, LibreTranslate, and OpenAI-Compatible Providers

**Files:**
- Create: `src/FloatDesk.Infrastructure/Translation/AzureTranslatorProvider.cs`
- Create: `src/FloatDesk.Infrastructure/Translation/DeepLTranslationProvider.cs`
- Create: `src/FloatDesk.Infrastructure/Translation/LibreTranslateProvider.cs`
- Create: `src/FloatDesk.Infrastructure/Translation/OpenAiCompatibleTranslationProvider.cs`
- Modify: `src/FloatDesk.Infrastructure/Translation/TranslationProviderFactory.cs`
- Modify: `src/FloatDesk.Infrastructure/Translation/TranslationLanguageMapper.cs`
- Test: `tests/FloatDesk.Infrastructure.Tests/CustomTranslationProviderTests.cs`

**Interfaces:**
- Consumes: `ITranslationProvider`, `TranslationProviderResult`, factory from Tasks 3-4.
- Produces: factory support for all five `TranslationProviderKind` values.

- [ ] **Step 1: Write failing provider contract tests**

```csharp
[Fact]
public async Task AzureUsesSubscriptionKeyAndConfiguredRegion()
{
    var handler = new RecordingHandler(
        """[{"detectedLanguage":{"language":"en"},"translations":[{"text":"你好","to":"zh-Hans"}]}]""");
    using var http = new HttpClient(handler);
    var provider = new AzureTranslatorProvider(
        http,
        new Uri("https://api.cognitive.microsofttranslator.com"),
        apiKey: "secret",
        region: "eastasia");

    var result = await provider.TranslateAsync(
        new TranslationRequest("Hello", "auto", "zh-Hans", TranslationPurpose.Interactive),
        CancellationToken.None);

    Assert.True(result.Success);
    Assert.Equal("secret", handler.LastRequest!.Headers.GetValues("Ocp-Apim-Subscription-Key").Single());
    Assert.Equal("eastasia", handler.LastRequest.Headers.GetValues("Ocp-Apim-Subscription-Region").Single());
}

[Fact]
public async Task OpenAiCompatiblePostsModelAndParsesMessageContent()
{
    var handler = new RecordingHandler(
        """{"choices":[{"message":{"content":"你好"}}]}""");
    using var http = new HttpClient(handler);
    var provider = new OpenAiCompatibleTranslationProvider(
        http,
        new Uri("https://example.com/v1/chat/completions"),
        "test-model",
        "secret");

    var result = await provider.TranslateAsync(
        new TranslationRequest("Hello", "en", "zh-Hans", TranslationPurpose.Interactive),
        CancellationToken.None);

    Assert.True(result.Success);
    Assert.Contains("\"model\":\"test-model\"", handler.LastBody, StringComparison.Ordinal);
    Assert.DoesNotContain("secret", handler.LastBody, StringComparison.Ordinal);
}

[Fact]
public async Task FactoryRejectsMissingRequiredCustomConfiguration()
{
    using var factory = new TranslationProviderFactory(new HttpClient(new RecordingHandler("{}")));
    Assert.Throws<TranslationProviderConfigurationException>(() =>
        factory.Create(new TranslationSettings(
            TranslationProviderKind.OpenAiCompatible,
            "auto",
            "zh-Hans",
            "https://example.com/v1/chat/completions",
            "",
            ""), "key"));
}
```

The concrete provider constructors are:

- `AzureTranslatorProvider(HttpClient, Uri endpoint, string apiKey, string? region = null)`
- `DeepLTranslationProvider(HttpClient, Uri endpoint, string apiKey)`
- `LibreTranslateProvider(HttpClient, Uri endpoint, string? apiKey)`
- `OpenAiCompatibleTranslationProvider(HttpClient, Uri endpoint, string model, string apiKey)`

Each stores the key in a private readonly field and exposes no key property.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FloatDesk.Infrastructure.Tests/FloatDesk.Infrastructure.Tests.csproj --filter "FullyQualifiedName~CustomTranslationProviderTests" --verbosity minimal`

Expected: FAIL because the four providers do not exist.

- [ ] **Step 3: Implement the four adapters and complete the factory**

Azure: POST `/translate?api-version=3.0` with source omitted for auto, target mapped from canonical tags, subscription key and optional region headers.

DeepL: POST `/v2/translate`, `Authorization: DeepL-Auth-Key <key>`, omit source for auto, map Chinese target to `ZH-HANS`.

LibreTranslate: POST `/translate` with `q`, `source`, `target`, and optional `api_key` in the JSON body.

OpenAI-compatible: POST the exact configured URL with `model`, one fixed system instruction, one user message, `temperature: 0`, and non-streaming output. Parse `choices[0].message.content`; reject empty or reasoning-only responses. Do not add a custom prompt field.

All adapters use typed failures for 401/403, 429, timeout/network, malformed JSON, and provider errors. Do not include the request text or key in exception messages.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FloatDesk.Infrastructure.Tests/FloatDesk.Infrastructure.Tests.csproj --filter "FullyQualifiedName~CustomTranslationProviderTests|FullyQualifiedName~MyMemoryTranslationProviderTests" --verbosity minimal`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/FloatDesk.Infrastructure/Translation/AzureTranslatorProvider.cs src/FloatDesk.Infrastructure/Translation/DeepLTranslationProvider.cs src/FloatDesk.Infrastructure/Translation/LibreTranslateProvider.cs src/FloatDesk.Infrastructure/Translation/OpenAiCompatibleTranslationProvider.cs src/FloatDesk.Infrastructure/Translation/TranslationProviderFactory.cs src/FloatDesk.Infrastructure/Translation/TranslationLanguageMapper.cs tests/FloatDesk.Infrastructure.Tests/CustomTranslationProviderTests.cs
git commit -m "feat: add configurable translation providers"
```

---

### Task 6: DPAPI Credential Store

**Files:**
- Create: `src/FloatDesk.Windows/Translation/ITranslationDataProtector.cs`
- Create: `src/FloatDesk.Windows/Translation/DpapiTranslationCredentialStore.cs`
- Modify: `Directory.Packages.props`
- Modify: `src/FloatDesk.Windows/FloatDesk.Windows.csproj`
- Modify: `src/FloatDesk.Windows/packages.lock.json`
- Test: `tests/FloatDesk.Windows.Tests/DpapiTranslationCredentialStoreTests.cs`

**Interfaces:**
- Consumes: `ITranslationCredentialStore`, `TranslationCredentialKey`.
- Produces:
  - `ITranslationDataProtector.Protect(byte[])`, `.Unprotect(byte[])`
  - `DpapiTranslationCredentialStore(string filePath, ITranslationDataProtector? protector = null)`
  - Implements `GetAsync`, `SaveAsync`, `DeleteAsync`, `HasAsync`

- [ ] **Step 1: Write failing credential tests**

```csharp
[Fact]
public async Task CredentialRoundTripsUnderProviderAndEndpoint()
{
    var path = Path.Combine(_root, "translation-secrets.dat");
    var store = new DpapiTranslationCredentialStore(
        path,
        new XorTestProtector(0x5A));

    await store.SaveAsync(
        TranslationProviderKind.DeepL,
        "https://api-free.deepl.com",
        "secret-key",
        CancellationToken.None);

    Assert.True(await store.HasAsync(
        TranslationProviderKind.DeepL,
        "https://api-free.deepl.com",
        CancellationToken.None));
    Assert.Equal("secret-key", await store.GetAsync(
        TranslationProviderKind.DeepL,
        "https://api-free.deepl.com",
        CancellationToken.None));
    Assert.Null(await store.GetAsync(
        TranslationProviderKind.DeepL,
        "https://other.example.com",
        CancellationToken.None));
}

[Fact]
public async Task CorruptCredentialFileReturnsNoKeyInsteadOfThrowing()
{
    var path = Path.Combine(_root, "translation-secrets.dat");
    await File.WriteAllTextAsync(path, "corrupt");
    var store = new DpapiTranslationCredentialStore(
        path,
        new XorTestProtector(0x5A));

    Assert.Null(await store.GetAsync(
        TranslationProviderKind.MyMemory,
        "",
        CancellationToken.None));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FloatDesk.Windows.Tests/FloatDesk.Windows.Tests.csproj --filter "FullyQualifiedName~DpapiTranslationCredentialStoreTests" --verbosity minimal`

Expected: FAIL because the store does not exist.

- [ ] **Step 3: Implement encrypted storage and package changes**

Add centrally managed `System.Security.Cryptography.ProtectedData` version `10.0.0` and reference it from `FloatDesk.Windows`. Implement the default protector with `DataProtectionScope.CurrentUser`.

Store versioned JSON containing credential-ID/key pairs, protect the complete JSON bytes, and write through a unique temporary file plus replace/move. Use a semaphore for access. Return missing/null for missing, corrupt, or foreign-user blobs. Never fall back to plaintext. Do not log file content, provider keys, or protector exceptions that could contain payloads.

Run `dotnet restore FloatDesk.sln -p:NuGetAudit=false` to update the Windows lock file.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FloatDesk.Windows.Tests/FloatDesk.Windows.Tests.csproj --filter "FullyQualifiedName~DpapiTranslationCredentialStoreTests" --verbosity minimal`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add Directory.Packages.props src/FloatDesk.Windows/Translation/ITranslationDataProtector.cs src/FloatDesk.Windows/Translation/DpapiTranslationCredentialStore.cs src/FloatDesk.Windows/FloatDesk.Windows.csproj src/FloatDesk.Windows/packages.lock.json tests/FloatDesk.Windows.Tests/DpapiTranslationCredentialStoreTests.cs
git commit -m "feat: store translation keys with DPAPI"
```

---

### Task 7: Settings ViewModel and Merge Rules

**Files:**
- Modify: `src/FloatDesk.App/ViewModels/SettingsViewModel.cs`
- Test: `tests/FloatDesk.Ui.Tests/SettingsViewModelTranslationTests.cs`
- Modify: `tests/FloatDesk.Ui.Tests/SettingsViewModelMergeTests.cs`

**Interfaces:**
- Consumes: `TranslationSettings`, `TranslationLanguages`, translation hotkey/wheel enum values.
- Produces:
  - `SettingsViewModel.From(AppSettings settings, bool hasSavedTranslationKey = false, IReadOnlyList<OcrLanguageOption>? ocrLanguages = null)`
  - Translation properties: `TranslationProviderIndex`, `TranslationSourceLanguageIndex`, `TranslationTargetLanguageIndex`, `TranslationEndpoint`, `TranslationModel`, `TranslationAzureRegion`, `TranslationOcrLanguageIndex`, `SubtitleRefreshIntervalMs`, `SubtitleOverlayOpacityPercent`, `SubtitleFontSizeDip`
  - Hotkey properties: `TranslationSelectionHotkey`, `TranslationRegionHotkey`, `SubtitlesHotkey`
  - Wheel properties: `ShowTranslateSelectionInWheel`, `ShowTranslateRegionInWheel`, `ShowTranslateSubtitlesInWheel`
  - `HasSavedTranslationKey`, `ClearSavedTranslationKey`
  - `ObservableCollection<OcrLanguageOption> OcrLanguages`

- [ ] **Step 1: Write failing merge/validation tests**

```csharp
[Fact]
public void UntouchedTranslationPreservesConcurrentProviderChange()
{
    var original = AppSettings.CreateDefault();
    var vm = SettingsViewModel.From(original);
    var latest = original with
    {
        Translation = original.Translation with
        {
            Provider = TranslationProviderKind.DeepL
        }
    };

    var candidate = vm.BuildCandidate(latest);

    Assert.Equal(TranslationProviderKind.DeepL, candidate.Translation.Provider);
}

[Fact]
public void EditedTranslationTargetWinsOverConcurrentValue()
{
    var original = AppSettings.CreateDefault();
    var vm = SettingsViewModel.From(original);
    vm.TranslationTargetLanguageIndex =
        SettingsViewModel.TranslationTargetLanguages.ToList().IndexOf(TranslationLanguages.English);
    var latest = original with
    {
        Translation = original.Translation with
        {
            TargetLanguage = TranslationLanguages.Japanese
        }
    };

    var candidate = vm.BuildCandidate(latest);

    Assert.Equal(TranslationLanguages.English, candidate.Translation.TargetLanguage);
}

[Fact]
public void TranslationHotkeysAndWheelTogglesMerge()
{
    var original = AppSettings.CreateDefault();
    var vm = SettingsViewModel.From(original);
    vm.TranslationSelectionHotkey = "Ctrl+Alt+Y";
    vm.ShowTranslateSubtitlesInWheel = false;

    var candidate = vm.BuildCandidate(original);

    Assert.Equal(
        0x59u,
        candidate.Hotkeys[HotkeyAction.TranslateSelection].VirtualKey);
    Assert.DoesNotContain(
        WheelActionKind.TranslateSubtitles,
        candidate.WheelActions);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj --filter "FullyQualifiedName~SettingsViewModelTranslationTests" --verbosity minimal`

Expected: FAIL because the translation properties/mappings do not exist.

- [ ] **Step 3: Implement ViewModel state and baseline merging**

Expose fixed ordered language lists:

- source: Auto, Simplified Chinese, Traditional Chinese, English, Japanese, Korean, French, German, Spanish, Russian, Arabic, Portuguese, Italian;
- target: all except Auto, default index for `zh-Hans`.

Map indexes to canonical tags in both directions. Add OCR options from the supplied list, with the first item representing the Windows user profile. Merge every translation field against `_baseline`, matching the existing pattern. Include the three new hotkeys in `TryBuildCandidate` format errors and the three wheel toggles in `BuildWheelActions`.

Do not store a staged API key in the ViewModel.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj --filter "FullyQualifiedName~SettingsViewModelTranslationTests|FullyQualifiedName~SettingsViewModelMergeTests" --verbosity minimal`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/FloatDesk.App/ViewModels/SettingsViewModel.cs tests/FloatDesk.Ui.Tests/SettingsViewModelTranslationTests.cs tests/FloatDesk.Ui.Tests/SettingsViewModelMergeTests.cs
git commit -m "feat: add translation settings view model"
```

---

### Task 8: Translation Settings Page and Encrypted Key Workflow

**Files:**
- Create: `src/FloatDesk.App/Translation/TranslationSettingsApplier.cs`
- Modify: `src/FloatDesk.App/Views/SettingsWindow.xaml`
- Modify: `src/FloatDesk.App/Views/SettingsWindow.xaml.cs`
- Modify: `src/FloatDesk.App/Resources/SettingsWindowResources.xaml`
- Modify: `src/FloatDesk.App/AppRuntime.cs`
- Test: `tests/FloatDesk.Ui.Tests/TranslationSettingsApplierTests.cs`
- Modify: `tests/FloatDesk.Ui.Tests/SettingsWindowVisualTests.cs`

**Interfaces:**
- Consumes: `ISettingsCoordinator`, `ITranslationCredentialStore`, `ITranslationProviderFactory`, `OcrLanguageOption`.
- Produces:
  - `TranslationSettingsApplier(ISettingsCoordinator, ITranslationCredentialStore)`
  - `Task<OperationResult> ApplyAsync(Func<AppSettings, AppSettings>, string? stagedApiKey, bool clearSavedKey, CancellationToken)`
  - `SettingsWindow(..., ITranslationCredentialStore, ITranslationProviderFactory, Func<IReadOnlyList<OcrLanguageOption>> getOcrLanguages)`

- [ ] **Step 1: Write failing applier and XAML tests**

```csharp
[Fact]
public async Task SavingSettingsNeverWritesApiKeyIntoAppSettings()
{
    var settings = new MemorySettingsCoordinator(AppSettings.CreateDefault());
    var credentials = new MemoryCredentialStore();
    var applier = new TranslationSettingsApplier(settings, credentials);
    var candidate = AppSettings.CreateDefault() with
    {
        Translation = AppSettings.CreateDefault().Translation with
        {
            Provider = TranslationProviderKind.DeepL,
            Endpoint = "https://api-free.deepl.com"
        }
    };

    var result = await applier.ApplyAsync(
        _ => candidate,
        stagedApiKey: "secret",
        clearSavedKey: false,
        CancellationToken.None);

    Assert.True(result.Success, result.Message);
    Assert.Equal("secret", await credentials.GetAsync(
        TranslationProviderKind.DeepL,
        "https://api-free.deepl.com",
        CancellationToken.None));
    Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(settings.Current));
}

[Fact]
public void TranslationPageExposesProviderLanguageSecretOcrAndSubtitleFields()
{
    var xaml = XDocument.Load(FindRepoFile(
        "src/FloatDesk.App/Views/SettingsWindow.xaml")).ToString();
    foreach (var name in new[]
    {
        "TranslationProviderComboBox",
        "TranslationSourceLanguageComboBox",
        "TranslationTargetLanguageComboBox",
        "TranslationEndpointTextBox",
        "TranslationApiKeyBox",
        "TranslationModelTextBox",
        "TranslationAzureRegionTextBox",
        "TranslationOcrLanguageComboBox",
        "SubtitleRefreshIntervalTextBox",
        "SubtitleOverlayOpacityTextBox",
        "SubtitleFontSizeTextBox",
        "TestTranslationConnectionButton"
    })
    {
        Assert.Contains($"x:Name=\"{name}\"", xaml);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj --filter "FullyQualifiedName~TranslationSettingsApplierTests|FullyQualifiedName~TranslationPageExposes" --verbosity minimal`

Expected: FAIL because the applier and controls do not exist.

- [ ] **Step 3: Implement the settings page and key application flow**

Add one `实时翻译` tab between `AI 网页` and `截图`. Use the existing ice-blue card, text box, combo box, action button, and status styles. Add a `PasswordBox` style to `SettingsWindowResources.xaml`; the key is read from the control only on Apply/Test and is never bound into `AppSettings`.

Page fields:

- provider, source/target language;
- endpoint, model, Azure region;
- masked API key, saved-key status, clear-key option;
- OCR language;
- subtitle interval, opacity, font size;
- test connection and inline status;
- free-service and privacy notes.

Add six hotkey rows total in `常规`: existing three plus selected text, region OCR, and subtitle toggle.

`TranslationSettingsApplier.ApplyAsync` first merges/saves settings through `ISettingsCoordinator.UpdateAsync`; then:

- if a staged non-empty key exists, save it under the resulting provider/endpoint credential ID;
- if `clearSavedKey` is true and no staged key exists, delete the resulting credential;
- if key storage fails, return `TranslationKeySaveFailed` and state that settings were saved but the key was not.

On a successful Apply, reload the ViewModel, clear the `PasswordBox`, and refresh saved-key status. Test Connection uses the staged key or stored key and a short fixed string; it never saves.

Add provider-dependent enabled/visibility behavior: MyMemory hides endpoint/key/model/region requirements; Azure shows region; DeepL shows key/endpoint; LibreTranslate shows endpoint and optional key; OpenAI-compatible shows endpoint/key/model.

In this task, `AppRuntime` passes `static () => []` for OCR languages so the app compiles before the Windows OCR implementation exists. Task 10 replaces that delegate with the real OCR service's language list.

Update `SettingsWindowVisualTests` from eight to nine tabs and include `实时翻译`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj --filter "FullyQualifiedName~TranslationSettingsApplierTests|FullyQualifiedName~SettingsWindowVisualTests|FullyQualifiedName~SettingsViewModel" --verbosity minimal`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/FloatDesk.App/Translation/TranslationSettingsApplier.cs src/FloatDesk.App/Views/SettingsWindow.xaml src/FloatDesk.App/Views/SettingsWindow.xaml.cs src/FloatDesk.App/Resources/SettingsWindowResources.xaml src/FloatDesk.App/AppRuntime.cs tests/FloatDesk.Ui.Tests/TranslationSettingsApplierTests.cs tests/FloatDesk.Ui.Tests/SettingsWindowVisualTests.cs
git commit -m "feat: add translation settings page"
```

---

### Task 9: Shared Translation Overlay

**Files:**
- Create: `src/FloatDesk.App/Views/TranslationOverlayWindow.xaml`
- Create: `src/FloatDesk.App/Views/TranslationOverlayWindow.xaml.cs`
- Create: `src/FloatDesk.Windows/Translation/WindowCaptureExclusion.cs`
- Test: `tests/FloatDesk.Ui.Tests/TranslationOverlayWindowTests.cs`
- Modify: `tests/FloatDesk.Ui.Tests/ThemeResourceTests.cs`

**Interfaces:**
- Consumes: `TranslationResponse` and `TranslationSettings`.
- Produces:
  - `TranslationOverlayWindow.ApplySettings(AppSettings)`
  - `ShowStatus(string status, PixelPoint anchor)`
  - `ShowResult(TranslationResponse response, PixelPoint anchor)`
  - `ShowError(string message, PixelPoint anchor)`
  - `SetSubtitleControls(bool active, bool paused, bool canReselect)`
  - events `PauseResumeRequested`, `ReselectRegionRequested`, `StopSubtitleRequested`, `CopyRequested`
  - `bool IsSubtitleMode { get; }`
  - `string TranslatedText { get; }`
  - `string StatusText { get; }`

- [ ] **Step 1: Write failing overlay tests**

```csharp
[Fact(Timeout = 10_000)]
public async Task OverlayShowsResultWithoutActivationAndClampsToWorkArea()
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            var window = new TranslationOverlayWindow();
            window.ShowResult(
                TranslationResponse.Ok(
                    "Hello",
                    "你好",
                    "en",
                    "MyMemory"),
                new PixelPoint(-500, -500));
            window.UpdateLayout();
            Assert.False(window.ShowActivated);
            Assert.Equal(WindowStyle.None, window.WindowStyle);
            Assert.True(window.Left >= 0);
            Assert.True(window.Top >= 0);
            Assert.Contains("你好", window.TranslatedText);
            window.Close();
        }
        catch (Exception exception)
        {
            failure = exception;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    await Task.CompletedTask;
    if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
}

[Fact]
public void OverlayUsesNoActivateToolWindowAndCaptureExclusionHooks()
{
    var code = File.ReadAllText(FindRepoFile(
        "src/FloatDesk.App/Views/TranslationOverlayWindow.xaml.cs"));
    Assert.Contains("WS_EX_NOACTIVATE", code, StringComparison.Ordinal);
    Assert.Contains("WS_EX_TOOLWINDOW", code, StringComparison.Ordinal);
    Assert.Contains("WindowCaptureExclusion.TryExclude", code, StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj --filter "FullyQualifiedName~TranslationOverlayWindowTests" --verbosity minimal`

Expected: FAIL because the overlay does not exist.

- [ ] **Step 3: Implement the overlay**

Use an ice-blue translucent card with border, original text, translated text, status, language/provider metadata, copy, close, and subtitle control rows. Use dynamic app theme brushes. Set `ShowActivated=false`, `ShowInTaskbar=false`, `WindowStyle=None`, `AllowsTransparency=true`, and `Topmost` from settings. Expose `TranslatedText` and `StatusText` as read-only values from the named text controls for UI/accessibility tests.

Create `WindowCaptureExclusion.TryExclude(nint hwnd)` using `SetWindowDisplayAffinity(hwnd, 0x11)` and returning `false` on failure. On `SourceInitialized`, add `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` and call `WindowCaptureExclusion.TryExclude`. Place one-shot overlays near the anchor and clamp to the monitor work area. Subtitle mode uses the saved placement, defaults to bottom center, and exposes pause/resume, reselect, and stop.

Use a 100-140 ms opacity/translate transition only when animations are enabled. Copy places only the translated text on the clipboard and does not persist it.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj --filter "FullyQualifiedName~TranslationOverlayWindowTests|FullyQualifiedName~ThemeResourceTests" --verbosity minimal`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/FloatDesk.App/Views/TranslationOverlayWindow.xaml src/FloatDesk.App/Views/TranslationOverlayWindow.xaml.cs src/FloatDesk.Windows/Translation/WindowCaptureExclusion.cs tests/FloatDesk.Ui.Tests/TranslationOverlayWindowTests.cs tests/FloatDesk.Ui.Tests/ThemeResourceTests.cs
git commit -m "feat: add shared translation overlay"
```

---

### Task 10: Windows OCR and Reusable Region Selection

**Files:**
- Create: `src/FloatDesk.Windows/Translation/WindowsScreenOcrService.cs`
- Create: `src/FloatDesk.App/Coordination/RegionSelectionCoordinator.cs`
- Create: `src/FloatDesk.App/Coordination/CaptureSessionGate.cs`
- Modify: `src/FloatDesk.App/Coordination/CaptureCoordinator.cs`
- Modify: `src/FloatDesk.App/FloatDesk.App.csproj`
- Modify: `src/FloatDesk.Windows/FloatDesk.Windows.csproj`
- Modify: `tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj`
- Modify: `tests/FloatDesk.Windows.Tests/FloatDesk.Windows.Tests.csproj`
- Modify: affected `packages.lock.json` files
- Test: `tests/FloatDesk.Windows.Tests/WindowsScreenOcrServiceTests.cs`
- Modify: `tests/FloatDesk.Ui.Tests/CaptureOverlayWindowTests.cs`

**Interfaces:**
- Consumes: `CaptureFrame`, `CaptureOverlayWindow`, `GdiScreenCapture`.
- Produces:
  - `WindowsScreenOcrService` implementing the `IScreenOcrService` contract from Task 3
  - `static string WindowsScreenOcrService.NormalizeLines(IEnumerable<string>)`
  - `RegionSelectionCoordinator.SelectAsync(CaptureFrame desktop, Action<string, IReadOnlyDictionary<string, object?>?>? diagnostics, CancellationToken)`
  - `CaptureSessionGate.EnterAsync(CancellationToken)`

- [ ] **Step 1: Write failing OCR/refactor tests**

```csharp
[Fact]
public void WindowsOcrReportsInstalledRecognizerLanguages()
{
    var service = new WindowsScreenOcrService();
    Assert.All(service.AvailableLanguages, option =>
        Assert.False(string.IsNullOrWhiteSpace(option.LanguageTag)));
}

[Fact]
public void OcrTextNormalizerKeepsReadingOrderAndDropsBlankLines()
{
    var text = WindowsScreenOcrService.NormalizeLines(
        ["第一行", " ", "第二行"]);
    Assert.Equal("第一行\n第二行", text);
}

[Fact]
public async Task EmptyFrameReturnsTypedFailureWithoutCallingOcr()
{
    var service = new WindowsScreenOcrService();
    using var frame = new CaptureFrame(
        new PixelRect(0, 0, 1, 1),
        4,
        [0, 0, 0, 0]);
    var result = await service.RecognizeAsync(frame, null, CancellationToken.None);
    Assert.Contains(
        result.ErrorCode,
        new[] { "OcrNoText", "OcrLanguageUnavailable" });
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FloatDesk.Windows.Tests/FloatDesk.Windows.Tests.csproj --filter "FullyQualifiedName~WindowsScreenOcrServiceTests" --verbosity minimal`

Expected: FAIL because the OCR service does not exist.

- [ ] **Step 3: Add Windows OCR and extract the region selector**

Change the Windows-facing target frameworks for `FloatDesk.App`, `FloatDesk.Windows`, UI tests, and Windows tests to `net10.0-windows10.0.19041.0`, with `SupportedOSPlatformVersion=10.0.19041.0`. Run a normal restore to update all lock files.

Implement `WindowsScreenOcrService` with `OcrEngine.AvailableRecognizerLanguages`, `TryCreateFromLanguage`, `TryCreateFromUserProfileLanguages`, `SoftwareBitmap` BGRA8 conversion, and ordered line joining. Return typed `OcrLanguageUnavailable`, `OcrNoText`, and `OcrFailed` results. Dispose SoftwareBitmap/WinRT objects.

Replace the temporary empty OCR-language delegate passed to `SettingsWindow` in Task 8 with `() => ocrService.AvailableLanguages`.

Move the existing region-selection logic out of `CaptureCoordinator.SelectRegionAsync` into `RegionSelectionCoordinator`. Preserve overlay behavior, diagnostics event names, selection cancellation, confirmation, and NUDGE handling. Make screenshot capture use the shared coordinator and `CaptureSessionGate`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FloatDesk.Windows.Tests/FloatDesk.Windows.Tests.csproj --filter "FullyQualifiedName~WindowsScreenOcrServiceTests|FullyQualifiedName~PlatformAdapterTests" --verbosity minimal`

Run: `dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj --filter "FullyQualifiedName~CaptureOverlayWindowTests" --verbosity minimal`

Expected: PASS; screenshot selection behavior is unchanged.

- [ ] **Step 5: Commit**

```powershell
git add src/FloatDesk.Windows/Translation/WindowsScreenOcrService.cs src/FloatDesk.App/Coordination/RegionSelectionCoordinator.cs src/FloatDesk.App/Coordination/CaptureSessionGate.cs src/FloatDesk.App/Coordination/CaptureCoordinator.cs src/FloatDesk.App/FloatDesk.App.csproj src/FloatDesk.Windows/FloatDesk.Windows.csproj tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj tests/FloatDesk.Windows.Tests/FloatDesk.Windows.Tests.csproj src/FloatDesk.App/packages.lock.json src/FloatDesk.Windows/packages.lock.json tests/FloatDesk.Ui.Tests/packages.lock.json tests/FloatDesk.Windows.Tests/packages.lock.json tests/FloatDesk.Windows.Tests/WindowsScreenOcrServiceTests.cs tests/FloatDesk.Ui.Tests/CaptureOverlayWindowTests.cs
git commit -m "feat: add Windows OCR and shared region selection"
```

---

### Task 11: Selected-Text Extraction

**Files:**
- Create: `src/FloatDesk.App/Translation/SelectedTextReader.cs`
- Modify: `src/FloatDesk.App/AssemblyInfo.cs`
- Test: `tests/FloatDesk.Ui.Tests/SelectedTextReaderTests.cs`

**Interfaces:**
- Consumes: `ForegroundSnapshot`.
- Produces:
  - `SelectedTextReadResult(string? Text, string? ErrorCode, string? Message)`
  - `ISelectedTextReader.ReadAsync(ForegroundSnapshot target, CancellationToken)`
  - Production `SelectedTextReader`
  - Internal injectable boundaries `IClipboardSession`, `ICopyShortcutSender`, `IHotkeyModifierState`, `IForegroundActivator`

- [ ] **Step 1: Write failing selected-text tests**

```csharp
[Fact]
public async Task ReaderWaitsForHotkeyModifiersBeforeSendingCopy()
{
    var order = new List<string>();
    var modifiers = new FakeModifiers { IsDown = true, ReleaseAfterPolls = 2, OnPoll = () => order.Add("poll") };
    var sender = new FakeCopySender(() => order.Add("copy"));
    var reader = new SelectedTextReader(
        new FakeClipboardSession("selected text"),
        sender,
        modifiers,
        new FakeActivator(),
        TimeSpan.FromMilliseconds(800));

    var result = await reader.ReadAsync(Target(), CancellationToken.None);

    Assert.True(result.Success);
    Assert.True(order.IndexOf("copy") > order.LastIndexOf("poll"));
}

[Fact]
public async Task ReaderRestoresClipboardWhenCopyReturnsNoText()
{
    var clipboard = new FakeClipboardSession(text: null, sequenceChanges: false);
    var reader = new SelectedTextReader(
        clipboard,
        new FakeCopySender(),
        new FakeModifiers { IsDown = false },
        new FakeActivator(),
        TimeSpan.FromMilliseconds(50));

    var result = await reader.ReadAsync(Target(), CancellationToken.None);

    Assert.Equal("NoSelectedText", result.ErrorCode);
    Assert.Equal(1, clipboard.RestoreCount);
}

[Fact]
public async Task ReaderAbortsWhenTargetCannotBecomeForeground()
{
    var sender = new FakeCopySender();
    var reader = new SelectedTextReader(
        new FakeClipboardSession("text"),
        sender,
        new FakeModifiers { IsDown = false },
        new FakeActivator { Success = false },
        TimeSpan.FromMilliseconds(50));

    var result = await reader.ReadAsync(Target(), CancellationToken.None);

    Assert.Equal("TargetNotForeground", result.ErrorCode);
    Assert.Equal(0, sender.Count);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj --filter "FullyQualifiedName~SelectedTextReaderTests" --verbosity minimal`

Expected: FAIL because the reader does not exist.

- [ ] **Step 3: Implement safe clipboard extraction**

Add `[assembly: InternalsVisibleTo("FloatDesk.Ui.Tests")]`. Implement the production boundaries with WPF `Clipboard`, `GetClipboardSequenceNumber`, `SendInput`, `GetAsyncKeyState`, `GetForegroundWindow`, and `SetForegroundWindow`.

Order strictly:

1. activate/verify the recorded target if it is not foreground;
2. wait up to one second until Ctrl/Alt/Shift/Win are physically released;
3. record the clipboard sequence number and snapshot all accessible `DataObject` formats;
4. send Ctrl+C down/up through `SendInput`;
5. wait up to 800 ms for a changed sequence number and read Unicode text;
6. restore the snapshot in `finally`;
7. return typed success, `NoSelectedText`, `ClipboardDenied`, `ModifiersHeld`, or `TargetNotForeground`.

Do not retain text after return beyond the result object. Do not log clipboard data or exception content.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj --filter "FullyQualifiedName~SelectedTextReaderTests" --verbosity minimal`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/FloatDesk.App/Translation/SelectedTextReader.cs src/FloatDesk.App/AssemblyInfo.cs tests/FloatDesk.Ui.Tests/SelectedTextReaderTests.cs
git commit -m "feat: add safe selected text extraction"
```

---

### Task 12: Translation Runtime, Selected-Text Route, Hotkeys, and Wheel

**Files:**
- Create: `src/FloatDesk.App/Translation/TranslationRouteHost.cs`
- Create: `src/FloatDesk.App/Translation/TranslationRuntime.cs`
- Create: `src/FloatDesk.App/Translation/SelectedTextTranslationController.cs`
- Modify: `src/FloatDesk.App/AppRuntime.cs`
- Modify: `src/FloatDesk.App/Views/QuickPanelWindow.xaml`
- Modify: `src/FloatDesk.App/Views/QuickPanelWindow.xaml.cs`
- Test: `tests/FloatDesk.Ui.Tests/QuickPanelWindowLayoutTests.cs`
- Test: `tests/FloatDesk.Ui.Tests/SettingsWindowVisualTests.cs`

**Interfaces:**
- Consumes: settings/provider factory/credential store/overlay/selected reader from Tasks 1-11.
- Produces:
  - `ITranslationRouteHost.ShowStatus(...)`, `.ShowResult(...)`, `.ShowError(...)`, `.TranslateAsync(string, TranslationPurpose, CancellationToken)`
  - `SelectedTextTranslationController(ISelectedTextReader, ITranslationRouteHost)` with `RunAsync(ForegroundSnapshot, CancellationToken)`
  - `TranslationRuntime` with `ApplySettings(AppSettings)`, `TranslateSelectionAsync(ForegroundSnapshot, CancellationToken)`, `SetSessionLocked(bool)`, `SetEmergencyPaused(bool)`, `PauseForCapture()`, `ResumeAfterCapture()`, `Dispose()`
  - QuickPanel events `TranslateSelectionRequested`, `TranslateRegionRequested`, `TranslateSubtitlesRequested`

- [ ] **Step 1: Write failing route and wheel tests**

```csharp
[Fact(Timeout = 10_000)]
public async Task TranslationWheelButtonsRaiseDistinctEvents()
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            var window = new QuickPanelWindow();
            var raised = new List<string>();
            window.TranslateSelectionRequested += () => raised.Add("selection");
            window.TranslateRegionRequested += () => raised.Add("region");
            window.TranslateSubtitlesRequested += () => raised.Add("subtitles");
            window.Apply(AppSettings.CreateDefault(), false, "等待前台应用");

            foreach (var (name, expected) in new[]
            {
                ("TranslateSelectionButton", "selection"),
                ("TranslateRegionButton", "region"),
                ("TranslateSubtitlesButton", "subtitles")
            })
            {
                var button = Assert.IsType<Button>(window.FindName(name));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Contains(expected, raised);
            }
        }
        catch (Exception exception) { failure = exception; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    await Task.CompletedTask;
    if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
}

[Fact]
public void AppRuntimeRoutesSelectedTextHotkeyAndWheelToTranslationRuntime()
{
    var source = File.ReadAllText(FindRepoFile("src/FloatDesk.App/AppRuntime.cs"));
    Assert.Contains("HotkeyAction.TranslateSelection", source, StringComparison.Ordinal);
    Assert.Contains("TranslateSelectionAsync", source, StringComparison.Ordinal);
    Assert.Contains("TranslateSelectionRequested", source, StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj --filter "FullyQualifiedName~TranslationWheelButtons|FullyQualifiedName~AppRuntimeRoutesSelectedText" --verbosity minimal`

Expected: FAIL because the wheel buttons/events/runtime do not exist.

- [ ] **Step 3: Implement provider lifecycle and selected-text route**

`TranslationRuntime` owns the current `TranslationCenter` and a reconfigure cancellation source. Reconfigure when provider, language, endpoint, model, region, OCR language, or overlay settings change. Read the API key from `ITranslationCredentialStore`; MyMemory proceeds without one. A missing custom key/invalid configuration becomes a user-facing error, not a crash.

Wire `TranslationOverlayWindow` and its actions. `TranslateSelectionAsync` chooses the current external foreground, otherwise the last external window supplied by `AppRuntime`; it shows a preparing state, invokes the selected-text controller, and renders typed success/error. Interactive calls cancel an in-flight subtitle request once subtitle routing exists.

Add the three wheel buttons using the existing radial button style and distinct blue/green/slate icons. Add their visibility to `QuickPanelWindow.Apply` through the new wheel enum values. Raise events from click handlers.

`AppRuntime`:

- construct one provider factory, credential store, overlay, selected reader, and translation runtime;
- pass provider factory/credential store/OCR service to the settings factory;
- route `HotkeyAction.TranslateSelection` and wheel selection events;
- store the wheel selection target before the panel hides;
- call `SetSessionLocked` on session changes;
- call `SetEmergencyPaused` on emergency pause;
- call `PauseForCapture`/`ResumeAfterCapture` around screenshot capture;
- dispose translation resources.

Do not log source text or translation content.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj --filter "FullyQualifiedName~TranslationWheelButtons|FullyQualifiedName~AppRuntimeRoutesSelectedText|FullyQualifiedName~SettingsWindowVisualTests" --verbosity minimal`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/FloatDesk.App/Translation/TranslationRouteHost.cs src/FloatDesk.App/Translation/TranslationRuntime.cs src/FloatDesk.App/Translation/SelectedTextTranslationController.cs src/FloatDesk.App/AppRuntime.cs src/FloatDesk.App/Views/QuickPanelWindow.xaml src/FloatDesk.App/Views/QuickPanelWindow.xaml.cs tests/FloatDesk.Ui.Tests/QuickPanelWindowLayoutTests.cs tests/FloatDesk.Ui.Tests/SettingsWindowVisualTests.cs
git commit -m "feat: add selected text translation route"
```

---

### Task 13: Region OCR Translation Route

**Files:**
- Create: `src/FloatDesk.App/Translation/TranslationRegionCaptureService.cs`
- Create: `src/FloatDesk.App/Translation/OcrTranslationController.cs`
- Modify: `src/FloatDesk.App/Translation/TranslationRuntime.cs`
- Modify: `src/FloatDesk.App/AppRuntime.cs`
- Test: `tests/FloatDesk.Ui.Tests/OcrTranslationControllerTests.cs`

**Interfaces:**
- Consumes: `IScreenOcrService`, `RegionSelectionCoordinator`, `GdiScreenCapture`, `CaptureSessionGate`, `IWindowCoordinator`, `ITranslationRouteHost`.
- Produces:
  - `CapturedTranslationRegion(CaptureFrame Frame, PixelRect Bounds)`
  - `TranslationRegionCaptureService.CaptureSelectedAsync(CancellationToken)`
  - `OcrTranslationController(IScreenOcrService, TranslationRegionCaptureService, ITranslationRouteHost)` with `RunAsync(PixelPoint fallbackAnchor, CancellationToken)`
  - `TranslationRuntime.TranslateRegionAsync(CancellationToken)`

- [ ] **Step 1: Write failing OCR route tests**

```csharp
[Fact]
public async Task OcrReceivesFrameButTranslationReceivesOnlyRecognizedText()
{
    using var frame = SolidFrame(white: false);
    var ocr = new FakeOcr(new ScreenOcrResult("Recognized text", null, null));
    var capture = new FakeRegionCapture(frame, new PixelRect(10, 20, 200, 80));
    var host = new FakeRouteHost();
    var controller = new OcrTranslationController(ocr, capture, host);

    await controller.RunAsync(new PixelPoint(10, 20), CancellationToken.None);

    Assert.Same(frame, ocr.LastFrame);
    Assert.Equal("Recognized text", host.LastText);
    Assert.DoesNotContain(
        Convert.ToBase64String(frame.Bgra32.Span),
        string.Join("|", host.Properties.Keys),
        StringComparison.Ordinal);
}

[Fact]
public async Task MissingOcrLanguageShowsActionableErrorAndDoesNotTranslate()
{
    var host = new FakeRouteHost();
    var controller = new OcrTranslationController(
        new FakeOcr(new ScreenOcrResult(null, "OcrLanguageUnavailable", "请安装 OCR 语言包。")),
        new FakeRegionCapture(SolidFrame(false), new PixelRect(1, 2, 30, 40)),
        host);

    await controller.RunAsync(new PixelPoint(1, 2), CancellationToken.None);

    Assert.Contains("语言包", host.LastError, StringComparison.Ordinal);
    Assert.Equal(0, host.TranslateCount);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj --filter "FullyQualifiedName~OcrTranslationControllerTests" --verbosity minimal`

Expected: FAIL because the OCR route does not exist.

- [ ] **Step 3: Implement local region capture and OCR translation**

`TranslationRegionCaptureService` enters `CaptureSessionGate`, hides YueYuOrb windows, freezes the virtual desktop with `GdiScreenCapture`, invokes `RegionSelectionCoordinator`, crops the selected frame, restores windows, and returns both the frame and bounds. It never writes an image and always disposes frames on cancellation/failure.

`OcrTranslationController`:

1. show `正在选择区域`;
2. capture the selected region;
3. call OCR with configured/profile language;
4. on success pass only the normalized recognized text to the route host;
5. place the result near the selected bounds;
6. map `OcrLanguageUnavailable`, `OcrNoText`, and `OcrFailed` to actionable overlay errors.

Reuse the shared route host so OCR and selected text use the same provider, cache, errors, and overlay.

Wire `HotkeyAction.TranslateRegion` and `TranslateRegionRequested` in `AppRuntime`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj --filter "FullyQualifiedName~OcrTranslationControllerTests|FullyQualifiedName~TranslationWheelButtons" --verbosity minimal`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/FloatDesk.App/Translation/TranslationRegionCaptureService.cs src/FloatDesk.App/Translation/OcrTranslationController.cs src/FloatDesk.App/Translation/TranslationRuntime.cs src/FloatDesk.App/AppRuntime.cs tests/FloatDesk.Ui.Tests/OcrTranslationControllerTests.cs
git commit -m "feat: add OCR translation route"
```

---

### Task 14: Fixed-Region Subtitle Pipeline

**Files:**
- Create: `src/FloatDesk.Core/Translation/SubtitleFrameChangeDetector.cs`
- Create: `src/FloatDesk.App/Translation/SubtitleTranslationController.cs`
- Modify: `src/FloatDesk.App/Translation/TranslationRegionCaptureService.cs`
- Modify: `src/FloatDesk.App/Translation/TranslationRuntime.cs`
- Modify: `src/FloatDesk.App/AppRuntime.cs`
- Test: `tests/FloatDesk.Core.Tests/SubtitleFrameChangeDetectorTests.cs`
- Test: `tests/FloatDesk.Ui.Tests/SubtitleTranslationControllerTests.cs`

**Interfaces:**
- Consumes: `CaptureFrame`, `IScreenOcrService`, `ITranslationRouteHost`, `TranslationRegionCaptureService`.
- Produces:
  - `FrameSignature(int Width, int Height, byte[] Luma)`
  - `SubtitleFrameChangeDetector.Create(CaptureFrame, int width = 64, int height = 36)`
  - `SubtitleFrameChangeDetector.Difference(FrameSignature, FrameSignature)`
  - `SubtitleFrameChangeDetector.HasMeaningfulChange(FrameSignature?, FrameSignature, double threshold = 0.03)`
  - `SubtitleTranslationController(IScreenOcrService, TranslationRegionCaptureService, ITranslationRouteHost, IClock)`
  - `StartAsync`, `StopAsync`, `Pause(string)`, `Resume()`, `ReselectAsync`, `bool IsRunning`

- [ ] **Step 1: Write failing change/loop tests**

```csharp
[Fact]
public void IdenticalFramesDoNotTriggerMeaningfulChange()
{
    using var first = SolidFrame(0x20);
    using var second = SolidFrame(0x20);
    var a = SubtitleFrameChangeDetector.Create(first, 16, 9);
    var b = SubtitleFrameChangeDetector.Create(second, 16, 9);
    Assert.False(SubtitleFrameChangeDetector.HasMeaningfulChange(a, b));
}

[Fact]
public void LargeFrameChangeExceedsDefaultThreshold()
{
    using var first = SolidFrame(0x10);
    using var second = SolidFrame(0xE0);
    var a = SubtitleFrameChangeDetector.Create(first, 16, 9);
    var b = SubtitleFrameChangeDetector.Create(second, 16, 9);
    Assert.True(SubtitleFrameChangeDetector.HasMeaningfulChange(a, b));
}

[Fact]
public async Task SubtitleLoopSkipsStableFrameAfterFirstTranslation()
{
    using var stableFrame = SolidFrame(0x80);
    var ocr = new FakeOcr(
        new ScreenOcrResult("Hello subtitle", null, null),
        new ScreenOcrResult("Hello subtitle", null, null));
    var source = new FakeSubtitleSource(stableFrame, stopAfterCaptures: 3);
    var host = new FakeRouteHost();
    var controller = new SubtitleTranslationController(
        ocr,
        source,
        host,
        new ImmediateClock());

    await controller.StartAsync(new TranslationSettings(
        TranslationProviderKind.MyMemory, "auto", "zh-Hans", "", "", "")
    {
        SubtitleRefreshIntervalMs = 300
    }, CancellationToken.None);

    await source.Finished.WaitAsync(TimeSpan.FromSeconds(2));
    await controller.StopAsync();

    Assert.Equal(1, host.TranslateCount);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FloatDesk.Core.Tests/FloatDesk.Core.Tests.csproj --filter "FullyQualifiedName~SubtitleFrameChangeDetectorTests" --verbosity minimal`

Run: `dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj --filter "FullyQualifiedName~SubtitleTranslationControllerTests" --verbosity minimal`

Expected: FAIL because the detector and controller do not exist.

- [ ] **Step 3: Implement frame gating and the subtitle loop**

The frame detector downsamples BGRA to a fixed grayscale grid and returns mean absolute luma difference normalized to `0..1`. The first frame is always a change. Treat `difference > threshold` as meaningful.

`SubtitleTranslationController` runs one serial loop:

1. capture only the saved region;
2. compare the signature with the previous frame;
3. skip stable frames without OCR;
4. after a change, delay `120 ms`, capture again, then OCR the stable frame;
5. normalize text and skip exact/near-duplicate text;
6. translate with `TranslationPurpose.Subtitle`;
7. update the overlay only if the sequence token is still current;
8. wait the configured interval using `IClock`.

The loop stores only the latest frame signature and text; it never queues arbitrary history. Pause/stop cancels OCR/translation and sampling. A provider failure updates the overlay, applies a bounded retry wait, and does not kill the session for network/quota errors. Authentication/unsupported-language errors pause automatic retries.

Pause on emergency pause/session lock and expose pause/resume/reselect/stop through the overlay. Persist a new region through `ISettingsCoordinator.UpdateAsync` without reconfiguring the provider when only region/placement changed.

Wire `HotkeyAction.ToggleSubtitles` and `TranslateSubtitlesRequested`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FloatDesk.Core.Tests/FloatDesk.Core.Tests.csproj --filter "FullyQualifiedName~SubtitleFrameChangeDetectorTests" --verbosity minimal`

Run: `dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj --filter "FullyQualifiedName~SubtitleTranslationControllerTests|FullyQualifiedName~TranslationWheelButtons" --verbosity minimal`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/FloatDesk.Core/Translation/SubtitleFrameChangeDetector.cs src/FloatDesk.App/Translation/SubtitleTranslationController.cs src/FloatDesk.App/Translation/TranslationRegionCaptureService.cs src/FloatDesk.App/Translation/TranslationRuntime.cs src/FloatDesk.App/AppRuntime.cs tests/FloatDesk.Core.Tests/SubtitleFrameChangeDetectorTests.cs tests/FloatDesk.Ui.Tests/SubtitleTranslationControllerTests.cs
git commit -m "feat: add fixed-region subtitle translation"
```

---

### Task 15: Capture Exclusion and Cross-Feature Integration

**Files:**
- Create: `src/FloatDesk.Core/Geometry/TranslationOverlayPlacementPolicy.cs`
- Modify: `src/FloatDesk.App/Coordination/WindowCoordinator.cs`
- Modify: `src/FloatDesk.App/Views/TranslationOverlayWindow.xaml.cs`
- Modify: `src/FloatDesk.App/Translation/TranslationRuntime.cs`
- Modify: `src/FloatDesk.App/AppRuntime.cs`
- Test: `tests/FloatDesk.Windows.Tests/WindowCaptureExclusionTests.cs`
- Test: `tests/FloatDesk.Core.Tests/TranslationOverlayPlacementPolicyTests.cs`
- Modify: `tests/FloatDesk.Ui.Tests/SubtitleTranslationControllerTests.cs`
- Modify: `tests/FloatDesk.Ui.Tests/ThemeResourceTests.cs`

**Interfaces:**
- Consumes: `WindowCaptureExclusion` from Task 9, translation overlay, window coordinator, capture coordinator.
- Produces:
  - `TranslationOverlayPlacementPolicy.PlaceOutside(PixelRect captureRegion, PixelSize overlaySize, PixelRect workArea, PixelPoint preferredTopLeft, int gap = 8)`
  - `WindowCoordinator.RegisterCaptureHiddenWindow(Window)`
  - `WindowCoordinator.UnregisterCaptureHiddenWindow(Window)`

- [ ] **Step 1: Write failing integration tests**

```csharp
[Fact]
public void CaptureExclusionUsesExcludeFromCaptureAffinity()
{
    var source = File.ReadAllText(FindRepoFile(
        "src/FloatDesk.Windows/Translation/WindowCaptureExclusion.cs"));
    Assert.Contains("WDA_EXCLUDEFROMCAPTURE", source, StringComparison.Ordinal);
    Assert.Contains("SetWindowDisplayAffinity", source, StringComparison.Ordinal);
}

[Fact]
public void OverlayPlacementFindsNonOverlappingPositionOrReportsNoSpace()
{
    var region = new PixelRect(80, 80, 700, 140);
    var placement = TranslationOverlayPlacementPolicy.PlaceOutside(
        region,
        new PixelSize(600, 80),
        new PixelRect(0, 0, 1920, 1080),
        new PixelPoint(100, 100));

    Assert.NotNull(placement);
    Assert.False(
        placement.Value.Left < region.Right &&
        placement.Value.Right > region.Left &&
        placement.Value.Top < region.Bottom &&
        placement.Value.Bottom > region.Top);

    Assert.Null(TranslationOverlayPlacementPolicy.PlaceOutside(
        new PixelRect(0, 0, 800, 600),
        new PixelSize(600, 200),
        new PixelRect(0, 0, 800, 600),
        new PixelPoint(0, 0)));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FloatDesk.Windows.Tests/FloatDesk.Windows.Tests.csproj --filter "FullyQualifiedName~WindowCaptureExclusionTests" --verbosity minimal`

Run: `dotnet test tests/FloatDesk.Core.Tests/FloatDesk.Core.Tests.csproj --filter "FullyQualifiedName~TranslationOverlayPlacementPolicyTests" --verbosity minimal`

Expected: FAIL because capture exclusion and integration behavior do not exist.

- [ ] **Step 3: Implement exclusion and lifecycle coordination**

Implement `TranslationOverlayPlacementPolicy` using these candidate positions in order: preferred/clamped, below the region, above the region, right, left. Return the first rectangle fully inside the work area that does not intersect the capture region; return `null` if all candidates overlap or overflow.

Extend `WindowCoordinator` with capture-hidden transient windows. Register/unregister the translation overlay. `HideForCaptureAsync` includes transient windows and restores only ones that were visible. Quick panel keeps its existing special path.

At subtitle start, verify whether the overlay's `WindowCaptureExclusion` call succeeded. When exclusion failed, call `TranslationOverlayPlacementPolicy.PlaceOutside`; move the overlay to the result, or pause subtitle mode with an actionable status when it returns `null`. Reject a manual subtitle-overlay drag that intersects the region when exclusion is unavailable.

Pause subtitle sampling while screenshot/OCR selection runs and resume afterward. On display topology change, resolve the saved monitor; pause/reseed if absent.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/FloatDesk.Windows.Tests/FloatDesk.Windows.Tests.csproj --filter "FullyQualifiedName~WindowCaptureExclusionTests" --verbosity minimal`

Run: `dotnet test tests/FloatDesk.Core.Tests/FloatDesk.Core.Tests.csproj --filter "FullyQualifiedName~TranslationOverlayPlacementPolicyTests" --verbosity minimal`

Run: `dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj --filter "FullyQualifiedName~SubtitleTranslationControllerTests|FullyQualifiedName~TranslationOverlayWindowTests" --verbosity minimal`

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/FloatDesk.Core/Geometry/TranslationOverlayPlacementPolicy.cs src/FloatDesk.App/Coordination/WindowCoordinator.cs src/FloatDesk.App/Views/TranslationOverlayWindow.xaml.cs src/FloatDesk.App/Translation/TranslationRuntime.cs src/FloatDesk.App/AppRuntime.cs tests/FloatDesk.Core.Tests/TranslationOverlayPlacementPolicyTests.cs tests/FloatDesk.Windows.Tests/WindowCaptureExclusionTests.cs tests/FloatDesk.Ui.Tests/SubtitleTranslationControllerTests.cs tests/FloatDesk.Ui.Tests/ThemeResourceTests.cs
git commit -m "feat: integrate translation capture lifecycle"
```

---

### Task 16: Privacy Hardening, Documentation, and Full Verification

**Files:**
- Modify: `src/FloatDesk.App/Views/SettingsWindow.xaml`
- Create: `src/FloatDesk.App/Translation/TranslationDiagnostics.cs`
- Modify: `src/FloatDesk.App/Translation/TranslationRuntime.cs`
- Modify: `src/FloatDesk.App/AppRuntime.cs`
- Modify: `docs/superpowers/specs/2026-10-26-realtime-translation-design.md` only if implementation reveals a factual correction
- Test: `tests/FloatDesk.Ui.Tests/TranslationPrivacyTests.cs`
- Modify: `tests/FloatDesk.Core.Tests/BuildBaselineTests.cs` if it asserts release constraints

**Interfaces:**
- Consumes: all prior tasks.
- Produces: no new public API; final privacy/acceptance coverage and complete build evidence.

- [ ] **Step 1: Write failing privacy/regression tests**

```csharp
[Fact]
public void TranslationSettingsJsonCannotContainCredentialFields()
{
    var json = JsonSerializer.Serialize(AppSettings.CreateDefault());
    Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
}

[Fact]
public void TranslationDiagnosticsAreContentFree()
{
    var properties = TranslationDiagnostics.Build(
        TranslationProviderKind.MyMemory,
        TranslationPurpose.Subtitle,
        totalCharacters: 842,
        elapsedMilliseconds: 213,
        TranslationFailureKind.Network,
        retryCount: 1);
    var serialized = JsonSerializer.Serialize(properties);
    Assert.DoesNotContain("Hello subtitle", serialized, StringComparison.Ordinal);
    Assert.DoesNotContain("apiKey", serialized, StringComparison.OrdinalIgnoreCase);
    Assert.Contains("characterBucket", serialized, StringComparison.Ordinal);
}

[Fact]
public void SettingsFreeAndPrivacyCopyIsExplicit()
{
    var xaml = File.ReadAllText(FindRepoFile(
        "src/FloatDesk.App/Views/SettingsWindow.xaml"));
    Assert.Contains("无需密钥", xaml, StringComparison.Ordinal);
    Assert.Contains("每日", xaml, StringComparison.Ordinal);
    Assert.Contains("截图不会上传", xaml, StringComparison.Ordinal);
    Assert.Contains("仅发送识别文字", xaml, StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj --filter "FullyQualifiedName~TranslationPrivacyTests" --verbosity minimal`

Expected: FAIL because `TranslationDiagnostics` and final privacy copy do not exist.

- [ ] **Step 3: Add content-free diagnostics and final copy**

Implement `TranslationDiagnostics.Build(...)` with only provider kind, purpose, character-count bucket (`0-99`, `100-499`, `500-1999`, `2000+`), elapsed milliseconds bucket, failure kind, and retry count. Use it for translation status logging. Do not accept text, translated text, endpoint, key, or exception message parameters.

Review every translation catch/log path and make sure it logs fixed metadata only. Do not log provider exception `.ToString()` or HTTP requests.

Add the final free-service/privacy text:

- MyMemory is free and needs no key but has daily/per-request limits.
- Screenshots are not uploaded.
- Only selected or recognized text is sent to the configured provider.
- Custom provider keys are stored encrypted on this Windows account.
- Exporting/importing settings does not export the key.

Update the About privacy paragraph to include translation behavior and test connection behavior.

- [ ] **Step 4: Run translation tests, full suite, and Release build**

Run: `dotnet test tests/FloatDesk.Core.Tests/FloatDesk.Core.Tests.csproj --filter "FullyQualifiedName~Translation|FullyQualifiedName~Subtitle" --verbosity minimal`

Run: `dotnet test tests/FloatDesk.Infrastructure.Tests/FloatDesk.Infrastructure.Tests.csproj --filter "FullyQualifiedName~Translation|FullyQualifiedName~SettingsStoreTests" --verbosity minimal`

Run: `dotnet test tests/FloatDesk.Windows.Tests/FloatDesk.Windows.Tests.csproj --filter "FullyQualifiedName~Translation|FullyQualifiedName~Ocr|FullyQualifiedName~CaptureExclusion" --verbosity minimal`

Run: `dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj --filter "FullyQualifiedName~Translation|FullyQualifiedName~Subtitle|FullyQualifiedName~SettingsWindowVisualTests|FullyQualifiedName~QuickPanelWindowLayoutTests" --verbosity minimal`

Run: `powershell -ExecutionPolicy Bypass -File scripts/test.ps1 -Configuration Release`

Run: `dotnet build FloatDesk.sln -c Release --no-restore --verbosity minimal`

Expected: all translation-focused tests pass; full suite passes; Release build reports `0 Warning(s)` and `0 Error(s)`.

- [ ] **Step 5: Commit**

```powershell
git add src/FloatDesk.App/Views/SettingsWindow.xaml src/FloatDesk.App/Translation/TranslationDiagnostics.cs src/FloatDesk.App/Translation/TranslationRuntime.cs src/FloatDesk.App/AppRuntime.cs tests/FloatDesk.Ui.Tests/TranslationPrivacyTests.cs tests/FloatDesk.Core.Tests/BuildBaselineTests.cs
git commit -m "chore: harden translation privacy and verification"
```

---

## Manual Verification Checklist

Run these on Windows 10 22H2 x64 and Windows 11 x64 after automated tasks pass:

- Fresh profile starts on MyMemory with no key and translates selected text.
- Selected text works in Edge, Notepad, Word, and a PDF reader.
- Clipboard contents containing text, an image, and file paths are restored after selected-text translation.
- Pressing the selected-text hotkey and holding its modifiers briefly never sends the wrong chord.
- OCR translation selects a region, shows original and translated text, and never creates an image file.
- OCR with a missing language pack and empty text show distinct actionable errors.
- Subtitle mode starts from a selected region, updates video captions, and ignores stable frames.
- Subtitle overlay is not present in OCR frames or ordinary screenshots.
- Pause/resume/reselect/stop all work; session lock and emergency pause stop sampling.
- A display removal pauses subtitle mode and requests a new region.
- MyMemory offline, 429/quota, Azure/DeepL 401, and OpenAI malformed response produce distinct errors.
- Provider switching applies without restarting the app; MyMemory requires no key.
- The API key never appears in `settings.json`, exported settings, or logs.
- Existing orb, wheel, screenshot, input lock, notes, and AI browser features still work.

## Implementation Self-Review

- Spec coverage: Tasks 1-8 cover settings, validation, migration, provider selection, encrypted keys, and the settings page. Tasks 3-6 cover MyMemory and all custom providers. Tasks 11-13 cover selected text and OCR. Tasks 14-15 cover subtitles, capture exclusion, and lifecycle. Task 16 covers privacy and full verification.
- Step scan: Every task has a failing test, an implementation step, a passing-test command, and a commit. No task uses "handle edge cases" or an undefined placeholder interface.
- Type consistency: `TranslationProviderKind`, `TranslationSettings`, `TranslationResponse`, `TranslationFailureKind`, `ITranslationProvider`, `ITranslationCredentialStore`, `IScreenOcrService`, and the three route methods use the same names across provider, Windows, App, and test tasks.
- Review focus: Modifier timing and clipboard preservation are tested in Task 11; subtitle feedback/exclusion in Task 15; latest-wins subtitle behavior in Task 14; content/secret leakage in Tasks 3-8 and 16.
- Proportion: The plan defines interfaces, boundaries, test names, and exact values; provider algorithms are described only where request format or failure mapping is not inferable from the contract.
