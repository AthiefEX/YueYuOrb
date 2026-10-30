namespace FloatDesk.Core.Models;

public sealed record BrightnessSettings(int TargetPercent = 50, int StepPercent = 5,
    int NightDimmingPercent = 40, bool ShowBrightnessInWheel = true, bool ShowNightModeInWheel = true);
public sealed record BrightnessDevice(string Id, string Name, int? Percent, string? UnavailableReason, bool IsPrimary);
public sealed record BrightnessSnapshot(IReadOnlyList<BrightnessDevice> Devices);
public sealed record BrightnessWriteResult(string DeviceId, int? ActualPercent, string? Error);
