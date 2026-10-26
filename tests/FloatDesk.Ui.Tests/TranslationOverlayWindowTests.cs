using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Reflection;
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
    public void ResultIsInactiveAndClampedToTheNearestWorkArea()
    {
        RunOnSta(() =>
        {
            var workArea = PrimaryWorkArea();
            var window = new TranslationOverlayWindow();
            try
            {
                window.ShowResult(
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
                window.ShowResult(
                    TranslationResponse.Ok(
                        "Hello",
                        "你好",
                        "en",
                        "MyMemory"),
                    new PixelPoint(40, 40));
                window.ShowStatus("正在翻译…", new PixelPoint(40, 40));

                Assert.Equal("正在翻译…", window.StatusText);
                Assert.Equal("", window.TranslatedText);
                Assert.Equal(
                    Visibility.Collapsed,
                    Assert.IsType<Border>(
                        window.FindName("TranslatedPanel")).Visibility);
                Assert.False(
                    Assert.IsType<Button>(
                        window.FindName("CopyButton")).IsEnabled);

                window.ShowError("网络连接不可用。", new PixelPoint(40, 40));
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
                    Assert.IsType<TextBlock>(
                        window.FindName("TranslatedTextBlock")).FontSize);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SubtitleModeShowsControlsAndRaisesDistinctEvents()
    {
        RunOnSta(() =>
        {
            var window = new TranslationOverlayWindow();
            try
            {
                var pauseCount = 0;
                var reselectCount = 0;
                var stopCount = 0;
                window.PauseResumeRequested += () => pauseCount++;
                window.ReselectRegionRequested += () => reselectCount++;
                window.StopSubtitleRequested += () => stopCount++;

                window.SetSubtitleControls(
                    active: true,
                    paused: false,
                    canReselect: true);

                Assert.True(window.IsSubtitleMode);
                Assert.Equal(
                    Visibility.Visible,
                    Assert.IsType<Border>(
                        window.FindName("SubtitleControlBar")).Visibility);
                var pauseButton = Assert.IsType<Button>(
                    window.FindName("PauseResumeButton"));
                Assert.Equal("暂停", pauseButton.Content);
                var reselectButton = Assert.IsType<Button>(
                    window.FindName("ReselectButton"));
                Assert.True(reselectButton.IsEnabled);

                pauseButton.RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent));
                reselectButton.RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent));
                Assert.IsType<Button>(
                    window.FindName("StopSubtitleButton")).RaiseEvent(
                    new RoutedEventArgs(Button.ClickEvent));

                Assert.Equal(1, pauseCount);
                Assert.Equal(1, reselectCount);
                Assert.Equal(1, stopCount);

                window.SetSubtitleControls(
                    active: true,
                    paused: true,
                    canReselect: false);
                Assert.Equal("继续", pauseButton.Content);
                Assert.False(reselectButton.IsEnabled);

                window.SetSubtitleControls(
                    active: false,
                    paused: false,
                    canReselect: false);
                Assert.False(window.IsSubtitleMode);
                Assert.False(window.IsVisible);
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

                window.ShowResult(
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
                    "复制译文",
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

                window.SetSubtitleControls(
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
            window.ShowStatus(
                "正在准备…",
                new PixelPoint(int.MinValue, int.MaxValue));
            window.ShowError(
                "未检测到可翻译文字。",
                new PixelPoint(int.MaxValue, int.MinValue));
            window.Close();

            window.ShowStatus(
                "已关闭窗口上的状态",
                new PixelPoint(-1, -1));
        });
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
}
