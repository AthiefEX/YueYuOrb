using FloatDesk.Core.Models;
using FloatDesk.Infrastructure.Settings;

namespace FloatDesk.Infrastructure.Tests;

public sealed class DesktopSessionSettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "FloatDesk.DesktopSessions.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task MissingLegacySectionDefaultsToDisabled()
    {
        using var store = new JsonSettingsStore(_root);
        var defaults = AppSettings.CreateDefault();
        await store.SaveAsync(defaults, CancellationToken.None);
        var path = Path.Combine(_root, "settings.json");
        var root = System.Text.Json.Nodes.JsonNode
            .Parse(await File.ReadAllTextAsync(path))!.AsObject();
        Assert.True(root.Remove("desktopSessions"));
        await File.WriteAllTextAsync(path, root.ToJsonString());

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.False(loaded.DesktopSessions.Enabled);
        Assert.Empty(loaded.DesktopSessions.Environments);
        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
    }

    [Fact]
    public async Task SettingsRoundTripPreservesDesktopSessionPreferences()
    {
        using var store = new JsonSettingsStore(_root);
        var expected = AppSettings.CreateDefault() with
        {
            General = AppSettings.CreateDefault().General with { Theme = AppTheme.Dark },
            DesktopSessions = new DesktopSessionSettings(
                true,
                [
                    new EnvironmentProfile(
                        Guid.Parse("11111111-1111-1111-1111-111111111111"),
                        "本机",
                        DesktopSessionBackendKind.LocalChild,
                        "local",
                        new SessionLimits())
                ],
                [],
                [])
        };

        await store.SaveAsync(expected, CancellationToken.None);
        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.True(loaded.DesktopSessions.Enabled);
        Assert.Single(loaded.DesktopSessions.Environments);
        Assert.Equal(AppTheme.Dark, loaded.General.Theme);
        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
    }

    [Fact]
    public async Task InvalidDesktopSessionLimitsRejectSave()
    {
        using var store = new JsonSettingsStore(_root);
        var defaults = AppSettings.CreateDefault();
        var invalid = defaults with
        {
            DesktopSessions = new DesktopSessionSettings(
                true,
                [
                    new EnvironmentProfile(
                        Guid.NewGuid(),
                        "本机",
                        DesktopSessionBackendKind.LocalChild,
                        "local",
                        new SessionLimits(MaxSteps: 201))
                ],
                [],
                [])
        };

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.SaveAsync(invalid, CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
