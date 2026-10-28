# Crystal Orb Display Design

Approved direction: scheme 1, layered WPF glass sphere. The user explicitly requested implementation after confirming proportional resizing.

## Behavior

- Add Classic / Crystal display modes; Classic remains the default for existing JSON and preserves current appearance.
- Orb size remains the outer diameter, from 32 to 128 DIP. Crystal image diameter is 0.93 times the sphere diameter, leaving a narrow glass rim on each side.
- All glass geometry, highlights, image inset, and shading share a 100-unit design surface uniformly scaled to the selected diameter. The center stays fixed.
- Use a transparent glass body, asymmetric upper-left highlights, weak lower-right reflection, and narrow varying rim reflections. Keep the image center readable. Effects are static and clipped to the sphere.
- No external glow, particles, base, ground shadow, or continuous animation. Suppress the wheel anchor glow in Crystal mode, including during entrance/exit and reapplying settings.
- Retain the current clock text and shadow without a background plate; place it above glass reflections.
- Crystal always uses circular clipping and circular hit testing even for a transparent PNG. Persist the user's Classic Circle/Contour preference unchanged and restore it when switching back.
- Image replacement and mode switching apply without restart. Without an image, the default green face appears inside the glass shell.
- Keep drag, edge snap, screen bounds, and wheel anchor based on the existing outer-diameter root; never grow the outer window for Crystal.

## Approach

Chosen: native layered WPF vector gradients and paths, no shaders or new dependencies. Real-time shader refraction was considered but adds hardware validation and distorts small images. This implementation provides visual depth, not physical refraction of the desktop.

## Configuration and Files

Add OrbDisplayMode { Classic, Crystal } and an init-only OrbSettings.DisplayMode defaulting to Classic; keep SchemaVersion 1 and the existing positional constructor. Validate unknown display modes. Merge only edited display mode in SettingsViewModel.

Add a mode selector to the existing orb appearance card; disable the image-shape selector in Crystal with a short circular-shell hint. Add CrystalOrbSurface as a static vector UserControl; OrbWindow sizes the image and shell and keeps the clock on top. QuickPanelWindow gates only AnchorGlowSurface visibility.

## Verification

Test JSON migration/round-trip/invalid enum; concurrent settings merge; size and geometry at 32/56/80/128 DIP; circular vs contour hit testing; returning to Classic restores image margin and clip; image replacement; missing image; opaque clock plate absence; wheel glow remains suppressed through animated and instant open/close. Render native WPF previews at several diameters and pixel scales, checking sphere-only alpha bounds. Run the full Release suite and zero-warning build in isolated artifacts output.

Windows 10 22H2 x64 and Windows 11 x64 remain targets. Actual Windows 11 and physical mixed-DPI testing must be distinguished from automated rendered-DPI coverage.
