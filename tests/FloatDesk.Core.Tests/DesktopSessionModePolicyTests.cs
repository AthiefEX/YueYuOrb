using FloatDesk.Core.Models;

namespace FloatDesk.Core.Tests;

public sealed class DesktopSessionModePolicyTests
{
    [Fact]
    public void LegacyEnabledOnlyEnablesConfiguredBackend()
    {
        var environment = EnvironmentProfile.CreateDefault() with { Id = Guid.NewGuid() };
        var settings = new DesktopSessionSettings(true, [environment], []);
        var normalized = DesktopSessionModePolicy.Normalize(settings);
        Assert.True(normalized.LocalChild!.Enabled);
        Assert.Equal(environment.Id, normalized.LocalChild.DefaultEnvironmentId);
        Assert.False(normalized.HyperV!.Enabled);
    }

    [Fact]
    public void ExplicitModesOverrideLegacyMirrorAndRoundTrip()
    {
        var settings = DesktopSessionSettings.CreateDefault() with
        {
            LocalChild = new(false, null),
            HyperV = new(true, null)
        };
        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        var restored = System.Text.Json.JsonSerializer.Deserialize<DesktopSessionSettings>(json)!;
        Assert.False(DesktopSessionModePolicy.IsEnabled(restored, DesktopSessionBackendKind.LocalChild));
        Assert.True(DesktopSessionModePolicy.IsEnabled(restored, DesktopSessionBackendKind.HyperV));
        Assert.True(DesktopSessionModePolicy.Normalize(restored).Enabled);
    }

    [Fact]
    public void LegacyDisabledStaysDisabledAndCrossBackendDefaultIsCleared()
    {
        var environment = EnvironmentProfile.CreateDefault() with { Id = Guid.NewGuid() };
        var settings = new DesktopSessionSettings(false, [environment], []);
        Assert.False(DesktopSessionModePolicy.Normalize(settings).LocalChild!.Enabled);
        var normalized = DesktopSessionModePolicy.Normalize(settings with
        {
            HyperV = new(true, environment.Id)
        });
        Assert.Null(normalized.HyperV!.DefaultEnvironmentId);
    }
}
