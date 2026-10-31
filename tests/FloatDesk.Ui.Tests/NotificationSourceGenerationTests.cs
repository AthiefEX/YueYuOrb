using FloatDesk.App.Danmaku;
namespace FloatDesk.Ui.Tests;

public sealed class NotificationSourceGenerationTests
{
    [Fact]
    public void ErrorFromOldReadCannotDisableAResetNotificationSession()
    {
        var source = new WindowsNotificationSource();
        source.Reset();
        var currentStatus = source.Status;
        source.HandleReadFailure(0, unchecked((int)0x80070005));
        Assert.Equal(currentStatus, source.Status);
        source.HandleReadFailure(1, unchecked((int)0x80070005));
        Assert.Contains("80070005", source.Status);
    }
}
