# Resource Usage Implementation Plan

> **For agentic workers:** Use executing-plans and dispatching-parallel-agents for disjoint units; independent final review.

**Goal:** Optional liquid and ring usage visualization on the existing orb.
**Architecture:** Native background sampler produces ResourceUsageSnapshot; immutable ResourceUsageSettings persist preferences; one WPF drawing surface renders normalized geometry and interpolated samples.
**Tech Stack:** C#, WPF, Windows APIs, xUnit.
**Spec:** docs/superpowers/specs/2026-11-04-resource-usage-design.md

## Constraints and review focus
Windows10/11 x64; no extra apps/drivers; disabled by default; preserve dirty work and existing gestures. Review missing/stale GPU data, multi-adapter selection, cancellation/restart, contour/crystal clipping, ring boundaries and tooltip suppression.

1. Native sampler: CPU delta, physical memory, per-adapter busiest GPU engine; disposable native resources; tests for aggregation and unavailable counters.
2. Settings: independent toggles, metric selection, ring checkboxes, colors, GPU selection and tooltip delay; three-way merge and legacy defaults tests.
3. Surface: area-correct liquid, proportional segmented arcs and red threshold; normalized geometry tests; integrate lifecycle/tooltip/edge bounds in OrbWindow.
4. Full Release tests/build and scoped independent review; unique portable artifact, update user guide. No stopping the running app.

## Completion record

- All four units implemented in the current checkout; existing dirty changes preserved. 14 native tests include actual local RTX 4070 SUPER sampling; 21 settings tests cover defaults/three-way merge/validation and the real GPU combo.
- Geometry tests proved circular-area fill and segmented arcs; raster tests cover 32/56/128 DIP, red threshold and absent readings. Actual orb tests preserve clock/crystal and ring hit areas.
- Independent review identified ring layout clipping, contour transparency mismatch and continued sampling/rendering when edge-collapsed. All three reproduced by new failing integration tests, then fixed and passed: Canvas gives the ring its complete layout slot; liquid uses image opacity mask; edge collapse suspends native sampling and rendering, restore resumes.
- Full suite initially exposed an existing subtitle-test scheduling race (second caption superseded while its OCR awaited the first display). Only the test was synchronized using the existing controlled clock and stable frame pairs; subtitle production code unchanged. Regression passed 10 focused repetitions.
- Final full Release suite: 1006/1006 (Core161, Infrastructure119, Windows110, UI616). Native and UI resources are released on close; hidden-window queued UI sampling callbacks are cancellable.
- Visual check: actual classic/crystal orb captures and ring rasters under artifacts/resource-usage-preview. Windows11/mixed DPI and GPU hot-plug remain unverified manually. GetSystemTimes on machines above 64 logical processors reports the calling thread's processor group rather than all groups; that workstation configuration remains a limitation.
- No git commit/push/publication requested. Package output under artifacts/resource-usage.
