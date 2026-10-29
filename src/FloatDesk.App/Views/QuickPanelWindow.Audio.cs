using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using FloatDesk.Core.Models;

namespace FloatDesk.App.Views;

public partial class QuickPanelWindow
{
    private sealed record AudioWheelTag;
    public event Action? AudioRequested;
    public event Action<AudioCycleGroup>? AudioGroupRequested;

    private void ApplyAudioWheel(AppSettings settings)
    {
        foreach (var button in WheelCanvas.Children.OfType<Button>().Where(b => b.Tag is AudioWheelTag).ToArray())
            WheelCanvas.Children.Remove(button);
        if (settings.ShowAudioInWheel)
            AddAudioButton("播放设备", "选择播放设备、调整音量", "\uE767", "MenuBlueBrush", () => AudioRequested?.Invoke());
        foreach (var group in settings.AudioGroups.Where(g => g.ShowInWheel))
            AddAudioButton(group.Name, "切换到组内下一个可用播放设备", "\uE8AB", "MenuVioletBrush", () => AudioGroupRequested?.Invoke(group));
    }

    private void AddAudioButton(string name, string tooltip, string glyph, string brushKey, Action action)
    {
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
        content.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = new Border
        {
            Width = 42, Height = 42, CornerRadius = new CornerRadius(21),
            Background = (Brush)FindResource(brushKey), BorderThickness = new Thickness(0),
            Effect = (Effect)FindResource("ActionShadow"),
            Child = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                Foreground = Brushes.White, FontSize = 20, VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center }
        };
        content.Children.Add(icon);
        var text = new TextBlock { Text = name, FontSize = 14, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        text.SetResourceReference(TextBlock.ForegroundProperty, "MenuStrongBrush");
        var label = new Border { Margin = new Thickness(6, 3, 0, 3), CornerRadius = new CornerRadius(17),
            Padding = new Thickness(14, 0, 14, 0), BorderThickness = new Thickness(0), Child = text };
        label.SetResourceReference(Border.BackgroundProperty, "MenuLabelBrush");
        Grid.SetColumn(label, 1);
        content.Children.Add(label);
        var button = new Button { Width = WheelButtonWidth, Height = WheelButtonHeight,
            Tag = new AudioWheelTag(), Content = content, ToolTip = tooltip,
            Style = (Style)FindResource("RadialActionButtonStyle") };
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => action();
        WheelCanvas.Children.Add(button);
    }
}
