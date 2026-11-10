using System.Text.Json;
using System.Text.Json.Nodes;
using FloatDesk.Core.Models;

namespace FloatDesk.Core.Tests;

public sealed class AuthenticatedRdpPreferenceTests
{
    [Fact]
    public void LegacyEnvironmentKeepsDisplaySharingAndClosePolicy()
    {
        var original = EnvironmentProfile.CreateDefault() with
        {
            Id = Guid.NewGuid(), Backend = DesktopSessionBackendKind.HyperV,
            DesktopWidth = 1920, DesktopHeight = 1080, ConsoleCloseAction = HyperVConsoleCloseAction.SaveState,
            HostSharingEnabled = true, HostSharedPaths = ["X:\\"]
        };
        var node = JsonSerializer.SerializeToNode(original)!.AsObject();
        node.Remove(nameof(EnvironmentProfile.UseAuthenticatedRdp));
        var restored = node.Deserialize<EnvironmentProfile>()!;
        Assert.True(restored.UseAuthenticatedRdp);
        Assert.Equal(original.Id, restored.Id);
        Assert.Equal(original.DesktopWidth, restored.DesktopWidth);
        Assert.Equal(original.DesktopHeight, restored.DesktopHeight);
        Assert.Equal(original.ConsoleCloseAction, restored.ConsoleCloseAction);
        Assert.Equal(original.HostSharedPaths, restored.HostSharedPaths);
    }
    [Fact]
    public void NativeFallbackPreferenceSurvivesRoundTrip()
    {
        var profile = EnvironmentProfile.CreateDefault() with { UseAuthenticatedRdp = false };
        Assert.False(JsonSerializer.Deserialize<EnvironmentProfile>(JsonSerializer.Serialize(profile))!.UseAuthenticatedRdp);
    }
}
