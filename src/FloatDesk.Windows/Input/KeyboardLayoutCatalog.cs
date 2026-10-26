using System.Globalization;
using FloatDesk.Core.Models;
using FloatDesk.Windows.Native;

namespace FloatDesk.Windows.Input;

public sealed record KeyboardLayoutInfo(
    nint Hkl,
    string Klid,
    uint LanguageId,
    bool IsEnglish,
    bool IsIme);

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
            .Select(hkl => Describe(hkl))
            .ToArray();
    }

    public static LayoutResolution Resolve(string klid)
    {
        var normalized = (klid ?? string.Empty)
            .Trim()
            .ToUpperInvariant();
        if (normalized.Length != 8 ||
            !uint.TryParse(
                normalized,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var requested))
        {
            return LayoutResolution.Fail(
                "LayoutInvalid",
                "目标 KLID 必须是八位十六进制标识，例如 00000409。");
        }
        if (!IsEnglishLanguage((ushort)(requested & 0xFFFF)))
        {
            return LayoutResolution.Fail(
                "LayoutNotEnglish",
                $"键盘布局 {normalized} 不是英文键盘，无法用于英文锁定。");
        }

        var hkl = NativeMethods.LoadKeyboardLayout(normalized, 0);
        if (hkl == 0)
        {
            return LayoutResolution.Fail(
                "LayoutUnavailable",
                $"Windows 尚未安装或无法加载键盘布局 {normalized}。请先在系统语言设置中添加对应的英文键盘。");
        }

        var layout = Describe(hkl, normalized);
        if (!layout.IsEnglish || layout.IsIme)
        {
            return LayoutResolution.Fail(
                "LayoutNotEnglish",
                $"键盘布局 {normalized} 不是可用的英文非 IME 键盘。");
        }

        return LayoutResolution.Ok(new InputLayoutTarget(
            layout.Hkl,
            layout.Klid,
            layout.LanguageId));
    }

    public static KeyboardLayoutInfo Describe(
        nint hkl,
        string? requestedKlid = null)
    {
        var languageId = (uint)(long)hkl & 0xFFFF;
        var isIme =
            NativeMethods.ImmGetProperty(
                hkl,
                NativeMethods.IgpConversion) != 0;
        return new KeyboardLayoutInfo(
            hkl,
            string.IsNullOrWhiteSpace(requestedKlid)
                ? languageId.ToString("X8", CultureInfo.InvariantCulture)
                : requestedKlid,
            languageId,
            IsEnglishLanguage((ushort)languageId) && !isIme,
            isIme);
    }

    private static bool IsEnglishLanguage(ushort languageId) =>
        (languageId & 0x3FF) == 0x09;
}
