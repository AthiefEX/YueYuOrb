using FloatDesk.Core.Models;

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
    public void PreparationRequiredBeforeStart()
    {
        var handle = new SessionHandle(
            Guid.NewGuid(),
            Guid.NewGuid(),
            2,
            Guid.NewGuid(),
            false);

        Assert.False(handle.PreparationCompleted);
        Assert.Equal(
            SessionStartupPolicy.EnvironmentDefault,
            EnvironmentProfile.CreateDefault().StartupPolicy);
    }

    [Fact]
    public void ExplicitStopMode()
    {
        Assert.Contains(SessionStopMode.HideView, Enum.GetValues<SessionStopMode>());
        Assert.Contains(SessionStopMode.Disconnect, Enum.GetValues<SessionStopMode>());
        Assert.Contains(SessionStopMode.LogoffOwnedSession, Enum.GetValues<SessionStopMode>());
        Assert.Contains(SessionStopMode.ShutdownGuest, Enum.GetValues<SessionStopMode>());
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
}
