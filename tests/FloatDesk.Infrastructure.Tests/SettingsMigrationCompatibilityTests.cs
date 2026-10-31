using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FloatDesk.Core.Models;
using FloatDesk.Infrastructure.Settings;

namespace FloatDesk.Infrastructure.Tests;

public sealed class SettingsMigrationCompatibilityTests
{
    [Fact]
    public void RetiredShortcutActionsDoNotDiscardExistingPreferences()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter() } };
        var defaults = AppSettings.CreateDefault();
        var expected = defaults with { General = new(true, AppTheme.Dark), Orb = defaults.Orb with { SizeDip = 96 } };
        var root = JsonNode.Parse(JsonSerializer.Serialize(expected, options))!.AsObject();
        root["danmaku"] = new JsonObject { ["enabled"] = true };
        var hotkeys = root["hotkeys"]!.AsObject();
        hotkeys["ToggleDanmaku"] = JsonSerializer.SerializeToNode(new HotkeyChord(ShortcutModifiers.Control, 0x78), options);
        hotkeys["17"] = JsonSerializer.SerializeToNode(new HotkeyChord(ShortcutModifiers.Control, 0x79), options);
        var restored = SettingsMigrator.Deserialize(root.ToJsonString(), options);
        Assert.Equal(expected.General, restored.General);
        Assert.Equal(96, restored.Orb.SizeDip);
        Assert.Equal(expected.Hotkeys.Count, restored.Hotkeys.Count);
        Assert.All(restored.Hotkeys.Keys, action => Assert.True(Enum.IsDefined(action)));
    }
}
