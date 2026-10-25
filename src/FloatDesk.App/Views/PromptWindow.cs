using System.Windows;
using System.Windows.Controls;

namespace FloatDesk.App.Views;

public sealed class PromptWindow : Window
{
    private readonly System.Windows.Controls.TextBox _first;
    private readonly System.Windows.Controls.TextBox _second;

    public PromptWindow(
        string title,
        string firstLabel,
        string firstHint,
        string secondLabel,
        string secondHint,
        bool isSelection = false)
    {
        Title = title;
        Width = 440;
        Height = 245;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        _first = new System.Windows.Controls.TextBox { Text = isSelection ? "" : firstHint, Margin = new Thickness(0, 4, 0, 12) };
        _second = new System.Windows.Controls.TextBox { Text = secondHint, Margin = new Thickness(0, 4, 0, 16) };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = firstLabel });
        panel.Children.Add(_first);
        panel.Children.Add(new TextBlock { Text = secondLabel });
        panel.Children.Add(_second);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var cancel = new Button { Content = "取消", Width = 76, Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => DialogResult = false;
        var ok = new Button { Content = "确定", Width = 76 };
        ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);
        Content = panel;
    }

    public string FirstValue => _first.Text;
    public string SecondValue => _second.Text;
}
