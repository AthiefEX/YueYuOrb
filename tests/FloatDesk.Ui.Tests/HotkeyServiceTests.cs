using System.Runtime.InteropServices;
using System.Windows.Interop;
using FloatDesk.App;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

public sealed class HotkeyServiceTests
{
    [Fact(Timeout = 10_000)]
    public async Task ApplyingUnchangedConflictingHotkeyKeepsAutomaticFallback()
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            HwndSource? blocker = null;
            HotkeyService? service = null;
            var blockerId = 0x6F00;
            try
            {
                var requested = new HotkeyChord(
                    ShortcutModifiers.Control | ShortcutModifiers.Alt,
                    0x87);
                blocker = new HwndSource(new HwndSourceParameters("HotkeyTestBlocker")
                {
                    ParentWindow = new nint(-3),
                    WindowStyle = 0
                });
                Assert.True(RegisterHotKey(
                    blocker.Handle,
                    blockerId,
                    (uint)requested.Modifiers | 0x4000,
                    requested.VirtualKey));
                service = new HotkeyService(
                    new Dictionary<HotkeyAction, HotkeyChord>
                    {
                        [HotkeyAction.RegionCapture] = requested
                    });

                Assert.NotEqual(
                    requested,
                    service.EffectiveInitialBindings[HotkeyAction.RegionCapture]);
                var result = service.Rebind(HotkeyAction.RegionCapture, requested);

                Assert.True(result.Success);
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
            finally
            {
                service?.Dispose();
                if (blocker is not null)
                {
                    UnregisterHotKey(blocker.Handle, blockerId);
                    blocker.Dispose();
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        thread.Join(TimeSpan.FromSeconds(2));
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(
        nint hwnd,
        int id,
        uint modifiers,
        uint key);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint hwnd, int id);
}
