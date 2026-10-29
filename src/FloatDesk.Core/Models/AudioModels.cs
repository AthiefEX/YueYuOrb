namespace FloatDesk.Core.Models;

public sealed record AudioDevice(string Id, string Name);
public sealed record AudioSnapshot(IReadOnlyList<AudioDevice> Devices, string? DefaultDeviceId, double Volume, bool Muted);
public sealed record AudioCycleGroup(Guid Id, string Name, IReadOnlyList<string> DeviceIds, bool ShowInWheel = true);
