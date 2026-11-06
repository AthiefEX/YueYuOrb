using System.ComponentModel;
using System.Windows;
using FloatDesk.App.DesktopSessions;

namespace FloatDesk.App.Views;

public partial class DesktopSessionWindow : Window
{
    private bool _allowClose;

    public DesktopSessionWindow(
        IRdpControl control,
        string environmentName)
    {
        ArgumentNullException.ThrowIfNull(control);
        InitializeComponent();
        EnvironmentNameText.Text = environmentName;
        Title = $"桌面分身 - {environmentName}";
        if (control is IRdpControlView controlView)
        {
            RdpHost.Child = controlView.View;
            PlaceholderPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            SetStatus("当前控件不支持嵌入显示");
        }

        Closing += OnClosing;
    }

    public void SetStatus(string status) =>
        StatusText.Text = status;

    public void AllowClose() => _allowClose = true;

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
            return;

        e.Cancel = true;
        Hide();
    }
}
