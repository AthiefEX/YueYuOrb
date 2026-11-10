using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using FloatDesk.Core.Models;
using FloatDesk.Windows.Network;

namespace FloatDesk.App;

internal static class SteamNetworkNativeProbe
{
    private static readonly JsonSerializerOptions ReportOptions = new() { WriteIndented = true };
    public static async Task<int> RunAsync(string mode)
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "steam-compatibility-native");
        Directory.CreateDirectory(directory);
        var data = Path.Combine(directory, "lease");
        var report = new Dictionary<string, object?>();
        var output = Path.Combine(directory, mode + ".json");
        var before = await File.ReadAllBytesAsync(SteamHostsHelper.HostsPath);
        var rootsBefore = Roots();
        var proxy = new WinInetProxyConfiguration();
        var proxyBefore = proxy.Read();
        using var controller = new SteamNetworkController(data);
        try
        {
            if (mode == "recover")
            {
                var journal = Path.Combine(data, "steam-network-trust.json");
                string[] keys = [];
                if (File.Exists(journal))
                {
                    using var document = JsonDocument.Parse(await File.ReadAllTextAsync(journal));
                    keys = document.RootElement.GetProperty("Keys").EnumerateArray().Select(entry => entry.GetString()!).ToArray();
                }
                await controller.RecoverAsync(CancellationToken.None);
                report["recoveryJournalRemoved"] = !File.Exists(journal);
                report["temporaryKeysRemoved"] = keys.All(key => !CngKey.Exists(key, CngProvider.MicrosoftSoftwareKeyStorageProvider));
                report["productRootCount"] = Roots().Length;
            }
            else
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                var settings = new SteamNetworkSettings { UseHostsCompatibility = true };
                await controller.StartAsync(settings, timeout.Token);
                report["hostsMapsCommunityToLoopback"] = (await Dns.GetHostAddressesAsync("steamcommunity.com", timeout.Token)).Any(IPAddress.IsLoopback);
                report["rootTrustedAfterStart"] = Roots().Length > rootsBefore.Length;
                report["pid"] = Environment.ProcessId;
                if (mode == "hold")
                {
                    report["ready"] = true;
                    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report));
                    var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
                    while (DateTimeOffset.UtcNow < deadline)
                    {
                        var livePath = Path.Combine(directory, "live.json");
                        await File.WriteAllTextAsync(livePath + ".tmp", JsonSerializer.Serialize(new { pid = Environment.ProcessId, status = controller.Status }));
                        File.Move(livePath + ".tmp", livePath, true);
                        await Task.Delay(500);
                    }
                }
                else
                {
                    report["httpsWithoutExplicitProxy"] = await controller.DiagnoseAsync(settings, true, timeout.Token);
                    report["bridgeStatus"] = controller.Status;
                }
            }
            await controller.StopAsync();
            var after = await File.ReadAllBytesAsync(SteamHostsHelper.HostsPath);
            report["hostsRestoredExactly"] = before.AsSpan().SequenceEqual(after);
            report["proxyUnchanged"] = proxy.Read() == proxyBefore;
            // The root stays trusted between starts on purpose: that is what removes the repeated
            // Windows confirmation. "恢复网络配置" is the action that removes it.
            report["rootKeptForReuse"] = mode == "recover" || rootsBefore.SequenceEqual(Roots());
            report["completed"] = true;
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, ReportOptions));
            return 0;
        }
        catch (Exception error)
        {
            report["errorType"] = error.GetType().Name; report["error"] = error.Message;
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report));
            return 1;
        }
    }

    private static string[] Roots()
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser); store.Open(OpenFlags.ReadOnly);
        var result = new List<string>();
        foreach (var certificate in store.Certificates)
        {
            using (certificate)
                if (certificate.Subject.StartsWith("CN=YueYu Steam Local CA ", StringComparison.Ordinal)) result.Add(certificate.Thumbprint);
        }
        return result.Order(StringComparer.Ordinal).ToArray();
    }
}
