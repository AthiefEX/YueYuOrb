using FloatDesk.Core.Models;
using FloatDesk.Core.Contracts;
using System.Runtime.CompilerServices;

namespace FloatDesk.Core.Tests;

public sealed class DesktopSessionContractTests
{
    [Fact]
    public void DefaultSettingsHaveSessionsDisabled()
    {
        var settings = AppSettings.CreateDefault();

        Assert.False(settings.DesktopSessions.Enabled);
        Assert.Empty(settings.DesktopSessions.Environments);
    }

    [Theory]
    [InlineData(201, 20, 30, 0, 0)]
    [InlineData(1, 21, 30, 0, 0)]
    [InlineData(1, 20, 0, 0, 0)]
    [InlineData(1, 20, 601, 0, 0)]
    [InlineData(1, 20, 30, 301, 0)]
    public void InvalidLimitsRejected(
        int stepCount,
        int queueLimit,
        int timeoutSeconds,
        int startupDelaySeconds,
        int duplicateEnvironmentIndex)
    {
        var settings = DesktopSessionSettings.CreateDefault() with
        {
            Environments =
            [
                new EnvironmentProfile(
                    Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    "本机",
                    DesktopSessionBackendKind.LocalChild,
                    "profile",
                    SessionStartupPolicy.EnvironmentDefault,
                    new SessionLimits(stepCount, queueLimit, timeoutSeconds),
                    [
                        new SessionStartupEntry(
                            Guid.Parse("33333333-3333-3333-3333-333333333333"),
                            "应用",
                            @"C:\app.exe",
                            [],
                            null,
                            0,
                            startupDelaySeconds,
                            true,
                            false)
                    ]),
                new EnvironmentProfile(
                    duplicateEnvironmentIndex == 0
                        ? Guid.Parse("22222222-2222-2222-2222-222222222222")
                        : Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    "虚拟机",
                    DesktopSessionBackendKind.HyperV,
                    "profile",
                    SessionStartupPolicy.EnvironmentDefault,
                    new SessionLimits(1, 1, 30),
                    [])
            ]
        };

        Assert.Throws<InvalidOperationException>(
            () => DesktopSessionSettingsValidator.Validate(settings));
    }

    [Fact]
    public void DuplicateEnvironmentIdsRejected()
    {
        var profile = new EnvironmentProfile(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "本机",
            DesktopSessionBackendKind.LocalChild,
            "profile",
            SessionStartupPolicy.EnvironmentDefault,
            new SessionLimits(),
            []);
        var settings = DesktopSessionSettings.CreateDefault() with
        {
            Environments = [profile, profile with { Name = "重复" }]
        };

        Assert.Throws<InvalidOperationException>(
            () => DesktopSessionSettingsValidator.Validate(settings));
    }

    [Fact]
    public async Task PreparationRequiredBeforeStart()
    {
        var backend = new RecordingBackend();
        var profile = EnvironmentProfile.CreateDefault();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => DesktopSessionStartCoordinator.StartAsync(
                backend,
                profile,
                new PreparationResult(
                    false,
                    false,
                    backend.Capabilities,
                    "not-prepared"),
                CancellationToken.None));
        Assert.Equal(0, backend.StartCalls);
    }

    [Fact]
    public void ExplicitStopMode()
    {
        var stopMethod = Assert.Single(
            typeof(IDesktopSessionBackend).GetMethods(),
            method => method.Name == nameof(IDesktopSessionBackend.StopAsync));
        Assert.Contains(
            stopMethod.GetParameters(),
            parameter => parameter.ParameterType == typeof(SessionStopMode));
    }

    [Fact]
    public void LoginIdentityStableAcrossHostRestart()
    {
        var previous = LoginInstanceIdentity.Create(
            new LoginIdentitySource(
                "boot-1",
                "S-1-5-21-100",
                "auth-1",
                new DateTimeOffset(2026, 11, 6, 8, 0, 0, TimeSpan.Zero)));
        var afterRestart = LoginInstanceIdentity.Create(
            new LoginIdentitySource(
                "boot-1",
                "S-1-5-21-100",
                "auth-1",
                new DateTimeOffset(2026, 11, 6, 8, 0, 0, TimeSpan.Zero)));

        Assert.Equal(previous, afterRestart);
        Assert.NotEqual(
            previous,
            LoginInstanceIdentity.Create(
                new LoginIdentitySource(
                    "boot-2",
                    "S-1-5-21-100",
                    "auth-1",
                    new DateTimeOffset(
                        2026,
                        11,
                        6,
                        8,
                        0,
                        0,
                        TimeSpan.Zero))));
    }

    [Fact]
    public void ExistingPreferencesRemainAssignable()
    {
        var defaults = AppSettings.CreateDefault();
        var preserved = defaults with
        {
            General = defaults.General with { Theme = AppTheme.Dark },
            DesktopSessions = DesktopSessionSettings.CreateDefault() with
            {
                Enabled = true
            }
        };

        Assert.Equal(AppTheme.Dark, preserved.General.Theme);
        Assert.True(preserved.DesktopSessions.Enabled);
        Assert.Equal(AppSettings.CurrentSchemaVersion, preserved.SchemaVersion);
    }

    [Fact]
    public void StartupAndTaskRevisionsChangeDeduplicationIdentity()
    {
        var environmentId = Guid.NewGuid();
        var loginInstanceId = Guid.NewGuid();
        var firstEntry = new SessionStartupEntry(
            Guid.NewGuid(),
            "应用",
            "app.exe",
            [],
            null,
            0,
            0,
            true,
            false,
            1);
        var secondEntry = firstEntry with { Revision = 2 };
        var firstTask = new DesktopSessionTask(
            1,
            Guid.NewGuid(),
            "任务",
            Guid.NewGuid(),
            [],
            new SessionTaskTrigger(TaskTriggerKind.Manual, null, null),
            1,
            true);
        var secondTask = firstTask with { Revision = 2 };

        Assert.NotEqual(
            new StartupExecutionKey(
                environmentId,
                loginInstanceId,
                firstEntry.Id,
                firstEntry.Revision),
            new StartupExecutionKey(
                environmentId,
                loginInstanceId,
                secondEntry.Id,
                secondEntry.Revision));
        Assert.NotEqual(firstTask.Revision, secondTask.Revision);
    }

    [Fact]
    public void ConfigurationEditorAdvancesRevisionsForEveryEdit()
    {
        var entry = new SessionStartupEntry(
            Guid.NewGuid(),
            "应用",
            "app.exe",
            [],
            null,
            0,
            0,
            true,
            false,
            1);
        var environment = new EnvironmentProfile(
            Guid.NewGuid(),
            "本机",
            DesktopSessionBackendKind.LocalChild,
            "profile",
            SessionStartupPolicy.EnvironmentDefault,
            new SessionLimits(),
            [entry]);
        var editedEnvironment = DesktopSessionConfigurationEditor.UpdateStartupEntry(
            environment,
            entry.Id,
            current => current with { Arguments = ["--changed"] });
        var task = new DesktopSessionTask(
            DesktopSessionFormat.CurrentTaskFormatVersion,
            Guid.NewGuid(),
            "任务",
            environment.Id,
            [],
            new SessionTaskTrigger(TaskTriggerKind.Manual, null, null),
            1,
            true);
        var settings = new DesktopSessionSettings(false, [environment], [task]);
        var editedSettings = DesktopSessionConfigurationEditor.UpdateTask(
            settings,
            task.Id,
            current => current with
            {
                Steps =
                [
                    new SessionTaskStep(
                        Guid.NewGuid(),
                        SessionCommandKind.Delay,
                        new Dictionary<string, string>
                        {
                            ["milliseconds"] = "100"
                        },
                        30,
                        0)
                ]
            });

        Assert.Equal(1, entry.Revision);
        Assert.Equal(2, editedEnvironment.StartupEntries[0].Revision);
        Assert.Equal(["--changed"], editedEnvironment.StartupEntries[0].Arguments);
        Assert.Equal(1, task.Revision);
        Assert.Equal(2, editedSettings.Tasks[0].Revision);
        Assert.Single(editedSettings.Tasks[0].Steps);
        Assert.Equal(1, DesktopSessionRevision.Next(int.MaxValue));
    }

    [Fact]
    public void DuplicateStartupEntryIdsRejected()
    {
        var entry = new SessionStartupEntry(
            Guid.NewGuid(),
            "应用",
            "app.exe",
            [],
            null,
            0,
            0,
            true,
            false);
        var environment = new EnvironmentProfile(
            Guid.NewGuid(),
            "本机",
            DesktopSessionBackendKind.LocalChild,
            "profile",
            SessionStartupPolicy.EnvironmentDefault,
            new SessionLimits(),
            [entry, entry with { DisplayName = "重复应用" }]);
        var settings = new DesktopSessionSettings(false, [environment], []);

        Assert.Throws<InvalidOperationException>(
            () => DesktopSessionSettingsValidator.Validate(settings));
    }

    [Fact]
    public void DuplicateTaskIdsRejected()
    {
        var environment = new EnvironmentProfile(
            Guid.NewGuid(),
            "本机",
            DesktopSessionBackendKind.LocalChild,
            "profile",
            SessionStartupPolicy.EnvironmentDefault,
            new SessionLimits(),
            []);
        var task = new DesktopSessionTask(
            DesktopSessionFormat.CurrentTaskFormatVersion,
            Guid.NewGuid(),
            "任务",
            environment.Id,
            [],
            new SessionTaskTrigger(TaskTriggerKind.Manual, null, null),
            1,
            true);
        var settings = new DesktopSessionSettings(
            false,
            [environment],
            [task, task with { Name = "重复任务", Revision = 2 }]);

        Assert.Throws<InvalidOperationException>(
            () => DesktopSessionSettingsValidator.Validate(settings));
    }

    [Fact]
    public void NegativeRevisionsRejected()
    {
        var entry = new SessionStartupEntry(
            Guid.NewGuid(),
            "应用",
            "app.exe",
            [],
            null,
            0,
            0,
            true,
            false,
            0);
        var environment = new EnvironmentProfile(
            Guid.NewGuid(),
            "本机",
            DesktopSessionBackendKind.LocalChild,
            "profile",
            SessionStartupPolicy.EnvironmentDefault,
            new SessionLimits(),
            [entry]);
        var task = new DesktopSessionTask(
            DesktopSessionFormat.CurrentTaskFormatVersion,
            Guid.NewGuid(),
            "任务",
            environment.Id,
            [],
            new SessionTaskTrigger(TaskTriggerKind.Manual, null, null),
            0,
            true);

        Assert.Throws<InvalidOperationException>(
            () => DesktopSessionSettingsValidator.Validate(
                new DesktopSessionSettings(false, [environment], [])));
        Assert.Throws<InvalidOperationException>(
            () => DesktopSessionSettingsValidator.Validate(
                new DesktopSessionSettings(false, [environment with
                {
                    StartupEntries = [entry with { Revision = 1 }]
                }], [task])));
    }

    [Fact]
    public void ConfigurationEditorRejectsIdentityChangesAndDuplicates()
    {
        var entry = new SessionStartupEntry(
            Guid.NewGuid(),
            "应用",
            "app.exe",
            [],
            null,
            0,
            0,
            true,
            false);
        var environment = new EnvironmentProfile(
            Guid.NewGuid(),
            "本机",
            DesktopSessionBackendKind.LocalChild,
            "profile",
            SessionStartupPolicy.EnvironmentDefault,
            new SessionLimits(),
            [entry]);
        var task = new DesktopSessionTask(
            DesktopSessionFormat.CurrentTaskFormatVersion,
            Guid.NewGuid(),
            "任务",
            environment.Id,
            [],
            new SessionTaskTrigger(TaskTriggerKind.Manual, null, null),
            1,
            true);
        var settings = new DesktopSessionSettings(false, [environment], [task]);

        Assert.Throws<InvalidOperationException>(
            () => DesktopSessionConfigurationEditor.UpdateStartupEntry(
                environment,
                entry.Id,
                current => current with { Id = Guid.NewGuid() }));
        Assert.Throws<InvalidOperationException>(
            () => DesktopSessionConfigurationEditor.UpdateTask(
                settings,
                task.Id,
                current => current with { Id = Guid.NewGuid() }));
        Assert.Throws<InvalidOperationException>(
            () => DesktopSessionConfigurationEditor.AddStartupEntry(
                environment,
                entry with { DisplayName = "重复应用" }));
        Assert.Throws<InvalidOperationException>(
            () => DesktopSessionConfigurationEditor.AddTask(
                settings,
                task with { Name = "重复任务", Revision = 2 }));
    }

    [Fact]
    public async Task BackendCannotStartWithoutSuccessfulPreparation()
    {
        var backend = new RecordingBackend();
        var preparation = new PreparationResult(
            true,
            false,
            backend.Capabilities,
            null);

        await DesktopSessionStartCoordinator.StartAsync(
            backend,
            EnvironmentProfile.CreateDefault(),
            preparation,
            CancellationToken.None);

        Assert.Equal(1, backend.StartCalls);
    }

    private sealed class RecordingBackend : IDesktopSessionBackend
    {
        public int StartCalls { get; private set; }
        public SessionCapabilities Capabilities { get; } = new(
            false,
            false,
            [],
            []);

        public Task<SessionCapabilities> ProbeAsync(
            EnvironmentProfile profile,
            CancellationToken cancellationToken) =>
            Task.FromResult(Capabilities);

        public Task<PreparationResult> PrepareAsync(
            EnvironmentProfile profile,
            SessionStartupPolicy policy,
            CancellationToken cancellationToken) =>
            Task.FromResult(new PreparationResult(
                true,
                false,
                Capabilities,
                null));

        public Task<SessionHandle> StartAsync(
            EnvironmentProfile profile,
            PreparationResult preparation,
            CancellationToken cancellationToken)
        {
            StartCalls++;
            return Task.FromResult(new SessionHandle(
                profile.Id,
                Guid.NewGuid(),
                1,
                Guid.NewGuid(),
                true));
        }

        public Task ShowAsync(
            SessionHandle handle,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task StopAsync(
            SessionHandle handle,
            SessionStopMode mode,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public async IAsyncEnumerable<SessionBackendEvent> WatchAsync(
            Guid environmentId,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
