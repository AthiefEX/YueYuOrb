using System.Runtime.InteropServices;
using FloatDesk.App.Views;
using FloatDesk.Core.Geometry;
using FloatDesk.Core.Models;
using FloatDesk.Core.Translation;

namespace FloatDesk.App.Translation;

public readonly record struct TranslationRouteHandle(
    TranslationOverlayOperationToken Operation,
    PixelPoint Anchor);

public interface ITranslationRouteHost
{
    void ApplySettings(AppSettings settings);

    TranslationRouteHandle ShowSubtitleStatus(
        PixelPoint anchor,
        string status) =>
        ShowStatus(anchor, status);

    TranslationRouteHandle ShowStatus(
        PixelPoint anchor,
        string status);

    void ShowStatus(
        TranslationRouteHandle operation,
        string status);

    void ShowResult(
        TranslationRouteHandle operation,
        TranslationResponse response);

    void ShowError(
        TranslationRouteHandle operation,
        string message);

    void SetSubtitleControls(
        TranslationRouteHandle operation,
        bool active,
        bool paused,
        bool canReselect)
    {
        if (!active)
            DismissActiveOperation();
    }

    Task<TranslationResponse> TranslateAsync(
        string text,
        TranslationPurpose purpose,
        CancellationToken cancellationToken);

    void DismissActiveOperation();
}

public sealed class TranslationRouteHost : ITranslationRouteHost
{
    private readonly TranslationOverlayWindow _overlay;
    private Func<string, TranslationPurpose, CancellationToken,
        Task<TranslationResponse>>? _translator;

    public TranslationRouteHost(TranslationOverlayWindow overlay)
    {
        _overlay = overlay ??
            throw new ArgumentNullException(nameof(overlay));
    }

    internal void SetTranslator(
        Func<string, TranslationPurpose, CancellationToken,
            Task<TranslationResponse>> translator)
    {
        _translator = translator ??
            throw new ArgumentNullException(nameof(translator));
    }

    public void ApplySettings(AppSettings settings) =>
        _overlay.ApplySettings(settings);

    public TranslationRouteHandle ShowSubtitleStatus(
        PixelPoint anchor,
        string status)
    {
        var operation = _overlay.BeginOperation(subtitle: true);
        _overlay.ShowStatus(operation, status, anchor);
        return new TranslationRouteHandle(operation, anchor);
    }

    public TranslationRouteHandle ShowStatus(
        PixelPoint anchor,
        string status)
    {
        var operation = _overlay.BeginOperation(subtitle: false);
        _overlay.ShowStatus(operation, status, anchor);
        return new TranslationRouteHandle(operation, anchor);
    }

    public void ShowStatus(
        TranslationRouteHandle operation,
        string status) =>
        _overlay.ShowStatus(
            operation.Operation,
            status,
            operation.Anchor);

    public void ShowResult(
        TranslationRouteHandle operation,
        TranslationResponse response) =>
        _overlay.ShowResult(
            operation.Operation,
            response,
            operation.Anchor);

    public void ShowError(
        TranslationRouteHandle operation,
        string message) =>
        _overlay.ShowError(
            operation.Operation,
            message,
            operation.Anchor);

    public void SetSubtitleControls(
        TranslationRouteHandle operation,
        bool active,
        bool paused,
        bool canReselect) =>
        _overlay.SetSubtitleControls(
            operation.Operation,
            active,
            paused,
            canReselect);

    public Task<TranslationResponse> TranslateAsync(
        string text,
        TranslationPurpose purpose,
        CancellationToken cancellationToken)
    {
        var translator = _translator;
        return translator is null
            ? Task.FromResult(TranslationResponse.Fail(
                text,
                "",
                "翻译服务",
                TranslationFailureKind.ProviderError,
                "TranslationRuntimeUnavailable",
                "翻译服务尚未准备完成，请稍后重试。"))
            : translator(text, purpose, cancellationToken);
    }

    public void DismissActiveOperation() =>
        _overlay.BeginOperation(subtitle: false);
}

internal static class TranslationRoutePoint
{
    public static PixelPoint GetCursorPosition()
    {
        if (!GetCursorPos(out var point))
            return default;
        return new PixelPoint(point.X, point.Y);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }
}
