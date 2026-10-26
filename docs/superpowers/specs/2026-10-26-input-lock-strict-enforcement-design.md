# Input Lock Strict Enforcement Design

## Intent

FloatDesk currently asks the foreground window to switch to an English
keyboard layout, but it does not use the configured target KLID, does not
restore the previous layout, and cannot prevent the user or another program
from switching away immediately. The result is an unreliable "best effort"
request rather than a usable English lock.

This change implements the approved enhanced user-mode lock:

- lock eligible foreground applications to the configured English keyboard;
- intercept known Windows keyboard-layout shortcuts while the lock is active;
- learn unknown switch shortcuts, undo the first switch quickly, and block
  later occurrences during the same session;
- restore the layout that was active before locking;
- report missing layouts, denied targets, and degraded interception honestly.

The feature remains a strict best-effort lock, not an operating-system security
boundary.

## Goals

- Use `InputLockSettings.TargetKlid`, defaulting to `00000409`, instead of
  searching for an arbitrary loaded English layout.
- Accept any configured, loadable English non-IME keyboard layout, including
  regional English variants. Reject IME, Chinese, and non-English targets with
  an actionable status.
- Resolve or load the target layout and verify the exact target HKL after a
  switch request.
- Save the last non-target HKL before forcing English and restore it when the
  manual lock is stopped, a matching rule no longer applies, or FloatDesk
  exits.
- Keep the current product boundaries:
  - manual lock applies to external foreground applications;
  - automatic rules apply only while their matching application is foreground;
  - exclusion rules win over manual and automatic locking;
  - FloatDesk's own interactive windows, capture pause, emergency pause, and
    session lock remain exempt.
- Read Windows' built-in language-toggle configuration from
  `HKCU\Keyboard Layout\Toggle` whenever a lock starts.
- Always account for common Windows switch shortcuts: `Alt+Shift`,
  `Ctrl+Shift`, `Win+Space`, and `Ctrl+Space`.
- Learn an unknown modifier-based switch chord when a layout drift follows the
  chord within a short observation window. The next occurrence is suppressed.
- Poll the foreground thread's HKL quickly only while a lock is active and
  return to the existing low-frequency cadence when idle.
- Expose actionable status text for a missing target layout, an elevated target
  that rejects the request, and an installed layout whose shortcut guard could
  not start.
- Preserve Windows 10/11 x64 compatibility and require no administrator
  privilege, service, or driver.

## Non-Goals

- Blocking UAC secure-desktop input, remote input, keyboard filter drivers,
  accessibility tools, or another process that deliberately defeats the hook.
- Reading every third-party shortcut. PowerToys, AutoHotkey, IME-specific
  shortcuts, and driver behavior cannot be enumerated reliably.
- Forcing a third-party IME's internal Chinese/English mode when it does not
  change the Windows HKL.
- Guaranteeing restoration of an IME's internal composition mode. The
  restoration contract is the previous Windows HKL, not private IME state.
- Installing or repairing Windows language features through undocumented
  registry edits.
- Adding an administrator service, keyboard driver, or TSF text service.
- Changing the existing Windows "let me use a different input method for each
  app window" preference.

## Anti-Goals

- Do not swallow ordinary typing, Shift-based text selection, Ctrl shortcuts
  unrelated to layout switching, or the Windows key when no switch chord is
  active.
- Do not report `Verified` when the requested HKL was never observed.
- Do not leave a global keyboard hook installed after unlocking, pausing, or
  disposing the engine.
- Do not alter the user's installed language list or default input language as
  a side effect of starting or stopping a lock.

## Approaches Considered

### 1. Enhanced user-mode enforcement (selected)

Use the configured KLID, request the target HKL on the foreground thread,
install `WH_KEYBOARD_LL` only while locked, read Windows' built-in toggle
settings, learn unknown switch chords, verify with fast polling, and restore a
saved HKL.

Trade-off: effective for ordinary Windows 10/11 applications with low
maintenance and no privileged component, but not an absolute lock.

### 2. Request-and-poll only

Continue posting `WM_INPUTLANGCHANGEREQUEST` and poll more frequently without a
keyboard hook.

Trade-off: simpler, but the user can visibly switch layouts and type briefly
before correction. It does not meet the approved behavior.

### 3. Driver or TSF enforcement

Install a signed keyboard filter driver or provide a system text service.

Trade-off: closer to an absolute lock, but requires privileged installation,
signing, a separate lifecycle, and significantly higher compatibility and
support risk. It is disproportionate for FloatDesk and is rejected.

## Design

### Domain model

Introduce a target-layout value that represents the resolved HKL and its KLID,
and make layout observations describe the actual HKL/KLID rather than hard-code
the US English test in the Windows adapter.

The backend contract will provide four operations:

1. resolve or load a configured KLID;
2. read the foreground thread's current layout;
3. request a resolved target HKL for the foreground window;
4. request restoration of a saved HKL.

Shortcut interception will be a separate injectable contract so the core lock
engine can be unit tested without installing a real keyboard hook:

- enable with the target HKL and any user-supplied shortcuts;
- disable and release the hook;
- observe the current HKL so the guard can learn an unknown switch chord;
- expose whether interception is active.

The engine receives `TargetKlid` and optional extra shortcuts as explicit input;
it does not read settings or the registry itself.

### Lock lifecycle

When policy first changes from unlocked to `LockEnglish`:

1. resolve the configured target layout;
2. read the current foreground-thread layout;
3. remember that HKL as the restoration value if it differs from the target;
4. request the target HKL and verify it by reading the foreground thread;
5. enable shortcut interception only after the layout is verified.

While the lock remains active, foreground changes reapply the target layout to
the new eligible thread. They do not overwrite the saved restoration HKL.

If one rule-managed application hands the foreground directly to another
application that also requires locking, the lock remains continuous. FloatDesk
does not restore application A merely to lock application B in the same
evaluation. The saved pre-lock HKL is restored only when no eligible
application requires the lock.

When the decision is no longer `LockEnglish`, or the engine is disposed:

1. disable and release the keyboard hook;
2. request restoration of the saved HKL on the current eligible foreground
   thread;
3. if that target disappeared, retain a pending restore and apply it once to
   the next eligible foreground target;
4. clear the restoration state only after a successful request or target
   replacement.

The saved HKL is the last non-target layout observed before the lock began. If
the target is already active when locking starts, there is nothing visible to
restore and the save value may remain empty.

### Shortcut interception

The Windows guard runs a low-level keyboard hook on a dedicated thread with a
message loop. The hook callback only updates state and returns quickly; it does
not perform layout requests or filesystem work.

Known shortcuts are assembled from:

- the supported values in `HKCU\Keyboard Layout\Toggle`;
- the common Windows switch chords `Alt+Shift`, `Ctrl+Shift`, `Win+Space`, and
  `Ctrl+Space`;
- shortcuts explicitly supplied by settings;
- chords learned during the current lock session.

When a blocked chord is recognized, the guard suppresses the chord's key events
until every involved modifier/key has been released. This prevents Windows and
the target application from completing the switch while avoiding a permanently
stuck modifier state.

The guard records recent modifier-based chords. When the engine observes a
layout drift within a short window after one of those chords, that chord is
added to the learned set. The engine immediately reapplies the target layout,
so the first unknown switch can show a brief correction but later presses are
blocked before Windows handles them.

Learning is intentionally narrow to limit false positives:

- at least one modifier is required;
- a chord qualifies only if it contains Alt or Win, or contains at least two
  modifiers;
- plain `Ctrl+letter` and `Shift+letter` operations are never learned as switch
  shortcuts;
- a chord is learned only when an actual HKL drift follows within the
  observation window;
- learned state is session-only in the first release.

`Ctrl+Space` is in the default blocked set because it can enable an IME.
Applications that rely on it for an unrelated command can only use that command
when the input lock is inactive; the alternative is to let Chinese input reappear
while English locking is requested.

The guard logs only modifiers and virtual-key identifiers for learned chords,
never typed characters or surrounding input.

### Timing and responsiveness

The existing 250 ms polling interval becomes an idle cadence. While a lock is
active, the runtime polls at 50 ms. Foreground change events continue to trigger
an immediate evaluation. The faster cadence is stopped when the policy releases
the lock.

No background keyboard hook or fast polling runs while the feature is idle,
paused, excluded, or showing only FloatDesk's own windows.

### Status and failure behavior

The lock states remain unchanged except for a new degraded state for the case
where the target layout is verified but the keyboard guard cannot start. The
reason text must distinguish:

- target layout not installed or not loadable;
- target layout request denied by the foreground application;
- foreground application changed before verification;
- target layout verified but shortcut interception unavailable.

An unavailable target never enables the guard and never claims success.

### Settings and compatibility

The existing target-KLID text field remains the source of truth. Its help text
is updated to explain that the value is a Windows keyboard layout, that the
layout must be installable on the machine, and that locking intercepts Windows
layout shortcuts while active.

The first release does not add a shortcut-editor UI. Dynamic learning covers
third-party shortcuts for the current session. This keeps existing saved
settings valid and avoids introducing a second configuration surface before the
detection and recovery behavior has been validated on Windows 10 and 11.

FloatDesk keeps its existing own-window exemption and exclusion-rule behavior.
This deliberately keeps configuration and explicitly excluded applications
usable in Chinese.

## Testing Strategy

Core unit tests:

- the configured KLID reaches the backend;
- the current HKL is saved before the first lock;
- the saved HKL is restored on manual unlock;
- the saved HKL is restored when a matching rule application leaves the
  foreground;
- a pending restoration is applied once to the next eligible foreground target;
- a missing target layout produces `Unavailable` and never enables the guard;
- guard startup failure produces `Degraded`, not `Verified`;
- shortcut parsing maps Windows toggle registry values and common chords;
- learned chords are matched only while the guard is enabled;
- stale epochs cannot trigger a restore or verification;
- disposal releases the guard and attempts restoration.

Windows adapter tests:

- resolving `00000409` returns a US non-IME layout or a clear unavailable
  result;
- layout observation reports the exact language identifier and IME state;
- layout requests reject a target that is no longer foreground;
- the guard installs and uninstalls its hook and message loop cleanly.

Manual validation:

- cleanly installed and absent US keyboard;
- manual lock and unlock with Chinese input restored;
- Edge rule entry and exit;
- `Alt+Shift`, `Ctrl+Shift`, `Win+Space`, and `Ctrl+Space`;
- an unknown third-party shortcut on first and second use;
- elevated target and secure desktop limitations;
- Windows 10 and Windows 11 x64.

## Decisions Left to the Builder

- Whether the Windows guard uses a dedicated native message-only window for
  instant layout notifications or relies on the specified 50 ms active poll.
  The user-visible requirement is rapid correction and smooth operation; the
  implementation may choose the simpler polling path if it meets that
  requirement.

The builder has settled the remaining cross-cutting choices:

- switch locks remain continuous when the foreground moves directly between
  applications that both require locking;
- static interception is limited to explicit switch chords rather than all
  modifier combinations;
- dynamic learning requires a qualifying chord plus an observed HKL drift and
  is session-only;
- the configured target must resolve to an English non-IME layout.

## Acceptance Criteria

- Locking uses the configured target KLID and verifies that exact layout.
- Known Windows switch shortcuts do not change the layout while locked.
- An unknown switch is corrected quickly, learned, and blocked on its next use.
- Stopping a manual lock or leaving a rule-managed application restores the
  previously saved Windows layout.
- Missing layouts, denied targets, and guard failures display honest,
  actionable status.
- FloatDesk never leaves a global hook active after unlock, pause, or exit.
- All automated tests pass and the Windows x64 Release build has zero warnings
  and zero errors.
