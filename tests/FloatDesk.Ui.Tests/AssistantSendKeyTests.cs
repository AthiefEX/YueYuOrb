using FloatDesk.App.Views;
using FloatDesk.Core.Assistant;
using System.Windows.Input;

namespace FloatDesk.Ui.Tests;

public sealed class AssistantSendKeyTests
{
    [Fact]
    public void AltSystemKeyEventCanSendButWrongKeyCannot()
    {
        var settings = new AssistantSettings { SendMode = 2, SendShortcut = "Alt+F8" };
        Assert.True(AssistantWindow.ShouldSend(settings, Key.System, ModifierKeys.Alt, false, Key.F8));
        Assert.False(AssistantWindow.ShouldSend(settings, Key.System, ModifierKeys.Alt, false, Key.F9));
    }

    [Theory]
    [InlineData(0, Key.Enter, ModifierKeys.Control, false, true)]
    [InlineData(0, Key.Enter, ModifierKeys.None, false, false)]
    [InlineData(1, Key.Enter, ModifierKeys.None, false, true)]
    [InlineData(1, Key.Enter, ModifierKeys.Shift, false, false)]
    [InlineData(1, Key.Enter, ModifierKeys.None, true, false)]
    [InlineData(2, Key.F8, ModifierKeys.Alt, false, true)]
    [InlineData(2, Key.F8, ModifierKeys.None, false, false)]
    public void SendKeyRespectsModeModifiersAndComposition(int mode, Key key, ModifierKeys modifiers, bool composing, bool expected)
        => Assert.Equal(expected, AssistantWindow.ShouldSend(new() { SendMode = mode, SendShortcut = "Alt+F8" }, key, modifiers, composing));
}
