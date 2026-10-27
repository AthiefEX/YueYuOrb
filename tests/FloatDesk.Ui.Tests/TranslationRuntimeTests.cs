using FloatDesk.App.Translation;
using FloatDesk.App.Views;
using FloatDesk.Core.Geometry;
using FloatDesk.Core.Models;
using FloatDesk.Core.Translation;

namespace FloatDesk.Ui.Tests;

public sealed class TranslationRuntimeTests
{
    private static readonly ForegroundSnapshot ExternalTarget = new(
        1,
        (nint)0x1234,
        (nint)0x5678,
        42,
        7,
        DateTimeOffset.UtcNow,
        @"C:\Apps\target.exe",
        IsOwnInteractive: false);

    [Fact]
    public async Task SelectedTextControllerShowsPreparingAndResult()
    {
        var response = TranslationResponse.Ok(
            "Hello",
            "你好",
            "en",
            "Test provider");
        var host = new RecordingRouteHost
        {
            TranslateHandler = (_, _, _) => Task.FromResult(response)
        };
        var reader = new DelegateSelectedTextReader(
            (_, _) => Task.FromResult(
                SelectedTextReadResult.Ok("Hello")));
        var controller = new SelectedTextTranslationController(
            reader,
            host);

        await controller.RunAsync(ExternalTarget, CancellationToken.None);

        Assert.Equal(
            ["正在读取选中文字。", "正在翻译…"],
            host.Statuses);
        Assert.Same(response, host.LastResult);
        Assert.Null(host.LastError);
        Assert.Equal(1, host.TranslateCount);
    }

    [Fact]
    public async Task SelectedTextFailureDoesNotCallTranslationProvider()
    {
        var host = new RecordingRouteHost();
        var reader = new DelegateSelectedTextReader(
            (_, _) => Task.FromResult(
                SelectedTextReadResult.Fail(
                    "NoSelectedText",
                    "sensitive clipboard details")));
        var controller = new SelectedTextTranslationController(
            reader,
            host);

        await controller.RunAsync(ExternalTarget, CancellationToken.None);

        Assert.Contains("未检测到", host.LastError);
        Assert.DoesNotContain(
            "sensitive clipboard details",
            host.LastError);
        Assert.Equal(0, host.TranslateCount);
    }

    [Fact]
    public async Task ExpectedCancellationDoesNotRenderAFailure()
    {
        var host = new RecordingRouteHost();
        var reader = new DelegateSelectedTextReader(
            (_, _) => Task.FromException<SelectedTextReadResult>(
                new OperationCanceledException()));
        var controller = new SelectedTextTranslationController(
            reader,
            host);

        await controller.RunAsync(ExternalTarget, CancellationToken.None);

        Assert.Equal(["正在读取选中文字。"], host.Statuses);
        Assert.Null(host.LastResult);
        Assert.Null(host.LastError);
    }

    [Fact]
    public async Task MissingCustomKeyShowsActionableErrorAndRecoversAfterKeySave()
    {
        var provider = new StaticTranslationProvider("你好");
        var factory = new RecordingProviderFactory(provider);
        var credentials = new MutableCredentialStore();
        var host = new RecordingRouteHost();
        using var runtime = CreateRuntime(host, credentials, factory);
        host.TranslateHandler = runtime.TranslateTextAsync;
        runtime.ApplySettings(
            CustomProviderSettings());

        await runtime.TranslateSelectionAsync(
            ExternalTarget,
            CancellationToken.None);

        Assert.NotNull(host.LastResult);
        Assert.False(host.LastResult!.Success);
        Assert.Contains("密钥", host.LastResult.Message);
        Assert.Equal(0, factory.CreateCount);

        credentials.Value = "top-secret-key";
        await runtime.TranslateSelectionAsync(
            ExternalTarget,
            CancellationToken.None);

        Assert.True(host.LastResult!.Success);
        Assert.DoesNotContain("top-secret-key", host.LastResult.Message);
        Assert.Equal(1, factory.CreateCount);
    }

    [Fact]
    public async Task SessionEmergencyAndCapturePauseBlockSelectionRoute()
    {
        var reader = new CountingSelectedTextReader();
        var host = new RecordingRouteHost
        {
            TranslateHandler = (_, _, _) => Task.FromResult(
                TranslationResponse.Ok(
                    "Hello",
                    "你好",
                    "en",
                    "Test provider"))
        };
        using var runtime = CreateRuntime(
            host,
            new MutableCredentialStore(),
            new RecordingProviderFactory(
                new StaticTranslationProvider("你好")),
            reader);
        runtime.ApplySettings(AppSettings.CreateDefault());

        runtime.SetSessionLocked(true);
        await runtime.TranslateSelectionAsync(
            ExternalTarget,
            CancellationToken.None);
        runtime.SetSessionLocked(false);

        runtime.SetEmergencyPaused(true);
        await runtime.TranslateSelectionAsync(
            ExternalTarget,
            CancellationToken.None);
        runtime.SetEmergencyPaused(false);

        runtime.PauseForCapture();
        await runtime.TranslateSelectionAsync(
            ExternalTarget,
            CancellationToken.None);
        runtime.ResumeAfterCapture();

        Assert.Equal(0, reader.ReadCount);
        Assert.Equal(0, host.TranslateCount);

        await runtime.TranslateSelectionAsync(
            ExternalTarget,
            CancellationToken.None);
        Assert.Equal(1, reader.ReadCount);
    }

    [Fact]
    public async Task SessionLockCancelsAnActiveSelectedTextRead()
    {
        var reader = new BlockingSelectedTextReader();
        var host = new RecordingRouteHost();
        using var runtime = CreateRuntime(
            host,
            new MutableCredentialStore(),
            new RecordingProviderFactory(
                new StaticTranslationProvider("你好")),
            reader);
        runtime.ApplySettings(AppSettings.CreateDefault());

        var translation = runtime.TranslateSelectionAsync(
            ExternalTarget,
            CancellationToken.None);
        await reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        runtime.SetSessionLocked(true);
        await translation.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(reader.WasCancelled);
        Assert.Null(host.LastResult);
        Assert.Null(host.LastError);
    }

    [Fact]
    public async Task NewSelectionCancelsThePreviousPendingRead()
    {
        var reader = new SequencedSelectedTextReader();
        var host = new RecordingRouteHost();
        using var runtime = CreateRuntime(
            host,
            new MutableCredentialStore(),
            new RecordingProviderFactory(
                new StaticTranslationProvider("你好")),
            reader);
        host.TranslateHandler = runtime.TranslateTextAsync;
        runtime.ApplySettings(AppSettings.CreateDefault());

        var first = runtime.TranslateSelectionAsync(
            ExternalTarget,
            CancellationToken.None);
        await reader.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = runtime.TranslateSelectionAsync(
            ExternalTarget,
            CancellationToken.None);

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(reader.FirstWasCancelled);
        Assert.Equal(2, reader.ReadCount);
        Assert.NotNull(host.LastResult);
        Assert.True(host.LastResult!.Success);
    }

    [Fact]
    public async Task EmergencyPauseCancelsAnInFlightProviderRequest()
    {
        var provider = new BlockingTranslationProvider();
        var host = new RecordingRouteHost();
        using var runtime = CreateRuntime(
            host,
            new MutableCredentialStore(),
            new RecordingProviderFactory(provider));
        host.TranslateHandler = runtime.TranslateTextAsync;
        runtime.ApplySettings(AppSettings.CreateDefault());

        var translation = runtime.TranslateSelectionAsync(
            ExternalTarget,
            CancellationToken.None);
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        runtime.SetEmergencyPaused(true);
        await translation.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(provider.WasCancelled);
        Assert.Null(host.LastResult);
        Assert.Null(host.LastError);
    }

    [Fact]
    public async Task CurrentExternalForegroundWinsOverLastExternalAndFallback()
    {
        var fallback = ExternalTarget with
        {
            Epoch = 1,
            TopLevelHwnd = (nint)0xA001,
            ExePath = @"C:\Apps\fallback.exe"
        };
        var current = ExternalTarget with
        {
            Epoch = 2,
            TopLevelHwnd = (nint)0xB001,
            ExePath = @"C:\Apps\current.exe"
        };
        var lastExternal = ExternalTarget with
        {
            Epoch = 3,
            TopLevelHwnd = (nint)0xC001,
            ExePath = @"C:\Apps\wheel.exe"
        };
        ForegroundSnapshot? readTarget = null;
        var reader = new DelegateSelectedTextReader((target, _) =>
        {
            readTarget = target;
            return Task.FromResult(SelectedTextReadResult.Ok("Hello"));
        });
        var host = new RecordingRouteHost
        {
            TranslateHandler = (_, _, _) => Task.FromResult(
                TranslationResponse.Ok(
                    "Hello",
                    "你好",
                    "en",
                    "Test provider"))
        };
        using var runtime = CreateRuntime(
            host,
            new MutableCredentialStore(),
            new RecordingProviderFactory(
                new StaticTranslationProvider("你好")),
            reader,
            currentForeground: () => current,
            lastExternal: () => lastExternal);
        runtime.ApplySettings(AppSettings.CreateDefault());

        await runtime.TranslateSelectionAsync(
            fallback,
            CancellationToken.None);

        Assert.Equal(current, readTarget);
        Assert.NotEqual(fallback, readTarget);
        Assert.NotEqual(lastExternal, readTarget);
    }

    [Fact]
    public async Task LastExternalWinsOverFallbackWhenCurrentIsOwnWindow()
    {
        var fallback = ExternalTarget with
        {
            Epoch = 1,
            TopLevelHwnd = (nint)0xA001,
            ExePath = @"C:\Apps\fallback.exe"
        };
        var current = ExternalTarget with
        {
            Epoch = 2,
            TopLevelHwnd = (nint)0xB001,
            ExePath = @"C:\Apps\self.exe",
            IsOwnInteractive = true
        };
        var lastExternal = ExternalTarget with
        {
            Epoch = 3,
            TopLevelHwnd = (nint)0xC001,
            ExePath = @"C:\Apps\wheel.exe"
        };
        ForegroundSnapshot? readTarget = null;
        var reader = new DelegateSelectedTextReader((target, _) =>
        {
            readTarget = target;
            return Task.FromResult(SelectedTextReadResult.Ok("Hello"));
        });
        using var runtime = CreateRuntime(
            new RecordingRouteHost(),
            new MutableCredentialStore(),
            new RecordingProviderFactory(
                new StaticTranslationProvider("你好")),
            reader,
            currentForeground: () => current,
            lastExternal: () => lastExternal);
        runtime.ApplySettings(AppSettings.CreateDefault());

        await runtime.TranslateSelectionAsync(
            fallback,
            CancellationToken.None);

        Assert.Equal(lastExternal, readTarget);
        Assert.NotEqual(fallback, readTarget);
    }

    [Fact]
    public async Task ProviderChangeDismissesActiveTranslationAndRejectsLateResult()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<TranslationResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new RecordingRouteHost
        {
            TranslateHandler = async (_, _, _) =>
            {
                entered.TrySetResult();
                return await release.Task;
            }
        };
        using var runtime = CreateRuntime(
            host,
            new MutableCredentialStore(),
            new RecordingProviderFactory(
                new StaticTranslationProvider("旧结果")));
        runtime.ApplySettings(AppSettings.CreateDefault());

        var translation = runtime.TranslateSelectionAsync(
            ExternalTarget,
            CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(host.IsOperationActive);
        Assert.Equal("正在翻译…", host.Statuses[^1]);

        runtime.ApplySettings(AppSettings.CreateDefault() with
        {
            Translation = TranslationSettings.CreateDefault() with
            {
                Provider = TranslationProviderKind.LibreTranslate,
                Endpoint = "https://example.test/translate"
            }
        });

        Assert.Equal(1, host.DismissCount);
        Assert.False(host.IsOperationActive);

        release.SetResult(TranslationResponse.Ok(
            "Hello",
            "不应显示",
            "en",
            "Late provider"));
        await translation.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, host.RejectedResultAttempts);
        Assert.Null(host.LastResult);
    }

    [Fact]
    public async Task OverlappingCapturePausesResumeOnlyAfterLastLease()
    {
        var reader = new CountingSelectedTextReader();
        var host = new RecordingRouteHost
        {
            TranslateHandler = (_, _, _) => Task.FromResult(
                TranslationResponse.Ok(
                    "Hello",
                    "你好",
                    "en",
                    "Test provider"))
        };
        using var runtime = CreateRuntime(
            host,
            new MutableCredentialStore(),
            new RecordingProviderFactory(
                new StaticTranslationProvider("你好")),
            reader);
        runtime.ApplySettings(AppSettings.CreateDefault());

        runtime.PauseForCapture();
        runtime.PauseForCapture();
        runtime.ResumeAfterCapture();

        await runtime.TranslateSelectionAsync(
            ExternalTarget,
            CancellationToken.None);
        Assert.Equal(0, reader.ReadCount);

        runtime.ResumeAfterCapture();
        await runtime.TranslateSelectionAsync(
            ExternalTarget,
            CancellationToken.None);
        Assert.Equal(1, reader.ReadCount);
    }

    [Fact]
    public async Task SessionLockClearsTranslationCacheAndDisplayUntilUnlock()
    {
        var provider = new StaticTranslationProvider("你好");
        var reader = new CountingSelectedTextReader();
        var host = new RecordingRouteHost();
        using var runtime = CreateRuntime(
            host,
            new MutableCredentialStore(),
            new RecordingProviderFactory(provider),
            reader);
        host.TranslateHandler = runtime.TranslateTextAsync;
        runtime.ApplySettings(AppSettings.CreateDefault());

        await runtime.TranslateSelectionAsync(
            ExternalTarget,
            CancellationToken.None);
        Assert.True(host.LastResult!.Success);
        Assert.Equal(1, provider.TranslateCount);
        Assert.Equal(1, reader.ReadCount);

        runtime.SetSessionLocked(true);

        Assert.Null(host.LastResult);
        Assert.Null(host.LastError);
        await runtime.TranslateSelectionAsync(
            ExternalTarget,
            CancellationToken.None);
        Assert.Equal(1, provider.TranslateCount);
        Assert.Equal(1, reader.ReadCount);

        runtime.SetSessionLocked(false);
        await runtime.TranslateSelectionAsync(
            ExternalTarget,
            CancellationToken.None);

        Assert.True(host.LastResult!.Success);
        Assert.Equal(2, provider.TranslateCount);
        Assert.Equal(2, reader.ReadCount);
    }

    [Fact]
    public async Task RegionRouteRecognizesSelectionThenUsesSharedRuntime()
    {
        var frame = new CaptureFrame(
            new PixelRect(0, 0, 4, 4),
            16,
            new byte[4 * 4 * 4]);
        var ocr = new RecordingOcr();
        var capture = new StubRegionCapture(
            new CapturedTranslationRegion(
                frame,
                new PixelRect(5, 6, 40, 20)));
        var host = new RecordingRouteHost();
        using var runtime = CreateRuntime(
            host,
            new MutableCredentialStore(),
            new RecordingProviderFactory(
                new StaticTranslationProvider("你好")),
            ocr: ocr,
            regionCapture: capture);
        host.TranslateHandler = runtime.TranslateTextAsync;
        runtime.ApplySettings(AppSettings.CreateDefault());

        await runtime.TranslateRegionAsync(CancellationToken.None);

        Assert.Equal(1, ocr.CallCount);
        Assert.True(host.LastResult!.Success);
        Assert.Equal("Recognized text", host.LastText);
        Assert.Contains("正在选择区域", host.Statuses);
        Assert.Contains("正在识别文字…", host.Statuses);
        Assert.Contains("正在翻译…", host.Statuses);
    }

    private static TranslationRuntime CreateRuntime(
        RecordingRouteHost host,
        MutableCredentialStore credentials,
        RecordingProviderFactory factory,
        ISelectedTextReader? reader = null,
        Func<ForegroundSnapshot>? currentForeground = null,
        Func<ForegroundSnapshot?>? lastExternal = null,
        IScreenOcrService? ocr = null,
        ITranslationRegionCapture? regionCapture = null) =>
        new(
            host,
            reader ?? new DelegateSelectedTextReader(
                (_, _) => Task.FromResult(
                    SelectedTextReadResult.Ok("Hello"))),
            factory,
            credentials,
            currentForeground ?? (() => ExternalTarget),
            lastExternal ?? (() => ExternalTarget),
            clock: null,
            ocr: ocr,
            regionCapture: regionCapture);

    private static AppSettings CustomProviderSettings() =>
        AppSettings.CreateDefault() with
        {
            Translation = TranslationSettings.CreateDefault() with
            {
                Provider = TranslationProviderKind.AzureTranslator,
                SourceLanguage = "en",
                Endpoint = "https://example.test/translate"
            }
        };

    private sealed class DelegateSelectedTextReader(
        Func<ForegroundSnapshot, CancellationToken,
            Task<SelectedTextReadResult>> read) : ISelectedTextReader
    {
        public Task<SelectedTextReadResult> ReadAsync(
            ForegroundSnapshot target,
            CancellationToken cancellationToken) =>
            read(target, cancellationToken);
    }

    private sealed class CountingSelectedTextReader : ISelectedTextReader
    {
        public int ReadCount { get; private set; }

        public Task<SelectedTextReadResult> ReadAsync(
            ForegroundSnapshot target,
            CancellationToken cancellationToken)
        {
            ReadCount++;
            return Task.FromResult(
                SelectedTextReadResult.Ok("Hello"));
        }
    }

    private sealed class BlockingSelectedTextReader : ISelectedTextReader
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool WasCancelled { get; private set; }

        public async Task<SelectedTextReadResult> ReadAsync(
            ForegroundSnapshot target,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            try
            {
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);
                return SelectedTextReadResult.Ok("Hello");
            }
            catch (OperationCanceledException)
            {
                WasCancelled = true;
                throw;
            }
        }
    }

    private sealed class SequencedSelectedTextReader : ISelectedTextReader
    {
        private int _readCount;

        public TaskCompletionSource FirstEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ReadCount => Volatile.Read(ref _readCount);
        public bool FirstWasCancelled { get; private set; }

        public async Task<SelectedTextReadResult> ReadAsync(
            ForegroundSnapshot target,
            CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _readCount);
            if (call > 1)
                return SelectedTextReadResult.Ok("Hello");

            FirstEntered.TrySetResult();
            try
            {
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);
                return SelectedTextReadResult.Ok("Hello");
            }
            catch (OperationCanceledException)
            {
                FirstWasCancelled = true;
                throw;
            }
        }
    }

    private sealed class RecordingRouteHost : ITranslationRouteHost
    {
        private int _nextOperationId;
        private TranslationOverlayOperationToken? _activeOperationToken;

        public Func<string, TranslationPurpose, CancellationToken,
            Task<TranslationResponse>> TranslateHandler { get; set; } =
            (_, _, _) => Task.FromResult(
                TranslationResponse.Fail(
                    "",
                    "",
                    "Test provider",
                    TranslationFailureKind.ProviderError));

        public List<string> Statuses { get; } = [];
        public string? LastText { get; private set; }
        public TranslationResponse? LastResult { get; private set; }
        public string? LastError { get; private set; }
        public int TranslateCount { get; private set; }
        public int DismissCount { get; private set; }
        public int RejectedResultAttempts { get; private set; }
        public bool IsOperationActive => _activeOperationToken is not null;

        public void ApplySettings(AppSettings settings)
        {
        }

        public TranslationRouteHandle ShowStatus(
            PixelPoint anchor,
            string status)
        {
            var operationToken =
                new TranslationOverlayOperationToken(++_nextOperationId);
            _activeOperationToken = operationToken;
            Statuses.Add(status);
            return new TranslationRouteHandle(operationToken, anchor);
        }

        public void ShowStatus(
            TranslationRouteHandle operation,
            string status)
        {
            if (!IsCurrent(operation))
                return;
            Statuses.Add(status);
        }

        public void ShowResult(
            TranslationRouteHandle operation,
            TranslationResponse response)
        {
            if (!IsCurrent(operation))
            {
                RejectedResultAttempts++;
                return;
            }
            LastResult = response;
        }

        public void ShowError(
            TranslationRouteHandle operation,
            string message)
        {
            if (!IsCurrent(operation))
                return;
            LastError = message;
        }

        public Task<TranslationResponse> TranslateAsync(
            string text,
            TranslationPurpose purpose,
            CancellationToken cancellationToken)
        {
            TranslateCount++;
            LastText = text;
            return TranslateHandler(text, purpose, cancellationToken);
        }

        public void DismissActiveOperation()
        {
            DismissCount++;
            _activeOperationToken = null;
            LastResult = null;
            LastError = null;
        }

        private bool IsCurrent(TranslationRouteHandle operation) =>
            _activeOperationToken is { } active &&
            operation.Operation.Equals(active);
    }

    private sealed class MutableCredentialStore : ITranslationCredentialStore
    {
        public string? Value { get; set; }

        public Task<bool> HasAsync(
            TranslationProviderKind provider,
            string endpoint,
            CancellationToken cancellationToken) =>
            Task.FromResult(Value is not null);

        public Task<string?> GetAsync(
            TranslationProviderKind provider,
            string endpoint,
            CancellationToken cancellationToken) =>
            Task.FromResult(Value);

        public Task SaveAsync(
            TranslationProviderKind provider,
            string endpoint,
            string apiKey,
            CancellationToken cancellationToken)
        {
            Value = apiKey;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(
            TranslationProviderKind provider,
            string endpoint,
            CancellationToken cancellationToken)
        {
            Value = null;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingProviderFactory(
        ITranslationProvider provider) : ITranslationProviderFactory
    {
        public int CreateCount { get; private set; }

        public ITranslationProvider Create(
            TranslationSettings settings,
            string? apiKey)
        {
            CreateCount++;
            return provider;
        }
    }

    private sealed class RecordingOcr : IScreenOcrService
    {
        public IReadOnlyList<OcrLanguageOption> AvailableLanguages { get; } =
            [new("en-US", "English")];
        public int CallCount { get; private set; }

        public Task<ScreenOcrResult> RecognizeAsync(
            CaptureFrame frame,
            string? languageTag,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(
                new ScreenOcrResult("Recognized text", null, null));
        }
    }

    private sealed class StubRegionCapture(
        CapturedTranslationRegion? result) : ITranslationRegionCapture
    {
        public Task<CapturedTranslationRegion?> CaptureSelectedAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class StaticTranslationProvider(
        string translatedText) : ITranslationProvider
    {
        private int _translateCount;

        public string Id => "test";
        public string DisplayName => "Test provider";
        public int TranslateCount => Volatile.Read(ref _translateCount);
        public TranslationProviderCapabilities Capabilities { get; } =
            new(
                SupportsAutoDetect: true,
                MaxCharacters: 10_000,
                MaxUtf8BytesPerRequest: 30_000,
                MaxChunks: 10,
                MaxConcurrentRequests: 1,
                MinimumRequestInterval: TimeSpan.Zero);

        public Task<TranslationProviderResult> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _translateCount);
            return Task.FromResult(
                TranslationProviderResult.Ok(
                    translatedText,
                    request.SourceLanguage));
        }
    }

    private sealed class BlockingTranslationProvider : ITranslationProvider
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool WasCancelled { get; private set; }
        public string Id => "blocking";
        public string DisplayName => "Blocking provider";
        public TranslationProviderCapabilities Capabilities { get; } =
            new(
                SupportsAutoDetect: true,
                MaxCharacters: 10_000,
                MaxUtf8BytesPerRequest: 30_000,
                MaxChunks: 10,
                MaxConcurrentRequests: 1,
                MinimumRequestInterval: TimeSpan.Zero);

        public async Task<TranslationProviderResult> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            try
            {
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);
                return TranslationProviderResult.Ok("你好");
            }
            catch (OperationCanceledException)
            {
                WasCancelled = true;
                throw;
            }
        }
    }
}
