using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace FloatDesk.App.Views;

/// <summary>A bindable hex editor with a modeless HSV and alpha picker.</summary>
public partial class ColorPickerField : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(ColorPickerField),
        new FrameworkPropertyMetadata("#FFFFFF", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnValueChanged));

    private bool synchronizing;
    private bool ready;
    private Color selectedColor = Colors.White;

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public ColorPickerField()
    {
        InitializeComponent();
        ApplyThemeFallbacks();
        foreach (var hex in new[] { "#FFFFFF", "#000000", "#EF4444", "#F59E0B", "#22C55E", "#06B6D4", "#3B82F6", "#A855F7" })
        {
            if (!ColorPickerMath.TryParse(hex, out var color))
                throw new InvalidOperationException("Invalid built-in color preset: " + hex);
            var button = new Button
            {
                Width = 26, Height = 26, Margin = new Thickness(2), Padding = new Thickness(2),
                Content = new Border { Background = new SolidColorBrush(color), Width = 18, Height = 18 },
                Tag = color, ToolTip = hex
            };
            AutomationProperties.SetName(button, "预设颜色 " + hex);
            button.Click += PresetClicked;
            PresetPanel.Children.Add(button);
        }
        ready = true;
        SynchronizeValue();
        Unloaded += (_, _) => PickerPopup.IsOpen = false;
    }

    // A style setter is the fallback; the dynamic resource remains a local expression,
    // so host theme resources can replace it later without being shadowed here.
    private static void Themed(FrameworkElement target, DependencyProperty property, object key, object fallback)
    {
        var style = new Style(target.GetType(), target.Style);
        style.Setters.Add(new Setter(property, fallback));
        target.Style = style;
        target.SetResourceReference(property, key);
    }

    private void ApplyThemeFallbacks()
    {
        Themed(PopupCard, Border.BackgroundProperty, "SettingsCardBrush", SystemColors.WindowBrush);
        PopupCard.SetResourceReference(Border.BorderBrushProperty, "SettingsLineBrush");
        Themed(SwatchButton, Control.BackgroundProperty, "SettingsFieldBrush", SystemColors.ControlBrush);
        SwatchButton.SetResourceReference(Control.ForegroundProperty, "SettingsInkBrush");
        var fallback = new Style(typeof(TextBox));
        fallback.Setters.Add(new Setter(Control.BackgroundProperty, SystemColors.WindowBrush));
        fallback.Setters.Add(new Setter(Control.ForegroundProperty, SystemColors.WindowTextBrush));
        // Missing dynamic styles fall back to the system TextBox style and colors.
        HexTextBox.Resources[typeof(TextBox)] = fallback;
        Themed(PopupCard, TextElement.ForegroundProperty, "SettingsInkBrush", SystemColors.WindowTextBrush);
    }

    private static void OnValueChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var field = (ColorPickerField)sender;
        if (field.ready && !field.synchronizing) field.SynchronizeValue();
    }

    private void SynchronizeValue()
    {
        synchronizing = true;
        try
        {
            if (HexTextBox.Text != Value) HexTextBox.Text = Value ?? string.Empty;
            if (!ColorPickerMath.TryParse(Value, out var color)) return;
            selectedColor = color;
            var hsv = ColorPickerMath.ToHsv(color);
            // Gray/black has no hue; retain the user's hue for the next saturation edit.
            if (hsv.Saturation > 0) HueSlider.Value = hsv.Hue;
            SaturationSlider.Value = hsv.Saturation;
            BrightnessSlider.Value = hsv.Value;
            AlphaSlider.Value = color.A;
            RefreshVisuals();
        }
        finally { synchronizing = false; }
    }

    private void HexTextChanged(object sender, TextChangedEventArgs args)
    {
        if (ready && !synchronizing) SetCurrentValue(ValueProperty, HexTextBox.Text);
    }

    private void ChannelChanged(object sender, RoutedPropertyChangedEventArgs<double> args)
    {
        if (!ready || synchronizing) return;
        selectedColor = ColorPickerMath.FromHsv(HueSlider.Value, SaturationSlider.Value,
            BrightnessSlider.Value, (byte)Math.Round(AlphaSlider.Value));
        PublishVisualColor();
    }

    private void PublishVisualColor()
    {
        synchronizing = true;
        try
        {
            var hex = ColorPickerMath.Format(selectedColor);
            HexTextBox.Text = hex;
            SetCurrentValue(ValueProperty, hex);
            RefreshVisuals();
        }
        finally { synchronizing = false; }
    }

    private void RefreshVisuals()
    {
        var brush = new SolidColorBrush(selectedColor);
        SwatchPreview.Background = brush;
        PopupPreview.Background = brush;
        HueSurface.Background = new SolidColorBrush(ColorPickerMath.FromHsv(HueSlider.Value, 1, 1, 255));
        Canvas.SetLeft(SelectionMarker, SaturationSlider.Value * SaturationValuePanel.ActualWidth - 6);
        Canvas.SetTop(SelectionMarker, (1 - BrightnessSlider.Value) * SaturationValuePanel.ActualHeight - 6);
    }

    private void PresetClicked(object sender, RoutedEventArgs args)
    {
        var preset = (Color)((Button)sender).Tag;
        preset.A = selectedColor.A;
        SetCurrentValue(ValueProperty, ColorPickerMath.Format(preset));
    }

    private void OpenPicker(object sender, RoutedEventArgs args) => PickerPopup.IsOpen = !PickerPopup.IsOpen;
    private void PickerOpened(object? sender, EventArgs args) => HueSlider.Focus();
    private void PickerClosed(object? sender, EventArgs args)
    {
        if (SaturationValuePanel.IsMouseCaptured) SaturationValuePanel.ReleaseMouseCapture();
    }

    private void PopupKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Escape) return;
        PickerPopup.IsOpen = false;
        SwatchButton.Focus();
        args.Handled = true;
    }

    private void PanelMouseDown(object sender, MouseButtonEventArgs args)
    {
        if (SaturationValuePanel.CaptureMouse()) UpdatePanel(args.GetPosition(SaturationValuePanel));
        args.Handled = true;
    }

    private void PanelMouseMove(object sender, MouseEventArgs args)
    {
        if (SaturationValuePanel.IsMouseCaptured && args.LeftButton == MouseButtonState.Pressed)
            UpdatePanel(args.GetPosition(SaturationValuePanel));
    }

    private void PanelMouseUp(object sender, MouseButtonEventArgs args)
    {
        if (!SaturationValuePanel.IsMouseCaptured) return;
        UpdatePanel(args.GetPosition(SaturationValuePanel));
        SaturationValuePanel.ReleaseMouseCapture();
        args.Handled = true;
    }

    private void UpdatePanel(Point point)
    {
        if (SaturationValuePanel.ActualWidth <= 0 || SaturationValuePanel.ActualHeight <= 0) return;
        synchronizing = true;
        try
        {
            SaturationSlider.Value = Math.Clamp(point.X / SaturationValuePanel.ActualWidth, 0, 1);
            BrightnessSlider.Value = 1 - Math.Clamp(point.Y / SaturationValuePanel.ActualHeight, 0, 1);
        }
        finally { synchronizing = false; }
        ChannelChanged(this, new RoutedPropertyChangedEventArgs<double>(0, 0));
    }

    private void PanelSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (ready) RefreshVisuals();
    }
}
