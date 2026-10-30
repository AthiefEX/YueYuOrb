using FloatDesk.Core.Brightness;
using FloatDesk.Core.Models;

namespace FloatDesk.Core.Tests;

public sealed class BrightnessPolicyTests
{
    [Fact]
    public void RelativeChangesKeepDifferencesAndClamp()
    {
        var snapshot = new BrightnessSnapshot([new("a", "A", 20, null, true), new("b", "B", 98, null, false)]);
        var result = BrightnessPolicy.CreateTargets(snapshot, 5, true);
        Assert.Equal(25, result["a"]);
        Assert.Equal(100, result["b"]);
        Assert.Equal(0, BrightnessPolicy.CreateTargets(snapshot, -100, true)["a"]);
    }

    [Fact]
    public void AbsoluteChangeSkipsUnavailableAndUnreadableScreens()
    {
        var snapshot = new BrightnessSnapshot([new("a", "A", 20, null, true), new("b", "B", 80, null, false),
            new("c", "C", null, null, false), new("d", "D", 50, "不支持", false)]);
        var result = BrightnessPolicy.CreateTargets(snapshot, 50, false);
        Assert.Equal(2, result.Count);
        Assert.All(result.Values, value => Assert.Equal(50, value));
        Assert.Empty(BrightnessPolicy.CreateTargets(new([]), 50, false));
    }

    [Fact]
    public void ExistingHotkeyValuesRemainStable()
    {
        Assert.Equal(10, (int)HotkeyAction.ExtractRegionText);
        Assert.Equal(11, (int)HotkeyAction.BrightnessPanel);
        Assert.Equal(16, (int)HotkeyAction.RecoverBrightness);
    }
}
