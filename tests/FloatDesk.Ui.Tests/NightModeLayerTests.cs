using FloatDesk.App.Brightness;
using FloatDesk.App.Views;

namespace FloatDesk.Ui.Tests;

public sealed class NightModeLayerTests
{
    [Fact]
    public void OrbIsDimmedWhileRecoveryControlsStayAboveMask()
    {
        Assert.False(NightModeController.KeepAboveMask(typeof(OrbWindow)));
        Assert.True(NightModeController.KeepAboveMask(typeof(BrightnessControlWindow)));
    }
}
