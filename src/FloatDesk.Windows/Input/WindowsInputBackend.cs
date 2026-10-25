using System.ComponentModel;
using System.Runtime.InteropServices;
using FloatDesk.Core.Models;
using FloatDesk.Windows.Native;

namespace FloatDesk.Windows.Input;

public sealed class WindowsInputBackend : IInputBackend
{
    public Task<LayoutObservation?> ReadAsync(
        ForegroundSnapshot target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hkl = NativeMethods.GetKeyboardLayout(target.ThreadId);
        if (hkl == 0)
            return Task.FromResult<LayoutObservation?>(null);
        var number = unchecked((uint)(long)hkl);
        var languageId = (ushort)(number & 0xFFFF);
        var isIme = NativeMethods.ImmIsIME(hkl);
        return Task.FromResult<LayoutObservation?>(
            new LayoutObservation(
                hkl,
                languageId == 0x0409 && !isIme,
                isIme));
    }

    public Task<OperationResult> RequestUsEnglishAsync(
        ForegroundSnapshot target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!NativeMethods.IsWindow(target.TopLevelHwnd))
            return Task.FromResult(OperationResult.Fail("TargetGone", "目标窗口已关闭。"));
        if (NativeMethods.GetForegroundWindow() != target.TopLevelHwnd)
            return Task.FromResult(OperationResult.Fail("TargetChanged", "前台应用已变化。"));

        var english = KeyboardLayoutCatalog.GetLoadedLayouts()
            .FirstOrDefault(layout => layout.IsUsEnglish && !layout.IsIme);
        if (english is null)
        {
            return Task.FromResult(OperationResult.Fail(
                "LayoutUnavailable",
                "Windows 尚未安装或加载英语（美国）键盘。"));
        }

        var destination = target.FocusHwnd != 0
            ? target.FocusHwnd
            : target.TopLevelHwnd;
        var posted = NativeMethods.PostMessage(
            destination,
            NativeMethods.WmInputLangChangeRequest,
            0,
            english.Hkl);
        if (!posted)
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            return Task.FromResult(OperationResult.Fail(
                error.NativeErrorCode == 5 ? "AccessDenied" : "RequestRejected",
                error.Message));
        }

        return Task.FromResult(OperationResult.Ok());
    }
}
