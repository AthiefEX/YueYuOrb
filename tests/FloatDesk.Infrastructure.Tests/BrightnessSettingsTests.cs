using System.Text.Json;
using FloatDesk.Core.Models;
using FloatDesk.Infrastructure.Settings;

namespace FloatDesk.Infrastructure.Tests;

public sealed class BrightnessSettingsTests
{
    [Fact]
    public void LegacySettingsReceiveDefaultsWithoutChangingSchema()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var node = JsonSerializer.SerializeToNode(AppSettings.CreateDefault(), options)!.AsObject();
        node.Remove("brightness");
        var settings = SettingsMigrator.Deserialize(node.ToJsonString(), options);
        Assert.Equal(new BrightnessSettings(), settings.Brightness);
        Assert.Equal(1, settings.SchemaVersion);
        Assert.Empty(SettingsValidator.Validate(settings));
    }

    [Theory]
    [InlineData(101, 5, 40)]
    [InlineData(-1, 5, 40)]
    [InlineData(50, 0, 40)]
    [InlineData(50, 101, 40)]
    [InlineData(50, 5, 91)]
    [InlineData(50, 5, -1)]
    public void InvalidRangesAreRejected(int target, int step, int night)
    {
        Assert.NotEmpty(SettingsValidator.Validate(AppSettings.CreateDefault() with
        { Brightness = new(target, step, night) }));
    }

    [Fact]
    public void NullSettingsAreRejected()
    {
        Assert.NotEmpty(SettingsValidator.Validate(AppSettings.CreateDefault() with { Brightness = null! }));
    }
}
