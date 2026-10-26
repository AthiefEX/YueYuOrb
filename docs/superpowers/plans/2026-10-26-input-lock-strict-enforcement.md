# Input Lock Strict Enforcement Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the approved enhanced user-mode English input lock using the configured KLID, foreground verification, shortcut interception, dynamic shortcut learning, and previous-layout restoration.

**Architecture:** Keep policy, lifecycle state, shortcut matching, and Windows integration separate. `InputLockEngine` owns lock/restore transitions; `WindowsInputBackend` owns layouts and foreground requests; `WindowsKeyboardSwitchGuard` owns a dedicated `WH_KEYBOARD_LL` thread while a lock is verified.

**Tech Stack:** C# 14, .NET 10, Win32 `WH_KEYBOARD_LL`, XUnit, WPF

**Spec:** `docs/superpowers/specs/2026-10-26-input-lock-strict-enforcement-design.md`

## Global Constraints

- Preserve Windows 10/11 x64 compatibility.
- Use `InputLockSettings.TargetKlid`, default `00000409`.
- Accept only loadable English non-IME target layouts.
- Do not request administrator privilege or install a service, driver, or TSF component.
- Keep FloatDesk interactive windows, exclusion rules, capture pause, emergency pause, and session lock exempt.
- Use 50 ms polling only while a lock is active; otherwise use 250 ms.
- Learned shortcuts are session-only.
- Do not leave the global hook installed after unlock, pause, exclusion, or disposal.
- Do not modify or revert the pre-existing orb/wheel worktree changes.
- Do not create commits because the worktree already contains user-owned uncommitted changes.

## Review Focus

- Foreground changes during an asynchronous request must not verify or restore against the wrong thread or epoch.
- Temporary exemptions must not discard a pending pre-lock layout that is still owed to the user.
- Rule-to-rule foreground changes must keep one continuous lock and avoid a visible restore/reapply cycle.
- A failed hook installation must report `Degraded` and must not claim that shortcut interception is active.
- Shortcut matching must not swallow ordinary typing, Shift selection, or unrelated Ctrl/Shift commands.

---

### Task 1: Core Contracts and Shortcut State

**Files:**
- Modify: `src/FloatDesk.Core/Models/InputModels.cs`
- Modify: `src/FloatDesk.Core/Input/InputPolicy.cs`
- Create: `src/FloatDesk.Core/Input/KeyboardSwitchGuardState.cs`
- Modify: `tests/FloatDesk.Core.Tests/InputPolicyTests.cs`
- Create: `tests/FloatDesk.Core.Tests/KeyboardSwitchGuardTests.cs`

**Interfaces:**
- Consumes: existing `ForegroundSnapshot`, `AppRule`, `InputContext`
- Produces:
  - `InputLockState.Degraded`
  - `KeyboardSwitchChord(ShortcutModifiers Modifiers, uint VirtualKey)`
  - `InputLayoutTarget(nint Hkl, string Klid, uint LanguageId)`
  - `LayoutResolution(InputLayoutTarget? Target, string? ErrorCode, string? Message)` with `Success`
  - `LayoutObservation(nint Hkl, string Klid, uint LanguageId, bool IsEnglish, bool IsIme)`
  - `InputDecision(InputAction Action, string Reason, bool PreserveLockState = false)`
  - `IInputBackend.ResolveAsync`, `ReadAsync`, `ActivateAsync`, `RestoreAsync`
  - `IKeyboardSwitchGuard.Enable`, `Disable`, `ObserveLayout`, `IsActive`
  - `KeyboardSwitchGuardState.Begin`, `End`, `HandleKeyEvent`, `ObserveLayout`, `IsBlocked`
  - `KeyboardSwitchShortcuts.ParseToggleValues`

- [ ] **Step 1: Write failing policy tests for preservation**

Add tests asserting own windows, capture pause, emergency pause, exclusion, session lock, and unknown paths return `PreserveLockState=true`; an ordinary non-matching external app returns `false`.

- [ ] **Step 2: Run the focused policy tests and verify failure**

Run: `dotnet test tests/FloatDesk.Core.Tests/FloatDesk.Core.Tests.csproj -c Debug --filter FullyQualifiedName~InputPolicyTests`

Expected: FAIL because `InputDecision` has no preservation flag and policy does not set it.

- [ ] **Step 3: Add the model contracts and policy flag**

Implement the exact records, enum value, and interfaces listed above. Preserve lock state for temporary exemptions; do not preserve it for a true no-rule/no-manual release.

- [ ] **Step 4: Write failing shortcut-state tests**

Cover:

- `Alt+Shift`, `Ctrl+Shift`, `Win+Space`, and `Ctrl+Space` are blocked.
- plain `Shift+A`, `Ctrl+C`, and `Shift+letter` are not blocked.
- an unknown `Alt+Q` is observed but not blocked; after an HKL drift inside 1500 ms it is learned and the next `Alt+Q` is blocked.
- a plain `Ctrl+letter` is never learned from a drift.
- toggle values `"1"` and `"2"` add Alt+Shift and Ctrl+Shift without duplicates.

- [ ] **Step 5: Run the shortcut tests and verify failure**

Run: `dotnet test tests/FloatDesk.Core.Tests/FloatDesk.Core.Tests.csproj -c Debug --filter FullyQualifiedName~KeyboardSwitchGuardTests`

Expected: FAIL because the shortcut state and parser do not exist.

- [ ] **Step 6: Implement pure shortcut matching**

Normalize modifier flags, track left/right modifier virtual keys, record only qualifying modifier chords, learn the latest chord when `ObserveLayout` sees a non-target HKL within 1500 ms, and preserve learned chords across `End`/`Begin`.

- [ ] **Step 7: Run focused core tests**

Run: `dotnet test tests/FloatDesk.Core.Tests/FloatDesk.Core.Tests.csproj -c Debug --filter "FullyQualifiedName~InputPolicyTests|FullyQualifiedName~KeyboardSwitchGuardTests"`

Expected: PASS.

---

### Task 2: Lock Engine Lifecycle

**Files:**
- Modify: `src/FloatDesk.Core/Input/InputLockEngine.cs`
- Modify: `tests/FloatDesk.Core.Tests/InputLockEngineTests.cs`

**Interfaces:**
- Consumes: all Task 1 contracts
- Produces:
  - `InputLockEngine(IInputBackend backend, IKeyboardSwitchGuard guard, TimeProvider? timeProvider = null)`
  - `UpdateAsync(ForegroundSnapshot target, InputContext context, IReadOnlyList<AppRule> rules, InputLockOptions options, CancellationToken cancellationToken)`
  - `bool IsActive { get; }`
  - `bool HasPendingRestore { get; }`

- [ ] **Step 1: Rewrite engine test fakes and add failing lifecycle tests**

Use a fake backend that exposes `ResolveCalls`, `ActivateCalls`, `RestoreCalls`, and mutable current HKL. Use a fake guard exposing `EnableCount`, `DisableCount`, and configurable enable failure. Cover:

- configured KLID reaches `ResolveAsync`;
- first lock saves the non-target HKL and manual unlock restores it;
- leaving a rule application restores it;
- a target-gone restore remains pending and is applied once to the next eligible external foreground;
- missing target layout returns `Unavailable` and never enables the guard;
- guard startup failure returns `Degraded`;
- rule A to rule B remains continuous with no restore;
- a stale epoch cannot verify or clear a restore;
- disposal disables the guard and attempts a best-effort restore;
- temporary `PreserveLockState` disables the hook but retains the saved HKL.

- [ ] **Step 2: Run the engine tests and verify failure**

Run: `dotnet test tests/FloatDesk.Core.Tests/FloatDesk.Core.Tests.csproj -c Debug --filter FullyQualifiedName~InputLockEngineTests`

Expected: FAIL against the old two-operation backend and no guard.

- [ ] **Step 3: Implement resolve/apply/verify/restore**

Serialize updates with a gate while advancing epochs before waiting. Cache the resolved target, save only the first non-target HKL, verify exact HKL equality, enable the guard only after verification, and map failure codes to actionable states.

- [ ] **Step 4: Implement pending restore and temporary preservation**

Disable the hook on every non-locking evaluation. Restore only for a true release and an eligible external foreground; retain pending restoration for own windows, exclusions, unknown targets, and `TargetGone`/`TargetChanged`. Clear it only after a successful restore request and a still-current epoch.

- [ ] **Step 5: Run focused core tests**

Run: `dotnet test tests/FloatDesk.Core.Tests/FloatDesk.Core.Tests.csproj -c Debug --filter "FullyQualifiedName~InputPolicyTests|FullyQualifiedName~KeyboardSwitchGuardTests|FullyQualifiedName~InputLockEngineTests"`

Expected: PASS.

---

### Task 3: Windows Layout Backend

**Files:**
- Modify: `src/FloatDesk.Windows/Native/NativeMethods.cs`
- Modify: `src/FloatDesk.Windows/Input/KeyboardLayoutCatalog.cs`
- Modify: `src/FloatDesk.Windows/Input/WindowsInputBackend.cs`
- Modify: `tests/FloatDesk.Windows.Tests/PlatformAdapterTests.cs`

**Interfaces:**
- Consumes: Task 1 records and `WindowsInputBackend`
- Produces:
  - `KeyboardLayoutCatalog.Resolve(string klid) -> LayoutResolution`
  - `KeyboardLayoutCatalog.Describe(nint hkl, string? requestedKlid = null) -> KeyboardLayoutInfo`
  - `WindowsInputBackend` implementing all four `IInputBackend` operations

- [ ] **Step 1: Write failing adapter tests**

Assert that `Resolve("00000409")` either resolves an English non-IME layout or fails with `LayoutUnavailable`/`LayoutNotInstalled` and an actionable message. Assert malformed and non-English KLIDs are rejected before activation. Assert a non-foreground target is rejected with `TargetChanged`.

- [ ] **Step 2: Run the Windows tests and verify failure**

Run: `dotnet test tests/FloatDesk.Windows.Tests/FloatDesk.Windows.Tests.csproj -c Debug --filter FullyQualifiedName~PlatformAdapterTests`

Expected: FAIL because `Resolve`, `ActivateAsync`, and `RestoreAsync` do not exist.

- [ ] **Step 3: Implement KLID resolution and layout description**

Normalize eight-digit KLIDs, call `LoadKeyboardLayoutW(klid, 0)`, derive the language ID from the low 16 bits, accept English non-IME layouts, and return exact HKL/KLID information. Keep loaded-layout enumeration using the normalized language ID rather than the raw HKL.

- [ ] **Step 4: Implement exact activation and restoration requests**

Guard both operations with `IsWindow` and foreground checks, post `WM_INPUTLANGCHANGEREQUEST` to the focus/top-level window, and classify UIPI denial as `AccessDenied`.

- [ ] **Step 5: Run Windows adapter tests**

Run: `dotnet test tests/FloatDesk.Windows.Tests/FloatDesk.Windows.Tests.csproj -c Debug --filter FullyQualifiedName~PlatformAdapterTests`

Expected: PASS.

---

### Task 4: Windows Keyboard Hook Guard

**Files:**
- Modify: `src/FloatDesk.Windows/Native/NativeMethods.cs`
- Create: `src/FloatDesk.Windows/Input/WindowsKeyboardSwitchGuard.cs`
- Modify: `tests/FloatDesk.Windows.Tests/PlatformAdapterTests.cs`

**Interfaces:**
- Consumes: `KeyboardSwitchGuardState`, `KeyboardSwitchShortcuts`, `InputLayoutTarget`
- Produces: `WindowsKeyboardSwitchGuard : IKeyboardSwitchGuard`

- [ ] **Step 1: Write a failing guard lifecycle test**

Resolve the current loaded layout through `KeyboardLayoutCatalog.GetLoadedLayouts`, create `WindowsKeyboardSwitchGuard`, enable it with that target, assert `IsActive`, disable it, and assert cleanup. If no layout can be obtained, fail with a diagnostic rather than silently passing.

- [ ] **Step 2: Run the guard test and verify failure**

Run: `dotnet test tests/FloatDesk.Windows.Tests/FloatDesk.Windows.Tests.csproj -c Debug --filter FullyQualifiedName~PlatformAdapterTests`

Expected: FAIL because `WindowsKeyboardSwitchGuard` does not exist.

- [ ] **Step 3: Implement the dedicated hook thread**

Create a message queue before readiness, install `WH_KEYBOARD_LL` with `GetModuleHandle(null)`, return `1` from the callback only for blocked key-down events, always forward other events, unhook on the hook thread, and post `WM_QUIT` during disable.

- [ ] **Step 4: Wire registry shortcuts and dynamic state**

Read `HKCU\Keyboard Layout\Toggle` on each enable, merge values with the common defaults and supplied extra chords, pass modifier/key events to `KeyboardSwitchGuardState`, and route layout observations back to the same state.

- [ ] **Step 5: Run Windows tests**

Run: `dotnet test tests/FloatDesk.Windows.Tests/FloatDesk.Windows.Tests.csproj -c Debug --filter FullyQualifiedName~PlatformAdapterTests`

Expected: PASS and guard is inactive after disposal.

---

### Task 5: Runtime Integration and UI Copy

**Files:**
- Modify: `src/FloatDesk.App/AppRuntime.cs`
- Modify: `src/FloatDesk.App/Views/SettingsWindow.xaml`
- Modify: `tests/FloatDesk.Ui.Tests/SettingsWindowVisualTests.cs`
- Modify: `docs/user-guide.md`

**Interfaces:**
- Consumes: all prior tasks
- Produces: production `InputLockEngine` wired to `WindowsInputBackend` and `WindowsKeyboardSwitchGuard`, active 50 ms cadence, actionable KLID help text

- [ ] **Step 1: Write a failing settings-copy test**

Assert the Input tab contains text explaining that the KLID must be an English non-IME loadable Windows layout, that shortcut switching is intercepted while active, and that the previous layout is restored after release.

- [ ] **Step 2: Run the UI test and verify failure**

Run: `dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj -c Debug --filter FullyQualifiedName~SettingsWindowVisualTests`

Expected: FAIL because the current help text only mentions `00000409`.

- [ ] **Step 3: Integrate the guard and pass settings**

Construct `WindowsKeyboardSwitchGuard`, pass `InputLockOptions` with `_settings.Current.InputLock.TargetKlid`, evaluate pending restores during polling, and update `_inputTimer.Interval` to 50 ms only when `_input.IsActive`; otherwise reset it to 250 ms.

- [ ] **Step 4: Update settings and user documentation**

Describe KLID resolution, English non-IME validation, shortcut interception, dynamic learning, restoration, exemptions, and degraded behavior without promising an OS security boundary.

- [ ] **Step 5: Run UI tests**

Run: `dotnet test tests/FloatDesk.Ui.Tests/FloatDesk.Ui.Tests.csproj -c Debug --filter FullyQualifiedName~SettingsWindowVisualTests`

Expected: PASS.

---

### Task 6: Full Verification

**Files:**
- Verify only

**Interfaces:**
- Consumes: completed implementation
- Produces: Release test/build evidence

- [ ] **Step 1: Run the complete Release test suite**

Run: `.\scripts\test.ps1 -Configuration Release`

Expected: all tests pass; no failed or skipped result is used to claim success.

- [ ] **Step 2: Run the Release build**

Run: `.\scripts\build.ps1 -Configuration Release -Locked`

Expected: `0 Warning(s)`, `0 Error(s)`.

- [ ] **Step 3: Review the final diff for user-owned changes**

Run: `git diff --check` and `git status --short`

Expected: no whitespace errors; pre-existing orb/wheel files remain present and are not reverted.

- [ ] **Step 4: Package only if requested**

Do not run `publish.ps1` unless the user asks for a new distributable.
