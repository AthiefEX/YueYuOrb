using System.Drawing;
using System.Windows.Forms;
using FloatDesk.Core;

namespace FloatDesk.App;

public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _subtitlePauseItem;
    private readonly ToolStripMenuItem _subtitleReselectItem;
    private readonly ToolStripMenuItem _subtitleStopItem;
    private readonly SynchronizationContext? _synchronizationContext;
    private readonly int _createdThreadId;

    public TrayService(
        Action openPanel,
        Action openSettings,
        Action openNotes,
        Action openBrowser,
        Action captureRegion,
        Action togglePause,
        Action exit,
        Action toggleOrb,
        Action toggleSubtitlePause,
        Action reselectSubtitleRegion,
        Action stopSubtitles,
        Action? extractRegionText = null,
        Action? toggleNightMode = null,
        Action? recoverBrightness = null)
    {
        _synchronizationContext = SynchronizationContext.Current;
        _createdThreadId = Environment.CurrentManagedThreadId;
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示/隐藏悬浮球", null, (_, _) => toggleOrb());
        menu.Items.Add("打开快捷面板", null, (_, _) => openPanel());
        menu.Items.Add("设置", null, (_, _) => openSettings());
        menu.Items.Add("AI 网页", null, (_, _) => openBrowser());
        menu.Items.Add("记事本", null, (_, _) => openNotes());
        if (toggleNightMode is not null) menu.Items.Add("切换黑夜模式", null, (_, _) => toggleNightMode());
        if (recoverBrightness is not null) menu.Items.Add("恢复可见亮度", null, (_, _) => recoverBrightness());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("框选截图", null, (_, _) => captureRegion());
        if (extractRegionText is not null)
            menu.Items.Add("框选提取文字", null, (_, _) => extractRegionText());
        _subtitlePauseItem = new ToolStripMenuItem(
            "暂停字幕翻译",
            null,
            (_, _) => toggleSubtitlePause());
        _subtitleReselectItem = new ToolStripMenuItem(
            "重新选择字幕区域",
            null,
            (_, _) => reselectSubtitleRegion());
        _subtitleStopItem = new ToolStripMenuItem(
            "停止字幕翻译",
            null,
            (_, _) => stopSubtitles());
        var subtitleMenu = new ToolStripMenuItem("字幕翻译");
        subtitleMenu.DropDownItems.Add(_subtitlePauseItem);
        subtitleMenu.DropDownItems.Add(_subtitleReselectItem);
        subtitleMenu.DropDownItems.Add(new ToolStripSeparator());
        subtitleMenu.DropDownItems.Add(_subtitleStopItem);
        menu.Items.Add(subtitleMenu);
        menu.Items.Add("暂停/恢复英文规则", null, (_, _) => togglePause());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => exit());

        _icon = new NotifyIcon
        {
            Icon = AppIcon.Create(),
            Text = $"{ProductInfo.Name}悬浮助手",
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => openPanel();
        UpdateSubtitleSession(running: false, paused: false);
    }

    public void UpdateSubtitleSession(bool running, bool paused)
    {
        RunOnCreatedThread(() =>
        {
            var state = new SubtitleTrayState(running, paused);
            _subtitlePauseItem.Text = state.PauseHeader;
            _subtitlePauseItem.Enabled = state.CommandsEnabled;
            _subtitleReselectItem.Enabled = state.CommandsEnabled;
            _subtitleStopItem.Enabled = state.CommandsEnabled;
        });
    }

    public void ShowWarning(string title, string message)
    {
        RunOnCreatedThread(() =>
            _icon.ShowBalloonTip(
                6000,
                title,
                message,
                ToolTipIcon.Warning));
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    private void RunOnCreatedThread(Action action)
    {
        if (Environment.CurrentManagedThreadId == _createdThreadId)
        {
            action();
            return;
        }

        _synchronizationContext?.Post(_ => action(), null);
    }
}

internal static class AppIcon
{
    public static Icon Create()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var background = new SolidBrush(
                System.Drawing.Color.FromArgb(15, 81, 68));
            graphics.FillEllipse(background, 1, 1, 30, 30);

            using var crescent = new System.Drawing.Drawing2D.GraphicsPath(
                System.Drawing.Drawing2D.FillMode.Alternate);
            crescent.AddEllipse(5, 4, 20, 20);
            crescent.AddEllipse(11, 1, 19, 19);
            using var moon = new SolidBrush(
                System.Drawing.Color.FromArgb(246, 241, 224));
            graphics.FillPath(moon, crescent);

            using var accent = new SolidBrush(
                System.Drawing.Color.FromArgb(225, 178, 96));
            graphics.FillEllipse(accent, 21, 20, 7, 7);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }
}
