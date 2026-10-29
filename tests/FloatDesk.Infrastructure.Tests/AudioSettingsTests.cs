using System.Text.Json;
using System.Text.Json.Nodes;
using FloatDesk.Core.Models;
using FloatDesk.Infrastructure.Settings;

namespace FloatDesk.Infrastructure.Tests;

public sealed class AudioSettingsTests
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public void LegacySchemaOneUsesAudioDefaults()
    {
        var json = JsonSerializer.SerializeToNode(AppSettings.CreateDefault(), Options)!.AsObject();
        json.Remove("audioGroups");
        json.Remove("showAudioInWheel");
        var restored = SettingsMigrator.Deserialize(json.ToJsonString(), Options);
        Assert.Empty(restored.AudioGroups);
        Assert.True(restored.ShowAudioInWheel);
        Assert.Equal(1, restored.SchemaVersion);
    }

    [Fact]
    public void RoundtripPreservesGroupOrderDisconnectedIdsAndVisibility()
    {
        var settings = AppSettings.CreateDefault() with
        {
            ShowAudioInWheel = false,
            AudioGroups = [new(Guid.NewGuid(), "工作", ["disconnected", "speakers"], false), new(Guid.NewGuid(), "游戏", [])]
        };
        var restored = SettingsMigrator.Deserialize(JsonSerializer.Serialize(settings, Options), Options);
        Assert.False(restored.ShowAudioInWheel);
        Assert.Equal(settings.AudioGroups.Select(g => g.Id), restored.AudioGroups.Select(g => g.Id));
        Assert.Equal(["disconnected", "speakers"], restored.AudioGroups[0].DeviceIds);
        Assert.False(restored.AudioGroups[0].ShowInWheel);
        Assert.Empty(SettingsValidator.Validate(restored));
    }

    [Fact]
    public void ValidatorRejectsExcessGroupsDuplicateGroupIdsAndLongNames()
    {
        var group = new AudioCycleGroup(Guid.NewGuid(), "A", []);
        Assert.NotEmpty(SettingsValidator.Validate(AppSettings.CreateDefault() with { AudioGroups = [group, group] }));
        Assert.NotEmpty(SettingsValidator.Validate(AppSettings.CreateDefault() with { AudioGroups = [group with { Name = new string('a', 81) }] }));
        var groups = Enumerable.Range(0, 17).Select(_ => group with { Id = Guid.NewGuid() }).ToArray();
        Assert.NotEmpty(SettingsValidator.Validate(AppSettings.CreateDefault() with { AudioGroups = groups }));
        Assert.Empty(SettingsValidator.Validate(AppSettings.CreateDefault() with { AudioGroups = groups.Take(16).ToArray() }));
        Assert.Empty(SettingsValidator.Validate(AppSettings.CreateDefault() with { AudioGroups = [group with { Name = new string('a', 80) }] }));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{\"id\":\"00000000-0000-0000-0000-000000000000\",\"name\":\"A\",\"deviceIds\":[]}]")]
    [InlineData("[{\"id\":\"11111111-1111-1111-1111-111111111111\",\"name\":\" \",\"deviceIds\":[]}]")]
    [InlineData("[{\"id\":\"11111111-1111-1111-1111-111111111111\",\"name\":\"A\",\"deviceIds\":null}]")]
    [InlineData("[{\"id\":\"11111111-1111-1111-1111-111111111111\",\"name\":\"A\",\"deviceIds\":[\" \" ,\"x\"]}]")]
    [InlineData("[{\"id\":\"11111111-1111-1111-1111-111111111111\",\"name\":\"A\",\"deviceIds\":[null]}]")]
    [InlineData("[{\"id\":\"11111111-1111-1111-1111-111111111111\",\"name\":\"A\",\"deviceIds\":[\"x\",\"x\"]}]")]
    public void ImportRejectsMalformedAudioGroups(string groups)
    {
        var json = JsonSerializer.SerializeToNode(AppSettings.CreateDefault(), Options)!.AsObject();
        json["audioGroups"] = JsonNode.Parse(groups);
        Assert.Throws<InvalidDataException>(() => SettingsMigrator.Deserialize(json.ToJsonString(), Options));
    }
}
