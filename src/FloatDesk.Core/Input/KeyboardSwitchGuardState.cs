using System.Numerics;
using FloatDesk.Core.Models;

namespace FloatDesk.Core.Input;

public sealed class KeyboardSwitchGuardState
{
    private static readonly TimeSpan LearningWindow =
        TimeSpan.FromMilliseconds(1500);
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly HashSet<KeyboardSwitchChord> _blocked = [];
    private readonly HashSet<KeyboardSwitchChord> _learned = [];
    private readonly List<RecentChord> _recentChords = [];
    private InputLayoutTarget? _target;
    private ShortcutModifiers _modifiers;

    public KeyboardSwitchGuardState(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public int LearnedCount
    {
        get
        {
            lock (_sync)
                return _learned.Count;
        }
    }

    public void Begin(
        InputLayoutTarget target,
        IEnumerable<KeyboardSwitchChord>? configuredShortcuts = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_sync)
        {
            _target = target;
            _modifiers = ShortcutModifiers.None;
            _recentChords.Clear();
            _blocked.Clear();
            foreach (var chord in KeyboardSwitchShortcuts.Defaults)
                _blocked.Add(chord);
            if (configuredShortcuts is not null)
            {
                foreach (var chord in configuredShortcuts)
                    _blocked.Add(Normalize(chord));
            }
            foreach (var chord in _learned)
                _blocked.Add(chord);
        }
    }

    public void End()
    {
        lock (_sync)
        {
            _target = null;
            _modifiers = ShortcutModifiers.None;
            _recentChords.Clear();
            _blocked.Clear();
        }
    }

    public bool HandleKeyEvent(uint virtualKey, bool isDown)
    {
        lock (_sync)
        {
            if (_target is null)
                return false;
            var modifier = ModifierForVirtualKey(virtualKey);
            if (!isDown)
            {
                if (modifier != ShortcutModifiers.None)
                    _modifiers &= ~modifier;
                return false;
            }

            if (modifier != ShortcutModifiers.None)
                _modifiers |= modifier;
            var chord = Normalize(new KeyboardSwitchChord(
                _modifiers,
                modifier == ShortcutModifiers.None ? virtualKey : 0));
            var blocked = ShouldBlock(chord, virtualKey, modifier);
            if (!blocked && IsLearningCandidate(chord) && !_blocked.Contains(chord))
                RecordCandidate(chord);
            return blocked;
        }
    }

    public void ObserveLayout(LayoutObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        lock (_sync)
        {
            if (_target is null)
                return;
            var now = _timeProvider.GetUtcNow();
            _recentChords.RemoveAll(item =>
                now - item.ObservedAt > LearningWindow);
            if (observation.Hkl == _target.Hkl ||
                _recentChords.Count == 0)
            {
                return;
            }

            var learned = _recentChords[^1].Chord;
            _recentChords.Clear();
            if (_learned.Add(learned))
                _blocked.Add(learned);
        }
    }

    public bool IsBlocked(KeyboardSwitchChord chord)
    {
        lock (_sync)
            return _blocked.Contains(Normalize(chord));
    }

    public void ClearPendingLearning()
    {
        lock (_sync)
            _recentChords.Clear();
    }

    private bool ShouldBlock(
        KeyboardSwitchChord chord,
        uint virtualKey,
        ShortcutModifiers modifier)
    {
        foreach (var blocked in _blocked)
        {
            var required = blocked.Modifiers;
            if ((chord.Modifiers & required) != required)
                continue;
            if (blocked.VirtualKey == 0)
            {
                if (modifier != ShortcutModifiers.None &&
                    (required & modifier) != ShortcutModifiers.None)
                {
                    return true;
                }
            }
            else if (blocked.VirtualKey == virtualKey)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsLearningCandidate(KeyboardSwitchChord chord)
    {
        var modifiers = chord.Modifiers;
        if (modifiers == ShortcutModifiers.None)
            return false;
        var modifierCount = BitOperations.PopCount((uint)modifiers);
        if (chord.VirtualKey == 0)
            return modifierCount >= 2;
        return (modifiers &
                (ShortcutModifiers.Alt | ShortcutModifiers.Win)) !=
               ShortcutModifiers.None ||
               modifierCount >= 2;
    }

    private void RecordCandidate(KeyboardSwitchChord chord)
    {
        var now = _timeProvider.GetUtcNow();
        _recentChords.RemoveAll(item =>
            now - item.ObservedAt > LearningWindow);
        if (_recentChords.Count > 0 &&
            _recentChords[^1].Chord == chord &&
            now - _recentChords[^1].ObservedAt <
            TimeSpan.FromMilliseconds(100))
        {
            return;
        }
        _recentChords.Add(new RecentChord(chord, now));
        if (_recentChords.Count > 8)
            _recentChords.RemoveAt(0);
    }

    private static KeyboardSwitchChord Normalize(KeyboardSwitchChord chord) =>
        chord with
        {
            Modifiers = chord.Modifiers & ~ShortcutModifiers.NoRepeat
        };

    private static ShortcutModifiers ModifierForVirtualKey(uint virtualKey) =>
        virtualKey switch
        {
            0x10 or 0xA0 or 0xA1 => ShortcutModifiers.Shift,
            0x11 or 0xA2 or 0xA3 => ShortcutModifiers.Control,
            0x12 or 0xA4 or 0xA5 => ShortcutModifiers.Alt,
            0x5B or 0x5C => ShortcutModifiers.Win,
            _ => ShortcutModifiers.None
        };

    private sealed record RecentChord(
        KeyboardSwitchChord Chord,
        DateTimeOffset ObservedAt);
}

public static class KeyboardSwitchShortcuts
{
    public static IReadOnlyList<KeyboardSwitchChord> Defaults { get; } =
    [
        new(
            ShortcutModifiers.Alt | ShortcutModifiers.Shift,
            0),
        new(
            ShortcutModifiers.Control | ShortcutModifiers.Shift,
            0),
        new(ShortcutModifiers.Win, 0x20),
        new(ShortcutModifiers.Control, 0x20)
    ];

    public static IReadOnlyList<KeyboardSwitchChord> ParseToggleValues(
        IEnumerable<KeyValuePair<string, string?>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var result = new List<KeyboardSwitchChord>();
        foreach (var chord in Defaults)
            Add(result, chord);
        foreach (var (_, value) in values)
        {
            var chord = value?.Trim() switch
            {
                "1" => new KeyboardSwitchChord(
                    ShortcutModifiers.Alt | ShortcutModifiers.Shift,
                    0),
                "2" => new KeyboardSwitchChord(
                    ShortcutModifiers.Control | ShortcutModifiers.Shift,
                    0),
                _ => (KeyboardSwitchChord?)null
            };
            if (chord is { } configured)
                Add(result, configured);
        }
        return result;
    }

    private static void Add(
        List<KeyboardSwitchChord> result,
        KeyboardSwitchChord chord)
    {
        if (!result.Contains(chord))
            result.Add(chord);
    }
}
