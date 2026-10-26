# AI Browser Theme Follow Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the embedded AI browser and its login popups follow FloatDesk's effective light/dark theme, with native WebView2 color-scheme support and one exact-host DeepSeek adapter.

**Architecture:** Put the pure theme resolution and exact-host catalog in `FloatDesk.Core`, keep settings/view-model merging in the existing settings path, and keep WebView2/JavaScript application in `FloatDesk.App`. Apply the resolved scheme before navigation, refresh one host-guarded document-created script when the scheme changes, and notify open popups through their owning browser window.

**Tech Stack:** C# 14, .NET 10, WPF, Microsoft.Web.WebView2 1.0.4314.35, XUnit

**Spec:** `docs/superpowers/specs/2026-10-26-ai-browser-theme-follow-design.md`

## Global Constraints

- Preserve Windows 10/11 x64 compatibility.
- Keep `net10.0-windows`, WPF, and WebView2 package `1.0.4314.35`.
- Use `CoreWebView2Profile.PreferredColorScheme` before the first navigation.
- Never add a global CSS inversion or generic forced-dark adapter.
- Apply site adapter changes only to exact top-level `chat.deepseek.com` pages.
- Add `BrowserTheme` as an init-only property defaulting to `FollowApp`; keep `SchemaVersion = 1`.
- Do not read or transmit credentials, cookies, tokens, or page content.
- Do not create commits because the worktree already contains user-owned uncommitted changes.
- Do not modify or revert the pre-existing orb, wheel, input-lock, or settings redesign changes.

## Review Focus

- An existing `settings.json` without `browserTheme` must load as `FollowApp`, not fail or silently become Light.
- `chat.deepseek.com.evil.test`, `deepseek.com`, and embedded frames must never receive the DeepSeek adapter.
- An older WebView2 runtime that rejects `PreferredColorScheme` must leave the browser usable instead of surfacing an initialization error.
- Setting changes, navigation, and browser rebuilds must not leave a stale adapter script or stale scheme attached.
- An untouched settings editor must preserve a concurrent browser-theme change while an edited value must win.

---

### Task 1: Core Theme Policy and Adapter Catalog

**Files:**
- Modify: `src/FloatDesk.Core/Models/BrowserModels.cs`
- Create: `src/FloatDesk.Core/Browser/BrowserThemePolicy.cs`
- Create: `tests/FloatDesk.Core.Tests/BrowserThemePolicyTests.cs`

**Interfaces:**
- Consumes: existing `AppTheme`
- Produces:
  - `BrowserThemeMode { FollowApp, Light, Dark }`
  - `BrowserColorScheme { Light, Dark }`
  - `readonly record struct BrowserThemeAdapter(string Id, string Host)`
  - `BrowserThemePolicy.DeepSeekHost = "chat.deepseek.com"`
- `BrowserThemePolicy.Resolve(BrowserThemeMode requested, AppTheme appTheme, bool systemUsesDarkTheme) -> BrowserColorScheme`
  - `BrowserThemePolicy.FindAdapter(Uri uri) -> BrowserThemeAdapter?`

- [ ] **Step 1: Write failing resolver tests**

Add `BrowserThemePolicyTests` with a theory covering:

- `FollowApp + AppTheme.System + light system -> Light`
- `FollowApp + AppTheme.System + dark system -> Dark`
- `FollowApp + AppTheme.Light + dark system -> Light`
- `FollowApp + AppTheme.Dark + light system -> Dark`
- explicit `Light` and `Dark` overriding the app theme

Use one assertion per inline case:

```csharp
[Theory]
[InlineData(BrowserThemeMode.FollowApp, AppTheme.System, true, BrowserColorScheme.Dark)]
[InlineData(BrowserThemeMode.FollowApp, AppTheme.Dark, false, BrowserColorScheme.Dark)]
public void ResolveUsesRequestedAndAppTheme(
    BrowserThemeMode requested,
    AppTheme appTheme,
    bool systemDark,
    BrowserColorScheme expected)
{
    Assert.Equal(expected, BrowserThemePolicy.Resolve(requested, appTheme, systemDark));
}
```

- [ ] **Step 2: Run the focused tests and verify failure**

Run: `dotnet test tests/FloatDesk.Core.Tests/FloatDesk.Core.Tests.csproj -c Debug --filter FullyQualifiedName~BrowserThemePolicyTests`

Expected: FAIL because `BrowserThemeMode`, `BrowserColorScheme`, and `BrowserThemePolicy` do not exist.

- [ ] **Step 3: Implement the enums and resolver**

Add the enums to `BrowserModels.cs`. Implement `BrowserThemePolicy.Resolve` with explicit modes first, then `FollowApp`, and throw `ArgumentOutOfRangeException` for undefined enum values.

- [ ] **Step 4: Write failing exact-host tests**

Add tests proving:

- `https://chat.deepseek.com/` and `https://CHAT.DEEPSEEK.COM/chat` return adapter id `deepseek`, host `chat.deepseek.com`.
- `https://chat.deepseek.com:443/` returns the same adapter.
- `https://deepseek.com/`, `https://chat.deepseek.com.evil.test/`, and `https://example.com/` return `null`.

Use `BrowserThemePolicy.FindAdapter(new Uri(value))`.

- [ ] **Step 5: Run the focused tests and verify failure**

Run: `dotnet test tests/FloatDesk.Core.Tests/FloatDesk.Core.Tests.csproj -c Debug --filter FullyQualifiedName~BrowserThemePolicyTests`

Expected: FAIL because `FindAdapter` and `BrowserThemeAdapter` do not exist.

- [ ] **Step 6: Implement normalized exact-host matching**

Compare `Uri.IdnHost` with `BrowserThemePolicy.DeepSeekHost` using `OrdinalIgnoreCase`, trim a single trailing dot, and return the singleton DeepSeek adapter record only for exact equality. Do not use suffix matching. `BrowserThemeScripts` must use the same `DeepSeekHost` constant so the catalog and injected guard cannot drift.

- [ ] **Step 7: Run focused core tests**

Run: `dotnet test tests/FloatDesk.Core.Tests/FloatDesk.Core.Tests.csproj -c Debug --filter FullyQualifiedName~BrowserThemePolicyTests`

Expected: PASS.

---

### Task 2: Settings Persistence, Merge, and Appearance Selector

**Files:**
- Modify: `src/FloatDesk.Core/Models/AppSettings.cs`
- Modify: `src/FloatDesk.Infrastructure/Settings/SettingsValidator.cs`
- Modify: `src/FloatDesk.App/ViewModels/SettingsViewModel.cs`
- Modify: `src/FloatDesk.App/Views/SettingsWindow.xaml`
- Modify: `tests/FloatDesk.Infrastructure.Tests/SettingsStoreTests.cs`
- Modify: `tests/FloatDesk.Ui.Tests/SettingsViewModelMergeTests.cs`
- Modify: `tests/FloatDesk.Ui.Tests/SettingsWindowVisualTests.cs`

**Interfaces:**
- Consumes: `BrowserThemeMode` from Task 1
- Produces:
  - `AppSettings.BrowserTheme { get; init; }` defaulting to `BrowserThemeMode.FollowApp`
  - `SettingsViewModel.BrowserThemeIndex { get; set; }`
  - `x:Name="BrowserThemeComboBox"` with options `跟随 FloatDesk`, `浅色`, `深色`

- [ ] **Step 1: Write failing legacy-settings and round-trip tests**

In `SettingsStoreTests`:

- Serialize default settings, remove `browserTheme` from the JSON object, deserialize through `SettingsMigrator`, and assert `BrowserThemeMode.FollowApp`.
- Save a setting with `BrowserTheme = BrowserThemeMode.Dark`, reload through a new `JsonSettingsStore`, and assert `Dark`.
- Set `BrowserTheme = (BrowserThemeMode)99` and assert `SettingsValidator.Validate` reports an error containing `BrowserTheme`.

- [ ] **Step 2: Run the focused tests and verify failure**

Run: `dotnet test tests/FloatDesk.Infrastructure.Tests/FloatDesk.Infrastructure.Tests.csproj -c Debug --filter FullyQualifiedName~SettingsStoreTests`

Expected: FAIL because `AppSettings.BrowserTheme` does not exist.

- [ ] **Step 3: Add the compatible settings property and validation**

Add the init-only `BrowserTheme` property to `AppSettings` without changing the positional constructor. Validate `Enum.IsDefined(value.BrowserTheme)` in `SettingsValidator`. Do not bump `CurrentSchemaVersion`.

- [ ] **Step 4: Write failing merge and XAML tests**

In `SettingsViewModelMergeTests`:

- Load `FollowApp`, apply a concurrent `Dark` change to `latest`, leave the editor untouched, and assert the candidate stays Dark.
- Load `FollowApp`, set `BrowserThemeIndex = (int)BrowserThemeMode.Light`, apply a concurrent Dark change, and assert the candidate becomes Light.

In `SettingsWindowVisualTests`, load the XAML and assert it contains `x:Name="BrowserThemeComboBox"`, `Text="网页外观"`, and all three option labels.

- [ ] **Step 5: Run the focused tests and verify failure**

Run: `dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj -c Debug --filter "FullyQualifiedName~SettingsViewModelMergeTests|FullyQualifiedName~SettingsWindowVisualTests"`

Expected: FAIL because the view-model property and selector do not exist.

- [ ] **Step 6: Implement view-model merge and the selector**

Add `BrowserThemeIndex`, initialize it from `settings.BrowserTheme`, and merge it with the same edited-versus-latest rule as `BrowserZoomPercent`. Add the named combo box to the existing AI window-behavior card using `SettingsComboBoxStyle`; keep the existing release-on-close and topmost controls.

- [ ] **Step 7: Run focused settings tests**

Run: `dotnet test tests/FloatDesk.Infrastructure.Tests/FloatDesk.Infrastructure.Tests.csproj -c Debug --filter FullyQualifiedName~SettingsStoreTests`

Run: `dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj -c Debug --filter "FullyQualifiedName~SettingsViewModelMergeTests|FullyQualifiedName~SettingsWindowVisualTests"`

Expected: PASS.

---

### Task 3: WebView2 Native Scheme and Host-Guarded Script

**Files:**
- Create: `src/FloatDesk.App/Browser/BrowserThemeScripts.cs`
- Create: `src/FloatDesk.App/Browser/WebViewThemeBridge.cs`
- Create: `src/FloatDesk.App/Browser/BrowserThemeRuntimeState.cs`
- Modify: `src/FloatDesk.App/Bootstrap/ThemeService.cs`
- Modify: `src/FloatDesk.App/Views/AiBrowserWindow.xaml.cs`
- Create: `tests/FloatDesk.Ui.Tests/BrowserThemeScriptTests.cs`

**Interfaces:**
- Consumes: `BrowserColorScheme`, `BrowserThemeMode`, `BrowserThemePolicy`, and `AppSettings.BrowserTheme`
- Produces:
  - `ThemeService.SystemUsesDarkTheme() -> bool` (public)
  - `BrowserThemeScripts.BuildDocumentScript(BrowserColorScheme scheme) -> string`
  - `BrowserThemeRuntimeState.ScriptId { get; } -> string?`
  - `BrowserThemeRuntimeState.NeedsReplacement(BrowserColorScheme scheme) -> bool`
  - `BrowserThemeRuntimeState.Record(string scriptId, BrowserColorScheme scheme) -> void`
  - `BrowserThemeRuntimeState.Reset() -> void`
  - `WebViewThemeBridge.IsOptionalFailure(Exception exception) -> bool`
  - `WebViewThemeBridge.ApplyPreferredColorScheme(CoreWebView2 core, BrowserColorScheme scheme) -> void`
  - `WebViewThemeBridge.ReplaceDocumentScriptAsync(CoreWebView2 core, string? existingScriptId, BrowserColorScheme scheme) -> Task<string>`
  - `WebViewThemeBridge.ApplyCurrentDocumentAsync(CoreWebView2 core, BrowserColorScheme scheme) -> Task`

- [ ] **Step 1: Write failing script tests**

In `BrowserThemeScriptTests` assert the dark script contains:

- exact host guard `location.hostname !== 'chat.deepseek.com'`;
- top-level guard `window.top !== window`;
- `documentElement.dataset.dsDarkTheme = ''`;
- `documentElement.style.colorScheme = 'dark'`;
- an idempotency marker keyed by the resolved scheme;
- `requestAnimationFrame` and `setTimeout(..., 250)` bounded retries;
- no `filter:` or blanket CSS injection.

Assert the light script removes `data-ds-dark-theme` and sets `colorScheme = 'light'`.

Add tests proving:

- `WebViewThemeBridge.IsOptionalFailure` returns true for `NotImplementedException`, `InvalidOperationException`, `COMException`, and `ObjectDisposedException`, and false for `OperationCanceledException`.
- `BrowserThemeRuntimeState.NeedsReplacement` is true for the initial scheme, false after recording that scheme, true after recording a different scheme, and true after `Reset()`.

- [ ] **Step 2: Run script tests and verify failure**

Run: `dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj -c Debug --filter FullyQualifiedName~BrowserThemeScriptTests`

Expected: FAIL because `BrowserThemeScripts` does not exist.

- [ ] **Step 3: Implement the script and WebView bridge**

`BuildDocumentScript` returns one IIFE that exits for non-top-level documents and non-DeepSeek hosts, marks itself idempotently, applies the site-owned attribute, and retries only immediately, on one animation frame, and after 250 ms. `BrowserThemeRuntimeState` owns the active script id and scheme; `IsOptionalFailure` names the WebView2 compatibility exceptions that may be ignored safely.

`WebViewThemeBridge` maps Light/Dark to `CoreWebView2PreferredColorScheme`, removes the prior document script when its id is non-null, adds the new script before navigation, and executes the same script in the current document. Expose no page data to callers.

- [ ] **Step 4: Run script tests**

Run: `dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj -c Debug --filter FullyQualifiedName~BrowserThemeScriptTests`

Expected: PASS.

- [ ] **Step 5: Integrate the main browser lifecycle**

In `AiBrowserWindow`:

- add `_browserTheme`, `_resolvedColorScheme`, `BrowserThemeRuntimeState`, and a `_themeGate`;
- make `ThemeService.SystemUsesDarkTheme` accessible and resolve in `ApplySettings`;
- after `EnsureCoreWebView2Async` and before `Navigate`, call `ApplyPreferredColorScheme`, replace the script, then navigate;
- when initialized and settings change, apply the profile scheme and asynchronously replace the script plus execute it in the current page;
- reapply the script in successful `NavigationCompleted`;
- catch `NotImplementedException`, `InvalidOperationException`, `COMException`, and `ObjectDisposedException` around best-effort theme operations so browser initialization and navigation remain functional;
- call `BrowserThemeRuntimeState.Reset()` when rebuilding or disposing the WebView.

- [ ] **Step 6: Run focused app tests**

Run: `dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj -c Debug --filter "FullyQualifiedName~BrowserThemeScriptTests|FullyQualifiedName~SettingsWindowVisualTests|FullyQualifiedName~ThemeResourceTests"`

Expected: PASS.

---

### Task 4: Login Popup Theme and Windows Preference Updates

**Files:**
- Modify: `src/FloatDesk.App/Views/AiBrowserWindow.xaml.cs`
- Modify: `src/FloatDesk.App/AppRuntime.cs`
- Modify: `tests/FloatDesk.Ui.Tests/ThemeResourceTests.cs`

**Interfaces:**
- Consumes: Task 3 bridge and resolved scheme
- Produces:
  - popup constructor accepting `BrowserColorScheme`
  - popup `ApplyThemeAsync(BrowserColorScheme)` for open-window updates
  - `AppRuntime` handling `SystemEvents.UserPreferenceChanged`

- [ ] **Step 1: Add the failing runtime-source regression check**

Add `RuntimeObservesWindowsThemePreferenceChanges` to `ThemeResourceTests`. It reads `AppRuntime.cs` and asserts the file contains both `UserPreferenceChanged +=` and `UserPreferenceChanged -=`. This protects the integration point that cannot be exercised without changing the Windows theme.

- [ ] **Step 2: Run the test and verify failure**

Run: `dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj -c Debug --filter FullyQualifiedName~ThemeResourceTests`

Expected: FAIL because `UserPreferenceChanged` is not wired.

- [ ] **Step 3: Implement popup scheme propagation**

Pass `_resolvedColorScheme` into `AiPopupWindow`; after its WebView initializes, apply the profile scheme and document-created script before navigating. Reapply the adapter in successful popup `NavigationCompleted` events. Track popups in `AiBrowserWindow`, update them when the main scheme changes, remove them from the tracking list on `Closed`, and make all popup theme calls best-effort and non-fatal.

- [ ] **Step 4: Handle Windows app-theme changes**

Subscribe to `Microsoft.Win32.SystemEvents.UserPreferenceChanged` after startup and unsubscribe in both normal and exceptional disposal paths. On the WPF dispatcher, reapply `ThemeService.Apply(_settings.Current.General.Theme)` and call the existing AI browser's `ApplySettings`; do not rebind hotkeys or rerun unrelated settings side effects. Let the browser no-op when its resolved scheme did not change.

- [ ] **Step 5: Run focused UI and core tests**

Run: `dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj -c Debug --filter "FullyQualifiedName~SettingsWindowVisualTests|FullyQualifiedName~BrowserThemeScriptTests"`

Run: `dotnet test tests/FloatDesk.Core.Tests/FloatDesk.Core.Tests.csproj -c Debug --filter FullyQualifiedName~BrowserThemePolicyTests`

Expected: PASS.

---

### Task 5: User Documentation and Release Verification

**Files:**
- Modify: `docs/user-guide.md`
- Modify: `README.md` only if the existing AI-browser feature list needs the new appearance option
- Modify: `tests/FloatDesk.Ui.Tests/SettingsWindowVisualTests.cs` if the final copy assertion needs tightening

**Interfaces:**
- Consumes: all previous tasks
- Produces: user-facing behavior documentation and verified Release output

- [ ] **Step 1: Update the user guide**

Document the three AI appearance modes, default Follow FloatDesk behavior, native-site limitation for unsupported sites, DeepSeek support, popup behavior, and the fact that this does not alter external browsers. Do not promise universal forced dark mode.

- [ ] **Step 2: Run the focused documentation-adjacent UI test**

Run: `dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj -c Debug --filter FullyQualifiedName~SettingsWindowVisualTests`

Expected: PASS and the XAML still contains all three user-visible options.

- [ ] **Step 3: Run the full Release test suite**

Run: `.\scripts\test.ps1 -Configuration Release`

Expected: restore and build succeed; all tests pass.

- [ ] **Step 4: Run the locked Release build**

Run: `.\scripts\build.ps1 -Configuration Release -Locked`

Expected: zero warnings and zero errors.

- [ ] **Step 5: Verify the executable path and document residual manual checks**

Confirm `D:\FloatDesk\src\FloatDesk.App\bin\Release\net10.0-windows\FloatDesk.exe` exists and record its timestamp. Report manual checks still requiring a desktop session: Dark launch without a white flash, live Light/Dark switching, Windows system-theme switching, popup theme, and navigation away from DeepSeek.
