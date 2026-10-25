using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;

namespace FloatDesk.Infrastructure.Settings;

public sealed class SettingsCoordinator : ISettingsCoordinator, IDisposable
{
    private readonly ISettingsStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppSettings _current = AppSettings.CreateDefault();
    private bool _initialized;

    public SettingsCoordinator(ISettingsStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    public AppSettings Current => _current;
    public event Action<AppSettings>? Changed;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
                return;
            _current = await _store.LoadAsync(cancellationToken);
            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke(_current);
    }

    public async Task<OperationResult> UpdateAsync(
        Func<AppSettings, AppSettings> update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_initialized)
                return OperationResult.Fail("SettingsNotInitialized", "设置尚未初始化。");
            var candidate = update(_current);
            var errors = SettingsValidator.Validate(candidate);
            if (errors.Count > 0)
                return OperationResult.Fail("InvalidSettings", string.Join(" ", errors));
            await _store.SaveAsync(candidate, cancellationToken);
            _current = candidate;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return OperationResult.Fail("SettingsSaveFailed", exception.Message);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(_current);
        return OperationResult.Ok("设置已保存。");
    }

    public void Dispose() => _gate.Dispose();
}
