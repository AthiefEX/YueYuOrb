using FloatDesk.Windows.Brightness;
using Xunit.Abstractions;

namespace FloatDesk.Windows.Tests;

public sealed class BrightnessNativeSmokeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ReadOnlyDiscoveryReturnsUniqueDisplayIds()
    {
        using var service = new WindowsBrightnessService();
        var snapshot = await service.GetSnapshotAsync(default);
        Assert.Equal(snapshot.Devices.Count, snapshot.Devices.Select(d => d.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var device in snapshot.Devices)
        {
            output.WriteLine($"{device.Name}: {device.Percent?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? device.UnavailableReason}");
            if (device.Percent.HasValue) Assert.InRange(device.Percent.Value, 0, 100);
        }
    }
}
