using System.Reflection;
using FloatDesk.App.Views;

namespace FloatDesk.Ui.Tests;

public sealed class VmConnectWindowSelectionTests
{
    [Theory]
    [InlineData("RdpClipRdrWindowClass", false)]
    [InlineData("RDPSoundDVCWnd", false)]
    [InlineData("MSCTFIME UI", false)]
    [InlineData("HwndWrapper[vmconnect.exe;;helper]", false)]
    [InlineData("WindowsForms10.Window.8.app.0.aa0c13_r20_ad1", true)]
    public void RejectsNativeHelpersEvenWhenTheirDimensionsLookLikeAWindow(string className, bool expected)
    {
        var surface = typeof(SettingsWindow).Assembly.GetType("FloatDesk.App.Views.VmConnectSurface", true)!;
        var method = surface.GetMethod("IsConsoleFrameClass", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(expected, method.Invoke(null, [className]));
    }
}
