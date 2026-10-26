using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FloatDesk.Core.Models;
using FloatDesk.Infrastructure.Settings;

namespace FloatDesk.Infrastructure.Tests;

public sealed class TranslationSettingsMigrationTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public void LegacySettingsWithoutTranslationUseDefaults()
    {
        var json = JsonSerializer.SerializeToNode(AppSettings.CreateDefault(), Options)!.AsObject();
        json.Remove("translation");

        var migrated = SettingsMigrator.Deserialize(json.ToJsonString(), Options);

        Assert.Equal(TranslationSettings.CreateDefault(), migrated.Translation);
    }

    [Fact]
    public void LegacyHotkeysBackfillOnlyMissingTranslationActions()
    {
        var json = JsonSerializer.SerializeToNode(AppSettings.CreateDefault(), Options)!.AsObject();
        var hotkeys = json["hotkeys"]!.AsObject();
        hotkeys.Remove("TranslateSelection");
        hotkeys["TranslateRegion"] = JsonSerializer.SerializeToNode(
            new HotkeyChord(ShortcutModifiers.Control | ShortcutModifiers.Alt, 0x51), Options);
        json["wheelActions"] = new JsonArray(JsonValue.Create("Notes"));

        var migrated = SettingsMigrator.Deserialize(json.ToJsonString(), Options);

        Assert.Equal(new HotkeyChord(ShortcutModifiers.Control | ShortcutModifiers.Alt, 0x54), migrated.Hotkeys[HotkeyAction.TranslateSelection]);
        Assert.Equal(new HotkeyChord(ShortcutModifiers.Control | ShortcutModifiers.Alt, 0x51), migrated.Hotkeys[HotkeyAction.TranslateRegion]);
        Assert.Equal([WheelActionKind.Notes], migrated.WheelActions);
    }
}
