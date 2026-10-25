# FloatDesk Settings Visual Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rebuild the FloatDesk settings window in the approved ice-blue WPF visual style while preserving every existing setting and Windows 10/11 behavior.

**Architecture:** Introduce a window-scoped WPF resource dictionary for the custom shell and control templates, then rebuild `SettingsWindow.xaml` around a custom-chrome window, horizontal `TabControl` navigation, scrolling card content, and a fixed action bar. Keep the existing view model, save/import/export handlers, control names, and data grids intact; add only shell-window behavior and short page-switch motion.

**Tech Stack:** WPF, .NET 10, xUnit, WindowChrome, Segoe UI Variable / Segoe MDL2 Assets

**Spec:** `docs/superpowers/specs/2026-10-26-settings-visual-redesign-design.md`

## Global Constraints

- Continue using WPF and remain compatible with Windows 10 and Windows 11 x64.
- Do not add third-party UI dependencies.
- Preserve all existing bindings, event handlers, named controls, and local save behavior.
- Keep `LauncherGrid`, `InputRuleGrid`, `BrowserSitesGrid`, `WheelSettingsTab`, and `SaveStatusText` available for behavior compatibility.
- Default size `1160 x 820` DIP; minimum size `960 x 620` DIP; resizable.
- Page-change motion is at most 120 ms fade plus 6 DIP vertical translation and must honor system animation settings.
- Yield a Release build with 0 warnings and 0 errors and keep the full existing test suite green.

## Review Focus

- A narrow or 125% DPI window must keep navigation and bottom actions reachable without clipping.
- Imported settings with long shortcut paths and long localizable labels must remain readable in styled grids and fields.
- A high-contrast user must still see selection, focus, enabled state, and text without relying on blue tint.
- Closing, minimizing, maximizing, restoring, dragging, and table interactions must remain available after custom chrome replaces the native frame.
- Invalid settings must report the existing validation text without losing edits or making the window disappear.

---

### Task 1: Add visual-structure regression tests

**Files:**
- Create: `tests/FloatDesk.Ui.Tests/SettingsWindowVisualTests.cs`
- Modify: `tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj` only if resource files require explicit test access

**Interfaces:**
- Consumes: existing XAML at `src/FloatDesk.App/Views/SettingsWindow.xaml` and new dictionary at `src/FloatDesk.App/Resources/SettingsWindowResources.xaml`
- Produces: regression assertions for eight navigation headers, required control names, custom-chrome markers, and named styles

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void SettingsWindowDeclaresAllNavigationAndRequiredControls()
{
    var document = XDocument.Load(FindRepoFile("src/FloatDesk.App/Views/SettingsWindow.xaml"));
    var xaml = document.ToString(SaveOptions.DisableFormatting);

    foreach (var header in new[] { "常规", "悬浮球", "快捷应用", "轮盘", "输入法", "AI 网页", "截图", "关于" })
        Assert.Contains(header, xaml);
    foreach (var name in new[] { "LauncherGrid", "InputRuleGrid", "BrowserSitesGrid", "WheelSettingsTab", "SaveStatusText" })
        Assert.Contains($"x:Name=\"{name}\"", xaml, StringComparison.Ordinal);
}
```

Add assertions that the window uses `WindowChrome.WindowChrome`, the resource dictionary contains `SettingsCardStyle`, `ToggleSwitchStyle`, `NavigationTabItemStyle`, and `ActionButtonStyle`, and no `TabItem` uses the old default header-only structure.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj --no-restore --filter SettingsWindowVisualTests`
Expected: FAIL because the visual resource file and custom structure do not exist.

- [ ] **Step 3: Implement no product code in this task**

Leave product files unchanged until the structural test has failed for the expected missing visual markers.

- [ ] **Step 4: Run test and record the failure**

Run the same filtered test and confirm the failure is limited to missing redesigned structure.

- [ ] **Step 5: Commit**

```bash
git add tests/FloatDesk.Ui.Tests/SettingsWindowVisualTests.cs docs/superpowers/specs/2026-10-26-settings-visual-redesign-design.md docs/superpowers/plans/2026-10-26-settings-visual-redesign.md
git commit -m "test: define settings visual redesign structure"
```

### Task 2: Build the window-scoped design system

**Files:**
- Create: `src/FloatDesk.App/Resources/SettingsWindowResources.xaml`
- Modify: `src/FloatDesk.App/Views/SettingsWindow.xaml`

**Interfaces:**
- Consumes: existing application brushes `AccentBrush`, `InkBrush`, and `MutedInkBrush`
- Produces: `SettingsCardStyle`, `SettingsSectionTitleStyle`, `ToggleSwitchStyle`, `NavigationTabItemStyle`, `SettingsTabControlStyle`, `ActionButtonStyle`, `PrimaryActionButtonStyle`, `SettingsTextBoxStyle`, `SettingsComboBoxStyle`, `SettingsDataGridStyle`

- [ ] **Step 1: Create the resource dictionary with color, typography, card, toggle, focus, button, input, tab, and data-grid styles**

Use `Segoe UI Variable, Segoe UI, Microsoft YaHei UI`; use `Segoe MDL2 Assets` only for icon TextBlocks. Keep colors semantic and define a high-contrast fallback block.

- [ ] **Step 2: Run the visual-structure test**

Run: `dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj --no-restore --filter SettingsWindowVisualTests`
Expected: still FAIL because `SettingsWindow.xaml` has not adopted the resources.

- [ ] **Step 3: Commit the design-system resource file**

```bash
git add src/FloatDesk.App/Resources/SettingsWindowResources.xaml
git commit -m "style: add settings window design resources"
```

### Task 3: Rebuild the custom shell and navigation

**Files:**
- Modify: `src/FloatDesk.App/Views/SettingsWindow.xaml`
- Modify: `src/FloatDesk.App/Views/SettingsWindow.xaml.cs`

**Interfaces:**
- Consumes: styles from Task 2; existing `SettingsViewModel` and constructor parameters
- Produces: named shell controls `SettingsRoot`, `SettingsTitleBar`, `SettingsTabControl`, `MinimizeSettingsButton`, `MaximizeSettingsButton`, `CloseSettingsButton`; handlers `OnMinimizeClick`, `OnMaximizeClick`, `OnCloseSettingsClick`, and `OnSettingsTabSelectionChanged`

- [ ] **Step 1: Replace the window root with the approved geometry, `WindowChrome`, atmosphere, title bar, eight icon-led navigation tabs, content host, and fixed action bar**

Keep `WindowStartupLocation="CenterScreen"`, resizability, and the `Closing` hide behavior. Add AutomationProperties names to all caption buttons.

- [ ] **Step 2: Implement caption and motion behavior in `SettingsWindow.xaml.cs`**

Use `WindowState` for minimize/maximize/restore. On maximize hide rounded outer margins through a state trigger. Animate the content host opacity from `0.35` to `1` and translate from `6` to `0` over `120` ms only when `SystemParameters.ClientAreaAnimation` is true.

- [ ] **Step 3: Run the visual-structure test**

Run: `dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj --no-restore --filter SettingsWindowVisualTests`
Expected: PASS for shell and navigation assertions.

- [ ] **Step 4: Build the app**

Run: `dotnet build src/FloatDesk.App/FloatDesk.App.csproj -c Release --no-restore`
Expected: 0 warnings, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add src/FloatDesk.App/Views/SettingsWindow.xaml src/FloatDesk.App/Views/SettingsWindow.xaml.cs
git commit -m "feat: rebuild settings window shell"
```

### Task 4: Convert all eight settings pages to card layouts

**Files:**
- Modify: `src/FloatDesk.App/Views/SettingsWindow.xaml`

**Interfaces:**
- Consumes: all existing view-model property names and event handlers
- Produces: complete card-based content for 常规, 悬浮球, 快捷应用, 轮盘, 输入法, AI 网页, 截图, and 关于 while retaining named controls

- [ ] **Step 1: Rebuild 常规 and 悬浮球 with grouped cards and toggle switches**

Keep all bindings identical. Use `ToggleSwitchStyle` only for single booleans; keep `WheelSettingsTab` checkboxes as explicit multi-select controls.

- [ ] **Step 2: Rebuild 快捷应用, 输入法, and AI 网页 with styled tables and stable command rows**

Keep all DataGrid names, columns, selected-item handlers, and command button content semantics. Allow path columns to stretch and reserve horizontal grid scrolling for narrow windows.

- [ ] **Step 3: Rebuild 轮盘, 截图, and 关于**

Keep all wheel checkboxes named exactly as before. Make the privacy and compatibility text readable at narrow widths.

- [ ] **Step 4: Run the filtered visual test and full UI tests**

Run:
`dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj -c Release --no-restore`
Expected: all UI tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/FloatDesk.App/Views/SettingsWindow.xaml
git commit -m "style: apply card layout to settings pages"
```

### Task 5: Verify runtime, accessibility, and window states

**Files:**
- Modify only if verification exposes visual or interaction defects:
  - `src/FloatDesk.App/Resources/SettingsWindowResources.xaml`
  - `src/FloatDesk.App/Views/SettingsWindow.xaml`
  - `src/FloatDesk.App/Views/SettingsWindow.xaml.cs`

**Interfaces:**
- Consumes: completed settings window
- Produces: screenshot evidence at 100% and 125% DPI and a runtime smoke record

- [ ] **Step 1: Run the full Release test suite**

Run: `.\scripts\test.ps1 -Configuration Release`
Expected: all tests pass with no build warnings or errors.

- [ ] **Step 2: Launch the built app and capture the settings window at 100% DPI**

Use the project smoke automation or a temporary local capture harness. Inspect for clipping, incorrect navigation states, missing headers, and unobstructed bottom actions.

- [ ] **Step 3: Repeat at 125% DPI and with keyboard focus moved through navigation, fields, switches, and action buttons**

Confirm the focused control remains visible and that switching pages preserves values.

- [ ] **Step 4: Test minimize, maximize/restore, drag, close-to-hide, and reopen**

Expected: all window operations remain available and settings survive close/reopen.

- [ ] **Step 5: Commit any verification fixes**

```bash
git add src/FloatDesk.App/Resources/SettingsWindowResources.xaml src/FloatDesk.App/Views/SettingsWindow.xaml src/FloatDesk.App/Views/SettingsWindow.xaml.cs
git commit -m "fix: polish settings layout after runtime verification"
```

### Task 6: Publish and smoke-test the Windows package

**Files:**
- Modify: `docs/superpowers/specs/2026-10-26-settings-visual-redesign-design.md` only to append final verification evidence
- Create: self-contained Windows x64 ZIP and SHA256 file under the user output directory

**Interfaces:**
- Consumes: verified Release build
- Produces: distributable archive, checksum, and final screenshot references

- [ ] **Step 1: Publish the application to an isolated staging directory**

Use the project's existing publish command and runtime identifier from `scripts/publish.ps1`, without writing into the source tree.

- [ ] **Step 2: Run the release smoke test**

Invoke the existing smoke script against the staged executable and verify process startup, single instance, settings availability, and clean shutdown.

- [ ] **Step 3: Create the ZIP and SHA256SUMS file**

Place both in `C:\Users\Administrator\Documents\Codex\2026-10-25\new-chat\outputs`.

- [ ] **Step 4: Run final tests and inspect `git status`**

Run `.\scripts\test.ps1 -Configuration Release` and `git status --short`.
Expected: tests pass and only the intended release evidence or generated artifact references remain.

- [ ] **Step 5: Commit**

```bash
git add docs/superpowers/specs/2026-10-26-settings-visual-redesign-design.md
git commit -m "docs: record settings redesign verification"
```
