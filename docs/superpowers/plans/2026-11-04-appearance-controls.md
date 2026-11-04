# Appearance Controls Follow-up

User requests a ring-width slider, visual color panels with direct hex entry for resource colors, and matching clock font color. Implement inline with a disjoint color-control worker and independent review.

- Default ring thickness remains 4.3% of orb diameter; slider 2–8%, scales with size. Expand layout/hit envelope and segment gaps consistently so thick round caps do not close gaps or clip.
- Reusable ColorPickerField.Value two-way string supports graphical HSV/alpha panel and #RRGGBB/#AARRGGBB entry. Existing theme resources, no external package.
- ClockColor defaults white, merges independently, validated before applying. Resource colors use the same control. Apply/save behavior remains existing settings workflow.
- Tests: merge/defaults/validation, ring envelope and gaps at minimum/default/maximum, color field conversion and binding, actual orb clock brush and wide-ring layout. Full Release suite, build, scoped review, local portable.

## Verification

- Full Release suite, serialized to avoid WPF timing races: 1035/1035 passed.
  - Core: 161
  - Infrastructure: 119
  - Windows: 110
  - UI: 645
- Release build: 0 warnings, 0 errors.
- Package smoke test: startup completed, primary instance stayed alive, second instance exited with code 0.
- Portable executable: `D:\FloatDesk\artifacts\appearance-controls\publish\win-x64\YueYuOrb.exe`
- Portable ZIP: `D:\FloatDesk\artifacts\appearance-controls\YueYuOrb-appearance-controls-win-x64-portable.zip`
- ZIP SHA-256: `911c788ee32d99ab94f15972e1af6de947474f4fb4df4cd212cf2632ceb1cc81`

Manual verification still pending for Windows 11, mixed-DPI layouts, GPU hot-plug, and popup placement/theme behavior on user hardware. CPU sampling is limited to 64 logical processors in one processor group.
