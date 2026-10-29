# Orb edge collapse implementation plan

Approved design: optional left/right edge strip, default off, 800ms outside delay, 180ms animation, 8 DIP strip with orb-height scaling. Hover restores; dragging/context menu/wheel prevent collapse; top/bottom alone do not collapse. Windows 10/11 x64.

Implementation: preserve the full native window bounds and persisted placement, draw the strip at the actual work-area edge, and restrict native hit testing to the strip when collapsed. A bounded dispatcher timer observes pointer and wheel state; a pure state machine owns the delay. Existing orb image/crystal visuals are restored without reloading assets.

- [x] Add settings model/viewmodel/UI and backward-compatible default.
- [x] Test and implement delay/hover/block state machine.
- [x] Integrate strip drawing, hit testing, animation, and lifecycle cleanup.
- [x] Run tests, Release build, independent review; publish separate local build.

Verification: 786 tests pass (148 Core, 97 Infrastructure, 66 Windows, 475 UI), Release 0 warnings/errors. Independent read-only review clean. Native WPF harness passed Classic/Crystal × left/right: actual timed collapse, routed MouseEnter restoration, wheel restoration, switch-off restoration and unchanged HWND bounds. Physical pointer delivery, Windows 11 and mixed-DPI monitor transitions remain manual validation.
