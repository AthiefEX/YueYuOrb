using FloatDesk.App.DesktopSessions;
using FloatDesk.Core.Models;
using FloatDesk.Windows.DesktopSessions;

namespace FloatDesk.Ui.Tests;

public sealed class DesktopSessionDiagnosticsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "FloatDeskDesktopDiagnostics",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ReportsBundledToolsAndUnavailableRuntimeWithoutProbing()
    {
        CreateTool("agent", "YueYuOrb.SessionAgent.exe");
        CreateTool("client", "YueYuOrb.SessionClient.exe");
        var diagnostics = new DesktopSessionDiagnostics(_root);

        var items = diagnostics.Inspect(
            AppSettings.CreateDefault().DesktopSessions,
            runtimeAvailable: false);

        Assert.Contains(
            items,
            item => item.Name == "功能开关" &&
                    item.State == "关闭");
        Assert.Contains(
            items,
            item => item.Name == "会话运行时" &&
                    item.State == "未准备");
        Assert.Contains(
            items,
            item => item.Name == "随包工具" &&
                    item.State == "完整");
        Assert.Contains(
            items,
            item => item.Name == "本机子会话" &&
                    item.State == "不可用");
    }

    [Fact]
    public void ReportsMissingToolsAndBroker()
    {
        var diagnostics = new DesktopSessionDiagnostics(_root);

        var items = diagnostics.Inspect(
            AppSettings.CreateDefault().DesktopSessions,
            runtimeAvailable: false);

        Assert.Contains(
            items,
            item => item.Name == "随包工具" &&
                    item.State == "不完整");
        Assert.Contains(
            items,
            item => item.Name == "窄权限准备组件" &&
                    item.State == "未提供");
    }

    [Fact]
    public void RuntimeWithoutBrokerKeepsBothBackendsUnavailable()
    {
        CreateTool("agent", "YueYuOrb.SessionAgent.exe");
        CreateTool("client", "YueYuOrb.SessionClient.exe");
        var diagnostics = new DesktopSessionDiagnostics(_root);

        var items = diagnostics.Inspect(
            AppSettings.CreateDefault().DesktopSessions,
            runtimeAvailable: true);

        Assert.Contains(
            items,
            item => item.Name == "本机子会话" &&
                    item.State == "不可用");
        Assert.Contains(
            items,
            item => item.Name == "Hyper-V" &&
                    item.State == "不可用");
        Assert.Contains(
            items,
            item => item.Name == "输入隔离" &&
                    item.State == "未开放");
    }

    [Fact]
    public void BrokerDoesNotMakeHyperVAppearAvailable()
    {
        CreateTool("agent", "YueYuOrb.SessionAgent.exe");
        CreateTool("client", "YueYuOrb.SessionClient.exe");
        CreateTool("broker", "YueYuOrb.SessionBroker.exe");
        var diagnostics = new DesktopSessionDiagnostics(_root);

        var items = diagnostics.Inspect(
            AppSettings.CreateDefault().DesktopSessions,
            runtimeAvailable: true);

        Assert.Contains(
            items,
            item => item.Name == "本机子会话" &&
                    item.State == "实验性");
        Assert.Contains(
            items,
            item => item.Name == "Hyper-V" &&
                    item.State == "不可用" &&
                    item.Detail.Contains(
                        "尚未读取",
                        StringComparison.Ordinal));
    }

    [Fact]
    public void ReportsConfiguredHyperVHostAndKeepsGuestPreparationExplicit()
    {
        var diagnostics = new DesktopSessionDiagnostics(_root);
        var items = diagnostics.Inspect(
            AppSettings.CreateDefault().DesktopSessions,
            runtimeAvailable: true,
            new HyperVFeatureStatus(true, true, true, true, false, null));

        var hyperV = Assert.Single(items, item => item.Name == "Hyper-V");
        Assert.Equal("宿主已就绪", hyperV.State);
        Assert.Contains("来宾 Agent", hyperV.Detail, StringComparison.Ordinal);
    }

    private void CreateTool(string folder, string file)
    {
        var directory = Path.Combine(
            _root,
            "desktop-sessions",
            folder);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, file), "");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
