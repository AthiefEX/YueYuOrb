using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FloatDesk.Core.Models;

namespace FloatDesk.App.ViewModels;

public partial class SettingsViewModel
{
    [ObservableProperty] private bool _resourceLiquidEnabled;
    [ObservableProperty] private bool _resourceRingsEnabled;
    [ObservableProperty] private double _resourceRingThicknessPercent = 4.3;
    [ObservableProperty] private ResourceMetric _resourceLiquidMetric = ResourceMetric.Memory;
    [ObservableProperty] private bool _resourceRingMemory = true;
    [ObservableProperty] private bool _resourceRingCpu = true;
    [ObservableProperty] private bool _resourceRingGpu = true;
    [ObservableProperty] private string _resourceLiquidColor = "#59C4A6";
    [ObservableProperty] private string _resourceMemoryColor = "#59C4A6";
    [ObservableProperty] private string _resourceCpuColor = "#60AAFF";
    [ObservableProperty] private string _resourceGpuColor = "#C69BFF";
    [ObservableProperty] private bool _resourceShowDetails = true;
    [ObservableProperty] private double _resourceHoverDelaySeconds = .8;
    [ObservableProperty] private string? _resourceGpuDeviceId;

    public IReadOnlyList<ResourceMetricOption> ResourceMetrics { get; } =
    [new(ResourceMetric.Memory, "内存"), new(ResourceMetric.Cpu, "CPU"), new(ResourceMetric.Gpu, "显卡")];

    public ObservableCollection<ResourceGpuDeviceOption> ResourceGpuDevices { get; } =
        [new(null, "自动（最忙显卡）")];

    private void LoadResourceUsage(ResourceUsageSettings settings)
    {
        ResourceLiquidEnabled = settings.LiquidEnabled;
        ResourceRingsEnabled = settings.RingsEnabled;
        ResourceRingThicknessPercent = settings.RingThicknessPercent;
        ResourceLiquidMetric = settings.LiquidMetric;
        ResourceRingMemory = settings.RingMemory;
        ResourceRingCpu = settings.RingCpu;
        ResourceRingGpu = settings.RingGpu;
        ResourceLiquidColor = settings.LiquidColor;
        ResourceMemoryColor = settings.MemoryColor;
        ResourceCpuColor = settings.CpuColor;
        ResourceGpuColor = settings.GpuColor;
        ResourceShowDetails = settings.ShowDetails;
        ResourceHoverDelaySeconds = settings.HoverDelaySeconds;
        // Add the saved option before bindings can select it, even without a sample.
        if (settings.GpuDeviceId is { } id)
            ResourceGpuDevices.Add(new(id, $"不可用（{id}）"));
        ResourceGpuDeviceId = settings.GpuDeviceId;
    }

    /// <summary>Updates GPU choices on the UI thread using devices supplied by the runtime.</summary>
    public void SetResourceGpuDevices(IReadOnlyList<GpuUsage> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var detected = devices.DistinctBy(d => d.Id).ToDictionary(d => d.Id, d => d.Name);
        foreach (var device in detected)
        {
            var option = ResourceGpuDevices.FirstOrDefault(o => o.Id == device.Key);
            if (option is null) ResourceGpuDevices.Add(new(device.Key, device.Value));
            else option.Name = device.Value;
        }
        // Keep the selected item instance: removing it would clear WPF SelectedValue.
        foreach (var option in ResourceGpuDevices.ToArray())
        {
            if (option.Id is null || detected.ContainsKey(option.Id)) continue;
            if (option.Id == ResourceGpuDeviceId) option.Name = $"不可用（{option.Id}）";
            else ResourceGpuDevices.Remove(option);
        }
        if (ResourceGpuDeviceId is { } selected && !ResourceGpuDevices.Any(o => o.Id == selected))
            ResourceGpuDevices.Add(new(selected, $"不可用（{selected}）"));
    }

    private ResourceUsageSettings BuildResourceUsage(ResourceUsageSettings latest)
    {
        var baseline = _baseline.ResourceUsage;
        var result = latest with
        {
            LiquidEnabled = ResourceLiquidEnabled != baseline.LiquidEnabled ? ResourceLiquidEnabled : latest.LiquidEnabled,
            RingsEnabled = ResourceRingsEnabled != baseline.RingsEnabled ? ResourceRingsEnabled : latest.RingsEnabled,
            RingThicknessPercent = ResourceRingThicknessPercent != baseline.RingThicknessPercent ? ResourceRingThicknessPercent : latest.RingThicknessPercent,
            LiquidMetric = ResourceLiquidMetric != baseline.LiquidMetric ? ResourceLiquidMetric : latest.LiquidMetric,
            RingMemory = ResourceRingMemory != baseline.RingMemory ? ResourceRingMemory : latest.RingMemory,
            RingCpu = ResourceRingCpu != baseline.RingCpu ? ResourceRingCpu : latest.RingCpu,
            RingGpu = ResourceRingGpu != baseline.RingGpu ? ResourceRingGpu : latest.RingGpu,
            LiquidColor = ResourceLiquidColor != baseline.LiquidColor ? ResourceLiquidColor : latest.LiquidColor,
            MemoryColor = ResourceMemoryColor != baseline.MemoryColor ? ResourceMemoryColor : latest.MemoryColor,
            CpuColor = ResourceCpuColor != baseline.CpuColor ? ResourceCpuColor : latest.CpuColor,
            GpuColor = ResourceGpuColor != baseline.GpuColor ? ResourceGpuColor : latest.GpuColor,
            ShowDetails = ResourceShowDetails != baseline.ShowDetails ? ResourceShowDetails : latest.ShowDetails,
            HoverDelaySeconds = !ResourceHoverDelaySeconds.Equals(baseline.HoverDelaySeconds)
                ? ResourceHoverDelaySeconds : latest.HoverDelaySeconds,
            GpuDeviceId = ResourceGpuDeviceId != baseline.GpuDeviceId ? ResourceGpuDeviceId : latest.GpuDeviceId
        };
        return result.RingsEnabled && !result.RingMemory && !result.RingCpu && !result.RingGpu
            ? result with { RingMemory = true } : result;
    }

    private static bool ValidateResourceUsage(ResourceUsageSettings settings, out string error)
    {
        if (!double.IsFinite(settings.RingThicknessPercent) || settings.RingThicknessPercent is < 2 or > 8)
        {
            error = "环形指示条粗细必须为球体直径的 2%–8%。";
            return false;
        }
        foreach (var (label, color) in new[]
        {
            ("液体", settings.LiquidColor), ("内存环形", settings.MemoryColor),
            ("CPU 环形", settings.CpuColor), ("显卡环形", settings.GpuColor)
        })
        {
            if (!IsAppearanceColor(color))
            {
                error = $"{label}颜色无效，请使用 #RRGGBB 或 #AARRGGBB（如 #59C4A6 或 #8059C4A6）。";
                return false;
            }
        }
        if (!double.IsFinite(settings.HoverDelaySeconds) || settings.HoverDelaySeconds is < .2 or > 5)
        {
            error = "资源占用悬停延迟必须为 0.2–5 秒。";
            return false;
        }
        if (!Enum.IsDefined(settings.LiquidMetric))
        {
            error = "请选择有效的液体指标：内存、CPU 或显卡。";
            return false;
        }
        error = "";
        return true;
    }

    private static bool IsAppearanceColor(string color) => color is { Length: 7 or 9 } && color[0] == '#' &&
        color.AsSpan(1).ToArray().All(char.IsAsciiHexDigit);
}

public sealed record ResourceMetricOption(ResourceMetric Id, string Name);

public partial class ResourceGpuDeviceOption(string? id, string name) : ObservableObject
{
    public string? Id { get; } = id;
    [ObservableProperty] private string _name = name;
}
