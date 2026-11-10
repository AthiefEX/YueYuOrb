using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace FloatDesk.App.Views;

public sealed class BackgroundCropWindow : Window
{
    private readonly BitmapFrame _frame;
    private readonly Canvas _canvas = new() { Background = Brushes.Transparent };
    private readonly Image _image = new();
    private readonly Rectangle _selection = new() { Stroke = Brushes.DeepSkyBlue, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(30, 100, 180, 255)), IsHitTestVisible = false };
    private Point? _start;
    private Rect _crop;
    private Rect _display;
    private double _scale;

    public BackgroundCropWindow(string source)
    {
        _frame = OrbImageImporter.LoadFrame(source);
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/YueYuOrb;component/Resources/SettingsWindowResources.xaml", UriKind.Relative) });
        Title = "裁切背景 · 拖动框选，未框选则使用整图";
        Width = 800; Height = 600; MinWidth = 480; MinHeight = 360;
        _crop = new Rect(0, 0, _frame.PixelWidth, _frame.PixelHeight);
        var root = new DockPanel { Margin = new Thickness(16) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var apply = new Button { Content = "使用裁切图片", Padding = new Thickness(18, 8, 18, 8), Margin = new Thickness(8) };
        apply.SetResourceReference(StyleProperty, "PrimaryActionButtonStyle");
        apply.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = "取消", Padding = new Thickness(18, 8, 18, 8), Margin = new Thickness(8), IsCancel = true };
        cancel.SetResourceReference(StyleProperty, "ToolbarButtonStyle");
        buttons.Children.Add(cancel); buttons.Children.Add(apply); DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        _image.Source = _frame; _image.IsHitTestVisible = false; _canvas.Children.Add(_image); _canvas.Children.Add(_selection);
        root.Children.Add(_canvas); Content = root; GlassWindowFrame.WrapStandardWindow(this);
        _canvas.SizeChanged += (_, _) => LayoutImage();
        _canvas.MouseLeftButtonDown += (_, e) =>
        {
            var point = e.GetPosition(_canvas); if (!_display.Contains(point)) return;
            _start = point; _canvas.CaptureMouse();
        };
        _canvas.MouseMove += (_, e) =>
        {
            if (_start is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
            var point = e.GetPosition(_canvas);
            point.X = Math.Clamp(point.X, _display.Left, _display.Right); point.Y = Math.Clamp(point.Y, _display.Top, _display.Bottom);
            var rect = new Rect(start, point);
            if (rect.Width < 2 || rect.Height < 2) return;
            _crop = new Rect((rect.Left - _display.Left) / _scale, (rect.Top - _display.Top) / _scale, rect.Width / _scale, rect.Height / _scale);
            DrawSelection();
        };
        _canvas.MouseLeftButtonUp += (_, _) => { _start = null; _canvas.ReleaseMouseCapture(); };
    }

    private void LayoutImage()
    {
        _scale = Math.Min(_canvas.ActualWidth / _frame.PixelWidth, _canvas.ActualHeight / _frame.PixelHeight);
        if (_scale <= 0) return;
        var size = new Size(_frame.PixelWidth * _scale, _frame.PixelHeight * _scale);
        _display = new Rect((_canvas.ActualWidth - size.Width) / 2, (_canvas.ActualHeight - size.Height) / 2, size.Width, size.Height);
        _image.Width = size.Width; _image.Height = size.Height; Canvas.SetLeft(_image, _display.Left); Canvas.SetTop(_image, _display.Top); DrawSelection();
    }

    private void DrawSelection()
    {
        _selection.Width = _crop.Width * _scale; _selection.Height = _crop.Height * _scale;
        Canvas.SetLeft(_selection, _display.Left + _crop.Left * _scale); Canvas.SetTop(_selection, _display.Top + _crop.Top * _scale);
    }

    public void Save(string destination)
    {
        var left = Math.Clamp((int)_crop.Left, 0, _frame.PixelWidth - 1); var top = Math.Clamp((int)_crop.Top, 0, _frame.PixelHeight - 1);
        BitmapSource image = new CroppedBitmap(_frame, new Int32Rect(left, top, Math.Clamp((int)_crop.Width, 1, _frame.PixelWidth - left), Math.Clamp((int)_crop.Height, 1, _frame.PixelHeight - top)));
        var scale = Math.Min(1, 2048d / Math.Max(image.PixelWidth, image.PixelHeight));
        if (scale < 1) image = new TransformedBitmap(image, new ScaleTransform(scale, scale));
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write)) encoder.Save(stream);
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
