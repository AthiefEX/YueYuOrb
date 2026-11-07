namespace FloatDesk.Core.Models.DesktopAutomation;

public sealed record HostResourceCapacity(int LogicalProcessors, long MemoryMiB, long FreeDiskGiB);

public sealed record VmResourceProfile(
    Guid EnvironmentId, int CpuCount, long MemoryStartupMiB, long MemoryMinimumMiB,
    long MemoryMaximumMiB, long DiskSizeGiB, string StoragePath, string NetworkSwitch,
    bool DynamicMemory = true, string GpuMode = "standard")
{
    public int Revision { get; init; } = 1;
}

public sealed record ResourceValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
    public static ResourceValidationResult Valid { get; } = new(true, []);
}

public static class VmResourceProfileValidator
{
    public static ResourceValidationResult Validate(VmResourceProfile profile, HostResourceCapacity host)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(host);
        var errors = new List<string>();
        if (profile.EnvironmentId == Guid.Empty) errors.Add("environmentId is required");
        if (profile.CpuCount < 2 || profile.CpuCount > host.LogicalProcessors) errors.Add("cpu count exceeds host capacity");
        if (profile.MemoryMinimumMiB < 2048 || profile.MemoryMinimumMiB > profile.MemoryStartupMiB || profile.MemoryStartupMiB > profile.MemoryMaximumMiB) errors.Add("memory range is invalid");
        if (profile.MemoryMaximumMiB > host.MemoryMiB - 2048) errors.Add("memory exceeds host reservation");
        if (profile.DiskSizeGiB < 32 || profile.DiskSizeGiB > host.FreeDiskGiB) errors.Add("disk size exceeds available capacity");
        if (string.IsNullOrWhiteSpace(profile.StoragePath) || !Path.IsPathRooted(profile.StoragePath)) errors.Add("storage path must be rooted");
        if (string.IsNullOrWhiteSpace(profile.NetworkSwitch)) errors.Add("network switch is required");
        if (profile.Revision < 1) errors.Add("revision is invalid");
        if (profile.GpuMode is not ("standard" or "experimental")) errors.Add("gpu mode is unsupported");
        return errors.Count == 0 ? ResourceValidationResult.Valid : new(false, errors);
    }
}
