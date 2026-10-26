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
public enum InputLockState
{
    Idle,
    Applying,
    Verified,
    Unavailable,
    Denied,
    Unsupported,
    Paused,
    Degraded
}

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

public readonly record struct KeyboardSwitchChord(
    ShortcutModifiers Modifiers,
    uint VirtualKey);
public sealed record InputLayoutTarget(nint Hkl, string Klid, uint LanguageId);
public sealed record LayoutResolution(
    InputLayoutTarget? Target,
    string? ErrorCode,
    string? Message)
{
    public bool Success => Target is not null;

    public static LayoutResolution Ok(InputLayoutTarget target) =>
        new(target, null, null);

    public static LayoutResolution Fail(string errorCode, string message) =>
        new(null, errorCode, message);
}
public sealed record AppRule(Guid Id, string ExePath, InputAction Action, bool Enabled);
public sealed record InputContext(
    bool SessionLocked,
    bool EmergencyPaused,
    bool CapturePaused,
    bool ManualEnabled,
    bool AutoEnabled);
public sealed record InputDecision(
    InputAction Action,
    string Reason,
    bool PreserveLockState = false);
public sealed record InputLockOptions(
    string TargetKlid,
    IReadOnlyList<KeyboardSwitchChord>? AdditionalShortcuts = null);
public sealed record LayoutObservation(
    nint Hkl,
    string Klid,
    uint LanguageId,
    bool IsEnglish,
    bool IsIme);
public sealed record InputStatus(InputLockState State, long Epoch, string Reason);

public interface IInputBackend
{
    Task<LayoutResolution> ResolveAsync(
        string klid,
        CancellationToken cancellationToken);
    Task<LayoutObservation?> ReadAsync(ForegroundSnapshot target, CancellationToken cancellationToken);
    Task<OperationResult> ActivateAsync(
        ForegroundSnapshot target,
        InputLayoutTarget layout,
        CancellationToken cancellationToken);
    Task<OperationResult> RestoreAsync(
        ForegroundSnapshot target,
        nint hkl,
        CancellationToken cancellationToken);
}

public interface IKeyboardSwitchGuard : IDisposable
{
    bool IsActive { get; }
    OperationResult Enable(
        InputLayoutTarget target,
        IReadOnlyList<KeyboardSwitchChord>? additionalShortcuts = null);
    void Disable();
    void ObserveLayout(LayoutObservation observation);
    void ClearPendingLearning();
}
