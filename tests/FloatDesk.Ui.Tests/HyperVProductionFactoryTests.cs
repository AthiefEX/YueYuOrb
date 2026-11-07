using FloatDesk.Core.Models;
using FloatDesk.App.DesktopSessions;
using FloatDesk.Core.Contracts;

namespace FloatDesk.Ui.Tests;

public sealed class HyperVProductionFactoryTests
{
    [Fact]
    public async Task HyperVEnvironmentReturnsGuestPairingErrorInsteadOfUnsupportedBackend()
    {
        var settings = AppSettings.CreateDefault();
        var environment = new EnvironmentProfile(
            Guid.NewGuid(), "Hyper-V", DesktopSessionBackendKind.HyperV,
            Guid.NewGuid().ToString(), SessionStartupPolicy.EnvironmentDefault,
            new SessionLimits(), []);
        var host = new DesktopSessionProductionHost(
            () => settings,
            _ => throw new InvalidOperationException("factory-not-used"),
            new RecordingLedger());

        var result = await host.StartEnvironmentAsync(environment, default);

        Assert.False(result.Success);
        Assert.DoesNotContain("HyperVProductionBackendUnavailable", result.Message, StringComparison.Ordinal);
    }

    private sealed class RecordingLedger : IStartupExecutionLedger
    {
        public Task<bool> TryBeginAsync(StartupExecutionKey key, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
