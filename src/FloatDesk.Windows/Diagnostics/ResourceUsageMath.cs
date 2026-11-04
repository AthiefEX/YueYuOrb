using System.Globalization;
using System.Text.RegularExpressions;

namespace FloatDesk.Windows.Diagnostics;

internal readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User);

internal sealed class CpuUsageDelta
{
    private CpuTimes? _previous;

    public double? Update(CpuTimes? current)
    {
        var previous = _previous;
        _previous = current;
        if (current is not { } now || previous is not { } before ||
            now.Idle < before.Idle || now.Kernel < before.Kernel || now.User < before.User)
            return null;
        // Kernel includes idle. Subtract integers before converting to avoid losing precision.
        double total = (double)(now.Kernel - before.Kernel) + (now.User - before.User);
        double idle = now.Idle - before.Idle;
        return total <= 0 || idle > total ? null : Math.Clamp(100 * (total - idle) / total, 0, 100);
    }
}

internal readonly record struct GpuEngineSample(string Instance, double? Percent);

internal static partial class ResourceUsageMath
{
    [GeneratedRegex(@"(?:^|_)luid_0x([0-9a-f]{8})_0x([0-9a-f]{8})_phys_(\d+)_eng_(\d+)(?:_|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EnginePattern();

    internal static string AdapterId(uint high, uint low) => $"luid_0x{high:x8}_0x{low:x8}";

    internal static Dictionary<string, double?> AggregateGpu(IEnumerable<GpuEngineSample> samples)
    {
        var engines = new Dictionary<(string Adapter, uint Physical, uint Engine), double>();
        var unavailable = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sample in samples)
        {
            var match = EnginePattern().Match(sample.Instance);
            if (!match.Success ||
                !uint.TryParse(match.Groups[3].Value, out var physical) ||
                !uint.TryParse(match.Groups[4].Value, out var engine)) continue;
            var adapter = AdapterId(uint.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                uint.Parse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            if (sample.Percent is not { } value || !double.IsFinite(value) || value < 0)
            {
                unavailable.Add(adapter);
                continue;
            }
            var key = (adapter, physical, engine);
            engines[key] = engines.GetValueOrDefault(key) + value;
        }
        var result = engines.GroupBy(e => e.Key.Adapter).ToDictionary(g => g.Key,
            g => (double?)Math.Clamp(g.Max(e => e.Value), 0, 100), StringComparer.Ordinal);
        foreach (var adapter in unavailable) result[adapter] = null;
        return result;
    }
}
