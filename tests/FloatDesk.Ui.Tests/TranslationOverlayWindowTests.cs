using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Reflection;
using System.Runtime.InteropServices;
using FloatDesk.App.Translation;
using FloatDesk.App.Views;
using FloatDesk.Core.Geometry;
using FloatDesk.Core.Models;
using FloatDesk.Core.Translation;
using FloatDesk.Windows.Capture;
using FloatDesk.Windows.Translation;
using FloatDesk.Windows.Windows;

namespace FloatDesk.Ui.Tests;

[Collection("WPF")]
public sealed class TranslationOverlayWindowTests
{
    [Fact]
    public void ExtractedTranslationRetainsTextWhileBusyAndAllowsRetryOnFailure()
    {
        RunOnSta(() =>
        {
            var window = new TranslationOverlayWindow();
            try
            {
                var anchor = new PixelPoint(40, 40);
                var operation = window.BeginOperation();
                window.ShowExtractedText(operation, "original extracted text", anchor);
                var button = Assert.IsType<Button>(window.FindName("TranslateExtractedButton"));
                var requests = new List<(TranslationRouteHandle Route, string Text)>();
                window.TranslateExtractedTextRequested += (route, text) => requests.Add((route, text));

                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                var request = Assert.Single(requests);
                Assert.Equal(operation, request.Route.Operation);
                Assert.Equal(anchor, request.Route.Anchor);
                Assert.Equal("original extracted text", request.Text);
                Assert.False(button.IsEnabled);
                Assert.Equal("original extracted text", window.TranslatedText);
                Assert.Equal("正在翻译…", window.StatusText);

                window.ShowExtractedTextTranslation(operation,
                    TranslationResponse.Fail(request.Text, "en", "provider", TranslationFailureKind.Network), anchor);
                Assert.True(button.IsEnabled);
                Assert.Equal("original extracted text", window.TranslatedText);
                Assert.Contains("网络", window.StatusText);

                window.BeginOperation();
                window.ShowExtractedTextTranslation(operation,
                    TranslationResponse.Ok(request.Text, "late text", "en", "provider"), anchor);
                Assert.Equal("", window.TranslatedText);
                Assert.Equal(Visibility.Collapsed, button.Visibility);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ExtractedTextOffersTranslationWhileOrdinaryResultsDoNot()
    {
        RunOnSta(() =>
        {
            var window = new TranslationOverlayWindow();
            try
            {
                var operation = window.BeginOperation();
                window.ShowExtractedText(operation, "Recognized text", new PixelPoint(40, 40));
                var button = Assert.IsType<Button>(window.FindName("TranslateExtractedButton"));
                Assert.Equal(Visibility.Visible, button.Visibility);
                Assert.True(button.IsEnabled);
                Assert.Equal("翻译", button.Content);
                Assert.Equal("Recognized text", window.TranslatedText);

                window.ShowResult(operation, TranslationResponse.Ok("Recognized text", "识别的文字", "en", "provider"),
                    new PixelPoint(40, 40));
                Assert.Equal(Visibility.Collapsed, button.Visibility);
                window.BeginOperation();
                Assert.Equal(Visibility.Collapsed, button.Visibility);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ClickingResultEnablesCopyActivationAndNextOperationRestoresNoActivate()
    {
        RunOnSta(() =>
        {
            var window = new TranslationOverlayWindow();
            try
            {
                var operation = window.BeginOperation();
                window.ShowResult(operation, TranslationResponse.Ok("source", "copy result", "en", "provider"),
                    new PixelPoint(40, 40));
                var handle = new WindowInteropHelper(window).Handle;
                Assert.NotEqual(0, GetWindowLongPtr(handle, -20).ToInt64() & 0x08000000L);
                var text = Assert.IsType<TextBox>(window.FindName("TranslatedTextBlock"));

                text.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                {
                    RoutedEvent = Mouse.PreviewMouseDownEvent
                });

                Assert.Equal(0, GetWindowLongPtr(handle, -20).ToInt64() & 0x08000000L);
                text.SelectAll();
                Assert.True(ApplicationCommands.Copy.CanExecute(null, text));
                window.BeginOperation();
                Assert.NotEqual(0, GetWindowLongPtr(handle, -20).ToInt64() & 0x08000000L);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void CompactResultTextCanBeSelectedAndOriginalDecorationStaysHidden()
    {
        RunOnSta(() =>
        {
            var window = new TranslationOverlayWindow();
            try
            {
                var operation = window.BeginOperation();
                window.ShowResult(operation, TranslationResponse.Ok("source", "copy this result", "en", "provider"),
                    new PixelPoint(40, 40));
                var text = Assert.IsType<TextBox>(window.FindName("TranslatedTextBlock"));
                Assert.True(text.IsReadOnly);
                text.Select(0, 4);
                Assert.Equal("copy", text.SelectedText);
                Assert.Equal(Visibility.Collapsed, Assert.IsType<Border>(window.FindName("OriginalPanel")).Visibility);
                Assert.Equal(Visibility.Collapsed, Assert.IsType<TextBlock>(window.FindName("MetadataTextBlock")).Visibility);
                Assert.False(window.IsClickThrough);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void InteractiveResultCanBeDraggedWithoutResultUpdateSnappingItBack()
    {
        RunOnSta(() =>
        {
            var window = new TranslationOverlayWindow();
            try
            {
                var work = PrimaryWorkArea();
                var anchor = new PixelPoint(work.Left + 20, work.Top + 20);
                var operation = window.BeginOperation();
                window.ShowStatus(operation, "working", anchor);
                var handle = new WindowInteropHelper(window).Handle;
                var before = DesktopGeometry.GetVisibleWindowBounds(handle);

                Assert.True(window.BeginInteractiveDrag(new PixelPoint(before.Left + 20, before.Top + 10)));
                window.MoveInteractiveDrag(new PixelPoint(before.Left + 90, before.Top + 60));
                window.EndInteractiveDrag();
                var moved = DesktopGeometry.GetVisibleWindowBounds(handle);
                Assert.Equal(before.Left + 70, moved.Left);
                Assert.Equal(before.Top + 50, moved.Top);

                window.ShowResult(operation, TranslationResponse.Ok("source", "result", "en", "provider"), anchor);
                var after = DesktopGeometry.GetVisibleWindowBounds(handle);
                Assert.Equal(moved.Left, after.Left);
                Assert.Equal(moved.Top, after.Top);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void ResultIsInactiveAndClampedToTheNearestWorkArea()
    {
        RunOnSta(() =>
        {
            var workArea = PrimaryWorkArea();
            var window = new TranslationOverlayWindow();
            try
            {
                var operation = window.BeginOperation();
                window.ShowResult(
                    operation,
                    TranslationResponse.Ok(
                        "Hello world",
                        "你好，世界",
                        "en",
                        "MyMemory"),
                    new PixelPoint(-500, -500));
                window.UpdateLayout();

                Assert.False(window.ShowActivated);
                Assert.False(window.ShowInTaskbar);
                Assert.Equal(WindowStyle.None, window.WindowStyle);
                Assert.True(window.AllowsTransparency);
                Assert.Equal("你好，世界", window.TranslatedText);
                Assert.Contains(
                    "MyMemory",
                    Assert.IsType<TextBlock>(
                        window.FindName("MetadataTextBlock")).Text,
                    StringComparison.Ordinal);
                Assert.Equal(
                    "Hello world",
                    Assert.IsType<TextBlock>(
                        window.FindName("OriginalTextBlock")).Text);

                var bounds = DesktopGeometry.GetVisibleWindowBounds(
                    new WindowInteropHelper(window).Handle);
                Assert.True(bounds.Left >= workArea.Left);
                Assert.True(bounds.Top >= workArea.Top);
                Assert.True(bounds.Right <= workArea.Right);
                Assert.True(bounds.Bottom <= workArea.Bottom);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void StatusAndErrorExposeReadableStateWithoutStaleTranslation()
    {
        RunOnSta(() =>
        {
            var window = new TranslationOverlayWindow();
            try
            {
                var operation = window.BeginOperation();
                window.ShowResult(
                    operation,
                    TranslationResponse.Ok(
                        "Hello",
                        "你好",
                        "en",
                        "MyMemory"),
                    new PixelPoint(40, 40));
                window.ShowStatus(
                    operation,
                    "正在翻译…",
                    new PixelPoint(40, 40));

                Assert.Equal("正在翻译…", window.StatusText);
                Assert.Equal("", window.TranslatedText);
                Assert.Equal(
                    Visibility.Collapsed,
                    Assert.IsType<Border>(
                        window.FindName("TranslatedPanel")).Visibility);
                Assert.False(
                    Assert.IsType<Button>(
                        window.FindName("CopyButton")).IsEnabled);

                window.ShowError(
                    operation,
                    "网络连接不可用。",
                    new PixelPoint(40, 40));
                Assert.Equal("网络连接不可用。", window.StatusText);
                Assert.Equal("", window.TranslatedText);
                Assert.Equal(
                    Visibility.Collapsed,
                    Assert.IsType<Border>(
                        window.FindName("TranslatedPanel")).Visibility);
                Assert.False(
                    Assert.IsType<Button>(
                        window.FindName("CopyButton")).IsEnabled);
                Assert.Equal(
                    AutomationLiveSetting.Polite,
                    AutomationProperties.GetLiveSetting(
                        Assert.IsType<TextBlock>(
                            window.FindName("StatusTextBlock"))));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ApplySettingsUpdatesTopmostOpacityAndTranslationFont()
    {
        RunOnSta(() =>
        {
            var window = new TranslationOverlayWindow();
            try
            {
                var settings = AppSettings.CreateDefault() with
                {
                    Overlay = new OverlaySettings(
                        AlwaysOnTop: false,
                        FullscreenPolicy.AutoHide),
                    Translation = TranslationSettings.CreateDefault() with
                    {
                        SubtitleOverlayOpacity = 0.78,
                        SubtitleFontSizeDip = 26
                    }
                };

                window.ApplySettings(settings);

                Assert.False(window.Topmost);
                Assert.Equal(0.78, window.Opacity, precision: 2);
                Assert.Equal(
                    26,
                    Assert.IsType<TextBox>(
                        window.FindName("TranslatedTextBlock")).FontSize);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void CompactSubtitleModeUsesTransparentTextSurface()
    {
        RunOnSta(() =>
        {
            var window = new TranslationOverlayWindow();
            try
            {
                var operation = window.BeginOperation(subtitle: true);
                window.SetSubtitleControls(
                    operation,
                    active: true,
                    paused: false,
                    canReselect: true);
                window.ShowSubtitleNotice(operation, "字幕翻译准备中。");
                window.UpdateLayout();

                Assert.True(window.IsSubtitleMode);
                Assert.Equal(
                    Visibility.Collapsed,
                    Assert.IsType<Grid>(
                        window.FindName("TransitionLayer")).Visibility);
                Assert.Equal(
                    Visibility.Visible,
                    Assert.IsType<Grid>(
                        window.FindName("SubtitleSurface")).Visibility);
                var card = Assert.IsType<Border>(
                    window.FindName("OverlayCard"));
                var background = Assert.IsAssignableFrom<SolidColorBrush>(
                    card.Background);
                Assert.Equal(0, background.Color.A);
                Assert.Equal(0, card.BorderThickness.Left);
                Assert.Equal(
                    Visibility.Collapsed,
                    Assert.IsType<Border>(
                        window.FindName("SubtitleControlBar")).Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void CompactSubtitleModeIsClickThrough()
    {
        RunOnSta(() =>
        {
            var window = new TranslationOverlayWindow();
            try
            {
                Assert.False(window.IsClickThrough);

                var operation = window.BeginOperation(subtitle: true);
                window.SetSubtitleControls(
                    operation,
                    active: true,
                    paused: false,
                    canReselect: true);

                Assert.True(window.IsClickThrough);

                window.SetSubtitleControls(
                    operation,
                    active: false,
                    paused: false,
                    canReselect: false);

                Assert.False(window.IsClickThrough);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SubtitleTextReplacesPreviousTextAndRejectsStaleToken()
    {
        RunOnSta(() =>
        {
            var window = new TranslationOverlayWindow();
            try
            {
                var operation = window.BeginOperation(subtitle: true);
                window.SetSubtitleControls(
                    operation,
                    active: true,
                    paused: false,
                    canReselect: true);
                window.PrepareSubtitleCaptureRegion(PrimaryCaptureRegion());

                Assert.Equal(
                    TranslationSubtitlePlacementStatus.Ready,
                    window.ShowSubtitleText(operation, "第一句"));
                Assert.Equal(
                    TranslationSubtitlePlacementStatus.Ready,
                    window.ShowSubtitleText(operation, "第二句"));

                var text = Assert.IsType<TextBlock>(
                    window.FindName("SubtitleTranslationTextBlock"));
                Assert.Equal("第二句", text.Text);
                Assert.Equal("第二句", window.TranslatedText);

                window.SetSubtitleControls(
                    operation,
                    active: false,
                    paused: false,
                    canReselect: false);
                window.ShowSubtitleText(operation, "迟到字幕");

                Assert.Equal("", text.Text);
                Assert.Equal("", window.TranslatedText);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void RejectedSubtitleCommitIsNotReportedAsDisplayed()
    {
        RunOnSta(() =>
        {
            var window = new TranslationOverlayWindow();
            try
            {
                var operation = window.BeginOperation(subtitle: true);
                window.SetSubtitleControls(
                    operation,
                    active: true,
                    paused: false,
                    canReselect: true);
                window.PrepareSubtitleCaptureRegion(PrimaryCaptureRegion());

                var rejected = window.ShowSubtitleTextWithResult(
                    operation,
                    "不应显示",
                    _ => false);
                var accepted = window.ShowSubtitleTextWithResult(
                    operation,
                    "应当显示",
                    commit =>
                    {
                        commit();
                        return true;
                    });

                Assert.False(rejected.Displayed);
                Assert.True(accepted.Displayed);
                Assert.Equal("应当显示", window.TranslatedText);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SubtitleNoticeDoesNotClearTranslatedText()
    {
        RunOnSta(() =>
        {
            var window = new TranslationOverlayWindow();
            try
            {
                var operation = window.BeginOperation(subtitle: true);
                window.SetSubtitleControls(
                    operation,
                    active: true,
                    paused: false,
                    canReselect: true);
                window.PrepareSubtitleCaptureRegion(PrimaryCaptureRegion());
                window.ShowSubtitleText(operation, "第一句");

                window.ShowSubtitleNotice(operation, "正在翻译…");

                Assert.Equal(
                    "第一句",
                    Assert.IsType<TextBlock>(
                        window.FindName(
                            "SubtitleTranslationTextBlock")).Text);
                Assert.Equal(
                    "正在翻译…",
                    Assert.IsType<TextBlock>(
                        window.FindName("SubtitleNoticeTextBlock")).Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SubtitleNoticePlacementFailureRestoresSafeContent()
    {
        RunOnSta(() =>
        {
            var monitor = DisplayTopology.SelectPrimaryMonitor(
                    DisplayTopology.GetMonitors())
                ?? throw new InvalidOperationException("No monitor found.");
            var window = new TranslationOverlayWindow();
            try
            {
                var operation = window.BeginOperation(subtitle: true);
                window.SetSubtitleControls(
                    operation,
                    active: true,
                    paused: false,
                    canReselect: true);
                window.PrepareSubtitleCaptureRegion(PrimaryCaptureRegion());
                window.ShowSubtitleText(operation, "安全字幕");
                ForceCaptureExcluded(window, excluded: false);

                window.PrepareSubtitleCaptureRegion(
                    new TranslationCaptureRegion(
                        monitor.DeviceName,
                        0,
                        0,
                        1,
                        1));
                var status = window.ShowSubtitleNotice(
                    operation,
                    "无法安全显示提示。");

                Assert.NotEqual(
                    TranslationSubtitlePlacementStatus.Ready,
                    status);
                Assert.Equal("安全字幕", window.TranslatedText);
                Assert.True(window.IsVisible);
                Assert.NotEqual(
                    TranslationSubtitlePlacementStatus.Ready,
                    window.PrepareSubtitleCaptureRegion(
                        new TranslationCaptureRegion(
                            monitor.DeviceName,
                            0,
                            0,
                            1,
                            1)));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SubtitleTextPlacementFailureKeepsPreviousTranslationVisible()
    {
        RunOnSta(() =>
        {
            var monitor = DisplayTopology.SelectPrimaryMonitor(
                    DisplayTopology.GetMonitors())
                ?? throw new InvalidOperationException("No monitor found.");
            var window = new TranslationOverlayWindow();
            try
            {
                var operation = window.BeginOperation(subtitle: true);
                window.SetSubtitleControls(
                    operation,
                    active: true,
                    paused: false,
                    canReselect: true);
                window.PrepareSubtitleCaptureRegion(PrimaryCaptureRegion());
                window.ShowSubtitleText(operation, "旧译文");
                ForceCaptureExcluded(window, excluded: false);
                window.PrepareSubtitleCaptureRegion(
                    new TranslationCaptureRegion(
                        monitor.DeviceName,
                        0,
                        0,
                        1,
                        1));

                var status = window.ShowSubtitleText(operation, "新译文");

                Assert.NotEqual(
                    TranslationSubtitlePlacementStatus.Ready,
                    status);
                Assert.Equal("旧译文", window.TranslatedText);
                Assert.True(window.IsVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ResizedSubtitleTargetPromotesFallbackBackToSafe()
    {
        Assert.Equal(
            TranslationOverlayWindow.SubtitleTargetKind.Safe,
            TranslationOverlayWindow.ResolveResizedTargetKind(
                TranslationOverlayWindow.SubtitleTargetKind.Fallback,
                TranslationOverlayWindow.SubtitleTargetKind.Safe));
        Assert.Equal(
            TranslationOverlayWindow.SubtitleTargetKind.Fallback,
            TranslationOverlayWindow.ResolveResizedTargetKind(
                TranslationOverlayWindow.SubtitleTargetKind.Safe,
                TranslationOverlayWindow.SubtitleTargetKind.Fallback));
    }

    [Fact]
    public void SettingsChangeReportsUnsafeSubtitlePlacement()
    {
        RunOnSta(() =>
        {
            var monitor = DisplayTopology.SelectPrimaryMonitor(
                    DisplayTopology.GetMonitors())
                ?? throw new InvalidOperationException("No monitor found.");
            var window = new TranslationOverlayWindow();
            try
            {
                var operation = window.BeginOperation(subtitle: true);
                window.SetSubtitleControls(
                    operation,
                    active: true,
                    paused: false,
                    canReselect: true);
                window.PrepareSubtitleCaptureRegion(
                    new TranslationCaptureRegion(
                        monitor.DeviceName,
                        0,
                        0,
                        1,
                        1));
                ForceCaptureExcluded(window, excluded: true);
                Assert.Equal(
                    TranslationSubtitlePlacementStatus.Ready,
                    window.ShowSubtitleText(
                        operation,
                        string.Concat(
                            Enumerable.Repeat(
                                "用于验证设置变更后字幕重新定位的长句。",
                                12))));

                ForceCaptureExcluded(window, excluded: false);
                var status = window.ApplySettings(
                    AppSettings.CreateDefault() with
                    {
                        Translation =
                            TranslationSettings.CreateDefault() with
                            {
                                SubtitleFontSizeDip = 36
                            }
                    });

                Assert.NotEqual(
                    TranslationSubtitlePlacementStatus.Ready,
                    status);
                Assert.True(window.IsVisible);
                Assert.NotEmpty(window.TranslatedText);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SubtitleTextUsesConfiguredFontSize()
    {
        RunOnSta(() =>
        {
            var window = new TranslationOverlayWindow();
            try
            {
                window.ApplySettings(
                    AppSettings.CreateDefault() with
                    {
                        Translation =
                            TranslationSettings.CreateDefault() with
                            {
                                SubtitleFontSizeDip = 30
                            }
                    });
                var operation = window.BeginOperation(subtitle: true);
                window.SetSubtitleControls(
                    operation,
                    active: true,
                    paused: false,
                    canReselect: true);
                window.PrepareSubtitleCaptureRegion(PrimaryCaptureRegion());
                window.ShowSubtitleText(operation, "字号测试");

                Assert.Equal(
                    30,
                    Assert.IsType<TextBlock>(
                        window.FindName(
                            "SubtitleTranslationTextBlock")).FontSize);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void CopyRequestIsRaisedOnlyForAReadyTranslation()
    {
        RunOnSta(() =>
        {
            var window = new TranslationOverlayWindow();
            try
            {
                var copyCount = 0;
                string? copiedText = null;
                var writerField = typeof(TranslationOverlayWindow).GetField(
                    "_clipboardWriter",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException(
                        "The clipboard writer seam was not found.");
                writerField.SetValue(
                    window,
                    new Action<string>(text => copiedText = text));
                window.CopyRequested += () => copyCount++;
                var copyButton = Assert.IsType<Button>(
                    window.FindName("CopyButton"));

                copyButton.RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(0, copyCount);

                var operation = window.BeginOperation();
                window.ShowResult(
                    operation,
                    TranslationResponse.Ok(
                        "Hello",
                        "你好",
                        "en",
                        "MyMemory"),
                    new PixelPoint(40, 40));
                Assert.True(copyButton.IsEnabled);
                copyButton.RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent));

                Assert.Equal(1, copyCount);
                Assert.Equal("你好", copiedText);
                Assert.Equal("你好", window.TranslatedText);
                Assert.Equal(
                    "复制全部文字",
                    AutomationProperties.GetName(copyButton));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SubtitlePlacementUsesSavedMonitorCoordinatesAndClamps()
    {
        RunOnSta(() =>
        {
            var monitor = DisplayTopology.SelectPrimaryMonitor(
                DisplayTopology.GetMonitors())
                ?? throw new InvalidOperationException("No monitor found.");
            var window = new TranslationOverlayWindow();
            try
            {
                window.ApplySettings(
                    AppSettings.CreateDefault() with
                    {
                        Translation = TranslationSettings.CreateDefault() with
                        {
                            SubtitleOverlayPlacement =
                                new TranslationOverlayPlacement(
                                    monitor.DeviceName,
                                    0,
                                    0)
                        }
                    });

                var operation = window.BeginOperation();
                window.SetSubtitleControls(
                    operation,
                    active: true,
                    paused: false,
                    canReselect: true);
                window.UpdateLayout();

                var bounds = DesktopGeometry.GetVisibleWindowBounds(
                    new WindowInteropHelper(window).Handle);
                Assert.InRange(
                    Math.Abs(bounds.Left - monitor.WorkArea.Left),
                    0,
                    4);
                Assert.InRange(
                    Math.Abs(bounds.Top - monitor.WorkArea.Top),
                    0,
                    4);
                Assert.True(bounds.Right <= monitor.WorkArea.Right);
                Assert.True(bounds.Bottom <= monitor.WorkArea.Bottom);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void InvalidAnchorAndClosedWindowDoNotThrow()
    {
        RunOnSta(() =>
        {
            var window = new TranslationOverlayWindow();
            var operation = window.BeginOperation();
            window.ShowStatus(
                operation,
                "正在准备…",
                new PixelPoint(int.MinValue, int.MaxValue));
            window.ShowError(
                operation,
                "未检测到可翻译文字。",
                new PixelPoint(int.MaxValue, int.MinValue));
            window.Close();

            window.ShowStatus(
                operation,
                "已关闭窗口上的状态",
                new PixelPoint(-1, -1));
        });
    }

    [Fact]
    public void CloseInSubtitleModeStopsAndBlocksLateResultsUntilNextOperation()
    {
        RunOnSta(() =>
        {
            var window = new TranslationOverlayWindow();
            try
            {
                var stopCount = 0;
                window.StopSubtitleRequested += () => stopCount++;
                var operation = window.BeginOperation();
                window.SetSubtitleControls(
                    operation,
                    active: true,
                    paused: false,
                    canReselect: true);
                window.ShowStatus(
                    operation,
                    "正在识别字幕…",
                    new PixelPoint(40, 40));

                Assert.IsType<Button>(
                    window.FindName("CloseButton")).RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent));

                Assert.Equal(1, stopCount);
                Assert.True(window.IsDismissed);
                Assert.False(window.IsSubtitleMode);
                Assert.False(window.IsVisible);

                window.ShowResult(
                    operation,
                    TranslationResponse.Ok(
                        "late source",
                        "迟到的结果",
                        "en",
                        "MyMemory"),
                    new PixelPoint(40, 40));
                window.ShowError(
                    operation,
                    "迟到的错误",
                    new PixelPoint(40, 40));
                window.ShowStatus(
                    operation,
                    "迟到的状态",
                    new PixelPoint(40, 40));

                Assert.False(window.IsVisible);
                Assert.Equal("", window.TranslatedText);
                Assert.Equal("", window.StatusText);

                var nextOperation = window.BeginOperation();
                Assert.False(window.IsDismissed);
                window.ShowResult(
                    nextOperation,
                    TranslationResponse.Ok(
                        "next source",
                        "下一次结果",
                        "en",
                        "MyMemory"),
                    new PixelPoint(40, 40));

                Assert.True(window.IsVisible);
                Assert.Equal("下一次结果", window.TranslatedText);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void CloseInOneShotModeDoesNotRaiseSubtitleStop()
    {
        RunOnSta(() =>
        {
            var window = new TranslationOverlayWindow();
            try
            {
                var stopCount = 0;
                window.StopSubtitleRequested += () => stopCount++;
                var operation = window.BeginOperation();
                window.ShowResult(
                    operation,
                    TranslationResponse.Ok(
                        "Hello",
                        "你好",
                        "en",
                        "MyMemory"),
                    new PixelPoint(40, 40));

                Assert.IsType<Button>(
                    window.FindName("CloseButton")).RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(0, stopCount);
                Assert.False(window.IsVisible);

                window.ShowResult(
                    operation,
                    TranslationResponse.Ok(
                        "late",
                        "迟到",
                        "en",
                        "MyMemory"),
                    new PixelPoint(40, 40));
                Assert.False(window.IsVisible);
                Assert.Equal("", window.TranslatedText);

                var nextOperation = window.BeginOperation();
                window.ShowStatus(
                    nextOperation,
                    "下一次操作",
                    new PixelPoint(40, 40));
                Assert.True(window.IsVisible);
                Assert.Equal("下一次操作", window.StatusText);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void StaleOperationCannotReplaceNewOperationAfterDismissal()
    {
        RunOnSta(() =>
        {
            var window = new TranslationOverlayWindow();
            try
            {
                var operationA = window.BeginOperation();
                window.ShowStatus(
                    operationA,
                    "操作 A",
                    new PixelPoint(40, 40));

                Assert.IsType<Button>(
                    window.FindName("CloseButton")).RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent));

                var operationB = window.BeginOperation();
                window.ShowResult(
                    operationB,
                    TranslationResponse.Ok(
                        "source B",
                        "操作 B 结果",
                        "en",
                        "MyMemory"),
                    new PixelPoint(40, 40));

                window.ShowResult(
                    operationA,
                    TranslationResponse.Ok(
                        "late source A",
                        "迟到的 A 结果",
                        "en",
                        "MyMemory"),
                    new PixelPoint(40, 40));
                window.ShowError(
                    operationA,
                    "迟到的 A 错误",
                    new PixelPoint(40, 40));
                window.ShowStatus(
                    operationA,
                    "迟到的 A 状态",
                    new PixelPoint(40, 40));
                window.SetSubtitleControls(
                    operationA,
                    active: true,
                    paused: false,
                    canReselect: true);

                Assert.True(window.IsVisible);
                Assert.False(window.IsSubtitleMode);
                Assert.Equal("操作 B 结果", window.TranslatedText);
                Assert.Equal("翻译完成", window.StatusText);
                Assert.Equal(
                    "source B",
                    Assert.IsType<TextBlock>(
                        window.FindName("OriginalTextBlock")).Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void LateSubtitleControlsDoNotReopenDismissedSubtitleMode()
    {
        RunOnSta(() =>
        {
            var window = new TranslationOverlayWindow();
            try
            {
                var operation = window.BeginOperation();
                window.SetSubtitleControls(
                    operation,
                    active: true,
                    paused: false,
                    canReselect: true);
                Assert.IsType<Button>(
                    window.FindName("CloseButton")).RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent));

                window.SetSubtitleControls(
                    operation,
                    active: true,
                    paused: false,
                    canReselect: true);

                Assert.True(window.IsDismissed);
                Assert.False(window.IsSubtitleMode);
                Assert.False(window.IsVisible);
                Assert.Equal("", window.StatusText);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void StoppingSubtitleInvalidatesOperationAndBlocksLateUpdates()
    {
        RunOnSta(() =>
        {
            var window = new TranslationOverlayWindow();
            try
            {
                var operation = window.BeginOperation(subtitle: true);
                window.SetSubtitleControls(
                    operation,
                    active: true,
                    paused: false,
                    canReselect: true);
                window.ShowStatus(
                    operation,
                    "字幕进行中",
                    new PixelPoint(40, 40));

                window.SetSubtitleControls(
                    operation,
                    active: false,
                    paused: false,
                    canReselect: false);

                Assert.True(window.IsDismissed);
                Assert.False(window.IsSubtitleMode);
                Assert.False(window.IsVisible);
                Assert.Equal("", window.StatusText);

                window.ShowStatus(
                    operation,
                    "迟到的状态",
                    new PixelPoint(40, 40));
                window.ShowResult(
                    operation,
                    TranslationResponse.Ok(
                        "late source",
                        "迟到的结果",
                        "en",
                        "MyMemory"),
                    new PixelPoint(40, 40));
                window.ShowError(
                    operation,
                    "迟到的错误",
                    new PixelPoint(40, 40));
                window.SetSubtitleControls(
                    operation,
                    active: true,
                    paused: false,
                    canReselect: true);

                Assert.True(window.IsDismissed);
                Assert.False(window.IsSubtitleMode);
                Assert.False(window.IsVisible);
                Assert.Equal("", window.TranslatedText);
                Assert.Equal("", window.StatusText);

                var nextOperation = window.BeginOperation(subtitle: true);
                window.SetSubtitleControls(
                    nextOperation,
                    active: true,
                    paused: false,
                    canReselect: true);
                window.ShowStatus(
                    nextOperation,
                    "新的字幕操作",
                    new PixelPoint(40, 40));

                Assert.False(window.IsDismissed);
                Assert.True(window.IsSubtitleMode);
                Assert.True(window.IsVisible);
                Assert.Equal("新的字幕操作", window.StatusText);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void InteractiveOperationAfterActiveSubtitleClearsSubtitleState()
    {
        RunOnSta(() =>
        {
            var window = new TranslationOverlayWindow();
            try
            {
                var subtitleOperation =
                    window.BeginOperation(subtitle: true);
                window.SetSubtitleControls(
                    subtitleOperation,
                    active: true,
                    paused: true,
                    canReselect: true);
                window.ShowResult(
                    subtitleOperation,
                    TranslationResponse.Ok(
                        "old subtitle source",
                        "旧字幕译文",
                        "en",
                        "MyMemory"),
                    new PixelPoint(40, 40));
                Assert.True(window.IsSubtitleMode);

                var interactiveOperation = window.BeginOperation();
                window.ShowStatus(
                    interactiveOperation,
                    "交互操作状态",
                    new PixelPoint(40, 40));

                Assert.True(window.IsVisible);
                Assert.False(window.IsSubtitleMode);
                Assert.Equal("交互操作状态", window.StatusText);
                Assert.Equal("", window.TranslatedText);
                Assert.Equal(
                    "",
                    Assert.IsType<TextBlock>(
                        window.FindName("OriginalTextBlock")).Text);
                Assert.Equal(
                    "",
                    Assert.IsType<TextBlock>(
                        window.FindName("MetadataTextBlock")).Text);
                Assert.Equal(
                    Visibility.Collapsed,
                    Assert.IsType<Border>(
                        window.FindName("SubtitleControlBar")).Visibility);
                Assert.False(
                    Assert.IsType<Button>(
                        window.FindName("CopyButton")).IsEnabled);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void NewSubtitleOperationResetsPreviousSubtitlePresentation()
    {
        RunOnSta(() =>
        {
            var window = new TranslationOverlayWindow();
            try
            {
                var operationA = window.BeginOperation(subtitle: true);
                window.SetSubtitleControls(
                    operationA,
                    active: true,
                    paused: true,
                    canReselect: true);
                window.ShowResult(
                    operationA,
                    TranslationResponse.Ok(
                        "old subtitle source",
                        "旧字幕译文",
                        "en",
                        "MyMemory"),
                    new PixelPoint(40, 40));

                var operationB = window.BeginOperation(subtitle: true);

                Assert.True(window.IsVisible);
                Assert.True(window.IsSubtitleMode);
                Assert.Equal("", window.TranslatedText);
                Assert.Equal("字幕翻译准备中。", window.StatusText);
                Assert.Equal(
                    "",
                    Assert.IsType<TextBlock>(
                        window.FindName("OriginalTextBlock")).Text);
                Assert.Equal(
                    "",
                    Assert.IsType<TextBlock>(
                        window.FindName("MetadataTextBlock")).Text);
                Assert.Equal(
                    Visibility.Collapsed,
                    Assert.IsType<Border>(
                        window.FindName("SubtitleControlBar")).Visibility);
                Assert.Equal(
                    "暂停",
                    Assert.IsType<Button>(
                        window.FindName("PauseResumeButton")).Content);
                Assert.False(
                    Assert.IsType<Button>(
                        window.FindName("ReselectButton")).IsEnabled);
                Assert.False(
                    Assert.IsType<Button>(
                        window.FindName("CopyButton")).IsEnabled);

                window.ShowStatus(
                    operationB,
                    "新字幕准备中",
                    new PixelPoint(40, 40));
                Assert.Equal("新字幕准备中", window.StatusText);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ClosedWindowRejectsNewOperationsWithoutWritingContent()
    {
        RunOnSta(() =>
        {
            var window = new TranslationOverlayWindow();
            window.Close();

            var operation = window.BeginOperation();
            window.ShowResult(
                operation,
                TranslationResponse.Ok(
                    "sensitive source",
                    "敏感译文",
                    "en",
                    "MyMemory"),
                new PixelPoint(40, 40));
            window.ShowStatus(
                operation,
                "敏感状态",
                new PixelPoint(40, 40));
            window.SetSubtitleControls(
                operation,
                active: true,
                paused: false,
                canReselect: true);

            Assert.True(window.IsDismissed);
            Assert.False(window.IsSubtitleMode);
            Assert.False(window.IsVisible);
            Assert.Equal("", window.TranslatedText);
            Assert.Equal("", window.StatusText);
            Assert.Equal(
                "",
                Assert.IsType<TextBlock>(
                    window.FindName("OriginalTextBlock")).Text);
            Assert.Equal(
                "",
                Assert.IsType<TextBlock>(
                    window.FindName("MetadataTextBlock")).Text);
        });
    }

    [Fact]
    public void VisibleStatusUpdatesDoNotRestartTheRevealTransition()
    {
        RunOnSta(() =>
        {
            var window = new TranslationOverlayWindow();
            try
            {
                var operation = window.BeginOperation();
                window.ShowStatus(
                    operation,
                    "第一次状态",
                    new PixelPoint(40, 40));
                var layer = Assert.IsType<Grid>(
                    window.FindName("TransitionLayer"));
                var translate = Assert.IsType<TranslateTransform>(
                    window.FindName("TransitionTranslate"));
                layer.BeginAnimation(UIElement.OpacityProperty, null);
                translate.BeginAnimation(
                    System.Windows.Media.TranslateTransform.YProperty,
                    null);
                layer.Opacity = 0.64;
                translate.Y = 5;

                window.ShowStatus(
                    operation,
                    "第二次状态",
                    new PixelPoint(40, 40));

                Assert.Equal(0.64, layer.Opacity, precision: 2);
                Assert.Equal(5, translate.Y, precision: 2);
                Assert.False(layer.HasAnimatedProperties);
                Assert.False(translate.HasAnimatedProperties);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void VisibleSubtitleControlUpdatesDoNotRestartTheRevealTransition()
    {
        RunOnSta(() =>
        {
            var window = new TranslationOverlayWindow();
            try
            {
                var operation = window.BeginOperation();
                window.SetSubtitleControls(
                    operation,
                    active: true,
                    paused: false,
                    canReselect: true);
                var surface = Assert.IsType<Grid>(
                    window.FindName("SubtitleSurface"));
                surface.BeginAnimation(UIElement.OpacityProperty, null);
                surface.Opacity = 0.71;

                window.SetSubtitleControls(
                    operation,
                    active: true,
                    paused: true,
                    canReselect: false);

                Assert.Equal(0.71, surface.Opacity, precision: 2);
                Assert.False(surface.HasAnimatedProperties);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void EnteringSubtitleModeClearsPreviousOneShotPresentation()
    {
        RunOnSta(() =>
        {
            var window = new TranslationOverlayWindow();
            try
            {
                var operation = window.BeginOperation();
                window.ShowResult(
                    operation,
                    TranslationResponse.Ok(
                        "old source",
                        "旧译文",
                        "en",
                        "MyMemory"),
                    new PixelPoint(40, 40));
                Assert.NotEqual("", window.TranslatedText);

                window.SetSubtitleControls(
                    operation,
                    active: true,
                    paused: false,
                    canReselect: true);

                Assert.Equal("", window.TranslatedText);
                Assert.Equal("字幕翻译准备中。", window.StatusText);
                Assert.Equal(
                    "",
                    Assert.IsType<TextBlock>(
                        window.FindName("OriginalTextBlock")).Text);
                Assert.Equal(
                    "",
                    Assert.IsType<TextBlock>(
                        window.FindName("MetadataTextBlock")).Text);
                Assert.Equal(
                    Visibility.Collapsed,
                    Assert.IsType<Border>(
                        window.FindName("OriginalPanel")).Visibility);
                Assert.Equal(
                    Visibility.Collapsed,
                    Assert.IsType<Border>(
                        window.FindName("TranslatedPanel")).Visibility);
                Assert.False(
                    Assert.IsType<Button>(
                        window.FindName("CopyButton")).IsEnabled);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ErrorAfterResultClearsStaleSourceAndProviderPresentation()
    {
        RunOnSta(() =>
        {
            var window = new TranslationOverlayWindow();
            try
            {
                var operation = window.BeginOperation();
                window.ShowResult(
                    operation,
                    TranslationResponse.Ok(
                        "old source",
                        "旧译文",
                        "en",
                        "MyMemory"),
                    new PixelPoint(40, 40));

                window.ShowError(
                    operation,
                    "网络连接不可用。",
                    new PixelPoint(40, 40));

                Assert.Equal("网络连接不可用。", window.StatusText);
                Assert.Equal("", window.TranslatedText);
                Assert.Equal(
                    "",
                    Assert.IsType<TextBlock>(
                        window.FindName("OriginalTextBlock")).Text);
                Assert.Equal(
                    "",
                    Assert.IsType<TextBlock>(
                        window.FindName("MetadataTextBlock")).Text);
                Assert.Equal(
                    Visibility.Collapsed,
                    Assert.IsType<Border>(
                        window.FindName("OriginalPanel")).Visibility);
                Assert.Equal(
                    Visibility.Collapsed,
                    Assert.IsType<Border>(
                        window.FindName("TranslatedPanel")).Visibility);
                Assert.False(
                    Assert.IsType<Button>(
                        window.FindName("CopyButton")).IsEnabled);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void FontSizeChangeLaysOutBeforeRepositioningVisibleSubtitle()
    {
        RunOnSta(() =>
        {
            var monitor = DisplayTopology.SelectPrimaryMonitor(
                    DisplayTopology.GetMonitors())
                ?? throw new InvalidOperationException("No monitor found.");
            var baseSettings = AppSettings.CreateDefault() with
            {
                Translation = TranslationSettings.CreateDefault() with
                {
                    SubtitleFontSizeDip = 14,
                    SubtitleOverlayPlacement =
                        new TranslationOverlayPlacement(
                            monitor.DeviceName,
                            0.5,
                            0.5)
                }
            };
            var window = new TranslationOverlayWindow();
            try
            {
                window.ApplySettings(baseSettings);
                var operation = window.BeginOperation();
                window.SetSubtitleControls(
                    operation,
                    active: true,
                    paused: false,
                    canReselect: true);
                window.PrepareSubtitleCaptureRegion(PrimaryCaptureRegion());
                window.ShowSubtitleText(
                    operation,
                    string.Concat(
                        Enumerable.Repeat(
                            "这是一段用于验证字幕字号变化后布局与定位的长译文。",
                            8)));
                window.UpdateLayout();
                var beforeTextHeight = Assert.IsType<TextBlock>(
                    window.FindName(
                        "SubtitleTranslationTextBlock")).ActualHeight;
                var beforeWindowHeight = window.ActualHeight;

                window.ApplySettings(baseSettings with
                {
                    Translation = baseSettings.Translation with
                    {
                        SubtitleFontSizeDip = 34
                    }
                });

                var afterTextHeight = Assert.IsType<TextBlock>(
                    window.FindName(
                        "SubtitleTranslationTextBlock")).ActualHeight;
                Assert.True(
                    afterTextHeight > beforeTextHeight + 20,
                    $"Expected relayout after font change: " +
                    $"{beforeTextHeight:0.0} -> {afterTextHeight:0.0}.");
                Assert.True(window.ActualHeight > beforeWindowHeight);

                var bounds = DesktopGeometry.GetVisibleWindowBounds(
                    new WindowInteropHelper(window).Handle);
                Assert.True(bounds.Left >= monitor.WorkArea.Left);
                Assert.True(bounds.Top >= monitor.WorkArea.Top);
                Assert.True(bounds.Right <= monitor.WorkArea.Right);
                Assert.True(bounds.Bottom <= monitor.WorkArea.Bottom);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void NativeWindowStylesAreAppliedAfterShowing()
    {
        RunOnSta(() =>
        {
            var window = new TranslationOverlayWindow();
            try
            {
                var operation = window.BeginOperation();
                window.ShowStatus(
                    operation,
                    "native style",
                    new PixelPoint(40, 40));
                var handle = new WindowInteropHelper(window).Handle;
                Assert.NotEqual(nint.Zero, handle);

                var style = GetWindowLongPtr(handle, -20).ToInt64();
                Assert.NotEqual(
                    0,
                    style & 0x08000000L);
                Assert.NotEqual(0, style & 0x00000080L);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void DisplayAffinitySeamReceivesHandleAndExcludeFlag()
    {
        var hwnd = new nint(0x1234);
        nint observedHandle = 0;
        uint observedAffinity = 0;

        Assert.True(WindowCaptureExclusion.TryExclude(
            hwnd,
            (handle, affinity) =>
            {
                observedHandle = handle;
                observedAffinity = affinity;
                return true;
            }));
        Assert.Equal(hwnd, observedHandle);
        Assert.Equal(0x11u, observedAffinity);

        Assert.False(WindowCaptureExclusion.TryExclude(
            hwnd,
            static (_, _) => false));
        Assert.False(WindowCaptureExclusion.TryExclude(
            hwnd,
            static (_, _) => throw new InvalidOperationException(
                "affinity failed")));

        var invoked = false;
        Assert.False(WindowCaptureExclusion.TryExclude(
            0,
            (_, _) =>
            {
                invoked = true;
                return true;
            }));
        Assert.False(invoked);
    }

    [Fact]
    public void OverlayUsesNoActivateToolWindowAndCaptureExclusionHooks()
    {
        var code = File.ReadAllText(FindRepoFile(
            "src/FloatDesk.App/Views/TranslationOverlayWindow.xaml.cs"));
        var exclusion = File.ReadAllText(FindRepoFile(
            "src/FloatDesk.Windows/Translation/WindowCaptureExclusion.cs"));

        Assert.Contains("WS_EX_NOACTIVATE", code, StringComparison.Ordinal);
        Assert.Contains("WS_EX_TOOLWINDOW", code, StringComparison.Ordinal);
        Assert.Contains("WS_EX_TRANSPARENT", code, StringComparison.Ordinal);
        Assert.Contains(
            "WindowCaptureExclusion.TryExclude",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "WDA_EXCLUDEFROMCAPTURE",
            exclusion,
            StringComparison.Ordinal);
        Assert.Contains(
            "SetWindowDisplayAffinity",
            exclusion,
            StringComparison.Ordinal);
        Assert.False(WindowCaptureExclusion.TryExclude(0));
    }

    private static PixelRect PrimaryWorkArea()
    {
        var monitor = DisplayTopology.SelectPrimaryMonitor(
                DisplayTopology.GetMonitors())
            ?? throw new InvalidOperationException("No monitor found.");
        return monitor.WorkArea;
    }

    private static TranslationCaptureRegion PrimaryCaptureRegion()
    {
        var monitor = DisplayTopology.SelectPrimaryMonitor(
                DisplayTopology.GetMonitors())
            ?? throw new InvalidOperationException("No monitor found.");
        return new TranslationCaptureRegion(
            monitor.DeviceName,
            0.1,
            0.7,
            0.8,
            0.15);
    }

    private static void ForceCaptureExcluded(
        TranslationOverlayWindow window,
        bool excluded)
    {
        var property = typeof(TranslationOverlayWindow).GetProperty(
            nameof(TranslationOverlayWindow.IsCaptureExcluded),
            BindingFlags.Instance | BindingFlags.Public |
            BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "Capture exclusion state was not found.");
        property.SetValue(window, excluded);
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(
            thread.Join(TimeSpan.FromSeconds(8)),
            "STA test thread timed out.");
        if (failure is not null)
            throw new Xunit.Sdk.XunitException(failure.ToString());
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

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);
}
