using FloatDesk.App;

namespace FloatDesk.Ui.Tests;

public sealed class StartupServiceTests
{
    [Theory]
    [InlineData("--startup", false)]
    [InlineData("--STARTUP", false)]
    [InlineData("", true)]
    public void OnlyManualLaunchMayOpenAnExistingInstancesSettings(string argument, bool expected)
    {
        Assert.Equal(expected, StartupService.ShouldNotifyExistingInstance([argument]));
    }

    [Fact]
    public void StartupRegistrationQuotesExecutableAndMarksBackgroundLaunch()
    {
        Assert.Equal("\"D:\\月语 悬浮球\\YueYuOrb.exe\" --startup",
            StartupService.BuildStartupCommand(@"D:\月语 悬浮球\YueYuOrb.exe"));
    }
}
