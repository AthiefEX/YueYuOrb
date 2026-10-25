namespace FloatDesk.Core.Models;

public sealed record ForegroundSnapshot(
    long Epoch,
    nint TopLevelHwnd,
    nint FocusHwnd,
    uint ProcessId,
    uint ThreadId,
    DateTimeOffset? ProcessStartUtc,
    string? ExePath,
    bool IsOwnInteractive);

public enum InputAction { None, LockEnglish, Exclude }
public enum InputLockState { Idle, Applying, Verified, Unavailable, Denied, Unsupported, Paused }

[Flags]
public enum ShortcutModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
    NoRepeat = 0x4000
}

public sealed record AppRule(Guid Id, string ExePath, InputAction Action, bool Enabled);
public sealed record InputContext(
    bool SessionLocked,
    bool EmergencyPaused,
    bool CapturePaused,
    bool ManualEnabled,
    bool AutoEnabled);
public sealed record InputDecision(InputAction Action, string Reason);
public sealed record LayoutObservation(nint Hkl, bool IsTargetUsEnglish, bool IsIme);
public sealed record InputStatus(InputLockState State, long Epoch, string Reason);

public interface IInputBackend
{
    Task<LayoutObservation?> ReadAsync(ForegroundSnapshot target, CancellationToken cancellationToken);
    Task<OperationResult> RequestUsEnglishAsync(ForegroundSnapshot target, CancellationToken cancellationToken);
}
