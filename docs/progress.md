# FloatDesk Progress Ledger

Plan: `01-产品与技术规格.md`, `02-Agent开发计划.md`,
`03-验收与兼容性矩阵.md`.

- T01 baseline scaffold: complete.
- T03-T13 product implementation: complete for the requested feature set.
- Automated tests: 51/51 passing in Debug on 2026-10-25
  (Core 26, Infrastructure 12, Windows 6, UI 7).
- Windows 10 22H2 startup and single-instance smoke: PASS.
- Final self-contained Release startup and single-instance smoke: PASS.
- Initial hotkey conflict fallback: PASS on current host.
- Windows 11: NOT TESTED; no test host available.
- Windows 10 21H2 and standard-user clean VM: NOT TESTED.
- Real IME, DeepSeek login, mixed-DPI capture, game/fullscreen and long-run:
  NOT TESTED; see `docs/manual-tests.md`.
- Release build/test, self-contained publish, ZIP and SHA256: complete after
  the final self-review fixes.
- Inno Setup package: NOT GENERATED because `iscc` is not installed; the
  complete script remains in `installer/FloatDesk.iss`.
- Final self-review: complete by the implementing agent. This is weaker than
  an independent fresh-context review; no subagent was used because current
  session instructions prohibit delegation without an explicit request.

Ruling: the plan names `FloatDesk.sln`; the .NET 10 template initially
created `.slnx`, so an explicit classic `.sln` was generated to match the
execution commands. Cost if wrong: newer solution-only tooling expects
`.slnx`, while all Visual Studio and `dotnet` commands in this plan remain
valid.

Ruling: `UseWindowsForms` reports WFO0003 because the app manifest also
declares PerMonitorV2. The manifest remains authoritative because WPF is the
UI framework; the WinForms rule is suppressed for this mixed-framework app.
Cost if wrong: WinForms controls could use a different DPI initialization
path, but no WinForms windows are used.

Ruling: Settings edited in one window are merged by comparing the opening
snapshot with each observable field. Unedited groups use the coordinator's
latest snapshot. This adds a small amount of comparison code instead of a
large dirty-field framework. Cost if wrong: a newly introduced future field
must be added to the merge function or it will retain the latest value, which
is the safe default.

Ruling: A newer settings schema loads defaults and marks the store read-only,
so local features can start while preserving the unknown file. Cost if wrong:
the user sees default preferences until launching the newer app version, but
their future-format file is not overwritten.

Ruling: Initial global hotkey conflicts are logged and do not display modal
dialogs during startup. Applying settings still reports conflicts. Cost if
wrong: a user may not notice immediately that a hotkey is unavailable, but the
tray menu, orb menu and settings remain usable.

Ruling: A stale foreground update is rejected before it can change the current
input status, and status publication is guarded by the same epoch lock. Cost if
wrong: a redundant update may be ignored, but a background target cannot replace
the current target's verified or denied state.

Ruling: Note deletion and recycle-bin restore attempt to save current edits
first and abort when that save fails. Cost if wrong: a user may need to retry a
save before deleting, but unsaved body text is not silently discarded.

Ruling: Closing a capture overlay requests cancellation; capture-owned closes
use a separate non-cancelling path. Cost if wrong: Alt+F4 and window teardown
have an extra cleanup branch, but the capture session cannot remain stuck.

Ruling: Capture editor export exceptions are reported in-window instead of
escaping an `async void` handler. Cost if wrong: an unexpected export failure
leaves the editor open with its content intact, but the background application
does not terminate.
