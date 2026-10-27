using FloatDesk.Core.Models;
using FloatDesk.Core.Translation;

namespace FloatDesk.App.Translation;

public sealed class SelectedTextTranslationController
{
    private readonly ISelectedTextReader _reader;
    private readonly ITranslationRouteHost _host;

    public SelectedTextTranslationController(
        ISelectedTextReader reader,
        ITranslationRouteHost host)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public async Task RunAsync(
        ForegroundSnapshot target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var operation = _host.ShowInteractiveStatus(
            TranslationRoutePoint.GetCursorPosition(),
            "正在读取选中文字。");

        SelectedTextReadResult read;
        try
        {
            read = await _reader.ReadAsync(target, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _host.DismissOperation(operation);
            return;
        }
        catch
        {
            _host.ShowError(
                operation,
                "无法读取选中文字，请稍后重试。");
            return;
        }

        if (!read.Success)
        {
            _host.ShowError(
                operation,
                ResolveReadFailure(read.ErrorCode));
            return;
        }

        _host.ShowStatus(operation, "正在翻译…");
        TranslationResponse response;
        try
        {
            response = await _host.TranslateAsync(
                    read.Text!,
                    TranslationPurpose.Interactive,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _host.DismissOperation(operation);
            return;
        }
        catch
        {
            _host.ShowError(
                operation,
                "翻译失败，请稍后重试。");
            return;
        }

        if (response.FailureKind == TranslationFailureKind.Cancelled)
        {
            _host.DismissOperation(operation);
            return;
        }
        _host.ShowResult(operation, response);
    }

    private static string ResolveReadFailure(string? errorCode) =>
        errorCode switch
        {
            "NoSelectedText" => "未检测到可翻译的选中文字。",
            "ClipboardDenied" => "无法读取或恢复剪贴板，请稍后重试。",
            "ModifiersHeld" => "请松开 Ctrl、Alt、Shift 或 Win 键后重试。",
            "TargetNotForeground" => "目标窗口已变化或无法切换至前台。",
            _ => "无法读取选中文字，请稍后重试。"
        };
}
