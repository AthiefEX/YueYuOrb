namespace FloatDesk.Core.Models;

public static class DesktopSessionResolutionPreferences
{
    public static void Validate(int width, int height)
    {
        if (width is < 800 or > 3840 || height is < 600 or > 2160)
            throw new ArgumentOutOfRangeException(nameof(width), "分身分辨率需在800×600至3840×2160之间。");
    }

    public static DesktopSessionSettings Update(DesktopSessionSettings settings, Guid environmentId, int width, int height)
    {
        Validate(width, height);
        if (!settings.Environments.Any(environment => environment.Id == environmentId))
            throw new InvalidOperationException("分身环境已删除，无法保存分辨率。");
        return settings with
        {
            Environments = settings.Environments.Select(environment => environment.Id == environmentId
                ? environment with { DesktopWidth = width, DesktopHeight = height } : environment).ToArray()
        };
    }
}
