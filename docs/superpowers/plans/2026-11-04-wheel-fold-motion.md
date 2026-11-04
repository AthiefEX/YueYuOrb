# Wheel Fold Motion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Synchronize each wheel action and beam along a bottom-to-top fan trajectory with configurable total duration.
**Architecture:** One WPF progress animation drives all visible button/beam pairs. Temporarily host moving buttons outside the scroll clipping surface and restore the original canvas at completion or interruption. Keep steady-state geometry and scrolling unchanged.
**Tech Stack:** WPF, C#, .NET 10, xUnit.
**Spec:** docs/superpowers/specs/2026-11-04-wheel-fold-motion-design.md

## Global Constraints
- Windows 10/11 x64; SchemaVersion 1; existing dirty changes preserved.
- Duration 200–800 ms, default 400 ms; disabled system animations render final state immediately.
- No publishing or wholesale staging of accumulated work.

## Review Focus
- Left/right mirror and top/bottom anchors: trajectories use normalized signed horizontal distance.
- Close/reopen and capture interruption: restore original parents and remove animation clocks.
- Scrolling or settings changes mid-animation: finish current entrance before changing layout.
- Legacy/malformed settings: default and clamp duration; preserve concurrent General changes.
- Initial composition and final handoff: no complete-wheel flash, no clipped moving buttons, no residual invisible actions.

## Task 1 — Timing preference
Files: AppSettings.cs, SettingsViewModel.cs, SettingsWindow.xaml, WheelFoldMotionTests.cs.
- [x] Test legacy default, JSON persistence, 200/800 bounds and concurrent merge.
- [x] Add General.WheelExpandDurationMs, clamp on use/save; bind themed slider in General.
- [x] Run focused tests.

## Task 2 — Paired fan motion
Files: Views/WheelFoldMotion.cs, QuickPanelWindow.FoldMotion.cs, QuickPanelWindow.xaml/.cs, WheelFoldMotionTests.cs, QuickPanelWindowLayoutTests.cs.
- [x] Test common bottom start, mirrored arcs, horizontal upright buttons and exact final position.
- [x] Animate one progress property; calculate center/radius/angle and update each beam from anchor to moving button.
- [x] Prepare hidden state before showing; restore normal parents and geometry on completion, scrolling, apply, close and capture.
- [x] Replace obsolete assertions for rotated text with paired-position tests; run layout suite.

## Task 3 — Verification and local build
- [x] Independent scoped review, fix material findings.
- [x] Full Release tests/build, diff check, user-guide update.
- [x] Publish unique local portable folder and ZIP, without stopping running app.

Execution: inline in the existing checkout, as continuation of the approved design. Keep changes uncommitted alongside earlier authorized fixes; no unrelated release/commit requested.

## Verification record
- Follow-up speed fix: input polling previously called full panel Apply, terminating the fold. It now updates only the input-lock button through UpdateInputStatus. Layout-only ScrollChanged events no longer terminate or reposition the moving entries; actual offset changes still finish the fold. Regression InputStatusRefreshDoesNotShortenConfiguredEntrance reproduced premature completion at 800 ms (RED), then passed for both 200/800 ms with repeated status updates (GREEN).
- Follow-up verification: 952/952 full Release tests (Core161, Infrastructure119, Windows96, UI576); build 0 warnings/errors. Updated local portable under artifacts/wheel-speed-fix.
- New preference/trajectory tests: RED missing APIs → GREEN; pairing tests: RED missing layer → GREEN; viewport continuity tests: RED missing local clips → GREEN.
- Independent reviewer Socrates identified a final-frame composition mismatch between separate polygons and an EvenOdd combined geometry. Added raster regression `OverlappingBeamsDoNotChangeBrightnessOnFinalHandoff`: RED → GREEN. Steady and animated beams now use the same polygons, blur and bottom-to-top stacking order. Pixel equality verified near both bottom anchors.
- Final full Release suite: 950/950 (Core 161, Infrastructure 119, Windows 96, UI 574); build 0 warnings, 0 errors.
- Local portable: `artifacts/wheel-fold-motion/YueYuOrb-wheel-fold-motion-win-x64-portable.zip`; executable: `artifacts/wheel-fold-motion/publish/win-x64/YueYuOrb.exe`. Includes previous uncommitted authorized fixes. No GitHub publication in this task.
- Windows 11/mixed-DPI hand-operated animation smoothness remains unverified. WPF shown-window duration, interruption, scrolling, all six left/right/top/middle/bottom anchor combinations, and raster handoff have automated coverage.
