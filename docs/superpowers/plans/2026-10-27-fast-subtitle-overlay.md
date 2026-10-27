# Fast Subtitle Overlay Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make fixed-region video subtitle translation responsive enough for live playback, keep the last valid translation visible until a newer first non-empty result arrives, and display translations as transparent, region-anchored text above or below the selected subtitle area.

**Architecture:** Keep Windows OCR and the existing translation services, but separate subtitle sampling/OCR from network translation so a slow request no longer blocks capture. Use monotonically increasing caption generations and cancellation to make stale OCR/translation results harmless. Reuse `TranslationOverlayWindow` with a compact, transparent, token-protected subtitle surface and a new above/below/auto placement policy.

**Tech Stack:** .NET 10, WPF, Windows.Media.Ocr, Win32 display affinity/click-through APIs, xUnit, existing `TranslationCenter`, existing DPAPI credential store.

**Spec:** `docs/superpowers/specs/2026-10-27-fast-subtitle-overlay-design.md`

## Global Constraints

- Target `net10.0-windows10.0.19041.0`; preserve Windows 10 22H2 x64 and Windows 11 x64 compatibility.
- Hold the last rendered translation while a newer caption is pending.
- On the first non-empty translation for the current caption generation, clear and replace the old text in one Dispatcher callback; no crossfade, overlap, or visible blank frame.
- Empty/no-text OCR cancels the pending translation for that caption but does not clear the last rendered text.
- Keep `SubtitleRefreshIntervalMs` JSON compatibility; accepted range becomes 100–1500 ms and new installations default to 150 ms.
- Normal compact subtitle display has no card background, title, original text, metadata, status card, or permanent control bar.
- Compact subtitle mode must be click-through; pause/resume, reselect, and stop remain available from the tray.
- Keep Task 9 opaque operation-token semantics; every subtitle update must carry the current operation token.
- Never log/display secrets, OCR source text, translated text, screenshots, image hashes, request bodies, or response bodies. Only OCR text may be sent to the selected translation provider.
- No streaming translation in this plan.
- Update `.superpowers/sdd/2026-10-27-fast-subtitle-overlay/progress.md` after every task with its commit, focused tests, full-task verification, rulings, and residual risks.
- Never use `git add -A`.
- Preserve unrelated dirty user work:
  - `src/FloatDesk.App/Bootstrap/OrbImageImporter.cs`
  - `src/FloatDesk.App/Views/OrbWindow.xaml`
  - `src/FloatDesk.App/Views/OrbWindow.xaml.cs`
  - the `图片裁切` → `图片轮廓` hunk in `src/FloatDesk.App/Views/SettingsWindow.xaml`
  - `src/FloatDesk.App/Views/SettingsWindow.xaml.cs`
  - `tests/FloatDesk.Ui.Tests/OrbWindowImageTests.cs`
  - untracked orb-crop source/test files

## Review Focus

- Rapid subtitle changes and late OCR/translation results: an older result must never overwrite a newer caption (Tasks 5 and 6).
- Subtitle disappearance: a translation still in flight when the caption becomes empty must be canceled and must not appear late (Task 6).
- Extreme placement geometry: screen edges, negative monitor coordinates, mixed DPI, and regions that leave no non-overlapping side (Task 1 and Task 3).
- Capture exclusion unavailable: the overlay must not cause recursive OCR by overlapping the sampled region while capture is active (Tasks 3, 6, and 8).
- Session controls/lifecycle: click-through display, tray pause/resume/reselect/stop, provider changes, pause, and stop must not leave sampling or UI state alive (Tasks 6, 7, and 8).

---

### Task 1: Settings Model and Anchored Placement

**Files:**
- Modify: `src/FloatDesk.Core/Models/TranslationModels.cs`
- Create: `src/FloatDesk.Core/Geometry/SubtitleOverlayPlacementPolicy.cs`
- Modify: `src/FloatDesk.Infrastructure/Settings/SettingsValidator.cs`
- Modify: `tests/FloatDesk.Infrastructure.Tests/TranslationSettingsValidationTests.cs`
- Modify: `tests/FloatDesk.Infrastructure.Tests/SettingsStoreTests.cs`
- Create: `tests/FloatDesk.Core.Tests/SubtitleOverlayPlacementPolicyTests.cs`

**Interfaces:**
- Produces:
  - `public enum SubtitleAttachmentMode { Auto, Above, Below }`
  - `TranslationSettings.SubtitleAttachment` initialized to `SubtitleAttachmentMode.Auto`
  - `SubtitleOverlayPlacementPolicy.PlaceAnchored(PixelRect captureRegion, PixelSize overlaySize, PixelRect workArea, SubtitleAttachmentMode mode, int gap = 8, bool allowCaptureOverlap = false) -> PixelRect?`
- Default `SubtitleRefreshIntervalMs` becomes `150`; validation accepts `100..1500`.
- `Auto` chooses the side with more usable vertical room, ties choose `Above`.
- If the configured side does not fit, try the other side. Return `null` if neither fits without intersecting unless `allowCaptureOverlap` is true.

- [ ] **Step 1: Write failing model and validation tests**

Add assertions equivalent to:

```csharp
Assert.Equal(150, settings.Translation.SubtitleRefreshIntervalMs);
Assert.Equal(SubtitleAttachmentMode.Auto, settings.Translation.SubtitleAttachment);
```

Update the existing `DefaultTranslationSettingsUseFreeProviderAndChineseTarget` expectation from 450 to 150 and change `ValidatorRejectsOutOfRangeSubtitleValuesAndCoordinates` to use 99 and a separate 1501 boundary case instead of 299.

Add a test named `ValidatorAcceptsSubtitleCaptureIntervalsFrom100Through1500` that accepts both `100` and `1500`, and a test named `ValidatorRejectsUndefinedSubtitleAttachment` that expects an error containing `SubtitleAttachment`.

Add `TranslationSettingsRoundTripIncludesSubtitleAttachment` to `SettingsStoreTests` and assert the saved/loaded `Translation` records are equal when `SubtitleAttachment = SubtitleAttachmentMode.Below`.

- [ ] **Step 2: Run the tests to verify RED**

Run:

```powershell
dotnet test tests\FloatDesk.Infrastructure.Tests\FloatDesk.Infrastructure.Tests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~TranslationSettingsValidationTests|FullyQualifiedName~SettingsStoreTests.TranslationSettingsRoundTripIncludesSubtitleAttachment"
```

Expected: compile failure because `SubtitleAttachmentMode`, the property, or 150-ms default does not exist.

- [ ] **Step 3: Write failing placement tests**

Add tests with these exact expected rectangles:

```csharp
var region = new PixelRect(100, 300, 600, 100);
var size = new PixelSize(400, 80);
var workArea = new PixelRect(0, 0, 1000, 800);

Assert.Equal(
    new PixelRect(200, 212, 400, 80),
    SubtitleOverlayPlacementPolicy.PlaceAnchored(
        region, size, workArea, SubtitleAttachmentMode.Above));

Assert.Equal(
    new PixelRect(200, 408, 400, 80),
    SubtitleOverlayPlacementPolicy.PlaceAnchored(
        region, size, workArea, SubtitleAttachmentMode.Below));

Assert.Equal(
    new PixelRect(200, 408, 400, 80),
    SubtitleOverlayPlacementPolicy.PlaceAnchored(
        region, size, workArea, SubtitleAttachmentMode.Auto));
```

Also cover negative monitor coordinates, requested-above-falls-back-below, no fit returns `null`, and `allowCaptureOverlap: true` returns a clamped rectangle.

- [ ] **Step 4: Run placement tests to verify RED**

Run:

```powershell
dotnet test tests\FloatDesk.Core.Tests\FloatDesk.Core.Tests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~SubtitleOverlayPlacementPolicyTests"
```

Expected: compile failure because `SubtitleOverlayPlacementPolicy` does not exist.

- [ ] **Step 5: Implement the model, validator, and placement policy**

Center the overlay horizontally on the capture region. Clamp the centered X and candidate Y to the work area. Reject overlay sizes larger than the work area. Evaluate candidates in this order:

1. Requested side.
2. Opposite side.
3. If `allowCaptureOverlap`, the preferred side clamped into the work area.

For `Auto`, order the larger usable side first. Do not clamp a non-overlap candidate into an overlapping position when `allowCaptureOverlap` is false.

- [ ] **Step 6: Run focused tests to verify GREEN**

Run:

```powershell
dotnet test tests\FloatDesk.Core.Tests\FloatDesk.Core.Tests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~SubtitleOverlayPlacementPolicyTests"
dotnet test tests\FloatDesk.Infrastructure.Tests\FloatDesk.Infrastructure.Tests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~TranslationSettingsValidationTests|FullyQualifiedName~SettingsStoreTests.TranslationSettingsRoundTripIncludesSubtitleAttachment"
```

Expected: both commands pass.

- [ ] **Step 7: Commit**

```powershell
git add docs/superpowers/specs/2026-10-27-fast-subtitle-overlay-design.md docs/superpowers/plans/2026-10-27-fast-subtitle-overlay.md src/FloatDesk.Core/Models/TranslationModels.cs src/FloatDesk.Core/Geometry/SubtitleOverlayPlacementPolicy.cs src/FloatDesk.Infrastructure/Settings/SettingsValidator.cs tests/FloatDesk.Infrastructure.Tests/TranslationSettingsValidationTests.cs tests/FloatDesk.Infrastructure.Tests/SettingsStoreTests.cs tests/FloatDesk.Core.Tests/SubtitleOverlayPlacementPolicyTests.cs
git commit -m "feat: add subtitle attachment settings and placement"
```

### Task 2: Subtitle Settings UI

**Files:**
- Modify: `src/FloatDesk.App/ViewModels/SettingsViewModel.cs`
- Modify: `src/FloatDesk.App/Views/SettingsWindow.xaml`
- Modify: `tests/FloatDesk.Ui.Tests/SettingsViewModelTranslationTests.cs`
- Modify: `tests/FloatDesk.Ui.Tests/SettingsWindowVisualTests.cs`
- Modify: `tests/FloatDesk.Ui.Tests/TranslationSettingsApplierTests.cs`

**Interfaces:**
- Consumes: `SubtitleAttachmentMode` and `TranslationSettings.SubtitleAttachment`.
- Produces:
  - `SettingsViewModel.SubtitleAttachmentIndex : int`
  - `SettingsViewModel.SubtitleAttachmentModes : IReadOnlyList<SubtitleAttachmentMode>`
  - XAML controls named `SubtitleAttachmentComboBox`, `SubtitleRefreshIntervalTextBox`, `SubtitleOverlayOpacityTextBox`, and `SubtitleFontSizeTextBox`.

- [ ] **Step 1: Write failing view-model and XAML tests**

Add tests asserting:

```csharp
Assert.Equal(2, viewModel.SubtitleAttachmentIndex);
viewModel.SubtitleAttachmentIndex = 1;
var latest = AppSettings.CreateDefault();
Assert.Equal(
    SubtitleAttachmentMode.Above,
    viewModel.BuildCandidate(latest).Translation.SubtitleAttachment);
```

Add `SubtitleSettingsExposeAttachmentIntervalOpacityAndFontControls` that checks the four control names, `SubtitleAttachmentComboBox` has the three localized options, and the interval label contains `100–1500 ms`.

- [ ] **Step 2: Run UI tests to verify RED**

Run:

```powershell
dotnet test tests\FloatDesk.Ui.Tests\FloatDesk.Ui.Tests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~SettingsViewModelTranslationTests|FullyQualifiedName~SettingsWindowVisualTests.SubtitleSettingsExposeAttachmentIntervalOpacityAndFontControls"
```

Expected: compile/test failure because `SubtitleAttachmentIndex` and the controls are missing.

- [ ] **Step 3: Implement view-model mapping and merge**

Map index `0/1/2` to `Auto/Above/Below`. Preserve the latest saved value if the index is outside the valid range. In `MergeTranslation`, update `SubtitleAttachment` only when the selected value differs from the baseline.

- [ ] **Step 4: Implement the settings UI**

Replace the three-row subtitle settings grid with a four-row grid:

- `画面检测间隔（100–1500ms，150ms 适合实时字幕）`
- `显示位置` with `自动`, `字幕上方`, `字幕下方`
- `文字不透明度（60–100%）`
- `字号（14–36 DIP）`

Add `AutomationProperties.Name` to every control. Change descriptive copy from a floating card to transparent, region-anchored subtitle text.

- [ ] **Step 5: Stage only the task XAML hunk and run focused tests**

Use `git add -p -- src/FloatDesk.App/Views/SettingsWindow.xaml`, select only the subtitle-settings changes, and reject the unrelated `图片裁切` → `图片轮廓` hunk.

Run:

```powershell
dotnet test tests\FloatDesk.Ui.Tests\FloatDesk.Ui.Tests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~SettingsViewModelTranslationTests|FullyQualifiedName~SettingsWindowVisualTests.SubtitleSettingsExposeAttachmentIntervalOpacityAndFontControls|FullyQualifiedName~TranslationSettingsApplierTests"
```

Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add src/FloatDesk.App/ViewModels/SettingsViewModel.cs tests/FloatDesk.Ui.Tests/SettingsViewModelTranslationTests.cs tests/FloatDesk.Ui.Tests/SettingsWindowVisualTests.cs tests/FloatDesk.Ui.Tests/TranslationSettingsApplierTests.cs
git commit -m "feat: expose fast subtitle display settings"
```

### Task 3: Compact Transparent Subtitle Surface

**Files:**
- Modify: `src/FloatDesk.App/Views/TranslationOverlayWindow.xaml`
- Modify: `src/FloatDesk.App/Views/TranslationOverlayWindow.xaml.cs`
- Modify: `src/FloatDesk.App/Translation/TranslationRouteHost.cs`
- Modify: `tests/FloatDesk.Ui.Tests/TranslationOverlayWindowTests.cs`
- Modify: `tests/FloatDesk.Ui.Tests/SubtitleTranslationControllerTests.cs`

**Interfaces:**
- Consumes: `SubtitleOverlayPlacementPolicy.PlaceAnchored` and `TranslationSettings.SubtitleAttachment`.
- Produces:
  - `TranslationOverlayWindow.ShowSubtitleText(TranslationOverlayOperationToken operation, string translatedText) -> TranslationSubtitlePlacementStatus`
  - `TranslationOverlayWindow.ShowSubtitleNotice(TranslationOverlayOperationToken operation, string notice)`
  - `ITranslationRouteHost.ShowSubtitleText(TranslationRouteHandle operation, string translatedText) -> TranslationSubtitlePlacementStatus`
  - `ITranslationRouteHost.ShowSubtitleNotice(TranslationRouteHandle operation, string notice)`

- [ ] **Step 1: Replace the old subtitle-control test with failing compact-mode tests**

Add:

- `CompactSubtitleModeUsesTransparentTextSurface`: asserts `TransitionLayer` is collapsed, `SubtitleSurface` is visible, the card has transparent background and no border, and `SubtitleControlBar` is collapsed.
- `SubtitleTextReplacesPreviousTextAndRejectsStaleToken`: displays two non-empty texts for the current token and asserts the second replaces the first; after `DismissOperation`, a late update cannot repopulate the text.
- `SubtitleNoticeDoesNotClearTranslatedText`: display `第一句`, show a notice, and assert the text remains `第一句`.
- `SubtitleTextUsesConfiguredFontSize`: applies 30 DIP and asserts the compact text font size.

Update fake route hosts in controller tests to record `ShowSubtitleText` and `ShowSubtitleNotice` calls.

- [ ] **Step 2: Run overlay tests to verify RED**

Run:

```powershell
dotnet test tests\FloatDesk.Ui.Tests\FloatDesk.Ui.Tests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~TranslationOverlayWindowTests.CompactSubtitleModeUsesTransparentTextSurface|FullyQualifiedName~TranslationOverlayWindowTests.SubtitleTextReplacesPreviousTextAndRejectsStaleToken|FullyQualifiedName~TranslationOverlayWindowTests.SubtitleNoticeDoesNotClearTranslatedText|FullyQualifiedName~TranslationOverlayWindowTests.SubtitleTextUsesConfiguredFontSize"
```

Expected: compile/test failure because the compact surface and methods are missing.

- [ ] **Step 3: Implement compact-mode XAML**

Add `x:Name="SubtitleSurface"` with a transparent root and:

- `SubtitleNoticeTextBlock`: small text-only notice, collapsed when empty.
- `SubtitleTranslationTextBlock`: centered, wrapping translation text with a soft shadow effect.

Name the existing decorative background layer `CardBackdrop`. In compact mode collapse `TransitionLayer` and `SubtitleControlBar`, clear `OverlayCard` background/border/padding, show `SubtitleSurface`, and hide the window until the first subtitle text or notice exists.

- [ ] **Step 4: Implement token-protected subtitle updates and placement**

`ShowSubtitleText` must run on the Dispatcher, return early for an invalid token or whitespace text, then assign empty and the new value in the same callback. Constrain compact text width from the capture region converted from physical pixels to DIPs, remeasure, and call the Task 1 placement policy with `allowCaptureOverlap: IsCaptureExcluded`.

If the window cannot fit, return `NoSafePlacement`; keep the previously rendered text unchanged.

`ShowSubtitleNotice` uses the current token and displays only the notice text. It never clears or replaces `SubtitleTranslationTextBlock.Text`.

Update `ShowStatus`/`ShowError` so subtitle-mode calls use notices instead of the old status card. Keep full-card behavior unchanged for selected-text and region OCR.

- [ ] **Step 5: Run focused tests to verify GREEN**

```powershell
dotnet test tests\FloatDesk.Ui.Tests\FloatDesk.Ui.Tests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~TranslationOverlayWindowTests|FullyQualifiedName~SubtitleTranslationControllerTests"
```

Expected: PASS, including updated tests for the removed permanent control bar.

- [ ] **Step 6: Commit**

```powershell
git add src/FloatDesk.App/Views/TranslationOverlayWindow.xaml src/FloatDesk.App/Views/TranslationOverlayWindow.xaml.cs src/FloatDesk.App/Translation/TranslationRouteHost.cs tests/FloatDesk.Ui.Tests/TranslationOverlayWindowTests.cs tests/FloatDesk.Ui.Tests/SubtitleTranslationControllerTests.cs
git commit -m "feat: add transparent compact subtitle surface"
```

### Task 4: Reuse the Windows OCR Engine

**Files:**
- Modify: `src/FloatDesk.Windows/Translation/WindowsScreenOcrService.cs`
- Modify: `tests/FloatDesk.Windows.Tests/WindowsScreenOcrServiceTests.cs`

**Interfaces:**
- Keeps `IScreenOcrService.RecognizeAsync(CaptureFrame, string?, CancellationToken)` unchanged.
- OCR engine instances remain confined to the existing OCR STA worker.

- [ ] **Step 1: Write failing engine-lifecycle tests**

Extend `FakeOcrBackend`/`FakeOcrEngine` with creation and disposal counters. Add:

- `RepeatedOcrWithSameLanguageReusesOneEngine`
- `ChangingOcrLanguageDisposesAndRecreatesEngine`
- `DisposingServiceDisposesCachedEngine`

Assert backend creation count is one for repeated same-language calls, two after a language change, and engine disposal count increments exactly once per replaced/final engine.

- [ ] **Step 2: Run tests to verify RED**

```powershell
dotnet test tests\FloatDesk.Windows.Tests\FloatDesk.Windows.Tests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~WindowsScreenOcrServiceTests.RepeatedOcrWithSameLanguageReusesOneEngine|FullyQualifiedName~WindowsScreenOcrServiceTests.ChangingOcrLanguageDisposesAndRecreatesEngine|FullyQualifiedName~WindowsScreenOcrServiceTests.DisposingServiceDisposesCachedEngine"
```

Expected: FAIL because each recognition currently creates and disposes a new engine.

- [ ] **Step 3: Implement worker-thread engine caching**

In `WindowsScreenOcrService`, keep `_cachedEngine` and `_cachedLanguageTag`. Resolve or create the engine only when the normalized requested language changes. Dispose the previous cached engine before replacing it and dispose the final cached engine when the worker exits.

Do not call or retain `OcrEngine` instances from caller threads.

- [ ] **Step 4: Run the complete OCR test class**

```powershell
dotnet test tests\FloatDesk.Windows.Tests\FloatDesk.Windows.Tests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~WindowsScreenOcrServiceTests"
```

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/FloatDesk.Windows/Translation/WindowsScreenOcrService.cs tests/FloatDesk.Windows.Tests/WindowsScreenOcrServiceTests.cs
git commit -m "perf: reuse Windows OCR engines"
```

### Task 5: Latest-Only OCR and Translation Pipeline

**Files:**
- Modify: `src/FloatDesk.App/Translation/SubtitleTranslationController.cs`
- Create: `src/FloatDesk.App/Translation/LatestSubtitleWorkSlot.cs`
- Modify: `tests/FloatDesk.Ui.Tests/SubtitleTranslationControllerTests.cs`
- Create: `tests/FloatDesk.Ui.Tests/LatestSubtitleWorkSlotTests.cs`

**Interfaces:**
- Consumes: `ITranslationRouteHost.TranslateAsync`, `ITranslationRouteHost.ShowSubtitleText`, existing frame detector.
- Produces internally:
  - `LatestSubtitleWorkSlot<T>.Publish(T item) : void`
  - `LatestSubtitleWorkSlot<T>.TakeAsync(CancellationToken) : Task<T?>`
  - `LatestSubtitleWorkSlot<T>.Dispose() : void`
  - where `T : class, IDisposable`
- New generations supersede old generations; stale OCR and translation results are discarded.

- [ ] **Step 1: Write failing pipeline tests**

Add tests using controllable fake clock/capture/OCR/host:

- `SlowTranslationDoesNotBlockNextCaptureOrOcr`: block the first translation, assert capture/OCR counts continue increasing.
- `NewCaptionCancelsPreviousTranslation`: return a canceled response only when the first translation token is canceled, then succeed for the second.
- `LateOcrForSupersededFrameDoesNotTranslate`: make the first OCR complete after a newer frame was published and assert only the newer text reaches translation.
- `LatestSubtitleWorkSlotDisposesReplacedPendingItem`: publish two disposable items before taking and assert only the latest is returned and the replaced item was disposed.

- [ ] **Step 2: Run tests to verify RED**

```powershell
dotnet test tests\FloatDesk.Ui.Tests\FloatDesk.Ui.Tests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~SubtitleTranslationControllerTests.SlowTranslationDoesNotBlockNextCaptureOrOcr|FullyQualifiedName~SubtitleTranslationControllerTests.NewCaptionCancelsPreviousTranslation|FullyQualifiedName~SubtitleTranslationControllerTests.LateOcrForSupersededFrameDoesNotTranslate|FullyQualifiedName~LatestSubtitleWorkSlotTests"
```

Expected: FAIL because the pipeline is still sequential and the latest-work slot does not exist.

- [ ] **Step 3: Implement `LatestSubtitleWorkSlot<T>`**

Use a lock plus `SemaphoreSlim(0, 1)` or an equivalent single-waiter signal. `Publish` replaces any unclaimed item and disposes the replaced item. `TakeAsync` transfers ownership to the caller. `Dispose` disposes any unclaimed item and causes waiting takes to return `null`.

- [ ] **Step 4: Refactor the controller into capture and OCR lanes**

The capture lane:

- Samples at the configured interval.
- Reuses the existing change detector and an 80–100 ms stability check.
- Publishes a generation-tagged frame candidate to the latest OCR slot.
- Does not await translation.

The OCR lane:

- Continuously takes the latest candidate.
- Ignores results whose generation is no longer the latest posted caption.
- Normalizes text and applies existing near-duplicate suppression.
- Starts or supersedes translation work for the newest accepted text.

Translation work:

- Keep one in-flight translation CTS and task.
- Cancel the previous CTS when newer OCR text is accepted.
- Await and observe every abandoned task during pause/stop/dispose so exceptions are not lost.
- Continue to call `TranslateAsync(text, TranslationPurpose.Subtitle, token)`.

- [ ] **Step 5: Run focused pipeline tests to verify GREEN**

```powershell
dotnet test tests\FloatDesk.Ui.Tests\FloatDesk.Ui.Tests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~SubtitleTranslationControllerTests|FullyQualifiedName~LatestSubtitleWorkSlotTests"
```

Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add src/FloatDesk.App/Translation/SubtitleTranslationController.cs src/FloatDesk.App/Translation/LatestSubtitleWorkSlot.cs tests/FloatDesk.Ui.Tests/SubtitleTranslationControllerTests.cs tests/FloatDesk.Ui.Tests/LatestSubtitleWorkSlotTests.cs
git commit -m "perf: add latest-caption subtitle pipeline"
```

### Task 6: Display Hold, Empty Frames, Failures, and Lifecycle

**Files:**
- Modify: `src/FloatDesk.App/Translation/SubtitleTranslationController.cs`
- Modify: `src/FloatDesk.App/Translation/TranslationRuntime.cs`
- Modify: `tests/FloatDesk.Ui.Tests/SubtitleTranslationControllerTests.cs`
- Modify: `tests/FloatDesk.Ui.Tests/TranslationRuntimeTests.cs`

**Interfaces:**
- Consumes: Task 3 `ShowSubtitleText`/`ShowSubtitleNotice` and Task 5 caption generations.
- Produces:
  - stable session semantics for hold, replace, blank, errors, pause/resume, reselect, and stop
  - optional `Action<string>? warning` constructor callback for actionable errors; it receives user-facing error text only, never OCR or translation content

- [ ] **Step 1: Write failing display/lifecycle tests**

Add:

- `PendingCaptionKeepsLastRenderedTranslation`: first result renders `第一句`; a second translation remains blocked; assert no empty/replacement subtitle call.
- `FirstNonEmptyNewTranslationReplacesLastTranslation`: release `第二句`; assert exactly one replacement call with non-empty text.
- `EmptyOcrCancelsPendingCaptionAndKeepsLastText`: display `第一句`, start a new caption translation, then OCR returns `OcrNoText`; assert the pending translation is canceled and displayed text remains `第一句`.
- `NetworkFailureKeepsLastTextAndRetriesCurrentCaption`: first result `第一句`, next request fails twice then succeeds; assert displayed text remains throughout and retry uses only the newest source text.
- `ActionableErrorRetainsTextShowsNoticeAndEmitsWarning`: authentication failure leaves `第一句` visible, emits one notice, and emits one warning message.
- `NoTranslationNeededDisplaysSourceSubtitle`: response `NoTranslationNeeded` results in `ShowSubtitleText(currentSourceText)`.
- `PauseRetainsTextAndCancelsPendingWork`, `ReselectClearsText`, `StopClearsTextAndEndsBothLanes`, and `SessionLockOrEmergencyPauseCancelsPendingWorkWithoutPublishingLateText`.
- Update `TranslationRuntimeTests` so provider/settings changes stop the new pipeline and dismiss only the current token.

- [ ] **Step 2: Run tests to verify RED**

```powershell
dotnet test tests\FloatDesk.Ui.Tests\FloatDesk.Ui.Tests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~SubtitleTranslationControllerTests.PendingCaptionKeepsLastRenderedTranslation|FullyQualifiedName~SubtitleTranslationControllerTests.FirstNonEmptyNewTranslationReplacesLastTranslation|FullyQualifiedName~SubtitleTranslationControllerTests.EmptyOcrCancelsPendingCaptionAndKeepsLastText|FullyQualifiedName~SubtitleTranslationControllerTests.NetworkFailureKeepsLastTextAndRetriesCurrentCaption|FullyQualifiedName~SubtitleTranslationControllerTests.NoTranslationNeededDisplaysSourceSubtitle|FullyQualifiedName~SubtitleTranslationControllerTests.PauseRetainsTextAndCancelsPendingWork|FullyQualifiedName~SubtitleTranslationControllerTests.ReselectClearsText|FullyQualifiedName~SubtitleTranslationControllerTests.StopClearsTextAndEndsBothLanes"
```

Expected: FAIL on display-state and cancellation semantics.

- [ ] **Step 3: Implement display and failure semantics**

- Track `lastRenderedText` and never call `ShowSubtitleText` with empty text.
- On current-generation success, call `ShowSubtitleText` once with the full non-empty result.
- On `NoTranslationNeeded`, display normalized source text.
- On `OcrNoText`/empty source, increment caption generation and cancel OCR/translation/retry for the superseded caption without clearing display.
- On OCR failure, keep the current generation and retry. On transient translation failure, retain display and retry only the current source after a short backoff.
- On authentication/unsupported-language/placement failure, pause sampling, retain display, call `ShowSubtitleNotice`, and invoke the optional warning callback once for actionable errors.

- [ ] **Step 4: Implement lifecycle cleanup**

Pause cancels frame/OCR/translation/retry work but keeps text and the overlay operation. Resume restarts from a fresh frame. Reselect ends the old generation, clears display with a fresh operation, and starts the new region. Stop awaits both lanes, cancels all child CTSs, disposes pending frames, clears text, and ends the operation.

- [ ] **Step 5: Run focused UI and runtime tests**

```powershell
dotnet test tests\FloatDesk.Ui.Tests\FloatDesk.Ui.Tests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~SubtitleTranslationControllerTests|FullyQualifiedName~TranslationRuntimeTests|FullyQualifiedName~TranslationOverlayWindowTests"
```

Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add src/FloatDesk.App/Translation/SubtitleTranslationController.cs src/FloatDesk.App/Translation/TranslationRuntime.cs tests/FloatDesk.Ui.Tests/SubtitleTranslationControllerTests.cs tests/FloatDesk.Ui.Tests/TranslationRuntimeTests.cs
git commit -m "feat: stabilize live subtitle display lifecycle"
```

### Task 7: Click-Through Display and Tray Controls

**Files:**
- Modify: `src/FloatDesk.App/Views/TranslationOverlayWindow.xaml.cs`
- Modify: `src/FloatDesk.App/Bootstrap/TrayService.cs`
- Create: `src/FloatDesk.App/Bootstrap/SubtitleTrayState.cs`
- Modify: `src/FloatDesk.App/AppRuntime.cs`
- Modify: `tests/FloatDesk.Ui.Tests/TranslationOverlayWindowTests.cs`
- Create: `tests/FloatDesk.Ui.Tests/SubtitleTrayStateTests.cs`
- Modify: `tests/FloatDesk.Ui.Tests/SettingsWindowVisualTests.cs`

**Interfaces:**
- Produces:
  - `SubtitleTrayState(bool Running, bool Paused)` with `CommandsEnabled` and `PauseHeader`.
  - `TrayService.UpdateSubtitleSession(bool running, bool paused)`.
  - `TranslationOverlayWindow.IsClickThrough` internal read-only state.
- `TrayService` receives callbacks for `toggleSubtitlePause`, `reselectSubtitleRegion`, and `stopSubtitles`.

- [ ] **Step 1: Write failing click-through and tray-state tests**

Add:

```csharp
Assert.True(window.IsClickThrough);
window.SetSubtitleControls(operation, active: false, paused: false, canReselect: false);
Assert.False(window.IsClickThrough);
```

Add `SubtitleTrayStateReflectsRunningAndPausedState` asserting:

```csharp
Assert.True(new SubtitleTrayState(true, false).CommandsEnabled);
Assert.Equal("暂停字幕翻译", new SubtitleTrayState(true, false).PauseHeader);
Assert.Equal("继续字幕翻译", new SubtitleTrayState(true, true).PauseHeader);
Assert.False(new SubtitleTrayState(false, false).CommandsEnabled);
```

Add a source-wiring test asserting `AppRuntime` subscribes tray subtitle callbacks and updates tray state in start/pause/stop paths.

- [ ] **Step 2: Run tests to verify RED**

```powershell
dotnet test tests\FloatDesk.Ui.Tests\FloatDesk.Ui.Tests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~TranslationOverlayWindowTests.CompactSubtitleModeIsClickThrough|FullyQualifiedName~SubtitleTrayStateTests|FullyQualifiedName~SettingsWindowVisualTests.AppRuntimeWiresSubtitleTrayCommands"
```

Expected: FAIL because click-through state and tray commands are missing.

- [ ] **Step 3: Implement click-through mode**

Add `WS_EX_TRANSPARENT` to the existing extended style while compact subtitle mode is active and remove it when leaving subtitle mode. Reapply the style after the HWND exists. Preserve `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` in both modes.

- [ ] **Step 4: Add tray subtitle commands**

Add a `字幕翻译` submenu containing:

- Pause/resume, header from `SubtitleTrayState.PauseHeader`.
- Reselect subtitle region.
- Stop subtitle translation.

Disable all three unless `Running`. Wire the callbacks through `AppRuntime` to the existing translation runtime methods and refresh state after start, pause, resume, reselect, and stop.

- [ ] **Step 5: Run focused tests**

```powershell
dotnet test tests\FloatDesk.Ui.Tests\FloatDesk.Ui.Tests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~TranslationOverlayWindowTests|FullyQualifiedName~SubtitleTrayStateTests|FullyQualifiedName~SettingsWindowVisualTests.AppRuntimeWiresSubtitleTrayCommands"
```

Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add src/FloatDesk.App/Views/TranslationOverlayWindow.xaml.cs src/FloatDesk.App/Bootstrap/TrayService.cs src/FloatDesk.App/Bootstrap/SubtitleTrayState.cs src/FloatDesk.App/AppRuntime.cs tests/FloatDesk.Ui.Tests/TranslationOverlayWindowTests.cs tests/FloatDesk.Ui.Tests/SubtitleTrayStateTests.cs tests/FloatDesk.Ui.Tests/SettingsWindowVisualTests.cs
git commit -m "feat: add click-through subtitles and tray controls"
```

### Task 8: Integration, Diagnostics, and Full Verification

**Files:**
- Modify: `src/FloatDesk.App/Translation/SubtitleTranslationController.cs`
- Modify: `.superpowers/sdd/2026-10-27-fast-subtitle-overlay/progress.md` (ignored ledger)
- Modify: `tests/FloatDesk.Ui.Tests/WindowCoordinatorCaptureTests.cs`
- Modify: `tests/FloatDesk.Ui.Tests/TranslationPrivacyTests.cs`
- Modify: `tests/FloatDesk.Ui.Tests/TranslationRuntimeTests.cs`

**Interfaces:**
- Consumes all prior task interfaces.
- Produces content-free diagnostics and cross-feature integration coverage.

- [ ] **Step 1: Write failing integration/privacy tests**

Add:

- `SubtitlePipelinePausesForCaptureAndResumesOnlyAfterLastLease`.
- `CaptionDiagnosticsContainNoSourceOrTranslatedText`: capture emitted diagnostic keys/values and assert no source or translation text appears.
- `ProviderReconfigureStopsAllSubtitleLanesAndDismissesCurrentToken`.
- Update existing capture-coordination tests to assert the new OCR/translation lanes are canceled and joined on capture pause.

- [ ] **Step 2: Run tests to verify RED**

```powershell
dotnet test tests\FloatDesk.Ui.Tests\FloatDesk.Ui.Tests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~WindowCoordinatorCaptureTests|FullyQualifiedName~TranslationPrivacyTests.CaptionDiagnosticsContainNoSourceOrTranslatedText|FullyQualifiedName~TranslationRuntimeTests.ProviderReconfigureStopsAllSubtitleLanesAndDismissesCurrentToken"
```

Expected: FAIL until integration and diagnostics are implemented.

- [ ] **Step 3: Implement content-free stage diagnostics**

Use the existing diagnostics callback with event names such as:

- `translation.subtitle.frame_changed`
- `translation.subtitle.displayed`

Allowed fields are duration buckets, character-count buckets, generation supersession count, failure kind, and retry count. Do not include OCR text, translated text, image data, or hashes.

- [ ] **Step 4: Run focused integration tests to verify GREEN**

```powershell
dotnet test tests\FloatDesk.Ui.Tests\FloatDesk.Ui.Tests.csproj -c Release --no-restore -p:NuGetAudit=false --filter "FullyQualifiedName~WindowCoordinatorCaptureTests|FullyQualifiedName~TranslationPrivacyTests.CaptionDiagnosticsContainNoSourceOrTranslatedText|FullyQualifiedName~TranslationRuntimeTests"
```

Expected: PASS.

- [ ] **Step 5: Run Release build and complete test suite**

```powershell
dotnet build src\FloatDesk.App\FloatDesk.App.csproj -c Release --no-restore -p:NuGetAudit=false
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\test.ps1 -Configuration Release
```

Expected: Release build reports `0 warnings`, `0 errors`; `scripts/test.ps1` reports zero failed tests.

- [ ] **Step 6: Run manual Windows smoke test**

Verify on Windows 10 22H2 and Windows 11 x64:

- Start subtitle translation, select a subtitle region, and receive the first translation.
- While the next translation is pending, the previous translation stays visible.
- The next translation replaces it with no blank frame or overlap.
- Pause/resume, reselect, and stop from the tray.
- Subtitle text does not block clicks in a playing video.
- Above, below, and auto placement at screen edges and at 100/125/150/200% scaling.
- Slow/offline translation keeps the previous text and does not stop subtitle detection.

Record unresolved hardware/provider-specific failures in the ledger.

- [ ] **Step 7: Commit integration coverage**

```powershell
git add src/FloatDesk.App/Translation/SubtitleTranslationController.cs tests/FloatDesk.Ui.Tests/WindowCoordinatorCaptureTests.cs tests/FloatDesk.Ui.Tests/TranslationPrivacyTests.cs tests/FloatDesk.Ui.Tests/TranslationRuntimeTests.cs
git commit -m "test: verify live subtitle integration and privacy"
```

### Final Review

- Generate a review package for commits from the plan base through Task 8.
- Dispatch one fresh independent whole-feature reviewer focused on the `Review Focus` list, stale-result races, WPF/Win32 input behavior, and multi-DPI geometry.
- Fix Critical/Important findings with RED→GREEN tests and rerun `scripts/test.ps1 -Configuration Release`.
- Report the final executable path: `src/FloatDesk.App/bin/Release/net10.0-windows10.0.19041.0/YueYuOrb.exe`.
