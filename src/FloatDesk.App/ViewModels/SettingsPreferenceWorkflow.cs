using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;

namespace FloatDesk.App.ViewModels;

internal sealed class SettingsPreferenceWorkflow
{
    private readonly ISettingsCoordinator _settings;
    private readonly SettingsReloadCoordinator _reload;
    private readonly Func<bool, Task> _setStartupEnabled;
    private readonly Action<AppSettings> _applied;

    public SettingsPreferenceWorkflow(
        ISettingsCoordinator settings,
        SettingsReloadCoordinator reload,
        Action<bool> setStartupEnabled,
        Action<AppSettings> applied,
        Func<bool, Task>? setStartupEnabledAsync = null)
    {
        _settings = settings ??
            throw new ArgumentNullException(nameof(settings));
        _reload = reload ??
            throw new ArgumentNullException(nameof(reload));
        ArgumentNullException.ThrowIfNull(setStartupEnabled);
        _setStartupEnabled = setStartupEnabledAsync ?? (enabled => { setStartupEnabled(enabled); return Task.CompletedTask; });
        _applied = applied ??
            throw new ArgumentNullException(nameof(applied));
    }

    public async Task<OperationResult> ImportAsync(
        AppSettings imported,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(imported);
        var result = await _settings.UpdateAsync(
            current => imported with { Windows = current.Windows },
            cancellationToken);
        if (!result.Success)
            return result;

        cancellationToken.ThrowIfCancellationRequested();
        await _setStartupEnabled(imported.General.StartWithWindows);
        await _reload.ReloadAsync(
            savedSnapshot: _settings.Current,
            cancellationToken);
        _applied(_settings.Current);
        return OperationResult.Ok("设置已导入并保存；快捷项未执行。");
    }

    public async Task<OperationResult> ResetAsync(
        AppSettings defaults,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        var result = await _settings.UpdateAsync(
            current => defaults with { Windows = current.Windows },
            cancellationToken);
        if (!result.Success)
            return result;

        cancellationToken.ThrowIfCancellationRequested();
        await _setStartupEnabled(false);
        await _reload.ReloadAsync(
            savedSnapshot: _settings.Current,
            cancellationToken);
        _applied(_settings.Current);
        return OperationResult.Ok("已恢复默认偏好。");
    }
}
