using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatDesk.App;
using FloatDesk.App.Views;
using FloatDesk.Core.Models;
using Microsoft.Web.WebView2.Wpf;
using Application = System.Windows.Application;
using TabControl = System.Windows.Controls.TabControl;
using ListBox = System.Windows.Controls.ListBox;

namespace SettingsWindowProbe;

internal static partial class Program
{
    private static async Task VerifyFloatingBrowserBackgroundAsync()
    {
        var output = Path.Combine(Environment.CurrentDirectory, "artifacts", "floating-browser-evidence");
        Directory.CreateDirectory(output);
        var paths = new AppDataPaths(Path.Combine(output, "isolated-profile-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        var source = Path.Combine(paths.Assets, "sample.png");
        var pixels = new byte[320 * 200 * 4];
        for (var y = 0; y < 200; y++)
        for (var x = 0; x < 320; x++)
        { var index = (y * 320 + x) * 4; pixels[index] = (byte)(x * 255 / 320); pixels[index + 1] = (byte)(y * 255 / 200); pixels[index + 2] = 160; pixels[index + 3] = 255; }
        var image = BitmapSource.Create(320, 200, 96, 96, PixelFormats.Bgra32, null, pixels, 320 * 4);
        SaveProbeBitmap(image, source);
        var crop = new BackgroundCropWindow(source);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        crop.Show(); crop.UpdateLayout();
        typeof(BackgroundCropWindow).GetField("_crop", flags)!.SetValue(crop, new Rect(40, 30, 160, 100));
        crop.Save(Path.Combine(paths.Assets, "background.png"));
        CaptureProbeWindow(crop, Path.Combine(output, "background-crop.png")); crop.Close();
        var cropped = new BitmapImage(); cropped.BeginInit(); cropped.CacheOption = BitmapCacheOption.OnLoad;
        cropped.UriSource = new Uri(Path.Combine(paths.Assets, "background.png")); cropped.EndInit();
        if (cropped.PixelWidth != 160 || cropped.PixelHeight != 100) throw new InvalidOperationException("Crop dimensions were not preserved.");
        var defaults = AppSettings.CreateDefault();
        var settings = defaults with { BrowserSites = [], CustomBackground = new() { Enabled = true, Opacity = .65 }, FloatingBrowser = new() { Bounds = new(90, 90, 680, 760) } };
        var window = new SettingsWindow(new FixedSettingsCoordinator(settings), paths, null, new NullCredentialStore(), new NullTranslationProviderFactory(), () => []);
        try
        {
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                ThemeService.Apply(theme); ThemeService.ApplyCustomBackground(settings with { General = settings.General with { Theme = theme } }, paths.Assets);
                if (Application.Current.Resources["SettingsShellBrush"] is not DrawingBrush) throw new InvalidOperationException("Background is hidden by the settings shell.");
                window.Show();
                var tabs = (TabControl)window.FindName("SettingsTabControl"); tabs.SelectedIndex = 0;
                if (((TabItem)tabs.Items[0]).Content is TabControl categories)
                {
                    if (categories.Items.OfType<TabItem>().Any(item => item.Header?.ToString() == "背景与用户数据")) throw new InvalidOperationException("Obsolete data/background category still exists.");
                    var category = categories.Items.OfType<TabItem>().Single(item => item.Header?.ToString() == "启动与外观"); categories.SelectedItem = category;
                    var configuration = categories.Items.OfType<TabItem>().Single(item => item.Header?.ToString() == "配置文件");
                    if (!System.Windows.Markup.XamlWriter.Save(configuration.Content).Contains("导出用户数据", StringComparison.Ordinal))
                        throw new InvalidOperationException("User data controls are not in Configuration.");
                }
                await Task.Delay(250); CaptureProbeWindow(window, Path.Combine(output, "settings-background-" + theme + ".png"));
            }
        }
        finally { window.Close(); }
        // Replacing the fixed background path must refresh without a restart.
        SaveProbeBitmap(image, Path.Combine(paths.Assets, "background.png"));
        ThemeService.ApplyCustomBackground(settings, paths.Assets);
        var refreshedBrush = (DrawingBrush)Application.Current.Resources["SettingsShellBrush"];
        var refreshedGroup = (DrawingGroup)((DrawingGroup)refreshedBrush.Drawing).Children[1];
        var refreshedImage = (BitmapSource)((ImageDrawing)refreshedGroup.Children[0]).ImageSource;
        var refreshedPixel = new byte[4];
        new FormatConvertedBitmap(refreshedImage, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(0, 0, 1, 1), refreshedPixel, 4, 0);
        if (refreshedPixel[0] > 5 || refreshedPixel[1] > 5 || refreshedPixel[2] < 100)
            throw new InvalidOperationException("Replacing the background reused stale pixels.");
        var browser = new AiBrowserWindow(paths, _ => Task.CompletedTask);
        browser.ApplySettings(settings with { BrowserTopmost = true, FloatingBrowser = settings.FloatingBrowser with { AlwaysOnTop = false } });
        if (browser.Topmost) throw new InvalidOperationException("The legacy topmost preference overrides the visible browser setting.");
        browser.ApplySettings(settings);
        try
        {
            await browser.ShowAndEnsureAsync(CancellationToken.None);
            var webview = (WebView2CompositionControl)typeof(AiBrowserWindow).GetField("_browser", flags)!.GetValue(browser)!;
            if (webview.CoreWebView2 is null) throw new InvalidOperationException("Real WebView2 failed to initialize.");
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            webview.CoreWebView2.AddWebResourceRequestedFilter("https://probe.invalid/*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All);
            webview.CoreWebView2.WebResourceRequested += (_, e) => e.Response = webview.CoreWebView2.Environment.CreateWebResourceResponse(
                new MemoryStream(System.Text.Encoding.UTF8.GetBytes("<html><title>Isolated probe</title><body style='background:#527fba;color:white;font:28px sans-serif'>Isolated browser rendering probe</body></html>")),
                200, "OK", "Content-Type: text/html");
            webview.CoreWebView2.NavigationCompleted += (_, e) => { if (e.IsSuccess) loaded.TrySetResult(); else loaded.TrySetException(new InvalidOperationException("Probe page did not render.")); };
            webview.CoreWebView2.Navigate("https://probe.invalid/");
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15)); await Task.Delay(700);
            if (webview.CoreWebView2.DocumentTitle != "Isolated probe" || ((FrameworkElement)browser.FindName("StatusPanel")).Visibility == Visibility.Visible)
                throw new InvalidOperationException("Web content is hidden by an error panel.");
            var sites = new[] { new BrowserSite(Guid.NewGuid(), "Probe home", new Uri("https://probe.invalid/"), 0), new BrowserSite(Guid.NewGuid(), "Second bookmark", new Uri("https://probe.invalid/second"), 1) };
            browser.ApplySettings(settings with { BrowserSites = sites, SelectedBrowserSiteId = sites[0].Id });
            var handle = new WindowInteropHelper(browser).Handle;
            var style = BrowserProbeGetStyle(handle, -20);
            if (!browser.IsClickThrough || (style & 0x08000020) != 0x08000020) throw new InvalidOperationException("Default watch mode can steal input.");
            browser.SetInteraction(true);
            if ((BrowserProbeGetStyle(handle, -20) & 0x08000020) != 0) throw new InvalidOperationException("Interactive browser is still transparent to input.");
            var moreButton = (System.Windows.Controls.Button)browser.FindName("BrowserBookmarksButton");
            moreButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); await Task.Delay(100);
            var bookmarks = (ListBox)browser.FindName("BrowserBookmarksList");
            if (bookmarks.Items.Count != 2 || !((System.Windows.Controls.Primitives.Popup)browser.FindName("BrowserBookmarksPopup")).IsOpen)
                throw new InvalidOperationException("Bookmark popup is not populated.");
            CaptureProbeElement((FrameworkElement)((System.Windows.Controls.Primitives.Popup)browser.FindName("BrowserBookmarksPopup")).Child, Path.Combine(output, "browser-bookmarks.png"));
            bookmarks.SelectedItem = sites[1]; await Task.Delay(300);
            if (webview.Source?.AbsolutePath != "/second") throw new InvalidOperationException("Choosing a bookmark did not navigate.");
            ((System.Windows.Controls.Primitives.Popup)browser.FindName("BrowserBookmarksPopup")).IsOpen = false;
            ((System.Windows.Controls.Button)browser.FindName("BrowserAppearanceButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await Task.Delay(100);
            var appearance = (System.Windows.Controls.Primitives.Popup)browser.FindName("BrowserAppearancePopup");
            if (!appearance.IsOpen) throw new InvalidOperationException("Appearance popup did not open.");
            CaptureProbeElement((FrameworkElement)appearance.Child, Path.Combine(output, "browser-appearance.png"));
            browser.SetInteraction(false);
            if (appearance.IsOpen) throw new InvalidOperationException("Watch mode leaves an interactive popup open.");
            browser.SetInteraction(true);
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            foreach (var width in new[] { 680d, 400d })
            {
                ThemeService.Apply(theme);
                browser.Width = width; browser.Height = 460; browser.UpdateLayout();
                var title = (FrameworkElement)browser.FindName("AiTitleBar"); var host = (FrameworkElement)browser.FindName("BrowserHost");
                if (title.ActualHeight > 40 || host.ActualHeight < browser.ActualHeight - 45) throw new InvalidOperationException("Browser content loses space to extra toolbar rows.");
                double Right(string name) { var element = (FrameworkElement)browser.FindName(name); return element.TranslatePoint(new System.Windows.Point(element.ActualWidth, 0), title).X; }
                double Left(string name) => ((FrameworkElement)browser.FindName(name)).TranslatePoint(new System.Windows.Point(), title).X;
                if (Right("BrowserAddressInput") > Left("PinButton") + 1 || Right("BrowserAppearanceButton") > Left("AiMinimizeButton") + 1 || Right("AiMinimizeButton") > Left("BrowserBookmarksButton") + 1 || Right("BrowserBookmarksButton") > Left("AiMaximizeRestoreButton") + 1)
                    throw new InvalidOperationException("Compact browser controls overlap or bookmarks are not after minimize.");
                CaptureProbeWindow(browser, Path.Combine(output, $"browser-compact-{theme}-{width:0}.png"));
            }
            var leftPoint = browser.PointToScreen(new System.Windows.Point(1, browser.ActualHeight / 2));
            var edgeHit = BrowserProbeSendMessage(handle, 0x84, 0, (nint)(((int)leftPoint.Y << 16) | ((int)leftPoint.X & 0xffff)));
            if (edgeHit != 10) throw new InvalidOperationException($"The layered browser edge cannot resize: hit={edgeHit}.");
            browser.Width = 420; browser.UpdateLayout();
            var opacity = (Slider)browser.FindName("BrowserOpacitySlider"); opacity.Value = 55;
            await VerifyBrowserBackdropTransparencyAsync(browser, opacity);
            if (Math.Abs(browser.DisplayOpacity - .55) > .001 || !browser.AllowsTransparency) throw new InvalidOperationException("Browser is not a composited translucent window.");
            await Task.Delay(350); CaptureProbeWindow(browser, Path.Combine(output, "browser-small-opacity.png"));
            browser.Close();
            if (browser.IsVisible) throw new InvalidOperationException("Close did not hide the reusable browser.");
            await browser.ShowAndEnsureAsync(CancellationToken.None);
            if (!browser.IsClickThrough) throw new InvalidOperationException("Reopening browser did not restore watch mode.");
            await browser.DisposeBrowserAsync();
            await browser.ShowAndEnsureAsync(CancellationToken.None);
            if (((WebView2CompositionControl)typeof(AiBrowserWindow).GetField("_browser", flags)!.GetValue(browser)!).CoreWebView2 is null)
                throw new InvalidOperationException("Browser could not recreate WebView2.");
        }
        finally { browser.Dispose(); browser.Dispose(); browser.Close(); }
        var orb = new OrbWindow(() => { }, _ => { }, () => { }, () => { }, () => { }, () => { }, () => { }, () => { });
        using (var night = new FloatDesk.App.Brightness.NightModeController(System.Windows.Threading.Dispatcher.CurrentDispatcher))
        {
            try
            {
                orb.ApplySettings(defaults); orb.Show();
                night.SetDimmingPercent(40); night.SetEnabled(true); await Task.Delay(250);
                // Showing/raising the orb must not let it escape the dimming mask.
                orb.Topmost = true; await Task.Delay(200);
                var orbHandle = new WindowInteropHelper(orb).Handle;
                var masks = Application.Current.Windows.Cast<Window>().Where(item => item.GetType().Name == "NightDimWindow" && item.IsVisible).ToArray();
                if (masks.Length == 0) throw new InvalidOperationException("Night mode created no visible mask.");
                foreach (var mask in masks)
                    for (var above = BrowserProbeGetWindow(new WindowInteropHelper(mask).Handle, 3); above != 0; above = BrowserProbeGetWindow(above, 3))
                        if (above == orbHandle) throw new InvalidOperationException("The orb is above the night-mode mask.");
            }
            finally { night.SetEnabled(false); orb.Close(); }
        }
        ThemeService.ApplyCustomBackground(settings with { CustomBackground = new() }, paths.Assets);
        if (Application.Current.Resources["SettingsShellBrush"] is not SolidColorBrush) throw new InvalidOperationException("Disabling background left the custom image active.");
        Console.WriteLine("floatingBrowser.realWebView2.watchInteractionOpacityResizeReopen=PASS");
        Console.WriteLine("background.realCrop.settingsDarkLight=PASS;userProfile=isolated");
        Console.WriteLine("nightMode.realOrbZOrder=PASS;background.disableRestoresTheme=PASS");
    }

    private static void CaptureProbeWindow(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); SaveProbeBitmap(bitmap, path);
    }

    private static void CaptureProbeElement(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element); SaveProbeBitmap(bitmap, path);
    }

    private static void SaveProbeBitmap(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int BrowserProbeGetStyle(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindow")]
    private static extern nint BrowserProbeGetWindow(nint window, uint command);

    private static Task VerifyBrowserBackdropTransparencyAsync(AiBrowserWindow browser, Slider slider) =>
        VerifyWindowBackdropTransparencyAsync(browser, slider, (FrameworkElement)browser.FindName("BrowserHost"), "browser");

    private static async Task VerifyWindowBackdropTransparencyAsync(Window browser, Slider slider, FrameworkElement host, string evidenceName)
    {
        var backdrop = new Window { Left = browser.Left, Top = browser.Top, Width = browser.ActualWidth, Height = browser.ActualHeight,
            WindowStartupLocation = WindowStartupLocation.Manual, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Topmost = true };
        try
        {
            backdrop.Show();
            _ = BrowserProbeSetWindowPos(new WindowInteropHelper(browser).Handle, new nint(-1), 0, 0, 0, 0, 0x13);
            var point = host.PointToScreen(new System.Windows.Point(host.ActualWidth / 2, host.ActualHeight - 25));
            async Task<uint> ReadAsync(System.Windows.Media.Brush color)
            {
                backdrop.Background = color; backdrop.UpdateLayout(); await Task.Delay(200); _ = BrowserProbeDwmFlush();
                var dc = BrowserProbeGetDC(0);
                try { var pixel = BrowserProbeGetPixel(dc, (int)point.X, (int)point.Y); if (pixel == uint.MaxValue) throw new InvalidOperationException("Screen pixel probe unavailable."); return pixel; }
                finally { _ = BrowserProbeReleaseDC(0, dc); }
            }
            slider.Value = 100;
            var opaqueRed = await ReadAsync(System.Windows.Media.Brushes.Red); var opaqueGreen = await ReadAsync(System.Windows.Media.Brushes.Lime);
            slider.Value = 55;
            var transparentRed = await ReadAsync(System.Windows.Media.Brushes.Red); var transparentGreen = await ReadAsync(System.Windows.Media.Brushes.Lime);
            _ = BrowserProbeGetLayeredAttributes(new WindowInteropHelper(browser).Handle, out _, out var alpha, out _);
            static int Delta(uint left, uint right) => Math.Abs((int)(left & 255) - (int)(right & 255)) + Math.Abs((int)((left >> 8) & 255) - (int)((right >> 8) & 255));
            if (Delta(opaqueRed, opaqueGreen) > 12 || Delta(transparentRed, transparentGreen) < 70)
                throw new InvalidOperationException($"Browser backdrop mismatch: opaqueDelta={Delta(opaqueRed, opaqueGreen)}, transparentDelta={Delta(transparentRed, transparentGreen)}; pixels={opaqueRed:X6},{opaqueGreen:X6},{transparentRed:X6},{transparentGreen:X6};alpha={alpha};style={BrowserProbeGetStyle(new WindowInteropHelper(browser).Handle, -20):X8}.");
            Console.WriteLine($"{evidenceName}.actualBackdropBlend=PASS;opaqueDelta={Delta(opaqueRed, opaqueGreen)};transparentDelta={Delta(transparentRed, transparentGreen)}");
            var origin = browser.PointToScreen(new System.Windows.Point());
            var corner = browser.PointToScreen(new System.Windows.Point(browser.ActualWidth, browser.ActualHeight));
            using var screenshot = new System.Drawing.Bitmap((int)(corner.X - origin.X), (int)(corner.Y - origin.Y));
            using (var graphics = System.Drawing.Graphics.FromImage(screenshot))
            {
                var sourceDc = BrowserProbeGetDC(0); var targetDc = graphics.GetHdc();
                try
                {
                    if (!BrowserProbeBitBlt(targetDc, 0, 0, screenshot.Width, screenshot.Height, sourceDc, (int)origin.X, (int)origin.Y, 0x40CC0020))
                        throw new InvalidOperationException("Composited probe screenshot failed.");
                }
                finally { graphics.ReleaseHdc(targetDc); _ = BrowserProbeReleaseDC(0, sourceDc); }
            }
            var evidenceDirectory = Path.Combine(Environment.CurrentDirectory, "artifacts", evidenceName == "browser" ? "floating-browser-evidence" : "ai-assistant-evidence"); Directory.CreateDirectory(evidenceDirectory);
            screenshot.Save(Path.Combine(evidenceDirectory, evidenceName + "-real-transparency.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        finally { backdrop.Close(); }
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowPos")] private static extern bool BrowserProbeSetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetDC")] private static extern nint BrowserProbeGetDC(nint window);
    [DllImport("user32.dll", EntryPoint = "ReleaseDC")] private static extern int BrowserProbeReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll", EntryPoint = "GetPixel")] private static extern uint BrowserProbeGetPixel(nint dc, int x, int y);
    [DllImport("dwmapi.dll", EntryPoint = "DwmFlush")] private static extern int BrowserProbeDwmFlush();
    [DllImport("user32.dll", EntryPoint = "GetLayeredWindowAttributes")] private static extern bool BrowserProbeGetLayeredAttributes(nint window, out uint color, out byte alpha, out uint flags);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint BrowserProbeSendMessage(nint window, int message, nint wparam, nint lparam);
    [DllImport("gdi32.dll", EntryPoint = "BitBlt")] private static extern bool BrowserProbeBitBlt(nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
}
