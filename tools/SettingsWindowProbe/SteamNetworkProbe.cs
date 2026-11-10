using FloatDesk.Core.Models;
using FloatDesk.Windows.Network;
using System.IO;

namespace SettingsWindowProbe;

internal static class SteamNetworkProbe
{
    public static void VerifyNativeLease()
    {
        var store = new WinInetProxyConfiguration();
        var before = store.Read();
        if ((before.Flags & 6) != 0)
        {
            Console.WriteLine("wininet.lease=NOT_TESTED_EXISTING_PROXY");
            return;
        }
        var path = Path.Combine(Path.GetTempPath(), "YueYu-Network-Probe-" + Guid.NewGuid().ToString("N"));
        using var controller = new SteamNetworkController(path);
        try
        {
            controller.Start(new() { UseSystemPac = true, UseHostsCompatibility = false });
            var active = store.Read();
            if (active.Flags != 5 || active.AutoConfigUrl != controller.PacUri!.AbsoluteUri)
                throw new InvalidOperationException("System PAC readback did not match the active lease.");
            Console.WriteLine("wininet.native.apply=PASS");
        }
        finally { controller.Stop(); }
        if (store.Read() != before) throw new InvalidOperationException("Native lease failed to restore original settings.");
        if (Directory.Exists(path)) Directory.Delete(path);
        Console.WriteLine("wininet.native.restore=PASS");
    }

    public static async Task RunAsync()
    {
        var store = new WinInetProxyConfiguration();
        var before = store.Read();
        Console.WriteLine($"wininet.read=PASS;flags={before.Flags};hasPac={!string.IsNullOrEmpty(before.AutoConfigUrl)}");
        using var service = new SteamNetworkService();
        var settings = new SteamNetworkSettings();
        service.Start(settings);
        var direct = service.DiagnoseAsync(settings, false, CancellationToken.None);
        var tunnel = service.DiagnoseAsync(settings, true, CancellationToken.None);
        await Task.WhenAll(direct, tunnel);
        Console.WriteLine("DIRECT:\n" + await direct);
        Console.WriteLine("TUNNEL:\n" + await tunnel);
        service.Stop();
        if (store.Read() != before) throw new InvalidOperationException("Diagnostics changed system proxy settings.");
        Console.WriteLine("wininet.unchanged=PASS");
    }
}
