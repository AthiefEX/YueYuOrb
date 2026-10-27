using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FloatDesk.App.Views;

public partial class OrbImageCropWindow : Window
{
    private readonly BitmapFrame _frame;
    private OrbCropSelection _selection;
    private OrbCropSelection _dragStartSelection;
    private OrbCropHandle? _dragHandle;
    private Point _dragStart;
    private double _imageLeft;
    private double _imageTop;
    private double _imageScale = 1;

    public OrbImageCropWindow(string sourcePath)
    {
        InitializeComponent();
        _frame = OrbImageImporter.LoadFrame(sourcePath);
        _selection = OrbCropSelection.CreateCentered(
            _frame.PixelWidth,
            _frame.PixelHeight);
        PreviewImage.Source = _frame;
        Loaded += (_, _) => UpdateImageLayout();
        UpdateCropSizeText();
    }

    public Int32Rect CropArea => _selection.ToBounds();

    private void OnImageStageSizeChanged(
        object sender,
        SizeChangedEventArgs e)
    {
        if (e.NewSize.Width <= 0 || e.NewSize.Height <= 0)
            return;
        UpdateImageLayout();
    }

    private void UpdateImageLayout()
    {
        var availableWidth = ImageStage.ActualWidth;
        var availableHeight = ImageStage.ActualHeight;
        if (availableWidth <= 0 || availableHeight <= 0)
            return;

        _imageScale = Math.Min(
            availableWidth / _frame.PixelWidth,
            availableHeight / _frame.PixelHeight);
        if (!double.IsFinite(_imageScale) || _imageScale <= 0)
            return;

        var displayWidth = _frame.PixelWidth * _imageScale;
        var displayHeight = _frame.PixelHeight * _imageScale;
        _imageLeft = (availableWidth - displayWidth) / 2;
        _imageTop = (availableHeight - displayHeight) / 2;
        PreviewImage.Width = displayWidth;
        PreviewImage.Height = displayHeight;
        Canvas.SetLeft(PreviewImage, _imageLeft);
        Canvas.SetTop(PreviewImage, _imageTop);
        UpdateCropVisuals();
    }

    private void UpdateCropVisuals()
    {
        var cropLeft = _imageLeft + (_selection.X * _imageScale);
        var cropTop = _imageTop + (_selection.Y * _imageScale);
        var cropSize = _selection.Size * _imageScale;
        Canvas.SetLeft(CropFrame, cropLeft);
        Canvas.SetTop(CropFrame, cropTop);
        CropFrame.Width = cropSize;
        CropFrame.Height = cropSize;

        var imageBounds = new Rect(
            _imageLeft,
            _imageTop,
            _frame.PixelWidth * _imageScale,
            _frame.PixelHeight * _imageScale);
        var cropBounds = new Rect(
            cropLeft,
            cropTop,
            cropSize,
            cropSize);
        CropShade.Data = new CombinedGeometry(
            GeometryCombineMode.Exclude,
            new RectangleGeometry(imageBounds),
            new RectangleGeometry(cropBounds));
    }

    private void OnCropFrameMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        BeginDrag(OrbCropHandle.Move, CropFrame, e);
        e.Handled = true;
    }

    private void OnCropHandleMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement handle ||
            !Enum.TryParse<OrbCropHandle>(
                handle.Tag?.ToString(),
                out var cropHandle))
        {
            return;
        }

        BeginDrag(cropHandle, handle, e);
        e.Handled = true;
    }

    private void BeginDrag(
        OrbCropHandle handle,
        UIElement captureElement,
        MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;
        _dragHandle = handle;
        _dragStart = e.GetPosition(ImageCanvas);
        _dragStartSelection = _selection;
        captureElement.CaptureMouse();
    }

    private void OnImageCanvasPreviewMouseMove(
        object sender,
        MouseEventArgs e)
    {
        if (_dragHandle is null ||
            e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        UpdateDragSelection(e.GetPosition(ImageCanvas));
    }

    private void OnImageCanvasPreviewMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;
        UpdateDragSelection(e.GetPosition(ImageCanvas));
        _dragHandle = null;
        if (Mouse.Captured is not null && Mouse.Captured != ImageCanvas)
            Mouse.Captured.ReleaseMouseCapture();
    }

    private void UpdateDragSelection(Point current)
    {
        var dragHandle = _dragHandle;
        if (dragHandle is null || _imageScale <= 0)
        {
            return;
        }

        var deltaX = (int)Math.Round(
            (current.X - _dragStart.X) / _imageScale);
        var deltaY = (int)Math.Round(
            (current.Y - _dragStart.Y) / _imageScale);
        _selection = dragHandle == OrbCropHandle.Move
            ? _dragStartSelection.Move(
                deltaX,
                deltaY,
                _frame.PixelWidth,
                _frame.PixelHeight)
            : _dragStartSelection.Resize(
                dragHandle.Value,
                deltaX,
                deltaY,
                _frame.PixelWidth,
                _frame.PixelHeight);
        UpdateCropVisuals();
        UpdateCropSizeText();
    }

    private void UpdateCropSizeText()
    {
        CropSizeText.Text =
            $"裁切区域：{_selection.Size} × {_selection.Size} px";
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
