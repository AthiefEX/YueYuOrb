using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatDesk.App.Launcher;
using FloatDesk.Core.Models;

namespace FloatDesk.App.Views;

public sealed class LauncherIconView : Grid
{
    public static readonly DependencyProperty LauncherProperty =
        DependencyProperty.Register(
            nameof(Launcher),
            typeof(LauncherItem),
            typeof(LauncherIconView),
            new FrameworkPropertyMetadata(
                null,
                OnIconInputChanged));

    public static readonly DependencyProperty IconProviderProperty =
        DependencyProperty.Register(
            nameof(IconProvider),
            typeof(ILauncherIconProvider),
            typeof(LauncherIconView),
            new FrameworkPropertyMetadata(
                null,
                OnIconInputChanged));

    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.Register(
            nameof(IconSize),
            typeof(double),
            typeof(LauncherIconView),
            new FrameworkPropertyMetadata(
                42d,
                OnIconSizeChanged));

    private readonly Border _fallback;
    private readonly TextBlock _fallbackGlyph;
    private readonly Image _image;
    private CancellationTokenSource? _requestCancellation;
    private int _requestVersion;

    public LauncherIconView()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        _fallbackGlyph = new TextBlock
        {
            FontFamily = new FontFamily(
                "Segoe Fluent Icons, Segoe MDL2 Assets"),
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _fallback = new Border
        {
            Background = new LinearGradientBrush(
                Color.FromRgb(82, 177, 255),
                Color.FromRgb(39, 113, 235),
                45),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = _fallbackGlyph
        };
        _image = new Image
        {
            Stretch = Stretch.Uniform,
            Visibility = Visibility.Collapsed,
            SnapsToDevicePixels = true
        };
        RenderOptions.SetBitmapScalingMode(
            _image,
            BitmapScalingMode.HighQuality);

        Children.Add(_fallback);
        Children.Add(_image);
        Loaded += (_, _) => RefreshIcon();
        Unloaded += (_, _) => CancelRequest();
        UpdateSize(IconSize);
    }

    public LauncherItem? Launcher
    {
        get => (LauncherItem?)GetValue(LauncherProperty);
        set => SetValue(LauncherProperty, value);
    }

    public ILauncherIconProvider? IconProvider
    {
        get => (ILauncherIconProvider?)GetValue(IconProviderProperty);
        set => SetValue(IconProviderProperty, value);
    }

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public bool IsFallbackVisible =>
        _fallback.Visibility == Visibility.Visible;

    public ImageSource? DisplayedImage => _image.Source;

    private static void OnIconInputChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is LauncherIconView view)
            view.RefreshIcon();
    }

    private static void OnIconSizeChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is LauncherIconView view)
            view.UpdateSize((double)e.NewValue);
    }

    private void UpdateSize(double size)
    {
        if (!double.IsFinite(size) || size <= 0)
            size = 42;

        Width = size;
        Height = size;
        _fallback.Width = size;
        _fallback.Height = size;
        _fallback.CornerRadius = new CornerRadius(size / 2);
        _fallbackGlyph.FontSize = Math.Max(10, size * 0.48);
        _image.Width = size;
        _image.Height = size;
    }

    private void RefreshIcon()
    {
        var version = ++_requestVersion;
        CancelRequest();
        ResetToFallback();

        var launcher = Launcher;
        var provider = IconProvider;
        if (launcher is null || provider is null)
            return;

        var cancellation = new CancellationTokenSource();
        _requestCancellation = cancellation;
        _ = LoadIconAsync(
            launcher,
            provider,
            version,
            cancellation);
    }

    private async Task LoadIconAsync(
        LauncherItem launcher,
        ILauncherIconProvider provider,
        int version,
        CancellationTokenSource cancellation)
    {
        try
        {
            var image = await provider.GetAsync(
                    launcher,
                    cancellation.Token)
                .ConfigureAwait(false);
            if (image is null ||
                cancellation.IsCancellationRequested ||
                version != _requestVersion)
            {
                return;
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (cancellation.IsCancellationRequested ||
                    version != _requestVersion)
                {
                    return;
                }

                _image.Source = image;
                _image.Visibility = Visibility.Visible;
                _fallback.Visibility = Visibility.Collapsed;
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
        }
    }

    private void ResetToFallback()
    {
        _image.Source = null;
        _image.Visibility = Visibility.Collapsed;
        _fallback.Visibility = Visibility.Visible;
    }

    private void CancelRequest()
    {
        _requestCancellation?.Cancel();
        _requestCancellation?.Dispose();
        _requestCancellation = null;
    }
}
