using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using FloatDesk.App.Views;

namespace FloatDesk.Ui.Tests;

[Collection("WPF")]
public sealed class ColorPickerFieldTests
{
    private static readonly string[] HsvSliderNames = { "HueSlider", "SaturationSlider", "BrightnessSlider" };

    [Theory]
    [InlineData("#FF0000", 0, 1, 1, 255)]
    [InlineData("#8000FF00", 120, 1, 1, 128)]
    [InlineData("#000000FF", 240, 1, 1, 0)]
    [InlineData("#808080", 0, 0, 0.5019607843137255, 255)]
    public void HexConvertsToHsvAndBack(string hex, double hue, double saturation, double value, byte alpha)
    {
        Assert.True(ColorPickerMath.TryParse(hex, out var color));
        var hsv = ColorPickerMath.ToHsv(color);
        Assert.Equal(hue, hsv.Hue, 6);
        Assert.Equal(saturation, hsv.Saturation, 6);
        Assert.Equal(value, hsv.Value, 6);
        Assert.Equal(alpha, color.A);
        Assert.Equal(color, ColorPickerMath.FromHsv(hsv.Hue, hsv.Saturation, hsv.Value, color.A));
    }

    [Theory]
    [InlineData(0, 255, 0, 0)]
    [InlineData(60, 255, 255, 0)]
    [InlineData(120, 0, 255, 0)]
    [InlineData(180, 0, 255, 255)]
    [InlineData(240, 0, 0, 255)]
    [InlineData(300, 255, 0, 255)]
    [InlineData(360, 255, 0, 0)]
    public void HueSectorsKeepAlpha(double hue, byte red, byte green, byte blue)
        => Assert.Equal(Color.FromArgb(73, red, green, blue), ColorPickerMath.FromHsv(hue, 1, 1, 73));

    [Theory]
    [InlineData("")]
    [InlineData("#")]
    [InlineData("#12345")]
    [InlineData("#GG1122")]
    [InlineData("red")]
    [InlineData(" #FFFFFF")]
    [InlineData("#123456789")]
    public void RejectsIncompleteAndNonHexInput(string text)
        => Assert.False(ColorPickerMath.TryParse(text, out _));

    [Fact]
    public void DefaultIsWhiteAndValueBindsTwoWaysByDefault() => OnSta(() =>
    {
        var field = new ColorPickerField();
        Assert.Equal("#FFFFFF", field.Value);
        Assert.True(((FrameworkPropertyMetadata)ColorPickerField.ValueProperty.GetMetadata(typeof(ColorPickerField))).BindsTwoWayByDefault);
    });

    [Fact]
    public void InvalidTextReachesBoundSourceWithoutReplacingLastSwatch() => OnSta(() =>
    {
        var source = new ColorSource { Value = "#804080C0" };
        var field = BoundField(source);
        var editor = (TextBox)field.FindName("HexTextBox");
        var swatch = (Border)field.FindName("SwatchPreview");
        var previous = ((SolidColorBrush)swatch.Background).Color;
        editor.Text = "#12";
        Assert.Equal("#12", source.Value);
        Assert.Equal("#12", field.Value);
        Assert.Equal(previous, ((SolidColorBrush)swatch.Background).Color);
        source.Value = "#00FF00";
        field.GetBindingExpression(ColorPickerField.ValueProperty)!.UpdateTarget();
        Assert.Equal("#00FF00", editor.Text);
        Assert.Equal(Colors.Lime, ((SolidColorBrush)swatch.Background).Color);
    });

    [Fact]
    public void VisualEditsPreserveBindingAndAlphaIncludingPresetSelection() => OnSta(() =>
    {
        var source = new ColorSource { Value = "#804080C0" };
        var field = BoundField(source);
        foreach (var name in HsvSliderNames)
        {
            var slider = (Slider)field.FindName(name);
            slider.Value = name == "HueSlider" ? 120 : 0.75;
            Assert.True(BindingOperations.IsDataBound(field, ColorPickerField.ValueProperty));
            Assert.Equal(field.Value, source.Value);
            Assert.True(ColorPickerMath.TryParse(source.Value, out var color));
            Assert.Equal(128, color.A);
        }
        ((Slider)field.FindName("AlphaSlider")).Value = 37;
        var presets = (Panel)field.FindName("PresetPanel");
        ((Button)presets.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(ColorPickerMath.TryParse(source.Value, out var selected));
        Assert.Equal(37, selected.A);
        Assert.Equal(Colors.White.R, selected.R);
        Assert.Equal(Colors.White.G, selected.G);
        Assert.Equal(Colors.White.B, selected.B);
        Assert.True(BindingOperations.IsDataBound(field, ColorPickerField.ValueProperty));
    });

    private static ColorPickerField BoundField(ColorSource source)
    {
        var field = new ColorPickerField();
        field.SetBinding(ColorPickerField.ValueProperty, new Binding(nameof(ColorSource.Value)) { Source = source });
        return field;
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "STA color-picker test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class ColorSource : INotifyPropertyChanged
    {
        private string value = "#FFFFFF";
        public string Value
        {
            get => value;
            set { this.value = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
