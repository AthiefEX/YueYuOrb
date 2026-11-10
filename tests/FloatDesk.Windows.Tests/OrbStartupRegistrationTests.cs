using FloatDesk.Windows.DesktopSessions;

namespace FloatDesk.Windows.Tests;

public sealed class OrbStartupRegistrationTests
{
    private const string Guard = @"C:\Program Files\月语\Agent.exe";

    [Fact]
    public void InstallAndUninstallPreserveOwnExecutableAndStartupFlag()
    {
        var original = OrbStartupRegistration.BuildCommand(@"D:\月语 球\YueYuOrb.exe");
        var guarded = OrbStartupRegistration.RewriteCommand(original, Guard, true);
        Assert.Equal(OrbStartupRegistration.BuildCommand(@"D:\月语 球\YueYuOrb.exe", Guard), guarded);
        Assert.Equal(original, OrbStartupRegistration.RewriteCommand(guarded!, Guard, false));
    }

    [Theory]
    [InlineData("\"D:\\Other.exe\" --startup")]
    [InlineData("\"D:\\YueYuOrb.exe\" --startup --extra")]
    [InlineData("\"YueYuOrb.exe\" --startup")]
    public void UnknownCommandsAreNotChanged(string command) =>
        Assert.Null(OrbStartupRegistration.RewriteCommand(command, Guard, true));
}
