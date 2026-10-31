using System.Runtime.InteropServices;
using System.Windows.Interop;
using FloatDesk.App.Views;
using FloatDesk.Core.Danmaku;

namespace FloatDesk.Ui.Tests;

[Collection("WPF")]
public sealed class DanmakuPointerTests
{
    [Fact]
    public void PassiveOverlayRejectsMouseHitAndActivationAndEditorRestoresHitTesting()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var window = new DanmakuOverlayWindow();
            try
            {
                window.ApplySettings(new() { ReduceMotion = true });
                window.Present(new("test", "TIM", "测试", "鼠标穿透测试"), false);
                var hwnd = new WindowInteropHelper(window).Handle;
                Assert.Equal(new nint(-1), SendMessage(hwnd, 0x84, 0, 0));
                Assert.Equal(new nint(3), SendMessage(hwnd, 0x21, 0, 0));
                Assert.False(window.IsHitTestVisible);
                Assert.False(window.Focusable);
                window.SetEditing(true);
                Assert.True(window.IsHitTestVisible);
                Assert.True(window.Focusable);
            }
            catch (Exception e) { failure = e; }
            finally { window.Clear(); window.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd, int msg, nint wparam, nint lparam);
}
