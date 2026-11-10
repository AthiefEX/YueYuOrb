namespace FloatDesk.Core.Models;

public enum HyperVInstallSource { Iso, Template }
public enum HyperVGpuMode { VirtualDisplay, Auto, GpuPartition, Dedicated }

public sealed record HyperVAutomationSettings
{
    public HyperVInstallSource Source { get; init; }
    public string StorageDirectory { get; init; } = string.Empty;
    public int CpuCount { get; init; } = 4;
    public long MemoryStartupMiB { get; init; } = 4096;
    public long MemoryMaximumMiB { get; init; } = 8192;
    public long DiskSizeGiB { get; init; } = 80;
    public string SwitchName { get; init; } = "Default Switch";
    public bool SecureBoot { get; init; } = true;
    public HyperVGpuMode GpuMode { get; init; } = HyperVGpuMode.VirtualDisplay;
    public bool UnattendedInstall { get; init; }
    public bool DeployAgent { get; init; } = true;
    public bool OpenViewWhenReady { get; init; } = true;

    public string? Validate()
    {
        if (!Enum.IsDefined(Source) || !Enum.IsDefined(GpuMode))
            return "请选择有效的安装来源和显卡模式。";
        if (string.IsNullOrWhiteSpace(StorageDirectory) || !Path.IsPathFullyQualified(StorageDirectory))
            return "请选择虚拟机的完整安装目录。";
        if (CpuCount is < 2 or > 128)
            return "CPU 数量必须为 2–128。";
        if (MemoryStartupMiB < 2048 || MemoryMaximumMiB < MemoryStartupMiB)
            return "启动内存至少为 2048 MiB，最大内存不能小于启动内存。";
        if (DiskSizeGiB is < 32 or > 65536)
            return "虚拟磁盘容量必须为 32–65536 GiB。";
        if (string.IsNullOrWhiteSpace(SwitchName))
            return "请选择网络交换机。";
        return null;
    }
}
