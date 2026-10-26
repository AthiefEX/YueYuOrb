using System.ComponentModel;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using FloatDesk.Core.Models;
using FloatDesk.Windows.Native;

namespace FloatDesk.Windows.Input;

public sealed class WindowsInputBackend : IInputBackend
{
    private readonly ConcurrentDictionary<nint, string> _resolvedKlids = new();

    public Task<LayoutResolution> ResolveAsync(
        string klid,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = KeyboardLayoutCatalog.Resolve(klid);
        if (result.Target is not null)
            _resolvedKlids[result.Target.Hkl] = result.Target.Klid;
        return Task.FromResult(result);
    }

    public Task<LayoutObservation?> ReadAsync(
        ForegroundSnapshot target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (target.ThreadId == 0)
            return Task.FromResult<LayoutObservation?>(null);
        var hkl = NativeMethods.GetKeyboardLayout(target.ThreadId);
        if (hkl == 0)
            return Task.FromResult<LayoutObservation?>(null);
        _resolvedKlids.TryGetValue(hkl, out var klid);
        var description = KeyboardLayoutCatalog.Describe(hkl, klid);
        return Task.FromResult<LayoutObservation?>(
            new LayoutObservation(
                description.Hkl,
                description.Klid,
                description.LanguageId,
                description.IsEnglish,
                description.IsIme));
    }

    public Task<OperationResult> ActivateAsync(
        ForegroundSnapshot target,
        InputLayoutTarget layout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return RequestLayoutAsync(
            target,
            layout.Hkl,
            cancellationToken);
    }

    public Task<OperationResult> RestoreAsync(
        ForegroundSnapshot target,
        nint hkl,
        CancellationToken cancellationToken) =>
        RequestLayoutAsync(target, hkl, cancellationToken);

    private static Task<OperationResult> RequestLayoutAsync(
        ForegroundSnapshot target,
        nint hkl,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (hkl == 0)
        {
            return Task.FromResult(OperationResult.Fail(
                "LayoutInvalid",
                "要切换的键盘布局无效。"));
        }
        if (!NativeMethods.IsWindow(target.TopLevelHwnd))
            return Task.FromResult(OperationResult.Fail(
                "TargetGone",
                "目标窗口已关闭。"));
        if (NativeMethods.GetForegroundWindow() != target.TopLevelHwnd)
            return Task.FromResult(OperationResult.Fail(
                "TargetChanged",
                "前台应用已变化。"));

        var destination = target.FocusHwnd != 0
            ? target.FocusHwnd
            : target.TopLevelHwnd;
        var posted = NativeMethods.PostMessage(
            destination,
            NativeMethods.WmInputLangChangeRequest,
            0,
            hkl);
        if (!posted)
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            var errorCode = error.NativeErrorCode switch
            {
                5 => "AccessDenied",
                1400 => "TargetGone",
                _ => "RequestRejected"
            };
            return Task.FromResult(OperationResult.Fail(
                errorCode,
                error.Message));
        }

        return Task.FromResult(OperationResult.Ok());
    }
}
