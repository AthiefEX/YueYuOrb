using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using FloatDesk.App.Capture;
using FloatDesk.Core;
using FloatDesk.Core.Capture;
using FloatDesk.Core.Geometry;
using FloatDesk.Core.Models;

namespace FloatDesk.App.Views;

public partial class CaptureEditorWindow : Window
{
    private readonly CaptureFrame _frame;
    private readonly CaptureSettings _settings;
    private readonly AnnotationDocument _annotations = new();
    private readonly TaskCompletionSource<bool> _completed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private AnnotationKind _tool = AnnotationKind.Rectangle;
    private PixelPoint? _dragStart;
    private int _previewStartIndex = -1;

    public CaptureEditorWindow(CaptureFrame frame, CaptureSettings settings)
    {
        InitializeComponent();
        _frame = frame;
        _settings = settings;
        CaptureImage.Source = CaptureExportService.ToBitmap(frame);
        EditorSurface.Width = frame.Bounds.Width;
        EditorSurface.Height = frame.Bounds.Height;
        AnnotationCanvas.Width = frame.Bounds.Width;
        AnnotationCanvas.Height = frame.Bounds.Height;
        PixelSizeText.Text = $"{frame.Bounds.Width} × {frame.Bounds.Height} 物理像素";
        SetTool(AnnotationKind.Rectangle);
        Loaded += (_, _) =>
        {
            Activate();
            EditorSurface.Focus();
        };
    }

    public Task<bool> Completed => _completed.Task;

    public void Cancel() => Complete(false);

    private void SetTool(AnnotationKind kind)
    {
        _tool = kind;
        RectangleToolButton.FontWeight =
            kind == AnnotationKind.Rectangle ? FontWeights.Bold : FontWeights.Normal;
        ArrowToolButton.FontWeight =
            kind == AnnotationKind.Arrow ? FontWeights.Bold : FontWeights.Normal;
        TextToolButton.FontWeight =
            kind == AnnotationKind.Text ? FontWeights.Bold : FontWeights.Normal;
    }

    private void OnRectangleToolClick(object sender, RoutedEventArgs e) =>
        SetTool(AnnotationKind.Rectangle);

    private void OnArrowToolClick(object sender, RoutedEventArgs e) =>
        SetTool(AnnotationKind.Arrow);

    private void OnTextToolClick(object sender, RoutedEventArgs e) =>
        SetTool(AnnotationKind.Text);

    private void OnSurfaceMouseDown(object sender, MouseButtonEventArgs e)
    {
        var point = ToPixelPoint(e.GetPosition(AnnotationCanvas));
        if (_tool == AnnotationKind.Text)
        {
            var dialog = new TextAnnotationWindow(_settings.FontSizePx)
            {
                Owner = this
            };
            if (dialog.ShowDialog() == true &&
                !string.IsNullOrWhiteSpace(dialog.AnnotationText))
            {
                _annotations.Add(Annotation.CreateText(
                    point,
                    dialog.AnnotationText,
                    _settings.AnnotationColor,
                    dialog.AnnotationFontSize));
                Redraw();
            }
            return;
        }
        _dragStart = point;
        EditorSurface.CaptureMouse();
        e.Handled = true;
    }

    private void OnSurfaceMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragStart is null ||
            e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }
        var current = ToPixelPoint(e.GetPosition(AnnotationCanvas));
        RemovePreview();
        _previewStartIndex = AnnotationCanvas.Children.Count;
        if (_tool == AnnotationKind.Rectangle)
        {
            AnnotationCanvas.Children.Add(
                PreviewRectangle(_dragStart.Value, current));
        }
        else
        {
            AddArrowShape(
                _dragStart.Value,
                current,
                new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(
                        _settings.AnnotationColor)),
                _settings.StrokeWidthPx);
        }
    }

    private void OnSurfaceMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is null)
            return;
        EditorSurface.ReleaseMouseCapture();
        var end = ToPixelPoint(e.GetPosition(AnnotationCanvas));
        var start = _dragStart.Value;
        _dragStart = null;
        RemovePreview();
        if (Math.Abs(end.X - start.X) < 2 &&
            Math.Abs(end.Y - start.Y) < 2)
        {
            return;
        }
        _annotations.Add(_tool == AnnotationKind.Rectangle
            ? Annotation.Rectangle(
                start,
                end,
                _settings.AnnotationColor,
                _settings.StrokeWidthPx)
            : Annotation.Arrow(
                start,
                end,
                _settings.AnnotationColor,
                _settings.StrokeWidthPx));
        Redraw();
    }

    private void Redraw()
    {
        AnnotationCanvas.Children.Clear();
        foreach (var annotation in _annotations.Items)
            AddDisplayAnnotation(annotation);
        UpdateUndoButtons();
    }

    private void AddDisplayAnnotation(Annotation annotation)
    {
        var brush = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(annotation.ColorHex));
        switch (annotation.Kind)
        {
            case AnnotationKind.Rectangle:
                AnnotationCanvas.Children.Add(PreviewRectangle(
                    annotation.Start,
                    annotation.End,
                    annotation.StrokeWidthPx));
                break;
            case AnnotationKind.Arrow:
                AddArrowShape(
                    annotation.Start,
                    annotation.End,
                    brush,
                    annotation.StrokeWidthPx);
                break;
            case AnnotationKind.Text when !string.IsNullOrEmpty(annotation.Text):
            {
                var text = new TextBlock
                {
                    Text = annotation.Text,
                    Foreground = brush,
                    FontSize = Math.Max(10, annotation.FontSizePx),
                    FontWeight = FontWeights.SemiBold,
                    Background = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0))
                };
                Canvas.SetLeft(text, annotation.Start.X);
                Canvas.SetTop(text, annotation.Start.Y);
                AnnotationCanvas.Children.Add(text);
                break;
            }
        }
    }

    private Rectangle PreviewRectangle(
        PixelPoint start,
        PixelPoint end,
        int? strokeWidth = null)
    {
        var rectangle = new Rectangle
        {
            Stroke = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(_settings.AnnotationColor)),
            StrokeThickness = strokeWidth ?? Math.Max(1, _settings.StrokeWidthPx),
            Fill = Brushes.Transparent
        };
        var left = Math.Min(start.X, end.X);
        var top = Math.Min(start.Y, end.Y);
        rectangle.Width = Math.Abs(end.X - start.X);
        rectangle.Height = Math.Abs(end.Y - start.Y);
        Canvas.SetLeft(rectangle, left);
        Canvas.SetTop(rectangle, top);
        return rectangle;
    }

    private void RemovePreview()
    {
        if (_previewStartIndex < 0)
            return;
        while (AnnotationCanvas.Children.Count > _previewStartIndex)
        {
            AnnotationCanvas.Children.RemoveAt(
                AnnotationCanvas.Children.Count - 1);
        }
        _previewStartIndex = -1;
    }

    private Line AddArrowShape(
        PixelPoint start,
        PixelPoint end,
        Brush brush,
        int strokeWidth)
    {
        var line = new Line
        {
            X1 = start.X,
            Y1 = start.Y,
            X2 = end.X,
            Y2 = end.Y,
            Stroke = brush,
            StrokeThickness = Math.Max(1, strokeWidth),
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        };
        AnnotationCanvas.Children.Add(line);
        var angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
        var length = Math.Max(10, strokeWidth * 4);
        var left = new Point(
            end.X - length * Math.Cos(angle - Math.PI / 6),
            end.Y - length * Math.Sin(angle - Math.PI / 6));
        var right = new Point(
            end.X - length * Math.Cos(angle + Math.PI / 6),
            end.Y - length * Math.Sin(angle + Math.PI / 6));
        var head = new Polygon
        {
            Fill = brush,
            Points = new PointCollection
            {
                new(end.X, end.Y),
                left,
                right
            }
        };
        AnnotationCanvas.Children.Add(head);
        return line;
    }

    private void OnUndoClick(object sender, RoutedEventArgs e)
    {
        if (_annotations.Undo())
            Redraw();
    }

    private void OnRedoClick(object sender, RoutedEventArgs e)
    {
        if (_annotations.Redo())
            Redraw();
    }

    private void UpdateUndoButtons() =>
        StatusText.Text =
            $"{_annotations.Items.Count} 个标注 · 所有坐标按物理像素输出";

    private async void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        await ExportAndCompleteAsync(saveAs: false);
    }

    private async void OnSaveAsClick(object sender, RoutedEventArgs e)
    {
        await ExportAndCompleteAsync(saveAs: true);
    }

    private async Task ExportAndCompleteAsync(bool saveAs)
    {
        try
        {
            var bitmap = CaptureExportService.Render(_frame, _annotations.Items);
            StatusText.Text = "正在复制到剪贴板…";
            var copy = await CaptureExportService.CopyAsync(
                bitmap,
                CancellationToken.None);
            OperationResult? fileResult = null;
            string? filePath = null;
            var shouldSave = saveAs || _settings.AutoSave;
            if (shouldSave)
            {
                var directory = _settings.SaveDirectory ??
                                System.IO.Path.Combine(
                                    Environment.GetFolderPath(
                                        Environment.SpecialFolder.MyPictures),
                                    ProductInfo.EnglishName);
                if (saveAs)
                {
                    var extension = _settings.Format == ImageFormat.Jpeg ? ".jpg" : ".png";
                    var dialog = new Microsoft.Win32.SaveFileDialog
                    {
                        Filter = _settings.Format == ImageFormat.Jpeg
                            ? "JPEG 图片|*.jpg"
                            : "PNG 图片|*.png",
                        DefaultExt = extension,
                        AddExtension = true,
                        OverwritePrompt = true,
                        FileName = $"Screenshot_{DateTime.Now:yyyyMMdd_HHmmss}{extension}"
                    };
                    if (dialog.ShowDialog(this) == true)
                    {
                        filePath = dialog.FileName;
                        fileResult = await CaptureExportService.SaveAsync(
                            bitmap,
                            filePath,
                            _settings.Format,
                            CancellationToken.None);
                    }
                }
                else
                {
                    filePath = CaptureExportService.CreateAutomaticPath(
                        directory,
                        _settings.Format);
                    fileResult = await CaptureExportService.SaveAsync(
                        bitmap,
                        filePath,
                        _settings.Format,
                        CancellationToken.None);
                }
            }

            if (!copy.Success)
            {
                StatusText.Text =
                    $"{copy.Message} 可继续编辑后另存，或再次复制。";
                return;
            }
            StatusText.Text = fileResult switch
            {
                { Success: true } => $"已复制并保存：{filePath}",
                { Success: false } => $"已复制，但保存失败：{fileResult.Message}",
                _ => "已复制到剪贴板。"
            };
            Complete(true);
        }
        catch (Exception exception)
        {
            StatusText.Text = $"截图复制或保存失败：{exception.Message}";
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) =>
        Complete(false);

    private async void OnPreviewKeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Complete(false);
            e.Handled = true;
        }
        else if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                OnRedoClick(this, new RoutedEventArgs());
            else
                OnUndoClick(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Y && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            OnRedoClick(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            await ExportAndCompleteAsync(saveAs: false);
        }
        else if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            await ExportAndCompleteAsync(saveAs: true);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_completed.Task.IsCompleted)
            return;
        e.Cancel = true;
        Complete(false);
    }

    private void Complete(bool saved)
    {
        _completed.TrySetResult(saved);
        Hide();
        Close();
    }

    private PixelPoint ToPixelPoint(Point value) =>
        new(
            Math.Clamp((int)Math.Round(value.X), 0, _frame.Bounds.Width),
            Math.Clamp((int)Math.Round(value.Y), 0, _frame.Bounds.Height));
}

internal sealed class TextAnnotationWindow : Window
{
    private readonly System.Windows.Controls.TextBox _text =
        new()
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 90
        };
    private readonly System.Windows.Controls.TextBox _fontSize;

    public TextAnnotationWindow(int defaultFontSize)
    {
        Title = "添加文字标注";
        Width = 430;
        Height = 280;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        _fontSize = new System.Windows.Controls.TextBox
        {
            Text = defaultFontSize.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            Width = 90
        };
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "文字（支持中文输入）" });
        panel.Children.Add(_text);
        var fontRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 14)
        };
        fontRow.Children.Add(new TextBlock
        {
            Text = "字号（10–72px）",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        fontRow.Children.Add(_fontSize);
        panel.Children.Add(fontRow);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var cancel = new Button
        {
            Content = "取消",
            Width = 76,
            Margin = new Thickness(0, 0, 8, 0)
        };
        cancel.Click += (_, _) => DialogResult = false;
        var ok = new Button
        {
            Content = "添加",
            Width = 76,
            IsDefault = true
        };
        ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => _text.Focus();
    }

    public string AnnotationText => _text.Text;
    public int AnnotationFontSize =>
        int.TryParse(_fontSize.Text, out var value)
            ? Math.Clamp(value, 10, 72)
            : 20;
}
