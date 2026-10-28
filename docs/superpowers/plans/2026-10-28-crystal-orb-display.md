# Crystal Orb Display Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans task by task, then independent whole-feature review.

**Goal:** Add a tightly fitted, proportionally scaled crystal sphere mode without changing Classic appearance.

**Architecture:** An additive OrbSettings display enum selects an in-window vector glass surface. Image and glass are clipped to concentric circles; the outer root and existing screen geometry remain unchanged. The wheel's anchor glow is suppressed only in Crystal mode.

**Tech Stack:** .NET 10, WPF, xUnit; no new dependencies.

**Spec:** docs/superpowers/specs/2026-10-28-crystal-orb-display-design.md

## Global Constraints

- Retain Windows 10 22H2 x64 / Windows 11 x64 compatibility, SchemaVersion 1, Classic default, and the existing OrbSettings positional constructor.
- Preserve Circle/Contour preference, instant image replacement, text-only clock, drag/snap and outer bounds.
- At every 32-128 DIP size, use image diameter 0.93D and a uniformly scaled 100-unit glass design; no exterior effects or continuous animation.
- Reuse D:\FloatDesk on feat/settings-visual-redesign; isolated build output avoids running-program DLL locks. Do not push or publish without a new request.

## Review Focus

- Legacy JSON has no DisplayMode: defaults to Classic; task 1 migration test.
- Concurrent settings updates: unedited mode preserves current value; task 1 merge tests.
- Transparent PNG with Contour preference in Crystal: complete sphere remains clickable; task 2 hit-test tests.
- Repeated mode/size/image changes: all image insets, circle clip and fallback state reset; task 2 transition tests.
- Instant and animated wheel paths reset opacity: use Visibility for mode gating and test task 3.

## Task 1: Compatible settings

Files: Core/Models/AppSettings.cs; Infrastructure/Settings/SettingsValidator.cs; App/ViewModels/SettingsViewModel.cs; App/Views/SettingsWindow.xaml; settings tests in Infrastructure.Tests and Ui.Tests.

Interfaces: OrbDisplayMode { Classic = 0, Crystal = 1 }; OrbSettings.DisplayMode init default Classic; SettingsViewModel.OrbDisplayModeIndex; read-only IsClassicOrbMode.

- [x] Add tests for legacy default, Crystal round-trip retaining Contour, invalid mode rejection, and edited/unedited concurrent mode merge.
- [x] Run focused tests and observe the missing mode fail.
- [x] Implement enum, additive property, validation, ViewModel merge and mode selector; disable the shape selector only in Crystal.
- [x] Run settings tests and commit exact task files.

## Task 2: Crystal surface and orb integration

Files: new App/Views/CrystalOrbSurface.xaml and .xaml.cs; App/Views/OrbWindow.xaml and .xaml.cs; new Ui.Tests/CrystalOrbWindowTests.cs.

Interfaces: CrystalOrbSurface : UserControl; OrbWindow uses effective circular shape in Crystal; image inset = diameter * 0.035; glass root size remains diameter. Extract internal IsTransparentOrbPoint(Point) from current OS cursor hit test for deterministic input testing.

- [x] Test scale at 32/56/80/128, circle alpha bounds, Classic transition, clock layering, fallback and transparent-image hit testing.
- [x] Run tests and observe failure before rendering code.
- [x] Implement the uniformly scaled native vector surface and integration, preserving Classic defaults.
- [x] Run orb tests and render previews for visual inspection; refine glass appearance without changing the agreed proportions.
- [x] Commit task-owned files.

## Task 3: Wheel integration and final validation

Files: App/Views/QuickPanelWindow.xaml.cs; Ui.Tests/QuickPanelWindowLayoutTests.cs or new CrystalWheelTests.cs; docs/user-guide.md.

- [x] Test Crystal hides AnchorGlowSurface, Classic restores it, and both instant/animated open/close respect the setting.
- [x] Run and observe failure, then gate glow Visibility in Apply.
- [x] Run focused wheel tests and the full Release suite with isolated artifacts output. Build must report 0 warnings / 0 errors.
- [x] Update user guide, obtain independent final review, handle material findings, commit and report the new local executable. Do not publish a new release.
