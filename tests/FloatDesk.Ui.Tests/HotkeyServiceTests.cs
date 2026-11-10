using System.Runtime.InteropServices;
using System.Windows.Interop;
using FloatDesk.App;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

public sealed class HotkeyServiceTests
{
    [Fact(Timeout = 10_000)]
    public async Task SingleAndCombinedBrightnessKeysRegisterAndDispatch()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var initial = AppSettings.CreateDefault();
                var vm = FloatDesk.App.ViewModels.SettingsViewModel.From(initial);
                string[] keys = ["Up", "Down", "PageUp", "PageDown", "Plus", "Minus"];
                for (var index = 0; index < vm.BrightnessKeys.Count; index++)
                    vm.BrightnessKeys[index].Text = "Ctrl+Alt+Shift+Win+" + keys[index];
                Assert.True(vm.TryBuildCandidate(initial, out var candidate, out var error), error);
                var brightnessActions = vm.BrightnessKeys.Select(item => item.Action).ToHashSet();
                var bindings = candidate.Hotkeys.Where(pair => brightnessActions.Contains(pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value);
                using var service = new HotkeyService(bindings);
                Assert.Empty(service.FailedInitialBindings);
                Assert.Equal(6, service.EffectiveInitialBindings.Count);
                var received = new List<HotkeyAction>();
                service.Pressed += received.Add;
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var source = (HwndSource)typeof(HotkeyService).GetField("_source", flags)!.GetValue(service)!;
                var registered = (Dictionary<int, (HotkeyAction Action, HotkeyChord Chord)>)
                    typeof(HotkeyService).GetField("_registered", flags)!.GetValue(service)!;
                foreach (var entry in registered)
                {
                    Assert.Equal(bindings[entry.Value.Action], entry.Value.Chord);
                    SendMessage(source.Handle, 0x0312, entry.Key, 0);
                    Assert.Equal(entry.Value.Action, received[^1]);
                }
                Assert.Equal(6, received.Distinct().Count());
                var available = FindAvailableSingleKeys(source.Handle);
                var singleBindings = new Dictionary<HotkeyAction, HotkeyChord>
                {
                    [HotkeyAction.BrightnessUp] = new(ShortcutModifiers.None, available[0]),
                    [HotkeyAction.BrightnessDown] = new(ShortcutModifiers.None, available[1])
                };
                using var singleKeys = new HotkeyService(singleBindings);
                Assert.Empty(singleKeys.FailedInitialBindings);
                Assert.Equal(2, singleKeys.EffectiveInitialBindings.Count);
                var singleReceived = new List<HotkeyAction>();
                singleKeys.Pressed += singleReceived.Add;
                var singleSource = (HwndSource)typeof(HotkeyService).GetField("_source", flags)!.GetValue(singleKeys)!;
                var singleRegistered = (Dictionary<int, (HotkeyAction Action, HotkeyChord Chord)>)
                    typeof(HotkeyService).GetField("_registered", flags)!.GetValue(singleKeys)!;
                foreach (var entry in singleRegistered)
                {
                    Assert.Equal(singleBindings[entry.Value.Action], entry.Value.Chord);
                    Assert.Equal(singleKeys.EffectiveInitialBindings[entry.Value.Action], entry.Value.Chord);
                    SendMessage(singleSource.Handle, 0x0312, entry.Key, 0);
                    Assert.Equal(entry.Value.Action, singleReceived[^1]);
                }
                Assert.Equal(2, singleReceived.Count);
                completion.TrySetResult();
            }
            catch (Exception exception) { completion.TrySetException(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        thread.Join(TimeSpan.FromSeconds(2));
    }

    private static List<uint> FindAvailableSingleKeys(nint window)
    {
        var available = new List<uint>();
        // Prefer keypad +/- but do not disturb a running app which owns them.
        uint[] candidates = [0x6B, 0x6D, 0x87, 0x86, 0x85, 0x84, 0x83, 0x82, 0x81, 0x80, 0x7F, 0x7E, 0x7D, 0x7C];
        foreach (var key in candidates)
        {
            if (!RegisterHotKey(window, 0x7E00, 0x4000, key)) continue;
            UnregisterHotKey(window, 0x7E00);
            available.Add(key);
            if (available.Count == 2) return available;
        }
        throw new InvalidOperationException("No two unassigned test hotkeys are available.");
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint hwnd, int message, nint wParam, nint lParam);

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
