using System.Collections.Concurrent;
using System.Text.Json;
using FloatDesk.App.Capture;
using FloatDesk.App.Translation;
using FloatDesk.App.Views;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Geometry;
using FloatDesk.Core.Models;
using FloatDesk.Core.Translation;
using FloatDesk.Windows.Windows;

namespace FloatDesk.Ui.Tests;

public sealed class TranslationPrivacyTests
{
    [Fact]
    public void TranslationSettingsJsonCannotContainCredentialFields()
    {
        var json = JsonSerializer.Serialize(AppSettings.CreateDefault());

        Assert.DoesNotContain(
            "apiKey",
            json,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "secret",
            json,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TranslationDiagnosticsAreContentFree()
    {
        var properties = TranslationDiagnostics.Build(
            TranslationProviderKind.MyMemory,
            TranslationPurpose.Subtitle,
            totalCharacters: 842,
            elapsedMilliseconds: 213,
            TranslationFailureKind.Network,
            retryCount: 1);

        var serialized = JsonSerializer.Serialize(properties);

        Assert.DoesNotContain(
            "Hello subtitle",
            serialized,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "apiKey",
            serialized,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "characterBucket",
            serialized,
            StringComparison.Ordinal);
        Assert.Contains(
            "500-1999",
            serialized,
            StringComparison.Ordinal);
        Assert.Contains(
            "Network",
            serialized,
            StringComparison.Ordinal);
        Assert.Equal(6, properties.Count);
    }

    [Fact]
    public void SettingsFreeAndPrivacyCopyIsExplicit()
    {
        var xaml = File.ReadAllText(FindRepoFile(
            "src/FloatDesk.App/Views/SettingsWindow.xaml"));

        Assert.Contains("无需密钥", xaml, StringComparison.Ordinal);
        Assert.Contains("每日", xaml, StringComparison.Ordinal);
        Assert.Contains("截图不会上传", xaml, StringComparison.Ordinal);
        Assert.Contains("仅发送识别文字", xaml, StringComparison.Ordinal);
        Assert.Contains("导出或导入设置", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "测试连接会使用当前编辑值",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact(Timeout = 10_000)]
    public async Task CaptionDiagnosticsContainNoSourceOrTranslatedText()
    {
        const string sourceText = "SOURCE_SECRET";
        const string translatedText = "TRANSLATED_SECRET";
        var eventNames = new ConcurrentQueue<string>();
        var payloads = new ConcurrentQueue<string>();
        var host = new DiagnosticSubtitleHost(translatedText);
        var region = new TranslationCaptureRegion(
            "monitor-1",
            0.1,
            0.7,
            0.8,
            0.2);
        using var controller = new SubtitleTranslationController(
            new DiagnosticOcr(sourceText),
            new DiagnosticSubtitleCapture(),
            host,
            new ImmediateClock(),
            diagnostics: (eventName, properties) =>
            {
                eventNames.Enqueue(eventName);
                payloads.Enqueue(JsonSerializer.Serialize(properties));
            });

        try
        {
            await controller.StartAsync(
                TranslationSettings.CreateDefault() with
                {
                    SubtitleRefreshIntervalMs = 100,
                    SubtitleRegion = region
                },
                CancellationToken.None);
            await host.Displayed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await controller.StopAsync();
        }

        var serialized = string.Join("\n", payloads);
        Assert.Contains(
            "translation.subtitle.frame_changed",
            eventNames,
            StringComparer.Ordinal);
        Assert.Contains(
            "translation.subtitle.displayed",
            eventNames,
            StringComparer.Ordinal);
        Assert.DoesNotContain(
            sourceText,
            serialized,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            translatedText,
            serialized,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "apiKey",
            serialized,
            StringComparison.OrdinalIgnoreCase);
    }

    private sealed class DiagnosticSubtitleHost(
        string translatedText) : ITranslationRouteHost
    {
        private TranslationOverlayOperationToken? _active;
        private long _nextOperationId;

        public TaskCompletionSource Displayed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ApplySettings(AppSettings settings)
        {
        }

        public TranslationRouteHandle ShowStatus(
            PixelPoint anchor,
            string status)
        {
            var operation = new TranslationOverlayOperationToken(
                ++_nextOperationId);
            _active = operation;
            return new TranslationRouteHandle(operation, anchor);
        }

        public void ShowStatus(
            TranslationRouteHandle operation,
            string status)
        {
        }

        public void ShowResult(
            TranslationRouteHandle operation,
            TranslationResponse response)
        {
        }

        public void ShowError(
            TranslationRouteHandle operation,
            string message)
        {
        }

        public TranslationSubtitlePlacementStatus ShowSubtitleText(
            TranslationRouteHandle operation,
            string text)
        {
            if (_active is { } active &&
                active.Equals(operation.Operation))
            {
                Displayed.TrySetResult();
            }
            return TranslationSubtitlePlacementStatus.Ready;
        }

        public Task<TranslationResponse> TranslateAsync(
            string text,
            TranslationPurpose purpose,
            CancellationToken cancellationToken) =>
            Task.FromResult(TranslationResponse.Ok(
                text,
                translatedText,
                "en",
                "Test provider"));

        public void DismissActiveOperation()
        {
            _active = null;
        }
    }

    private sealed class DiagnosticOcr(
        string text) : IScreenOcrService
    {
        public IReadOnlyList<OcrLanguageOption> AvailableLanguages { get; } =
            [new("en-US", "English")];

        public Task<ScreenOcrResult> RecognizeAsync(
            CaptureFrame frame,
            string? languageTag,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ScreenOcrResult(text, null, null));
    }

    private sealed class DiagnosticSubtitleCapture :
        ITranslationSubtitleCapture
    {
        public Task<CapturedTranslationRegion?> CaptureRegionAsync(
            TranslationCaptureRegion region,
            CancellationToken cancellationToken)
        {
            var frame = new CaptureFrame(
                new PixelRect(0, 0, 4, 4),
                16,
                Enumerable.Repeat((byte)0x10, 4 * 4 * 4).ToArray());
            return Task.FromResult<CapturedTranslationRegion?>(
                new CapturedTranslationRegion(
                    frame,
                    new PixelRect(0, 0, 4, 4),
                    region));
        }
    }

    private sealed class ImmediateClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

        public Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private static string FindRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not find repository file '{relativePath}'.");
    }
}
