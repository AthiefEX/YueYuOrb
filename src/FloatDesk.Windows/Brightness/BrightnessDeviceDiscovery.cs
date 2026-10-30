using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using FloatDesk.Core.Models;
using static FloatDesk.Windows.Brightness.BrightnessInterop;

namespace FloatDesk.Windows.Brightness;

internal sealed class BrightnessDeviceDiscovery : IBrightnessBackend
{
    private sealed record Endpoint(string Id, string Name, bool Primary, string? WmiInstance, nint Monitor, long Generation);
    private readonly object _gate = new();
    private Dictionary<string, Endpoint> _endpoints = [];
    private readonly Dictionary<string, string?> _owners = new(StringComparer.OrdinalIgnoreCase);
    private readonly BrightnessReadCoordinator _reads = new();
    private readonly ConcurrentDictionary<string, object> _deviceLocks = new();
    private Task<List<WmiBrightnessBackend.Screen>>? _wmiRead;
    private List<WmiBrightnessBackend.Screen> _lastWmi = [];
    private long _generation;

    internal void Invalidate() { lock (_gate) { _generation++; _endpoints.Clear(); } }

    public IReadOnlyList<BrightnessDevice> Enumerate()
    {
        long generation;
        lock (_gate) generation = _generation;
        if (_wmiRead?.IsCompletedSuccessfully == true) _lastWmi = _wmiRead.Result;
        if (_wmiRead is null || _wmiRead.IsCompleted) _wmiRead = Task.Run(WmiBrightnessBackend.Enumerate);
        var wmiFresh = false;
        try { _lastWmi = _wmiRead.WaitAsync(TimeSpan.FromMilliseconds(500)).GetAwaiter().GetResult(); wmiFresh = true; }
        catch { /* Preserve known WMI ownership on transient discovery failures. */ }
        var endpoints = new Dictionary<string, Endpoint>(StringComparer.OrdinalIgnoreCase);
        var screens = new List<BrightnessDevice>();
        var reads = new List<Task<BrightnessDevice>>();
        EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info)) return true;
            var devices = new List<DisplayDevice>();
            for (uint i = 0; i < 32; i++)
            {
                var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
                if (!EnumDisplayDevices(info.Device, i, ref device, 1)) break;
                if (!string.IsNullOrWhiteSpace(device.Id)) devices.Add(device);
            }
            if (devices.Count == 0)
            { screens.Add(new(info.Device, info.Device, null, "无法可靠识别显示器。", (info.Flags & 1) != 0)); return true; }
            foreach (var device in devices)
            {
                var id = BrightnessEndpointIdentity.FromInterface(device.Id);
                if (id is null) { screens.Add(new(device.Id, device.Description, null, "无法可靠识别显示器。", false)); continue; }
                var wmi = _lastWmi.FirstOrDefault(s => s.Instance.StartsWith(id + "_", StringComparison.OrdinalIgnoreCase));
                string? owner;
                lock (_gate)
                {
                    if (!_owners.TryGetValue(id, out owner) || (owner is null && wmi is not null))
                        _owners[id] = owner = wmi?.Instance;
                }
                var endpoint = new Endpoint(id, device.Description, (info.Flags & 1) != 0, owner, monitor, generation);
                if (!endpoints.TryAdd(id, endpoint)) continue;
                if (owner is not null)
                {
                    var current = wmiFresh ? _lastWmi.FirstOrDefault(s => s.Instance.Equals(owner, StringComparison.OrdinalIgnoreCase)) : null;
                    screens.Add(new(id, endpoint.Name, current?.Percent, current is null ? "暂时无法读取系统亮度。" : null, endpoint.Primary));
                }
                else if (devices.Count > 1)
                    screens.Add(new(id, endpoint.Name, null, "复制显示模式下无法可靠关联硬件目标，请改用扩展模式；黑夜模式仍可使用。", endpoint.Primary));
                else
                {
                    reads.Add(_reads.ReadAsync(id, generation, () => ReadDdc(endpoint), TimeSpan.FromMilliseconds(700)));
                }
            }
            return true;
        }, 0);
        screens.AddRange(Task.WhenAll(reads).GetAwaiter().GetResult());
        lock (_gate)
        {
            if (generation != _generation) throw new InvalidOperationException("显示器配置已变化。");
            _endpoints = endpoints;
        }
        return screens.DistinctBy(d => d.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private BrightnessDevice ReadDdc(Endpoint endpoint)
    {
        lock (_deviceLocks.GetOrAdd(endpoint.Id, _ => new()))
        {
            var result = AccessDdc(endpoint, null);
            return new(endpoint.Id, endpoint.Name, result.ActualPercent, result.Error, endpoint.Primary);
        }
    }
    public BrightnessWriteResult SetPercent(string id, int percent)
    {
        Endpoint? endpoint;
        lock (_gate) endpoint = _endpoints.GetValueOrDefault(id);
        if (endpoint is null) return new(id, null, "显示器已断开或配置已变化，请刷新。");
        lock (_deviceLocks.GetOrAdd(id, _ => new()))
        {
            lock (_gate) if (endpoint.Generation != _generation) return new(id, null, "显示器配置已变化。");
            if (endpoint.WmiInstance is { } instance)
            {
                if (!WmiBrightnessBackend.Set(instance, percent)) return new(id, null, "系统亮度设置失败。");
                var screen = WmiBrightnessBackend.Enumerate().FirstOrDefault(s => s.Instance == instance);
                return screen is null ? new(id, null, "无法读回系统亮度。") : new(id, screen.Percent, null);
            }
            return AccessDdc(endpoint, percent);
        }
    }
    private BrightnessWriteResult AccessDdc(Endpoint endpoint, int? percent)
    {
        var id = endpoint.Id;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(endpoint.Monitor, ref info)) return new(id, null, "显示器配置已变化。");
        var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
        if (!EnumDisplayDevices(info.Device, 0, ref device, 1) || BrightnessEndpointIdentity.FromInterface(device.Id) != id)
            return new(id, null, "显示器配置已变化。");
        var additional = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
        if (EnumDisplayDevices(info.Device, 1, ref additional, 1)) return new(id, null, "复制显示模式无法可靠关联硬件目标。");
        if (!GetNumberOfPhysicalMonitorsFromHMONITOR(endpoint.Monitor, out var count) || count != 1)
            return new(id, null, "无法唯一关联物理显示器。");
        var physical = new PhysicalMonitor[1];
        if (!GetPhysicalMonitorsFromHMONITOR(endpoint.Monitor, 1, physical)) return new(id, null, "无法访问显示器。");
        try
        {
            lock (_gate) if (endpoint.Generation != _generation) return new(id, null, "显示器配置已变化。");
            var handle = physical[0].Handle;
            if (!GetMonitorBrightness(handle, out var min, out var value, out var max) || max <= min)
                return new(id, null, "不支持亮度控制，请检查显示器 DDC/CI 设置。");
            if (percent is { } target)
            {
                lock (_gate) if (endpoint.Generation != _generation) return new(id, null, "显示器配置已变化。");
                value = min + (uint)Math.Round((max - min) * (Math.Clamp(target, 0, 100) / 100d));
                if (!SetMonitorBrightness(handle, value)) return new(id, null, "显示器亮度设置失败。");
                if (!GetMonitorBrightness(handle, out min, out value, out max) || max <= min)
                    return new(id, null, "请求已发送，但无法读回实际亮度。");
            }
            return new(id, Math.Clamp((int)Math.Round(((double)value - min) * 100 / (max - min)), 0, 100), null);
        }
        finally { DestroyPhysicalMonitors(1, physical); }
    }
}

public static class BrightnessEndpointIdentity
{
    public static string? FromInterface(string value)
    {
        var parts = value.Split('#');
        return parts.Length < 3 ? null : string.Join("\\", parts[0].Replace("\\\\?\\", ""), parts[1], parts[2]).ToUpperInvariant();
    }
}
