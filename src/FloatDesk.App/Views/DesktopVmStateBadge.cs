using FloatDesk.Windows.DesktopSessions;

namespace FloatDesk.App.Views;

/// <summary>Colour and wording for one environment state dot.</summary>
public sealed record DesktopVmStateBadge(string Text, uint Argb);

/// <summary>
/// Turns a snapshot into what the environment list shows. Kept apart from the window so the rule
/// the report complains about — a stopped machine must never keep a green "running" dot — can be
/// asserted without a live Hyper-V host.
/// </summary>
public static class DesktopVmStateBadges
{
    /// <summary>
    /// A snapshot older than this is not trusted. The dot turns grey rather than keeping a
    /// reassuring green that may describe a machine stopped minutes ago.
    /// </summary>
    public static readonly TimeSpan SnapshotLifetime = TimeSpan.FromSeconds(20);

    private const uint UnknownArgb = 0xFF808080;

    public static DesktopVmStateBadge For(bool known, HyperVVirtualMachineState state, TimeSpan age)
    {
        if (!known) return new("状态未确认", UnknownArgb);
        if (age > SnapshotLifetime) return new("状态待确认（超过 20 秒未能刷新）", UnknownArgb);
        return state switch
        {
            HyperVVirtualMachineState.Running => new("运行中", 0xFF3CB371),
            HyperVVirtualMachineState.Saved => new("已保存，尚未启动", 0xFF1E90FF),
            HyperVVirtualMachineState.Off => new("已关闭", 0xFFCD5C5C),
            HyperVVirtualMachineState.Paused => new("已暂停", 0xFFDAA520),
            _ => new("状态未确认", UnknownArgb)
        };
    }
}
