using System.Text.Json;
using System.Text.Json.Nodes;
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

        var compatible = JsonNode.Parse(json);
        if (compatible is JsonObject root && root.FirstOrDefault(property =>
                string.Equals(property.Key, "hotkeys", StringComparison.OrdinalIgnoreCase)).Value is JsonObject savedHotkeys)
        {
            foreach (var key in savedHotkeys.Select(property => property.Key).ToArray())
                if (!Enum.TryParse<HotkeyAction>(key, ignoreCase: true, out var action) || !Enum.IsDefined(action))
                    savedHotkeys.Remove(key);
        }
        var result = compatible.Deserialize<AppSettings>(options)
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
        result = result with { Hotkeys = hotkeys };
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
