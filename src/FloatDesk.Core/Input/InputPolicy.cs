using FloatDesk.Core.Models;

namespace FloatDesk.Core.Input;

public static class InputPolicy
{
    public static InputDecision Decide(
        ForegroundSnapshot target,
        InputContext context,
        IReadOnlyList<AppRule> rules)
    {
        if (context.SessionLocked)
            return new(InputAction.None, "锁屏期间暂停", true);
        if (context.EmergencyPaused)
            return new(InputAction.None, "紧急暂停", true);
        if (context.CapturePaused)
            return new(InputAction.None, "截图期间暂停", true);
        if (target.IsOwnInteractive)
            return new(InputAction.None, $"{ProductInfo.Name}自身窗口允许中文输入", true);
        if (string.IsNullOrWhiteSpace(target.ExePath))
            return new(InputAction.None, "无法识别目标应用路径", true);

        var matchingRules = rules
            .Where(rule => rule.Enabled && PathEquals(rule.ExePath, target.ExePath))
            .ToArray();
        if (matchingRules.Any(rule => rule.Action == InputAction.Exclude))
            return new(InputAction.Exclude, "应用排除规则", true);
        if (context.ManualEnabled)
            return new(InputAction.LockEnglish, "手动英文锁定");
        if (context.AutoEnabled && matchingRules.Any(rule => rule.Action == InputAction.LockEnglish))
            return new(InputAction.LockEnglish, "应用自动英文规则");
        return new(InputAction.None, "未启用英文锁定");
    }

    private static bool PathEquals(string first, string second)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(first),
                Path.GetFullPath(second),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
        }
    }
}
