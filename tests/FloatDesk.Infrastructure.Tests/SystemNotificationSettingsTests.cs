using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FloatDesk.Core.Models;
using FloatDesk.Infrastructure.Settings;

namespace FloatDesk.Infrastructure.Tests;

public sealed class SystemNotificationSettingsTests
{
    [Fact]
    public void OldChatOnlySettingsNeverExpandSilentlyToEveryApplication()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter() } };
        var original = AppSettings.CreateDefault();
        var json = JsonNode.Parse(JsonSerializer.Serialize(original, options))!;
        json["danmaku"] = new JsonObject { ["enabled"] = true, ["qq"] = true, ["tim"] = true, ["weChat"] = true };
        var migrated = SettingsMigrator.Deserialize(json.ToJsonString(), options);
        Assert.True(migrated.Danmaku.Enabled);
        Assert.False(migrated.Danmaku.ReceiveAllApplications);
        Assert.Empty(migrated.Danmaku.SelectedApplications);
        Assert.Equal(DanmakuDisplayMode.Always, migrated.Danmaku.DisplayMode);
        Assert.Equal(1, migrated.SchemaVersion);
        Assert.Equal(original.InputLock.TargetKlid, migrated.InputLock.TargetKlid);
        Assert.Equal(original.InputLock.AutoEnabled, migrated.InputLock.AutoEnabled);
        Assert.Equal(original.InputLock.Rules, migrated.InputLock.Rules);
    }
}
