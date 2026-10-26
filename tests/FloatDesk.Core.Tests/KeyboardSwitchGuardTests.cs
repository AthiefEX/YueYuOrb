using FloatDesk.Core.Input;
using FloatDesk.Core.Models;

namespace FloatDesk.Core.Tests;

public sealed class KeyboardSwitchGuardTests
{
    private const uint VkControl = 0x11;
    private const uint VkShift = 0x10;
    private const uint VkMenu = 0x12;
    private const uint VkLeftWin = 0x5B;
    private const uint VkSpace = 0x20;
    private const uint VkQ = 0x51;
    private const uint VkC = 0x43;

    [Fact]
    public void CommonWindowsSwitchShortcutsAreBlocked()
    {
        var state = NewState();

        Assert.True(PressChord(state, VkMenu, VkShift));
        Assert.True(PressChord(state, VkControl, VkShift));
        Assert.True(PressChord(state, VkLeftWin, VkSpace));
        Assert.True(PressChord(state, VkControl, VkSpace));
    }

    [Theory]
    [InlineData(VkC)]
    [InlineData(VkQ)]
    public void OrdinaryModifierCommandsAreNotBlocked(uint key)
    {
        var state = NewState();

        Assert.False(PressChord(state, VkControl, key));
        Assert.False(PressChord(state, VkShift, key));
    }

    [Fact]
    public void UnknownQualifyingChordIsLearnedAfterLayoutDrift()
    {
        var clock = new ManualTimeProvider();
        var state = NewState(clock);

        Assert.False(PressChord(state, VkMenu, VkQ));
        clock.Advance(TimeSpan.FromMilliseconds(400));
        state.ObserveLayout(new LayoutObservation(
            0x08040804,
            "00000804",
            0x0804,
            false,
            true));

        Assert.True(state.IsBlocked(new KeyboardSwitchChord(
            ShortcutModifiers.Alt,
            VkQ)));
        Assert.True(PressChord(state, VkMenu, VkQ));
    }

    [Fact]
    public void PlainControlLetterIsNeverLearned()
    {
        var clock = new ManualTimeProvider();
        var state = NewState(clock);

        Assert.False(PressChord(state, VkControl, VkC));
        clock.Advance(TimeSpan.FromMilliseconds(400));
        state.ObserveLayout(new LayoutObservation(
            0x08040804,
            "00000804",
            0x0804,
            false,
            true));
        state.End();
        state.Begin(Target());

        Assert.False(PressChord(state, VkControl, VkC));
        Assert.Equal(0, state.LearnedCount);
    }

    [Fact]
    public void LearnedChordOnlyBlocksWhileGuardIsEnabled()
    {
        var clock = new ManualTimeProvider();
        var state = NewState(clock);

        Assert.False(PressChord(state, VkMenu, VkQ));
        state.ObserveLayout(new LayoutObservation(
            0x08040804,
            "00000804",
            0x0804,
            false,
            true));
        Assert.True(state.IsBlocked(new KeyboardSwitchChord(
            ShortcutModifiers.Alt,
            VkQ)));

        state.End();
        Assert.False(state.IsBlocked(new KeyboardSwitchChord(
            ShortcutModifiers.Alt,
            VkQ)));

        state.Begin(Target());
        Assert.True(PressChord(state, VkMenu, VkQ));
    }

    [Fact]
    public void SingleAltKeyIsNeverLearnedFromLayoutDrift()
    {
        var state = NewState();

        Assert.False(state.HandleKeyEvent(VkMenu, isDown: true));
        state.ObserveLayout(new LayoutObservation(
            0x08040804,
            "00000804",
            0x0804,
            false,
            true));
        state.HandleKeyEvent(VkMenu, isDown: false);

        Assert.Equal(0, state.LearnedCount);
        Assert.False(state.IsBlocked(new KeyboardSwitchChord(
            ShortcutModifiers.Alt,
            0)));
    }

    [Fact]
    public void ForegroundChangeCanDiscardAnUnrelatedModifierChord()
    {
        var state = NewState();

        Assert.False(state.HandleKeyEvent(VkMenu, isDown: true));
        Assert.False(state.HandleKeyEvent(0x09, isDown: true));
        state.HandleKeyEvent(0x09, isDown: false);
        state.HandleKeyEvent(VkMenu, isDown: false);
        state.ClearPendingLearning();
        state.ObserveLayout(new LayoutObservation(
            0x08040804,
            "00000804",
            0x0804,
            false,
            true));

        Assert.Equal(0, state.LearnedCount);
        Assert.False(state.IsBlocked(new KeyboardSwitchChord(
            ShortcutModifiers.Alt,
            0x09)));
    }

    [Fact]
    public void ToggleValuesAddConfiguredChordsWithoutDuplicates()
    {
        var shortcuts = KeyboardSwitchShortcuts.ParseToggleValues(
        [
            new KeyValuePair<string, string?>("Hotkey", "1"),
            new KeyValuePair<string, string?>("Language Hotkey", "2"),
            new KeyValuePair<string, string?>("Layout Hotkey", "3")
        ]);

        Assert.Contains(shortcuts, chord =>
            chord.Modifiers ==
                (ShortcutModifiers.Alt | ShortcutModifiers.Shift) &&
            chord.VirtualKey == 0);
        Assert.Contains(shortcuts, chord =>
            chord.Modifiers ==
                (ShortcutModifiers.Control | ShortcutModifiers.Shift) &&
            chord.VirtualKey == 0);
        Assert.Contains(shortcuts, chord =>
            chord.Modifiers == ShortcutModifiers.Win &&
            chord.VirtualKey == VkSpace);
        Assert.Equal(shortcuts.Count, shortcuts.Distinct().Count());
    }

    private static KeyboardSwitchGuardState NewState(
        ManualTimeProvider? clock = null)
    {
        var state = new KeyboardSwitchGuardState(
            clock ?? new ManualTimeProvider());
        state.Begin(Target());
        return state;
    }

    private static InputLayoutTarget Target() =>
        new(0x04090409, "00000409", 0x0409);

    private static bool PressChord(
        KeyboardSwitchGuardState state,
        uint modifier,
        uint key)
    {
        Assert.False(state.HandleKeyEvent(modifier, isDown: true));
        var blocked = state.HandleKeyEvent(key, isDown: true);
        state.HandleKeyEvent(key, isDown: false);
        state.HandleKeyEvent(modifier, isDown: false);
        return blocked;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow =
            new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
