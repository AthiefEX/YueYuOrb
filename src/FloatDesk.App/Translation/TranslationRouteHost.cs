using System.Runtime.InteropServices;
using FloatDesk.App.Views;
using FloatDesk.Core.Geometry;
using FloatDesk.Core.Models;
using FloatDesk.Core.Translation;

namespace FloatDesk.App.Translation;

public readonly record struct TranslationRouteHandle(
    TranslationOverlayOperationToken Operation,
    PixelPoint Anchor);

public enum TranslationSubtitlePlacementStatus
{
    Ready,
    MonitorMissing,
    NoSafePlacement
}

public readonly record struct TranslationSubtitleDeliveryResult(
    bool Displayed,
    TranslationSubtitlePlacementStatus PlacementStatus);

public interface ITranslationRouteHost
{
    void ApplySettings(AppSettings settings);

    TranslationSubtitlePlacementStatus ApplySettingsForSubtitle(
        AppSettings settings)
    {
        ApplySettings(settings);
        return TranslationSubtitlePlacementStatus.Ready;
    }

    TranslationRouteHandle ShowSubtitleStatus(
        PixelPoint anchor,
        string status) =>
        ShowStatus(anchor, status);

    TranslationRouteHandle ShowStatus(
        PixelPoint anchor,
        string status);

    TranslationRouteHandle ShowInteractiveStatus(
        PixelPoint anchor,
        string status) =>
        ShowStatus(anchor, status);

    void ShowStatus(
        TranslationRouteHandle operation,
        string status);

    void ShowResult(
        TranslationRouteHandle operation,
        TranslationResponse response);

    void ShowExtractedText(TranslationRouteHandle operation, string text) =>
        ShowResult(operation, TranslationResponse.Ok("", text, "", "本地 OCR"));

    void SetExtractedTextTranslating(TranslationRouteHandle operation) =>
        ShowStatus(operation, "正在翻译…");

    void ShowExtractedTextTranslation(TranslationRouteHandle operation, TranslationResponse response) =>
        ShowResult(operation, response);

    void ShowError(
        TranslationRouteHandle operation,
        string message);

    TranslationSubtitlePlacementStatus ShowSubtitleText(
        TranslationRouteHandle operation,
        string translatedText,
        Func<Action, bool>? commitIfCurrent = null)
    {
        if (commitIfCurrent is not null)
        {
            if (!commitIfCurrent(
                    () => ShowStatus(operation, translatedText)))
            {
                return TranslationSubtitlePlacementStatus.Ready;
            }
        }
        else
        {
            ShowStatus(operation, translatedText);
        }
        return TranslationSubtitlePlacementStatus.Ready;
    }

    TranslationSubtitleDeliveryResult ShowSubtitleTextWithResult(
        TranslationRouteHandle operation,
        string translatedText,
        Func<Action, bool>? commitIfCurrent = null)
    {
        var committed = false;
        var placement = ShowSubtitleText(
            operation,
            translatedText,
            commit =>
            {
                if (commitIfCurrent is null)
                {
                    commit();
                    committed = true;
                }
                else
                {
                    committed = commitIfCurrent(commit);
                }
                return committed;
            });
        return new TranslationSubtitleDeliveryResult(
            committed &&
            placement == TranslationSubtitlePlacementStatus.Ready,
            placement);
    }

    TranslationSubtitlePlacementStatus ShowSubtitleNotice(
        TranslationRouteHandle operation,
        string notice)
    {
        ShowStatus(operation, notice);
        return TranslationSubtitlePlacementStatus.Ready;
    }

    void SetSubtitleControls(
        TranslationRouteHandle operation,
        bool active,
        bool paused,
        bool canReselect)
    {
        if (!active)
            DismissActiveOperation();
    }

    TranslationSubtitlePlacementStatus PrepareSubtitleCaptureRegion(
        TranslationCaptureRegion region) =>
        TranslationSubtitlePlacementStatus.Ready;

    bool IsOperationActive(TranslationRouteHandle operation) => true;

    bool HasActiveOperation() => false;

    Task<TranslationResponse> TranslateAsync(
        string text,
        TranslationPurpose purpose,
        CancellationToken cancellationToken);

    void DismissActiveOperation();

    void DismissOperation(TranslationRouteHandle operation) =>
        DismissActiveOperation();
}

public sealed class TranslationRouteHost : ITranslationRouteHost
{
    private readonly TranslationOverlayWindow _overlay;
    private Func<string, TranslationPurpose, CancellationToken,
        Task<TranslationResponse>>? _translator;
    private Func<bool>? _interactivePresentationAllowed;

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

    internal void SetInteractivePresentationAllowed(
        Func<bool> isAllowed)
    {
        _interactivePresentationAllowed = isAllowed ??
            throw new ArgumentNullException(nameof(isAllowed));
    }

    public void ApplySettings(AppSettings settings) =>
        _overlay.ApplySettings(settings);

    public TranslationSubtitlePlacementStatus ApplySettingsForSubtitle(
        AppSettings settings) =>
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

    public TranslationRouteHandle ShowInteractiveStatus(
        PixelPoint anchor,
        string status)
    {
        if (_interactivePresentationAllowed?.Invoke() == false)
            throw new OperationCanceledException();
        return ShowStatus(anchor, status);
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

    public void ShowExtractedText(TranslationRouteHandle operation, string text) =>
        _overlay.ShowExtractedText(operation.Operation, text, operation.Anchor);

    public void SetExtractedTextTranslating(TranslationRouteHandle operation) =>
        _overlay.SetExtractedTextTranslating(operation.Operation);

    public void ShowExtractedTextTranslation(TranslationRouteHandle operation, TranslationResponse response) =>
        _overlay.ShowExtractedTextTranslation(operation.Operation, response, operation.Anchor);

    public TranslationSubtitlePlacementStatus ShowSubtitleText(
        TranslationRouteHandle operation,
        string translatedText,
        Func<Action, bool>? commitIfCurrent = null) =>
        _overlay.ShowSubtitleText(
            operation.Operation,
            translatedText,
            commitIfCurrent);

    public TranslationSubtitleDeliveryResult ShowSubtitleTextWithResult(
        TranslationRouteHandle operation,
        string translatedText,
        Func<Action, bool>? commitIfCurrent = null) =>
        _overlay.ShowSubtitleTextWithResult(
            operation.Operation,
            translatedText,
            commitIfCurrent);

    public TranslationSubtitlePlacementStatus ShowSubtitleNotice(
        TranslationRouteHandle operation,
        string notice) =>
        _overlay.ShowSubtitleNotice(
            operation.Operation,
            notice);

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

    public TranslationSubtitlePlacementStatus PrepareSubtitleCaptureRegion(
        TranslationCaptureRegion region) =>
        _overlay.PrepareSubtitleCaptureRegion(region);

    public bool IsOperationActive(TranslationRouteHandle operation) =>
        _overlay.IsOperationActive(operation.Operation);

    public bool HasActiveOperation() =>
        _overlay.HasActiveOperation;

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
        _overlay.DismissCurrentOperation();

    public void DismissOperation(TranslationRouteHandle operation) =>
        _overlay.DismissOperation(operation.Operation);
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
