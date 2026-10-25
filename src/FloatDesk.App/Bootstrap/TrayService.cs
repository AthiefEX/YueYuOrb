using System.Drawing;
using System.Windows.Forms;

namespace FloatDesk.App;

public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayService(
        Action openPanel,
        Action openSettings,
        Action openNotes,
        Action openBrowser,
        Action captureRegion,
        Action togglePause,
        Action exit,
        Action toggleOrb)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("显示/隐藏悬浮球", null, (_, _) => toggleOrb());
        menu.Items.Add("打开快捷面板", null, (_, _) => openPanel());
        menu.Items.Add("设置", null, (_, _) => openSettings());
        menu.Items.Add("AI 网页", null, (_, _) => openBrowser());
        menu.Items.Add("记事本", null, (_, _) => openNotes());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("框选截图", null, (_, _) => captureRegion());
        menu.Items.Add("暂停/恢复英文规则", null, (_, _) => togglePause());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => exit());

        _icon = new NotifyIcon
        {
            Icon = AppIcon.Create(),
            Text = "FloatDesk 悬浮助手",
            Visible = true,
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => openPanel();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
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
            using var brush = new SolidBrush(System.Drawing.Color.FromArgb(20, 108, 91));
            graphics.FillEllipse(brush, 2, 2, 28, 28);
            using var font = new Font("Segoe UI", 11, FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(System.Drawing.Color.White);
            graphics.DrawString("FD", font, textBrush, 5.5f, 8f);
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }
}
