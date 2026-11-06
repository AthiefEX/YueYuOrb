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
        var defaults = AppSettings.CreateDefault() with
        {
            General = AppSettings.CreateDefault().General with
            {
                Theme = AppTheme.Dark,
                ShowSystemNotifications = false
            }
        };
        await store.SaveAsync(defaults, CancellationToken.None);
        var path = Path.Combine(_root, "settings.json");
        var root = System.Text.Json.Nodes.JsonNode
            .Parse(await File.ReadAllTextAsync(path))!.AsObject();
        Assert.True(root.Remove("desktopSessions"));
        await File.WriteAllTextAsync(path, root.ToJsonString());

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.False(loaded.DesktopSessions.Enabled);
        Assert.Empty(loaded.DesktopSessions.Environments);
        Assert.Equal(AppTheme.Dark, loaded.General.Theme);
        Assert.False(loaded.General.ShowSystemNotifications);
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
                        SessionStartupPolicy.ManagedOnly,
                        new SessionLimits(),
                        [
                            new SessionStartupEntry(
                                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                                "记事本",
                                "notepad.exe",
                                ["/a"],
                                null,
                                0,
                                5,
                                true,
                                false)
                        ])
                ],
                [
                    new DesktopSessionTask(
                        1,
                        Guid.Parse("44444444-4444-4444-4444-444444444444"),
                        "任务",
                        Guid.Parse("11111111-1111-1111-1111-111111111111"),
                        [
                            new SessionTaskStep(
                                Guid.Parse("55555555-5555-5555-5555-555555555555"),
                                SessionCommandKind.Delay,
                                new Dictionary<string, string>
                                {
                                    ["milliseconds"] = "1000"
                                },
                                30,
                                0)
                        ],
                        new SessionTaskTrigger(
                            TaskTriggerKind.Scheduled,
                            "08:00",
                            "China Standard Time"),
                        3,
                        true)
                ])
        };

        await store.SaveAsync(expected, CancellationToken.None);
        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.True(loaded.DesktopSessions.Enabled);
        var environment = Assert.Single(loaded.DesktopSessions.Environments);
        Assert.Equal(SessionStartupPolicy.ManagedOnly, environment.StartupPolicy);
        var startup = Assert.Single(environment.StartupEntries);
        Assert.False(startup.WaitForExit);
        Assert.Equal(5, startup.DelaySeconds);
        var task = Assert.Single(loaded.DesktopSessions.Tasks);
        Assert.Equal(3, task.Revision);
        Assert.Equal("China Standard Time", task.Trigger.TimeZoneId);
        Assert.Equal(SessionCommandKind.Delay, Assert.Single(task.Steps).Kind);
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
                        SessionStartupPolicy.EnvironmentDefault,
                        new SessionLimits(MaxSteps: 201),
                        [])
                ],
                [])
        };

        await Assert.ThrowsAsync<InvalidDataException>(
            () => store.SaveAsync(invalid, CancellationToken.None));
    }

    [Fact]
    public async Task MalformedPrimaryUsesValidBackup()
    {
        using var store = new JsonSettingsStore(_root);
        var original = AppSettings.CreateDefault() with
        {
            General = AppSettings.CreateDefault().General with
            {
                Theme = AppTheme.Dark
            }
        };
        await store.SaveAsync(original, CancellationToken.None);
        await store.SaveAsync(
            original with
            {
                General = original.General with { Theme = AppTheme.Light }
            },
            CancellationToken.None);
        var path = Path.Combine(_root, "settings.json");
        var root = System.Text.Json.Nodes.JsonNode
            .Parse(await File.ReadAllTextAsync(path))!.AsObject();
        root["desktopSessions"] = null;
        await File.WriteAllTextAsync(path, root.ToJsonString());

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(AppTheme.Dark, loaded.General.Theme);
        Assert.False(loaded.DesktopSessions.Enabled);
    }

    [Fact]
    public async Task InvalidTaskReferencesAndScheduleRejectSave()
    {
        using var store = new JsonSettingsStore(_root);
        var defaults = AppSettings.CreateDefault();
        var environmentId = Guid.NewGuid();
        var invalid = defaults with
        {
            DesktopSessions = new DesktopSessionSettings(
                true,
                [
                    new EnvironmentProfile(
                        environmentId,
                        "本机",
                        DesktopSessionBackendKind.LocalChild,
                        "local",
                        SessionStartupPolicy.EnvironmentDefault,
                        new SessionLimits(MaxSteps: 1),
                        [])
                ],
                [
                    new DesktopSessionTask(
                        1,
                        Guid.NewGuid(),
                        "无效",
                        Guid.NewGuid(),
                        [
                            new SessionTaskStep(
                                Guid.NewGuid(),
                                SessionCommandKind.Delay,
                                new Dictionary<string, string>(),
                                30,
                                0),
                            new SessionTaskStep(
                                Guid.NewGuid(),
                                SessionCommandKind.Delay,
                                new Dictionary<string, string>(),
                                30,
                                0)
                        ],
                        new SessionTaskTrigger(
                            TaskTriggerKind.Scheduled,
                            "99:99",
                            ""),
                        1,
                        true)
                ])
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
