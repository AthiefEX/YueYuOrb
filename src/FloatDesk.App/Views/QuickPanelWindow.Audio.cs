using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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
            AddAudioButton("播放设备", "选择播放设备、调整音量", () => AudioRequested?.Invoke());
        foreach (var group in settings.AudioGroups.Where(g => g.ShowInWheel))
            AddAudioButton(group.Name, "切换到组内下一个可用播放设备", () => AudioGroupRequested?.Invoke(group));
    }

    private void AddAudioButton(string name, string tooltip, Action action)
    {
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.Children.Add(new TextBlock { Text = "♫", FontSize = 24, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center });
        var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 0, 10, 0) };
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
