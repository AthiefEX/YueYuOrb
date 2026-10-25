# Compatibility Report

## Current host

- Windows version: Windows 10 22H2, build 19045, x64.
- .NET SDK: 10.0.400.
- Start date: 2026-10-25.

## Capability probes

| Probe | Windows 10 | Windows 11 | Result |
|---|---|---|---|
| Self-contained launch | PASS on 19045 | NOT TESTED | PASS on current host |
| Framework-dependent Debug launch | PASS on 19045 | NOT TESTED | PASS on current host |
| Single-instance handoff | PASS on 19045 | NOT TESTED | PASS |
| Initial hotkey conflict does not block startup | PASS on 19045 | NOT TESTED | PASS |
| Transparent non-activating orb | Visual interaction not yet recorded | NOT TESTED | NOT TESTED |
| WebView2 independent window | Runtime/interactive flow not yet recorded | NOT TESTED | NOT TESTED |
| Keyboard-layout readback | Real target applications not yet recorded | NOT TESTED | NOT TESTED |
| Mixed-DPI screen capture | Unit parity PASS; real mixed-DPI host unavailable | NOT TESTED | PARTIAL |

No compatibility claim is made until the respective probe records actual
evidence. The absence of a Windows 11 test host is recorded as NOT TESTED,
not passed by inference.

## Windows 10 22H2 smoke evidence

- Host: Windows 10 Pro 22H2, build 19045, x64.
- Application reached `startup.complete`.
- Primary process remained alive after startup.
- A second invocation exited with code 0 within five seconds and opened the
  primary instance settings window.
- The final self-contained Release package reached `startup.complete`, the
  primary process remained alive, and the second instance exited with code 0.
- `Ctrl+Alt+A` was occupied by another application. FloatDesk logged
  `hotkey.initial.failed` for region capture and continued startup; tray
  fallback remains available.
