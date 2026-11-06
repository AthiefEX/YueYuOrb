using FloatDesk.Core.Models;

namespace FloatDesk.App.DesktopSessions;

public sealed record DesktopSessionDiagnostic(
    string Name,
    string State,
    string Detail);

public sealed class DesktopSessionDiagnostics
{
    private readonly string _baseDirectory;

    public DesktopSessionDiagnostics(string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        _baseDirectory = Path.GetFullPath(baseDirectory);
    }

    public IReadOnlyList<DesktopSessionDiagnostic> Inspect(
        DesktopSessionSettings settings,
        bool runtimeAvailable)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var agent = ToolExists(
            "agent",
            "YueYuOrb.SessionAgent.exe");
        var client = ToolExists(
            "client",
            "YueYuOrb.SessionClient.exe");
        var broker = ToolExists(
            "broker",
            "YueYuOrb.SessionBroker.exe");
        var localChildReady = runtimeAvailable && broker;
        return
        [
            new(
                "功能开关",
                settings.Enabled ? "已启用" : "关闭",
                settings.Enabled
                    ? "不会在月语启动时自动创建会话；仍需显式打开视图或运行任务。"
                    : "默认关闭；启动月语不会探测 Hyper-V、创建子会话或启动虚拟机。"),
            new(
                "会话运行时",
                runtimeAvailable ? "已接入" : "未准备",
                runtimeAvailable
                    ? "生产宿主已接入，操作仍受后端能力与环境状态约束。"
                    : "当前进程未配置生产宿主；所有运行请求会明确拒绝，不会回退主桌面。"),
            new(
                "随包工具",
                agent && client ? "完整" : "不完整",
                agent && client
                    ? "会话助手与任务客户端已随当前包提供。"
                    : "缺少会话助手或任务客户端；请使用完整发布包。"),
            new(
                "窄权限准备组件",
                broker ? "已随包提供" : "未提供",
                broker
                    ? "仅允许验证父/子会话、启动固定路径助手和交付一次性引导秘密。"
                    : "本机子会话引导所需的 broker 尚未随当前构建提供。"),
            new(
                "本机子会话",
                localChildReady ? "实验性" : "不可用",
                localChildReady
                    ? "仍需完成 WTS、RDP ActiveX、锁屏和混合 DPI 实机验收。"
                    : "运行时或窄权限准备组件未就绪。"),
            new(
                "Hyper-V",
                "不可用",
                "生产 Hyper-V 执行宿主与来宾身份探测尚未接入；不会从主桌面替代执行。"),
            new(
                "输入隔离",
                localChildReady ? "强制校验" : "未开放",
                localChildReady
                    ? "任务不使用宿主 SendInput；无受信助手会话时拒绝执行。"
                    : "完整运行时准备前不开放输入自动化。")
        ];
    }

    private bool ToolExists(string folder, string fileName) =>
        File.Exists(Path.Combine(
            _baseDirectory,
            "desktop-sessions",
            folder,
            fileName));
}
