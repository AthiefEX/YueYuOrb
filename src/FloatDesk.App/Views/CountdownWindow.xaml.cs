using System.Windows;

namespace FloatDesk.App.Views;

public partial class CountdownWindow : Window
{
    private readonly TaskCompletionSource<bool> _completed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CountdownWindow()
    {
        InitializeComponent();
        Closing += (_, args) =>
        {
            args.Cancel = true;
            _completed.TrySetResult(false);
            Hide();
        };
        Loaded += (_, _) => Activate();
    }

    public Task<bool> Completed => _completed.Task;

    public void SetCountdown(int seconds) => CountText.Text = seconds.ToString(
        System.Globalization.CultureInfo.InvariantCulture);

    public void Finish()
    {
        _completed.TrySetResult(true);
        Hide();
    }

    private void OnPreviewKeyDown(
        object sender,
        System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape)
            return;
        _completed.TrySetResult(false);
        Hide();
        e.Handled = true;
    }
}
