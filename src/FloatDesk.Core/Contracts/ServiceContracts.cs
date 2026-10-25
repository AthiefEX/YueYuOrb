using FloatDesk.Core.Models;

namespace FloatDesk.Core.Contracts;

public interface IForegroundMonitor : IDisposable
{
    event Action<ForegroundSnapshot>? Changed;
    ForegroundSnapshot Current { get; }
    void Start();
    Task<ForegroundSnapshot> RefreshAsync(CancellationToken cancellationToken);
}

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken);
    Task SaveAsync(AppSettings value, CancellationToken cancellationToken);
}

public interface ISettingsCoordinator
{
    AppSettings Current { get; }
    event Action<AppSettings>? Changed;
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<OperationResult> UpdateAsync(
        Func<AppSettings, AppSettings> update,
        CancellationToken cancellationToken);
}

public interface IAppLauncher
{
    Task<OperationResult> LaunchAsync(LauncherItem item, CancellationToken cancellationToken);
}

public interface INoteRepository
{
    Task<IReadOnlyList<NoteSummary>> SearchAsync(
        string query,
        bool recycleBin,
        CancellationToken cancellationToken);
    Task<NoteDocument?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<OperationResult> SaveAsync(NoteDocument note, CancellationToken cancellationToken);
    Task<OperationResult> SetDeletedAsync(Guid id, bool deleted, CancellationToken cancellationToken);
    Task<OperationResult> PurgeDeletedAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken);
}

public enum ToolWindow { QuickPanel, Settings, AiBrowser, Notes }

public interface IWindowCoordinator
{
    Task<IAsyncDisposable> HideForCaptureAsync(CancellationToken cancellationToken);
    void ShowTool(ToolWindow tool);
}

public interface IHotkeyService : IDisposable
{
    event Action<HotkeyAction>? Pressed;
    OperationResult Rebind(HotkeyAction action, HotkeyChord chord);
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}
