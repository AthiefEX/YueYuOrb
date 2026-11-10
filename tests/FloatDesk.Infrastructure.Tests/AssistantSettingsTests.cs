using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FloatDesk.Core.Models;
using FloatDesk.Infrastructure.Settings;

namespace FloatDesk.Infrastructure.Tests;

public sealed class AssistantSettingsTests
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public void MissingAssistantInOldSettingsGetsDisabledDefaults()
    {
        var root = JsonSerializer.SerializeToNode(AppSettings.CreateDefault(), Json)!.AsObject();
        root.Remove("assistant");
        var migrated = SettingsMigrator.Deserialize(root.ToJsonString(), Json);
        var property = typeof(AppSettings).GetProperty("Assistant");
        Assert.NotNull(property);
        var assistant = property.GetValue(migrated)!;
        Assert.False((bool)assistant.GetType().GetProperty("Enabled")!.GetValue(assistant)!);
        Assert.False((bool)assistant.GetType().GetProperty("AllowComputerControl")!.GetValue(assistant)!);
        Assert.Equal(1, migrated.SchemaVersion);
        Assert.Equal(3, (int)WheelActionKind.AiBrowser);
        Assert.Equal(17, (int)HotkeyAction.ToggleBrowserInteraction);
    }

    [Theory]
    [InlineData("http://example.com/v1")]
    [InlineData("https://name:password@example.com/v1")]
    [InlineData("file:///C:/private")]
    public void UnsafeAssistantEndpointsAreRejected(string endpoint)
    {
        var root = JsonSerializer.SerializeToNode(AppSettings.CreateDefault(), Json)!.AsObject();
        root["assistant"] = new JsonObject { ["enabled"] = true, ["endpoint"] = endpoint, ["model"] = "test-model" };
        Assert.Throws<InvalidDataException>(() => SettingsMigrator.Deserialize(root.ToJsonString(), Json));
    }

    [Theory]
    [InlineData(-1)] [InlineData(101)]
    public void InvalidAssistantWindowOpacityCannotBeSaved(int percent)
    {
        var root = JsonSerializer.SerializeToNode(AppSettings.CreateDefault(), Json)!.AsObject();
        root["assistant"] = new JsonObject { ["window"] = new JsonObject { ["opacity"] = percent / 100d } };
        Assert.Throws<InvalidDataException>(() => SettingsMigrator.Deserialize(root.ToJsonString(), Json));
    }

    [Fact]
    public void LocalModelCanBeConfiguredWithoutInstallingAnything()
    {
        var root = JsonSerializer.SerializeToNode(AppSettings.CreateDefault(), Json)!.AsObject();
        root["assistant"] = new JsonObject { ["enabled"] = true, ["endpoint"] = "http://127.0.0.1:11434/v1", ["model"] = "local-model" };
        Assert.Empty(SettingsValidator.Validate(SettingsMigrator.Deserialize(root.ToJsonString(), Json)));
    }
}
