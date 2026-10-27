using System.Collections.Concurrent;
using System.Text.Json;
using FloatDesk.App.Translation;
using FloatDesk.App.Views;
using FloatDesk.Core.Contracts;
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
    public async Task TranslationDiagnosticsReportBucketsWithoutContent()
    {
        var diagnostics =
            new List<(string EventName,
                IReadOnlyDictionary<string, object?>? Properties)>();
        var host = new RecordingRouteHost();
        using var runtime = CreateRuntime(
            host,
            new MutableCredentialStore(),
            new RecordingProviderFactory(
                new StaticTranslationProvider("你好")),
            diagnostics: (eventName, properties) =>
                diagnostics.Add((eventName, properties)));
        host.TranslateHandler = runtime.TranslateTextAsync;
        runtime.ApplySettings(AppSettings.CreateDefault());

        await runtime.TranslateSelectionAsync(
            ExternalTarget,
            CancellationToken.None);

        var entry = Assert.Single(diagnostics);
        Assert.Equal("translation.completed", entry.EventName);
        var serialized = JsonSerializer.Serialize(entry.Properties);
        Assert.Contains("characterBucket", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Hello", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("你好", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "apiKey",
            serialized,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ThrowingTranslationDiagnosticsDoNotChangeTranslationResult()
    {
        var host = new RecordingRouteHost();
        using var runtime = CreateRuntime(
            host,
            new MutableCredentialStore(),
            new RecordingProviderFactory(
                new StaticTranslationProvider("你好")),
            diagnostics: (_, _) =>
                throw new InvalidOperationException("diagnostics failed"));
        host.TranslateHandler = runtime.TranslateTextAsync;
        runtime.ApplySettings(AppSettings.CreateDefault());

        await runtime.TranslateSelectionAsync(
            ExternalTarget,
            CancellationToken.None);

        Assert.NotNull(host.LastResult);
        Assert.True(host.LastResult!.Success);
    }

    [Fact]
    public async Task CancelledTranslationDiagnosticsRemainContentFree()
    {
        var diagnostics =
            new List<(string EventName,
                IReadOnlyDictionary<string, object?>? Properties)>();
        var host = new RecordingRouteHost();
        using var runtime = CreateRuntime(
            host,
            new MutableCredentialStore(),
            new RecordingProviderFactory(
                new StaticTranslationProvider("你好")),
            diagnostics: (eventName, properties) =>
                diagnostics.Add((eventName, properties)));
        runtime.ApplySettings(AppSettings.CreateDefault());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var response = await runtime.TranslateTextAsync(
            "sensitive source",
            TranslationPurpose.Interactive,
            cancellation.Token);

        Assert.Equal(TranslationFailureKind.Cancelled, response.FailureKind);
        var entry = Assert.Single(diagnostics);
        var serialized = JsonSerializer.Serialize(entry.Properties);
        Assert.Contains("Cancelled", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "sensitive source",
            serialized,
            StringComparison.Ordinal);
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

    [Fact]
    public async Task SavedSubtitleRegionStartsSharedTranslationRoute()
    {
        var region = new TranslationCaptureRegion(
            "monitor-1",
            0.1,
            0.7,
            0.8,
            0.2);
        var capture = new SubtitleCaptureStub(region);
        var host = new RecordingRouteHost();
        using var runtime = CreateRuntime(
            host,
            new MutableCredentialStore(),
            new RecordingProviderFactory(
                new StaticTranslationProvider("字幕翻译")),
            ocr: new RecordingOcr(),
            regionCapture: capture);
        host.TranslateHandler = runtime.TranslateTextAsync;
        runtime.ApplySettings(SubtitleSettings(region));

        try
        {
            await runtime.ToggleSubtitlesAsync(CancellationToken.None);
            await host.TranslationAttempted.Task.WaitAsync(
                TimeSpan.FromSeconds(2));
        }
        finally
        {
            await runtime.StopSubtitlesAsync();
        }

        Assert.True(capture.RegionCaptureCount >= 1);
        Assert.Equal(TranslationPurpose.Subtitle, host.LastPurpose);
        Assert.Equal("Recognized text", host.LastText);
    }

    [Fact]
    public async Task SelectedTextRoutePausesSubtitleSamplingUntilCompletion()
    {
        var region = new TranslationCaptureRegion(
            "monitor-1",
            0.1,
            0.7,
            0.8,
            0.2);
        var capture = new SubtitleCaptureStub(region);
        var reader = new GatedSelectedTextReader();
        var host = new RecordingRouteHost();
        using var runtime = CreateRuntime(
            host,
            new MutableCredentialStore(),
            new RecordingProviderFactory(
                new StaticTranslationProvider("选中文字翻译")),
            reader,
            ocr: new RecordingOcr(),
            regionCapture: capture);
        host.TranslateHandler = runtime.TranslateTextAsync;
        runtime.ApplySettings(SubtitleSettings(region));

        try
        {
            await runtime.ToggleSubtitlesAsync(CancellationToken.None);
            var translation = runtime.TranslateSelectionAsync(
                ExternalTarget,
                CancellationToken.None);
            await reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(host.SubtitleControlsPaused);

            reader.Release.TrySetResult(
                SelectedTextReadResult.Ok("Hello"));
            await translation.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(host.SubtitleControlsPaused);

            runtime.OnInteractiveOperationDismissed();
            await WaitUntilAsync(
                () => !host.SubtitleControlsPaused,
                TimeSpan.FromSeconds(2));
        }
        finally
        {
            await runtime.StopSubtitlesAsync();
        }
    }

    [Fact]
    public async Task NestedCapturePauseDismissesActiveSelectionOverlay()
    {
        var reader = new GatedSelectedTextReader();
        var host = new RecordingRouteHost();
        using var runtime = CreateRuntime(
            host,
            new MutableCredentialStore(),
            new RecordingProviderFactory(
                new StaticTranslationProvider("选中文字翻译")),
            reader);
        host.TranslateHandler = runtime.TranslateTextAsync;
        runtime.ApplySettings(AppSettings.CreateDefault());

        var translation = runtime.TranslateSelectionAsync(
            ExternalTarget,
            CancellationToken.None);
        await reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        runtime.PauseForCapture();
        await translation.WaitAsync(TimeSpan.FromSeconds(2));
        runtime.ResumeAfterCapture();

        Assert.True(host.DismissCount >= 1);
        Assert.False(host.IsOperationActive);
    }

    [Fact]
    public async Task DismissingInteractiveOperationCancelsInFlightSelection()
    {
        var reader = new BlockingSelectedTextReader();
        var host = new RecordingRouteHost();
        using var runtime = CreateRuntime(
            host,
            new MutableCredentialStore(),
            new RecordingProviderFactory(
                new StaticTranslationProvider("选中文字翻译")),
            reader);
        host.TranslateHandler = runtime.TranslateTextAsync;
        runtime.ApplySettings(AppSettings.CreateDefault());

        var translation = runtime.TranslateSelectionAsync(
            ExternalTarget,
            CancellationToken.None);
        await reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        runtime.OnInteractiveOperationDismissed();
        await translation.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(reader.WasCancelled);
        Assert.Equal(0, host.TranslateCount);
    }

    [Fact]
    public async Task DismissingOverlappingInteractiveOperationDoesNotResumeSubtitlesDuringCapture()
    {
        var region = new TranslationCaptureRegion(
            "monitor-1",
            0.1,
            0.7,
            0.8,
            0.2);
        var capture = new SubtitleCaptureStub(region);
        var reader = new QueuedGatedSelectedTextReader(2);
        var host = new RecordingRouteHost();
        using var runtime = CreateRuntime(
            host,
            new MutableCredentialStore(),
            new RecordingProviderFactory(
                new StaticTranslationProvider("选中文字翻译")),
            reader,
            ocr: new RecordingOcr(),
            regionCapture: capture);
        host.TranslateHandler = runtime.TranslateTextAsync;
        runtime.ApplySettings(SubtitleSettings(region));

        try
        {
            await runtime.ToggleSubtitlesAsync(CancellationToken.None);
            await host.TranslationAttempted.Task.WaitAsync(
                TimeSpan.FromSeconds(2));
            var first = runtime.TranslateSelectionAsync(
                ExternalTarget,
                CancellationToken.None);
            await reader.WaitEnteredAsync(0, TimeSpan.FromSeconds(2));
            reader.Release(0, SelectedTextReadResult.Ok("Hello"));
            await first.WaitAsync(TimeSpan.FromSeconds(2));

            var second = runtime.TranslateSelectionAsync(
                ExternalTarget,
                CancellationToken.None);
            await reader.WaitEnteredAsync(1, TimeSpan.FromSeconds(2));

            host.DismissActiveOperation();
            runtime.OnInteractiveOperationDismissed();
            var pausedAfterDismissal = host.SubtitleControlsPaused;

            reader.Release(1, SelectedTextReadResult.Ok("Hello"));
            await second.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(pausedAfterDismissal);
            await WaitUntilAsync(
                () => !host.SubtitleControlsPaused,
                TimeSpan.FromSeconds(2));
        }
        finally
        {
            await runtime.StopSubtitlesAsync();
        }
    }

    [Fact]
    public async Task CapturePauseResumesSubtitlesWhileSubtitleOperationRemainsActive()
    {
        var region = new TranslationCaptureRegion(
            "monitor-1",
            0.1,
            0.7,
            0.8,
            0.2);
        var capture = new SubtitleCaptureStub(region);
        var host = new RecordingRouteHost();
        using var runtime = CreateRuntime(
            host,
            new MutableCredentialStore(),
            new RecordingProviderFactory(
                new StaticTranslationProvider("字幕翻译")),
            ocr: new RecordingOcr(),
            regionCapture: capture);
        host.TranslateHandler = runtime.TranslateTextAsync;
        runtime.ApplySettings(SubtitleSettings(region));
        var stateChanges = new ConcurrentQueue<(bool Running, bool Paused)>();
        runtime.SubtitleStateChanged += () =>
            stateChanges.Enqueue(
                (runtime.IsSubtitleRunning, runtime.IsSubtitlePaused));

        try
        {
            await runtime.ToggleSubtitlesAsync(CancellationToken.None);
            await host.TranslationAttempted.Task.WaitAsync(
                TimeSpan.FromSeconds(2));

            runtime.PauseForCapture();
            Assert.True(host.SubtitleControlsPaused);
            Assert.True(host.IsOperationActive);
            Assert.Contains((true, true), stateChanges);
            stateChanges.Clear();

            runtime.ResumeAfterCapture();

            Assert.False(host.SubtitleControlsPaused);
            Assert.True(host.IsOperationActive);
            Assert.Contains((true, false), stateChanges);
        }
        finally
        {
            await runtime.StopSubtitlesAsync();
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task ProviderReconfigureStopsAllSubtitleLanesAndDismissesCurrentToken()
    {
        var region = new TranslationCaptureRegion(
            "monitor-1",
            0.1,
            0.7,
            0.8,
            0.2);
        var provider = new BlockingTranslationProvider();
        var capture = new SubtitleCaptureStub(region);
        var ocr = new RecordingOcr();
        var host = new RecordingRouteHost();
        using var runtime = CreateRuntime(
            host,
            new MutableCredentialStore(),
            new RecordingProviderFactory(provider),
            ocr: ocr,
            regionCapture: capture);
        host.TranslateHandler = runtime.TranslateTextAsync;
        runtime.ApplySettings(SubtitleSettings(region));

        try
        {
            await runtime.ToggleSubtitlesAsync(CancellationToken.None);
            await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            runtime.ApplySettings(SubtitleSettings(region) with
            {
                Translation = SubtitleSettings(region).Translation with
                {
                    Provider = TranslationProviderKind.LibreTranslate,
                    Endpoint = "https://example.test/translate"
                }
            });

            await WaitUntilAsync(
                () => provider.WasCancelled &&
                      !host.SubtitleControlsActive &&
                      !host.IsOperationActive,
                TimeSpan.FromSeconds(2));
            Assert.True(host.DismissCount > 0);
            Assert.Empty(host.SubtitleTexts);

            await Task.Delay(350);
            var captureCount = capture.RegionCaptureCount;
            var ocrCount = ocr.CallCount;
            await Task.Delay(350);
            Assert.Equal(captureCount, capture.RegionCaptureCount);
            Assert.Equal(ocrCount, ocr.CallCount);
        }
        finally
        {
            await runtime.StopSubtitlesAsync();
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task ProviderReconfigureStopWaitsForInFlightOcrCleanup()
    {
        var region = new TranslationCaptureRegion(
            "monitor-1",
            0.1,
            0.7,
            0.8,
            0.2);
        var ocr = new ControlledOcr("Recognized text");
        var capture = new SubtitleCaptureStub(region);
        var host = new RecordingRouteHost();
        using var runtime = CreateRuntime(
            host,
            new MutableCredentialStore(),
            new RecordingProviderFactory(
                new StaticTranslationProvider("字幕翻译")),
            ocr: ocr,
            regionCapture: capture);
        host.TranslateHandler = runtime.TranslateTextAsync;
        runtime.ApplySettings(SubtitleSettings(region));

        try
        {
            await runtime.ToggleSubtitlesAsync(CancellationToken.None);
            await ocr.FirstCallEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(2));

            runtime.ApplySettings(SubtitleSettings(region) with
            {
                Translation = SubtitleSettings(region).Translation with
                {
                    Provider = TranslationProviderKind.LibreTranslate,
                    Endpoint = "https://example.test/translate"
                }
            });
            await ocr.FirstCallCancelled.Task.WaitAsync(
                TimeSpan.FromSeconds(2));

            var stop = runtime.StopSubtitlesAsync();
            await Task.Delay(100);
            Assert.False(stop.IsCompleted);
            Assert.False(ocr.FirstCallCompleted.Task.IsCompleted);

            ocr.ReleaseFirstCallCancellation();
            await ocr.FirstCallCompleted.Task.WaitAsync(
                TimeSpan.FromSeconds(2));
            await stop.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(host.IsOperationActive);
        }
        finally
        {
            ocr.ReleaseFirstCallCancellation();
            await runtime.StopSubtitlesAsync();
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task ProviderReconfigureWaitsForRetiredProviderCleanup()
    {
        var region = new TranslationCaptureRegion(
            "monitor-1",
            0.1,
            0.7,
            0.8,
            0.2);
        var provider = new ControlledTranslationProvider("字幕翻译");
        var host = new RecordingRouteHost();
        using var runtime = CreateRuntime(
            host,
            new MutableCredentialStore(),
            new RecordingProviderFactory(provider),
            ocr: new RecordingOcr(),
            regionCapture: new SubtitleCaptureStub(region));
        host.TranslateHandler = runtime.TranslateTextAsync;
        runtime.ApplySettings(SubtitleSettings(region));

        try
        {
            await runtime.ToggleSubtitlesAsync(CancellationToken.None);
            await provider.FirstCallEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(2));

            runtime.ApplySettings(SubtitleSettings(region) with
            {
                Translation = SubtitleSettings(region).Translation with
                {
                    Provider = TranslationProviderKind.LibreTranslate,
                    Endpoint = "https://example.test/translate"
                }
            });
            await provider.FirstCallCancelled.Task.WaitAsync(
                TimeSpan.FromSeconds(2));

            var stop = runtime.StopSubtitlesAsync();
            await Task.Delay(100);
            Assert.False(stop.IsCompleted);
            Assert.False(provider.FirstCallCompleted.Task.IsCompleted);

            provider.ReleaseFirstCallCancellation();
            await provider.FirstCallCompleted.Task.WaitAsync(
                TimeSpan.FromSeconds(2));
            await stop.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            provider.ReleaseFirstCallCancellation();
            await runtime.StopSubtitlesAsync();
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task ToggleSubtitlesWaitsForPendingProviderCleanup()
    {
        var region = new TranslationCaptureRegion(
            "monitor-1",
            0.1,
            0.7,
            0.8,
            0.2);
        var provider = new ControlledTranslationProvider("字幕翻译");
        var host = new RecordingRouteHost();
        using var runtime = CreateRuntime(
            host,
            new MutableCredentialStore(),
            new RecordingProviderFactory(provider),
            ocr: new RecordingOcr(),
            regionCapture: new SubtitleCaptureStub(region));
        host.TranslateHandler = runtime.TranslateTextAsync;
        runtime.ApplySettings(SubtitleSettings(region));

        try
        {
            await runtime.ToggleSubtitlesAsync(CancellationToken.None);
            await provider.FirstCallEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(2));

            runtime.ApplySettings(SubtitleSettings(region) with
            {
                Translation = SubtitleSettings(region).Translation with
                {
                    Provider = TranslationProviderKind.LibreTranslate,
                    Endpoint = "https://example.test/translate"
                }
            });
            await provider.FirstCallCancelled.Task.WaitAsync(
                TimeSpan.FromSeconds(2));

            var toggle = runtime.ToggleSubtitlesAsync(
                CancellationToken.None);
            await Task.Delay(150);
            Assert.False(toggle.IsCompleted);
            Assert.False(provider.SecondCallEntered.Task.IsCompleted);

            provider.ReleaseFirstCallCancellation();
            await provider.FirstCallCompleted.Task.WaitAsync(
                TimeSpan.FromSeconds(2));
            await toggle.WaitAsync(TimeSpan.FromSeconds(2));
            await provider.SecondCallEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(2));
            Assert.True(runtime.IsSubtitleRunning);

            await runtime.StopSubtitlesAsync();
            Assert.False(runtime.IsSubtitleRunning);
        }
        finally
        {
            provider.ReleaseFirstCallCancellation();
            await runtime.StopSubtitlesAsync();
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task SubtitlePipelinePausesForCaptureAndResumesOnlyAfterLastLease()
    {
        var region = new TranslationCaptureRegion(
            "monitor-1",
            0.1,
            0.7,
            0.8,
            0.2);
        var capture = new ControlledSubtitleCapture(region);
        var ocr = new RecordingOcr();
        var host = new RecordingRouteHost();
        using var runtime = CreateRuntime(
            host,
            new MutableCredentialStore(),
            new RecordingProviderFactory(
                new StaticTranslationProvider("字幕翻译")),
            ocr: ocr,
            regionCapture: capture);
        host.TranslateHandler = runtime.TranslateTextAsync;
        runtime.ApplySettings(SubtitleSettings(region));

        try
        {
            await runtime.ToggleSubtitlesAsync(CancellationToken.None);
            await capture.FirstCallEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(2));
            Assert.Equal(0, ocr.CallCount);

            var firstPause = runtime.PauseForCaptureAsync();
            var lastPause = runtime.PauseForCaptureAsync();
            await capture.FirstCallCancelled.Task.WaitAsync(
                TimeSpan.FromSeconds(2));
            await Task.Delay(100);
            Assert.False(firstPause.IsCompleted);
            Assert.False(lastPause.IsCompleted);
            Assert.True(runtime.IsSubtitlePaused);
            Assert.Equal(0, ocr.CallCount);
            var pausedCaptureCount = capture.RegionCaptureCount;

            Assert.False(capture.FirstCallCompleted.Task.IsCompleted);

            capture.ReleaseFirstCallCancellation();
            await capture.FirstCallCompleted.Task.WaitAsync(
                TimeSpan.FromSeconds(2));
            await Task.WhenAll(firstPause, lastPause)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, ocr.CallCount);

            runtime.ResumeAfterCapture();
            await Task.Delay(200);
            Assert.True(runtime.IsSubtitlePaused);
            Assert.Equal(
                pausedCaptureCount,
                capture.RegionCaptureCount);

            runtime.ResumeAfterCapture();
            await WaitUntilAsync(
                () => !runtime.IsSubtitlePaused,
                TimeSpan.FromSeconds(2));
            await WaitUntilAsync(
                () => capture.RegionCaptureCount > pausedCaptureCount,
                TimeSpan.FromSeconds(2));
            await host.TranslationAttempted.Task.WaitAsync(
                TimeSpan.FromSeconds(2));
        }
        finally
        {
            capture.ReleaseFirstCallCancellation();
            await runtime.StopSubtitlesAsync();
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task CapturePauseCancelsInFlightOcrBeforeFinalResume()
    {
        var region = new TranslationCaptureRegion(
            "monitor-1",
            0.1,
            0.7,
            0.8,
            0.2);
        var capture = new SubtitleCaptureStub(region);
        var ocr = new ControlledOcr("Recognized text");
        var host = new RecordingRouteHost();
        using var runtime = CreateRuntime(
            host,
            new MutableCredentialStore(),
            new RecordingProviderFactory(
                new StaticTranslationProvider("字幕翻译")),
            ocr: ocr,
            regionCapture: capture);
        host.TranslateHandler = runtime.TranslateTextAsync;
        runtime.ApplySettings(SubtitleSettings(region));

        try
        {
            await runtime.ToggleSubtitlesAsync(CancellationToken.None);
            await ocr.FirstCallEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(2));
            Assert.Equal(0, host.TranslateCount);

            var firstPause = runtime.PauseForCaptureAsync();
            var lastPause = runtime.PauseForCaptureAsync();
            await ocr.FirstCallCancelled.Task.WaitAsync(
                TimeSpan.FromSeconds(2));
            await Task.Delay(100);
            Assert.False(firstPause.IsCompleted);
            Assert.False(lastPause.IsCompleted);
            Assert.True(runtime.IsSubtitlePaused);
            var pausedOcrCount = ocr.CallCount;

            Assert.False(ocr.FirstCallCompleted.Task.IsCompleted);

            ocr.ReleaseFirstCallCancellation();
            await ocr.FirstCallCompleted.Task.WaitAsync(
                TimeSpan.FromSeconds(2));
            await Task.WhenAll(firstPause, lastPause)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, host.TranslateCount);

            runtime.ResumeAfterCapture();
            await Task.Delay(200);
            Assert.True(runtime.IsSubtitlePaused);
            Assert.Equal(pausedOcrCount, ocr.CallCount);

            runtime.ResumeAfterCapture();
            await WaitUntilAsync(
                () => !runtime.IsSubtitlePaused,
                TimeSpan.FromSeconds(2));
            await WaitUntilAsync(
                () => ocr.CallCount > pausedOcrCount,
                TimeSpan.FromSeconds(2));
            await host.TranslationAttempted.Task.WaitAsync(
                TimeSpan.FromSeconds(2));
        }
        finally
        {
            ocr.ReleaseFirstCallCancellation();
            await runtime.StopSubtitlesAsync();
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task CapturePauseCancelsInFlightTranslationBeforeFinalResume()
    {
        var region = new TranslationCaptureRegion(
            "monitor-1",
            0.1,
            0.7,
            0.8,
            0.2);
        var provider = new ControlledTranslationProvider("字幕翻译");
        var capture = new SubtitleCaptureStub(region);
        var host = new RecordingRouteHost();
        using var runtime = CreateRuntime(
            host,
            new MutableCredentialStore(),
            new RecordingProviderFactory(provider),
            ocr: new RecordingOcr(),
            regionCapture: capture);
        host.TranslateHandler = runtime.TranslateTextAsync;
        runtime.ApplySettings(SubtitleSettings(region));

        try
        {
            await runtime.ToggleSubtitlesAsync(CancellationToken.None);
            await provider.FirstCallEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(2));

            var firstPause = runtime.PauseForCaptureAsync();
            var lastPause = runtime.PauseForCaptureAsync();
            await provider.FirstCallCancelled.Task.WaitAsync(
                TimeSpan.FromSeconds(2));
            await Task.Delay(100);
            Assert.False(firstPause.IsCompleted);
            Assert.False(lastPause.IsCompleted);
            Assert.True(runtime.IsSubtitlePaused);

            Assert.False(provider.FirstCallCompleted.Task.IsCompleted);

            provider.ReleaseFirstCallCancellation();
            await provider.FirstCallCompleted.Task.WaitAsync(
                TimeSpan.FromSeconds(2));
            await Task.WhenAll(firstPause, lastPause)
                .WaitAsync(TimeSpan.FromSeconds(2));

            runtime.ResumeAfterCapture();
            await Task.Delay(200);
            Assert.True(runtime.IsSubtitlePaused);

            runtime.ResumeAfterCapture();
            await WaitUntilAsync(
                () => !runtime.IsSubtitlePaused,
                TimeSpan.FromSeconds(2));
            await provider.SecondCallEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(2));
            Assert.Equal(2, provider.CallCount);
        }
        finally
        {
            provider.ReleaseFirstCallCancellation();
            await runtime.StopSubtitlesAsync();
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task SessionLockCancelsPendingSubtitleWorkWithoutPublishingLateText()
    {
        var region = new TranslationCaptureRegion(
            "monitor-1",
            0.1,
            0.7,
            0.8,
            0.2);
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<TranslationResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = new SubtitleCaptureStub(region);
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
                new StaticTranslationProvider("字幕翻译")),
            ocr: new RecordingOcr(),
            regionCapture: capture);
        runtime.ApplySettings(SubtitleSettings(region));

        try
        {
            await runtime.ToggleSubtitlesAsync(CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            runtime.SetSessionLocked(true);
            release.SetResult(TranslationResponse.Ok(
                "Recognized text",
                "不应显示",
                "en",
                "Late provider"));

            await WaitUntilAsync(
                () => !host.SubtitleControlsActive,
                TimeSpan.FromSeconds(2));
            Assert.DoesNotContain("不应显示", host.SubtitleTexts);
        }
        finally
        {
            runtime.SetSessionLocked(false);
            await runtime.StopSubtitlesAsync();
        }
    }

    [Fact(Timeout = 10_000)]
    public async Task EmergencyPauseCancelsPendingSubtitleWorkWithoutPublishingLateText()
    {
        var region = new TranslationCaptureRegion(
            "monitor-1",
            0.1,
            0.7,
            0.8,
            0.2);
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<TranslationResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = new SubtitleCaptureStub(region);
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
                new StaticTranslationProvider("字幕翻译")),
            ocr: new RecordingOcr(),
            regionCapture: capture);
        runtime.ApplySettings(SubtitleSettings(region));

        try
        {
            await runtime.ToggleSubtitlesAsync(CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

            runtime.SetEmergencyPaused(true);
            release.SetResult(TranslationResponse.Ok(
                "Recognized text",
                "不应显示",
                "en",
                "Late provider"));

            await WaitUntilAsync(
                () => !host.SubtitleControlsActive,
                TimeSpan.FromSeconds(2));
            Assert.DoesNotContain("不应显示", host.SubtitleTexts);
        }
        finally
        {
            runtime.SetEmergencyPaused(false);
            await runtime.StopSubtitlesAsync();
        }
    }

    [Fact]
    public async Task InvalidSelectionTargetReleasesInteractiveCaptureOwnership()
    {
        var ownTarget = ExternalTarget with { IsOwnInteractive = true };
        var host = new RecordingRouteHost();
        using var runtime = CreateRuntime(
            host,
            new MutableCredentialStore(),
            new RecordingProviderFactory(
                new StaticTranslationProvider("选中文字翻译")),
            currentForeground: () => ownTarget,
            lastExternal: () => null);
        runtime.ApplySettings(AppSettings.CreateDefault());

        await runtime.TranslateSelectionAsync(
            ownTarget,
            CancellationToken.None);
        host.DismissActiveOperation();
        var dismissCount = host.DismissCount;

        runtime.PauseForCapture();

        Assert.Equal(dismissCount, host.DismissCount);
        runtime.ResumeAfterCapture();
    }

    [Fact]
    public async Task FirstSubtitleStartSelectsAndPersistsRegion()
    {
        var initial = AppSettings.CreateDefault();
        var settings = new MutableSettingsCoordinator(initial);
        var selectedRegion = new TranslationCaptureRegion(
            "monitor-2",
            0.2,
            0.75,
            0.6,
            0.18);
        var capture = new SubtitleCaptureStub(selectedRegion);
        var host = new RecordingRouteHost();
        var factory = new RecordingProviderFactory(
            new StaticTranslationProvider("字幕翻译"));
        using var runtime = CreateRuntime(
            host,
            new MutableCredentialStore(),
            factory,
            ocr: new RecordingOcr(),
            regionCapture: capture,
            settingsCoordinator: settings);
        host.TranslateHandler = runtime.TranslateTextAsync;
        runtime.ApplySettings(initial);

        try
        {
            await runtime.ToggleSubtitlesAsync(CancellationToken.None);
            await host.TranslationAttempted.Task.WaitAsync(
                TimeSpan.FromSeconds(2));
        }
        finally
        {
            await runtime.StopSubtitlesAsync();
        }

        Assert.Equal(1, capture.SelectionCount);
        Assert.True(capture.RegionCaptureCount >= 1);
        Assert.Equal(
            selectedRegion,
            settings.Current.Translation.SubtitleRegion);
        Assert.Equal(1, factory.CreateCount);
    }

    [Fact]
    public async Task ShortcutSubtitleStartSelectsRegionEvenWhenRegionIsSaved()
    {
        var savedRegion = new TranslationCaptureRegion(
            "monitor-1",
            0.1,
            0.7,
            0.8,
            0.2);
        var selectedRegion = new TranslationCaptureRegion(
            "monitor-2",
            0.2,
            0.75,
            0.6,
            0.18);
        var initial = SubtitleSettings(savedRegion);
        var settings = new MutableSettingsCoordinator(initial);
        var capture = new SubtitleCaptureStub(selectedRegion);
        var host = new RecordingRouteHost();
        using var runtime = CreateRuntime(
            host,
            new MutableCredentialStore(),
            new RecordingProviderFactory(
                new StaticTranslationProvider("字幕翻译")),
            ocr: new RecordingOcr(),
            regionCapture: capture,
            settingsCoordinator: settings);
        host.TranslateHandler = runtime.TranslateTextAsync;
        runtime.ApplySettings(initial);

        try
        {
            await runtime.ToggleSubtitlesAsync(
                CancellationToken.None,
                selectRegionOnStart: true);
            await host.TranslationAttempted.Task.WaitAsync(
                TimeSpan.FromSeconds(2));
        }
        finally
        {
            await runtime.StopSubtitlesAsync();
        }

        Assert.Equal(1, capture.SelectionCount);
        Assert.Equal(
            selectedRegion,
            settings.Current.Translation.SubtitleRegion);
    }

    private static TranslationRuntime CreateRuntime(
        RecordingRouteHost host,
        MutableCredentialStore credentials,
        RecordingProviderFactory factory,
        ISelectedTextReader? reader = null,
        Func<ForegroundSnapshot>? currentForeground = null,
        Func<ForegroundSnapshot?>? lastExternal = null,
        IScreenOcrService? ocr = null,
        ITranslationRegionCapture? regionCapture = null,
        ISettingsCoordinator? settingsCoordinator = null,
        Action<string, IReadOnlyDictionary<string, object?>?>?
            diagnostics = null) =>
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
            regionCapture: regionCapture,
            settingsCoordinator: settingsCoordinator,
            diagnostics: diagnostics);

    private static AppSettings SubtitleSettings(
        TranslationCaptureRegion region) =>
        AppSettings.CreateDefault() with
        {
            Translation = TranslationSettings.CreateDefault() with
            {
                SubtitleRefreshIntervalMs = 300,
                SubtitleRegion = region
            }
        };

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

    private sealed class GatedSelectedTextReader : ISelectedTextReader
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<SelectedTextReadResult> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<SelectedTextReadResult> ReadAsync(
            ForegroundSnapshot target,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            return await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class QueuedGatedSelectedTextReader : ISelectedTextReader
    {
        private readonly TaskCompletionSource[] _entered;
        private readonly TaskCompletionSource<SelectedTextReadResult>[] _release;

        public QueuedGatedSelectedTextReader(int count)
        {
            _entered = Enumerable.Range(0, count)
                .Select(_ => new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously))
                .ToArray();
            _release = Enumerable.Range(0, count)
                .Select(_ => new TaskCompletionSource<SelectedTextReadResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously))
                .ToArray();
        }

        private int CallCount { get; set; }

        public Task WaitEnteredAsync(int call, TimeSpan timeout) =>
            _entered[call].Task.WaitAsync(timeout);

        public void Release(int call, SelectedTextReadResult result) =>
            _release[call].TrySetResult(result);

        public async Task<SelectedTextReadResult> ReadAsync(
            ForegroundSnapshot target,
            CancellationToken cancellationToken)
        {
            var call = CallCount++;
            _entered[call].TrySetResult();
            return await _release[call].Task;
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
        public TranslationPurpose? LastPurpose { get; private set; }
        public TranslationResponse? LastResult { get; private set; }
        public TaskCompletionSource TranslationAttempted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? LastError { get; private set; }
        public int TranslateCount { get; private set; }
        public int DismissCount { get; private set; }
        public int RejectedResultAttempts { get; private set; }
        public bool IsOperationActive => _activeOperationToken is not null;
        public bool SubtitleControlsActive { get; private set; }
        public bool SubtitleControlsPaused { get; private set; }
        public List<string> SubtitleTexts { get; } = [];
        public List<string> SubtitleNotices { get; } = [];

        public bool HasActiveOperation() =>
            IsOperationActive;

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

        public TranslationSubtitlePlacementStatus ShowSubtitleText(
            TranslationRouteHandle operation,
            string translatedText)
        {
            if (!IsCurrent(operation))
                return TranslationSubtitlePlacementStatus.Ready;
            SubtitleTexts.Add(translatedText);
            return TranslationSubtitlePlacementStatus.Ready;
        }

        public void ShowSubtitleNotice(
            TranslationRouteHandle operation,
            string notice)
        {
            if (IsCurrent(operation))
                SubtitleNotices.Add(notice);
        }

        public Task<TranslationResponse> TranslateAsync(
            string text,
            TranslationPurpose purpose,
            CancellationToken cancellationToken)
        {
            TranslateCount++;
            LastText = text;
            LastPurpose = purpose;
            TranslationAttempted.TrySetResult();
            return TranslateHandler(text, purpose, cancellationToken);
        }

        public void DismissActiveOperation()
        {
            DismissCount++;
            _activeOperationToken = null;
            LastResult = null;
            LastError = null;
        }

        public void DismissOperation(TranslationRouteHandle operation)
        {
            if (IsCurrent(operation))
                DismissActiveOperation();
        }

        public void SetSubtitleControls(
            TranslationRouteHandle operation,
            bool active,
            bool paused,
            bool canReselect)
        {
            SubtitleControlsActive = active;
            if (active)
                SubtitleControlsPaused = paused;
            else
            {
                SubtitleControlsPaused = false;
                SubtitleTexts.Clear();
                SubtitleNotices.Clear();
                DismissActiveOperation();
            }
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

    private sealed class ControlledOcr(string text) : IScreenOcrService
    {
        private readonly TaskCompletionSource _releaseFirstCall =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public IReadOnlyList<OcrLanguageOption> AvailableLanguages { get; } =
            [new("en-US", "English")];
        public int CallCount => Volatile.Read(ref _callCount);
        public TaskCompletionSource FirstCallEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstCallCancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstCallCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ScreenOcrResult> RecognizeAsync(
            CaptureFrame frame,
            string? languageTag,
            CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _callCount);
            if (call == 1)
            {
                FirstCallEntered.TrySetResult();
                try
                {
                    await Task.Delay(
                        Timeout.InfiniteTimeSpan,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    FirstCallCancelled.TrySetResult();
                    await _releaseFirstCall.Task;
                    FirstCallCompleted.TrySetResult();
                    throw;
                }
            }

            return new ScreenOcrResult(text, null, null);
        }

        public void ReleaseFirstCallCancellation() =>
            _releaseFirstCall.TrySetResult();
    }

    private sealed class StubRegionCapture(
        CapturedTranslationRegion? result) : ITranslationRegionCapture
    {
        public Task<CapturedTranslationRegion?> CaptureSelectedAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(result);

        public Task<CapturedTranslationRegion?> CaptureRegionAsync(
            TranslationCaptureRegion region,
            CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class SubtitleCaptureStub(
        TranslationCaptureRegion region) : ITranslationRegionCapture
    {
        private int _selectionCount;
        private int _regionCaptureCount;

        public int SelectionCount => Volatile.Read(ref _selectionCount);
        public int RegionCaptureCount =>
            Volatile.Read(ref _regionCaptureCount);

        public Task<CapturedTranslationRegion?> CaptureSelectedAsync(
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _selectionCount);
            return Task.FromResult<CapturedTranslationRegion?>(
                new CapturedTranslationRegion(
                    NewFrame(),
                    new PixelRect(0, 0, 4, 4),
                    region));
        }

        public Task<CapturedTranslationRegion?> CaptureRegionAsync(
            TranslationCaptureRegion requested,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _regionCaptureCount);
            Assert.Equal(region, requested);
            return Task.FromResult<CapturedTranslationRegion?>(
                new CapturedTranslationRegion(
                    NewFrame(),
                    new PixelRect(0, 0, 4, 4),
                    requested));
        }

        private static CaptureFrame NewFrame() =>
            new(
                new PixelRect(0, 0, 4, 4),
                16,
                new byte[4 * 4 * 4]);
    }

    private sealed class ControlledSubtitleCapture(
        TranslationCaptureRegion region) : ITranslationRegionCapture
    {
        private readonly TaskCompletionSource _releaseFirstCall =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _regionCaptureCount;

        public int RegionCaptureCount =>
            Volatile.Read(ref _regionCaptureCount);
        public TaskCompletionSource FirstCallEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstCallCancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstCallCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<CapturedTranslationRegion?> CaptureSelectedAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<CapturedTranslationRegion?>(null);

        public async Task<CapturedTranslationRegion?>
            CaptureRegionAsync(
                TranslationCaptureRegion requested,
                CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _regionCaptureCount);
            Assert.Equal(region, requested);
            if (call == 1)
            {
                FirstCallEntered.TrySetResult();
                try
                {
                    await Task.Delay(
                        Timeout.InfiniteTimeSpan,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    FirstCallCancelled.TrySetResult();
                    await _releaseFirstCall.Task;
                    FirstCallCompleted.TrySetResult();
                    throw;
                }
            }

            return new CapturedTranslationRegion(
                NewFrame(),
                new PixelRect(0, 0, 4, 4),
                requested);
        }

        public void ReleaseFirstCallCancellation() =>
            _releaseFirstCall.TrySetResult();

        private static CaptureFrame NewFrame() =>
            new(
                new PixelRect(0, 0, 4, 4),
                16,
                new byte[4 * 4 * 4]);
    }

    private sealed class MutableSettingsCoordinator(
        AppSettings initial) : ISettingsCoordinator
    {
        public AppSettings Current { get; private set; } = initial;
        public event Action<AppSettings>? Changed;

        public Task InitializeAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<OperationResult> UpdateAsync(
            Func<AppSettings, AppSettings> update,
            CancellationToken cancellationToken)
        {
            Current = update(Current);
            Changed?.Invoke(Current);
            return Task.FromResult(OperationResult.Ok());
        }
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

    private sealed class ControlledTranslationProvider(
        string translatedText) : ITranslationProvider
    {
        private readonly TaskCompletionSource _releaseFirstCall =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public string Id => "controlled";
        public string DisplayName => "Controlled provider";
        public int CallCount => Volatile.Read(ref _callCount);
        public TaskCompletionSource FirstCallEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstCallCancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstCallCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondCallEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
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
            var call = Interlocked.Increment(ref _callCount);
            if (call == 1)
            {
                FirstCallEntered.TrySetResult();
                try
                {
                    await Task.Delay(
                        Timeout.InfiniteTimeSpan,
                        cancellationToken);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    FirstCallCancelled.TrySetResult();
                    await _releaseFirstCall.Task;
                    FirstCallCompleted.TrySetResult();
                    throw;
                }
            }
            else
            {
                SecondCallEntered.TrySetResult();
            }

            return TranslationProviderResult.Ok(
                translatedText,
                request.SourceLanguage);
        }

        public void ReleaseFirstCallCancellation() =>
            _releaseFirstCall.TrySetResult();
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

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Condition was not reached.");
            await Task.Delay(10);
        }
    }
}
