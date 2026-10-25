using FloatDesk.Windows.Native;

namespace FloatDesk.Windows.Input;

public sealed record KeyboardLayoutInfo(nint Hkl, string Klid, string LanguageTag, bool IsUsEnglish, bool IsIme);

public static class KeyboardLayoutCatalog
{
    public static IReadOnlyList<KeyboardLayoutInfo> GetLoadedLayouts()
    {
        var count = NativeMethods.GetKeyboardLayoutList(0, null);
        if (count <= 0)
            return [];
        var handles = new nint[count];
        if (NativeMethods.GetKeyboardLayoutList(count, handles) <= 0)
            return [];
        return handles
            .Select(hkl =>
            {
                var value = unchecked((long)hkl);
                var klid = ((uint)value).ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
                var languageId = (ushort)(value & 0xFFFF);
                var isIme = NativeMethods.ImmIsIME(hkl);
                return new KeyboardLayoutInfo(
                    hkl,
                    klid,
                    languageId == 0x0409 ? "en-US" : $"0x{languageId:X4}",
                    languageId == 0x0409 && !isIme,
                    isIme);
            })
            .ToArray();
    }
}
