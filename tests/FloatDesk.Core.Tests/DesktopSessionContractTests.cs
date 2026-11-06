using FloatDesk.Core.Models;

namespace FloatDesk.Core.Tests;

public sealed class DesktopSessionContractTests
{
    [Fact]
    public void OldSettingsLoadWithSessionsDisabled()
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
                    new SessionLimits(stepCount, queueLimit, timeoutSeconds)),
                new EnvironmentProfile(
                    duplicateEnvironmentIndex == 0
                        ? Guid.Parse("22222222-2222-2222-2222-222222222222")
                        : Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    "虚拟机",
                    DesktopSessionBackendKind.HyperV,
                    "profile",
                    new SessionLimits(1, 1, 30))
            ],
            StartupEntries =
            [
                new SessionStartupEntry(
                    Guid.Parse("33333333-3333-3333-3333-333333333333"),
                    "应用",
                    @"C:\app.exe",
                    [],
                    null,
                    0,
                    startupDelaySeconds,
                    true)
            ]
        };

        Assert.Throws<InvalidOperationException>(
            () => DesktopSessionSettingsValidator.Validate(settings));
    }

    [Fact]
    public void RoundTripPreservesExistingPreferences()
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
