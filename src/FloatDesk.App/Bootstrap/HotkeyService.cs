using System.Runtime.InteropServices;
using System.Windows.Interop;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Input;
using FloatDesk.Core.Models;

namespace FloatDesk.App;

public sealed class HotkeyService : IHotkeyService
{
    private const int WmHotkey = 0x0312;
    private readonly HwndSource _source;
    private readonly Dictionary<int, (HotkeyAction Action, HotkeyChord Chord)> _registered = [];
    private readonly Dictionary<HotkeyAction, HotkeyChord> _desired;
    private readonly Dictionary<HotkeyAction, HotkeyChord> _effectiveInitialBindings = [];
    private readonly List<HotkeyAction> _failedInitialBindings = [];
    private int _nextId = 0x4A00;

    public HotkeyService(IReadOnlyDictionary<HotkeyAction, HotkeyChord> chords)
    {
        _desired = new Dictionary<HotkeyAction, HotkeyChord>(chords);
        var parameters = new HwndSourceParameters("FloatDesk.Hotkeys")
        {
            ParentWindow = new nint(-3),
            WindowStyle = 0
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
        foreach (var (action, chord) in _desired)
            RegisterInitial(action, chord);
    }

    public event Action<HotkeyAction>? Pressed;

    public IReadOnlyList<HotkeyAction> FailedInitialBindings =>
        _failedInitialBindings;

    public IReadOnlyDictionary<HotkeyAction, HotkeyChord> EffectiveInitialBindings =>
        _effectiveInitialBindings;

    public OperationResult Rebind(HotkeyAction action, HotkeyChord chord)
    {
        var oldEntry = _registered.FirstOrDefault(pair => pair.Value.Action == action);
        if (oldEntry.Key != 0 && oldEntry.Value.Chord == chord)
            return OperationResult.Ok();
        if (_desired.TryGetValue(action, out var desired) &&
            desired == chord &&
            _effectiveInitialBindings.TryGetValue(action, out var effective) &&
            oldEntry.Key != 0 &&
            oldEntry.Value.Chord == effective)
        {
            return OperationResult.Ok();
        }
        var candidateId = _nextId++;
        if (!RegisterHotKey(
                _source.Handle,
                candidateId,
                (uint)chord.Modifiers | 0x4000,
                chord.VirtualKey))
        {
            return OperationResult.Fail("HotkeyConflict", "快捷键已被其他程序占用。");
        }

        if (oldEntry.Key != 0)
        {
            UnregisterHotKey(_source.Handle, oldEntry.Key);
            _registered.Remove(oldEntry.Key);
        }
        _registered[candidateId] = (action, chord);
        _desired[action] = chord;
        _effectiveInitialBindings[action] = chord;
        return OperationResult.Ok();
    }

    public void Unbind(HotkeyAction action)
    {
        foreach (var entry in _registered.Where(pair => pair.Value.Action == action).ToArray())
        { UnregisterHotKey(_source.Handle, entry.Key); _registered.Remove(entry.Key); }
        _desired.Remove(action);
        _effectiveInitialBindings.Remove(action);
    }

    private void RegisterInitial(HotkeyAction action, HotkeyChord requested)
    {
        foreach (var candidate in HotkeyFallbackPolicy.For(action, requested))
        {
            var id = _nextId++;
            if (!RegisterHotKey(
                    _source.Handle,
                    id,
                    (uint)candidate.Modifiers | 0x4000,
                    candidate.VirtualKey))
            {
                continue;
            }

            _registered[id] = (action, candidate);
            _effectiveInitialBindings[action] = candidate;
            return;
        }

        _failedInitialBindings.Add(action);
    }

    private nint WndProc(
        nint hwnd,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        if (message == WmHotkey &&
            _registered.TryGetValue((int)wParam, out var entry))
        {
            Pressed?.Invoke(entry.Action);
            handled = true;
        }
        return 0;
    }

    public void Dispose()
    {
        foreach (var id in _registered.Keys)
            UnregisterHotKey(_source.Handle, id);
        _registered.Clear();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint hwnd, int id);
}
