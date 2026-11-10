using FloatDesk.Core.Models;
using FloatDesk.Windows.Network;

namespace FloatDesk.Windows.Tests;

public sealed class SteamNetworkControllerTests
{
    [Fact]
    public async Task StartupFailureSurvivesRollbackStopAndControllerRecreation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "YueYu-Steam-State-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using (var first = new SteamNetworkController(directory))
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => first.StartAsync(
                    new SteamNetworkSettings { Store = false, Community = false, Content = false },
                    CancellationToken.None));

                Assert.Contains("最近失败", first.Status);
                Assert.Equal("InvalidSettings", first.LastFailure?.Diagnostic.ErrorCode);
                await first.StopAsync();
                Assert.Contains("最近失败", first.Status);
            }

            using (var recreated = new SteamNetworkController(directory))
            {
                Assert.Contains("最近失败", recreated.Status);
                Assert.Contains("InvalidSettings", recreated.Status);
                recreated.OnBridgeDiagnostic(new SteamNetworkDiagnostic(
                    "steamcommunity.com", "8.8.8.8", "https-dns", "domain", "tls-handshake", 25, "None", true));
                Assert.DoesNotContain("最近失败", recreated.Status);
            }

            using var afterSuccess = new SteamNetworkController(directory);
            Assert.DoesNotContain("最近失败", afterSuccess.Status);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task StoreSuccessDoesNotClearCommunityFailure()
    {
        var directory = Path.Combine(Path.GetTempPath(), "YueYu-Steam-Scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var controller = new SteamNetworkController(directory);
            controller.OnBridgeDiagnostic(new SteamNetworkDiagnostic(
                "steamcommunity.com", "8.8.8.8", "https-dns", "domain", "tls-handshake", 25, "ConnectionReset", false));
            Assert.Contains("steamcommunity.com", controller.Status);

            controller.OnBridgeDiagnostic(new SteamNetworkDiagnostic(
                "store.steampowered.com", "8.8.8.8", "https-dns", "domain", "tls-handshake", 25, "None", true));

            Assert.Contains("steamcommunity.com", controller.Status);
            Assert.Equal("ConnectionReset", controller.LastFailure?.Diagnostic.ErrorCode);

            controller.OnBridgeDiagnostic(new SteamNetworkDiagnostic(
                "steamcommunity.com", "8.8.8.8", "https-dns", "none", "tls-handshake", 25, "None", true));
            Assert.DoesNotContain("最近失败", controller.Status);
            await controller.StopAsync();
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
