using FloatDesk.Core.Input;
using FloatDesk.Core.Models;

namespace FloatDesk.Core.Tests;

public sealed class HotkeyFallbackPolicyTests
{
    [Fact]
    public void RegionCaptureCandidatesStartWithConfiguredChordAndAddAltShiftFallback()
    {
        var requested = new HotkeyChord(
            ShortcutModifiers.Control | ShortcutModifiers.Alt,
            0x41);

        var candidates = HotkeyFallbackPolicy.For(
            HotkeyAction.RegionCapture,
            requested);

        Assert.Equal(requested, candidates[0]);
        Assert.Contains(
            requested with
            {
                Modifiers = requested.Modifiers | ShortcutModifiers.Shift
            },
            candidates);
        Assert.Equal(candidates.Count, candidates.Distinct().Count());
    }

    [Fact]
    public void OtherActionsReceiveAtLeastOneAlternativeBeforeReportingConflict()
    {
        var requested = new HotkeyChord(
            ShortcutModifiers.Control | ShortcutModifiers.Alt,
            0x20);

        var candidates = HotkeyFallbackPolicy.For(
            HotkeyAction.Panel,
            requested);

        Assert.True(candidates.Count > 1);
        Assert.Equal(requested, candidates[0]);
    }
}
