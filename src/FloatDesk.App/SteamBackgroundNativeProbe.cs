using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using FloatDesk.Core.Models;
using FloatDesk.Windows.Network;

namespace FloatDesk.App;

internal static class SteamBackgroundNativeProbe
{
    private static readonly JsonSerializerOptions ReportOptions = new() { WriteIndented = true };
    internal static async Task<int> RunAsync(string mode)
    {
        var directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "steam-background-native-evidence");
        Directory.CreateDirectory(directory);
        var report = new Dictionary<string, object?>();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var ready = await SteamBackgroundClient.RequestAsync(new("STATUS"), timeout.Token);
            report["ready"] = ready.Success;
            report["readyCode"] = ready.Code;
            if (!ready.Success || ready.Certificate is null) throw new IOException("后台未就绪。");
            using var root = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(ready.Certificate));
            report["certificatePublicOnly"] = !root.HasPrivateKey;
            report["rootThumbprint"] = root.Thumbprint;
            var unauthorized = await SteamBackgroundClient.RequestAsync(new("EXEC"), timeout.Token);
            report["arbitraryCommandRejected"] = !unauthorized.Success;
            if (mode == "cycle")
            {
                var before = await File.ReadAllBytesAsync(SteamHostsHelper.HostsPath, timeout.Token);
                using var controller = new SteamNetworkController(Path.Combine(directory, "client"));
                await controller.RecoverRuntimeAsync(timeout.Token);
                for (var iteration = 1; iteration <= 3; iteration++)
                {
                    await controller.StartAsync(new SteamNetworkSettings { Store = false, Content = false }, timeout.Token);
                    report["cycle" + iteration + "Running"] = controller.IsRunning;
                    if (iteration == 1)
                    {
                        var diagnosis = await controller.DiagnoseResultsAsync(controller.ActiveSettings!, true, timeout.Token);
                        report["realCommunityDiagnosis"] = SteamCompatibilityDiagnostics.Format(diagnosis);
                        report["realCommunityBridgeUsable"] = diagnosis.All(result => result.Usable && result.ReachedLocalBridge);
                    }
                    await controller.StopAsync();
                    var restored = await File.ReadAllBytesAsync(SteamHostsHelper.HostsPath, timeout.Token);
                    report["cycle" + iteration + "HostsRestored"] = before.AsSpan().SequenceEqual(restored);
                }
            }
            var after = await SteamBackgroundClient.RequestAsync(new("STATUS"), timeout.Token);
            report["rootReused"] = ready.Thumbprint == after.Thumbprint;
            report["completed"] = true;
            await File.WriteAllTextAsync(Path.Combine(directory, mode + ".json"), JsonSerializer.Serialize(report, ReportOptions), timeout.Token);
            return report.Values.OfType<bool>().All(value => value) ? 0 : 1;
        }
        catch (Exception error)
        {
            report["completed"] = false;
            report["errorType"] = error.GetType().Name;
            report["errorCode"] = error.HResult;
            var chain = new List<object>();
            for (Exception? current = error; current is not null; current = current.InnerException)
                chain.Add(new { type = current.GetType().Name, code = current.HResult,
                    detail = current.Message.StartsWith("后台 hosts 恢复失败：", StringComparison.Ordinal) ? current.Message : null });
            report["errorChain"] = chain;
            await File.WriteAllTextAsync(Path.Combine(directory, mode + ".json"), JsonSerializer.Serialize(report));
            return 1;
        }
    }
}
