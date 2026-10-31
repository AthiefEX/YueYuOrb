using System.Text.Json;
using FloatDesk.Core.Models;

namespace FloatDesk.Infrastructure.Settings;

public static class SettingsMigrator
{
    public static AppSettings Deserialize(string json, JsonSerializerOptions options)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("SchemaVersion", out var schema) &&
            !document.RootElement.TryGetProperty("schemaVersion", out schema))
        {
            throw new InvalidDataException("Settings schema version is missing.");
        }

        var version = schema.GetInt32();
        if (version > AppSettings.CurrentSchemaVersion)
            throw new NewerSettingsVersionException(version);
        if (version < 1)
            throw new InvalidDataException("Settings schema version is invalid.");

        var result = JsonSerializer.Deserialize<AppSettings>(json, options)
            ?? throw new InvalidDataException("Settings JSON was empty.");
        var hotkeys = new Dictionary<HotkeyAction, HotkeyChord>(result.Hotkeys);
        var defaultHotkeys = AppSettings.CreateDefault().Hotkeys;
        foreach (var action in new[]
                 {
                     HotkeyAction.TranslateSelection,
                     HotkeyAction.TranslateRegion,
                     HotkeyAction.ToggleSubtitles,
                     HotkeyAction.ExtractRegionText
                 })
        {
            hotkeys.TryAdd(action, defaultHotkeys[action]);
        }
        result = result with { Hotkeys = hotkeys, Danmaku = (result.Danmaku ?? new()).Normalize() };
        var errors = SettingsValidator.Validate(result);
        if (errors.Count > 0)
            throw new InvalidDataException(string.Join(" ", errors));
        return result;
    }
}

internal sealed class NewerSettingsVersionException(int version) :
    IOException($"Settings schema {version} was created by a newer app version.")
{
    public int Version { get; } = version;
}
