using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using FloatDesk.Core.Geometry;
using FloatDesk.Windows.Capture;
using FloatDesk.Windows.Windows;

namespace FloatDesk.App.Views;

public partial class CaptureOverlayWindow : Window
{
    public event Action<PixelPoint>? DragStarted;
    public event Action<PixelPoint>? DragMoved;
    public event Action<PixelPoint>? DragCompleted;
    public event Action? CancelRequested;
    public event Action? ConfirmRequested;
    public event Action<int, int>? NudgeRequested;

    private bool _dragging;
    private bool _allowClose;

    public CaptureOverlayWindow(
        DisplayMonitor monitor,
        PixelRect frozenBounds,
        BitmapSource frozenDesktop)
    {
        InitializeComponent();
        Monitor = monitor;
        var crop = new CroppedBitmap(
            frozenDesktop,
            new Int32Rect(
                monitor.Bounds.Left - frozenBounds.Left,
                monitor.Bounds.Top - frozenBounds.Top,
                monitor.Bounds.Width,
                monitor.Bounds.Height));
        crop.Freeze();
        FrozenImage.Source = crop;
        SourceInitialized += (_, _) => PlaceWindow();
        Closing += OnClosing;
    }

    public DisplayMonitor Monitor { get; }

    public void PlaceWindow()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != 0)
            DesktopGeometry.SetWindowPixelBounds(handle, Monitor.Bounds);
    }

    internal void CloseWithoutCancelling()
    {
        _allowClose = true;
        Hide();
        Close();
    }

    public void ShowSelection(PixelRect selection)
    {
        var left = Math.Max(Monitor.Bounds.Left, selection.Left);
        var top = Math.Max(Monitor.Bounds.Top, selection.Top);
        var right = Math.Min(Monitor.Bounds.Right, selection.Right);
        var bottom = Math.Min(Monitor.Bounds.Bottom, selection.Bottom);
        if (right <= left || bottom <= top)
        {
            SelectionBorder.Visibility = Visibility.Collapsed;
            SizeText.Visibility = Visibility.Collapsed;
            return;
        }

        var topLeft = PointFromScreen(new Point(left, top));
        var bottomRight = PointFromScreen(new Point(right, bottom));
        SelectionBorder.Margin = new Thickness(
            topLeft.X,
            topLeft.Y,
            Math.Max(0, ActualWidth - bottomRight.X),
            Math.Max(0, ActualHeight - bottomRight.Y));
        SelectionBorder.Visibility = Visibility.Visible;
        SizeText.Text = $"{selection.Width} × {selection.Height} px";
        SizeText.Visibility = Visibility.Visible;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            ConfirmRequested?.Invoke();
            e.Handled = true;
            return;
        }
        _dragging = true;
        CaptureMouse();
        DragStarted?.Invoke(GetPointerPosition(e));
        e.Handled = true;
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed)
            return;
        DragMoved?.Invoke(GetPointerPosition(e));
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging)
            return;
        _dragging = false;
        ReleaseMouseCapture();
        DragCompleted?.Invoke(GetPointerPosition(e));
        e.Handled = true;
    }

    private PixelPoint GetPointerPosition(System.Windows.Input.MouseEventArgs e)
    {
        var point = PointToScreen(e.GetPosition(this));
        return new PixelPoint(
            (int)Math.Round(point.X),
            (int)Math.Round(point.Y));
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelRequested?.Invoke();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter)
        {
            ConfirmRequested?.Invoke();
            e.Handled = true;
            return;
        }
        var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        switch (e.Key)
        {
            case Key.Left:
                NudgeRequested?.Invoke(-step, 0);
                e.Handled = true;
                break;
            case Key.Right:
                NudgeRequested?.Invoke(step, 0);
                e.Handled = true;
                break;
            case Key.Up:
                NudgeRequested?.Invoke(0, -step);
                e.Handled = true;
                break;
            case Key.Down:
                NudgeRequested?.Invoke(0, step);
                e.Handled = true;
                break;
        }
    }

    private void OnMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        CancelRequested?.Invoke();
        e.Handled = true;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
            return;
        e.Cancel = true;
        CancelRequested?.Invoke();
    }
}
