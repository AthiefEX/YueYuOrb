using FloatDesk.Core.Models;
using System.Text.Json;

namespace FloatDesk.Core.Tests;

public sealed class DesktopSessionResolutionPreferenceTests
{
    [Fact]
    public void OldProfilesDefaultTo1280By720AndChosenResolutionRoundTrips()
    {
        var profile = EnvironmentProfile.CreateDefault() with { Id = Guid.NewGuid() };
        Assert.Equal(1280, profile.DesktopWidth);
        Assert.Equal(720, profile.DesktopHeight);
        var settings = new DesktopSessionSettings(true, [profile], []);
        var changed = DesktopSessionResolutionPreferences.Update(settings, profile.Id, 1920, 1080);
        var restored = JsonSerializer.Deserialize<DesktopSessionSettings>(JsonSerializer.Serialize(changed))!;
        Assert.Equal(1920, restored.Environments[0].DesktopWidth);
        Assert.Equal(1080, restored.Environments[0].DesktopHeight);
        Assert.Equal(1280, settings.Environments[0].DesktopWidth);
        Assert.Throws<ArgumentOutOfRangeException>(() => DesktopSessionResolutionPreferences.Update(settings, profile.Id, 1, 1));
        Assert.Throws<InvalidOperationException>(() => DesktopSessionResolutionPreferences.Update(settings, Guid.NewGuid(), 1920, 1080));
    }
}
