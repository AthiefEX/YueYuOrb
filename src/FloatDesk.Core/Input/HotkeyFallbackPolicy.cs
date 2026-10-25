using FloatDesk.Core.Models;

namespace FloatDesk.Core.Input;

public static class HotkeyFallbackPolicy
{
    public static IReadOnlyList<HotkeyChord> For(
        HotkeyAction action,
        HotkeyChord requested)
    {
        var candidates = new List<HotkeyChord> { requested };
        var withShift = requested with
        {
            Modifiers = requested.Modifiers | ShortcutModifiers.Shift
        };
        if (withShift != requested)
            candidates.Add(withShift);

        if (action == HotkeyAction.RegionCapture &&
            requested.Modifiers.HasFlag(ShortcutModifiers.Control) &&
            requested.Modifiers.HasFlag(ShortcutModifiers.Alt))
        {
            var controlShift = requested with
            {
                Modifiers = requested.Modifiers & ~ShortcutModifiers.Alt
            };
            if (!candidates.Contains(controlShift))
                candidates.Add(controlShift);
        }

        return candidates;
    }
}
