using System.Windows.Threading;
using System.Threading;
using FloatDesk.App;
using FloatDesk.App.Views;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;
using FloatDesk.Core.Translation;

namespace SettingsWindowProbe;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        try
        {
            var app = new App
            {
                ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown
            };
            app.InitializeComponent();
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(
                    Dispatcher.CurrentDispatcher));
            VerifyWindow(AppSettings.CreateDefault());
            VerifyWindow(CreateDesktopSessionSettings());
            app.Shutdown();
            Console.WriteLine("Settings window lifecycle PASS.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static void VerifyWindow(AppSettings settings)
    {
        var window = new SettingsWindow(
            new FixedSettingsCoordinator(settings),
            new AppDataPaths(),
            currentAppPath: null,
            new NullCredentialStore(),
            new NullTranslationProviderFactory(),
            () => []);
        window.Show();
        window.UpdateLayout();
        window.Dispatcher.Invoke(
            () => { },
            DispatcherPriority.ApplicationIdle);
        for (var i = 0; i < 4; i++)
        {
            window.Hide();
            window.Show();
            window.UpdateLayout();
            window.Dispatcher.Invoke(
                () => { },
                DispatcherPriority.ApplicationIdle);
        }
        window.Close();
        if (window.IsVisible)
            throw new InvalidOperationException("Settings window did not close.");
    }

    private static AppSettings CreateDesktopSessionSettings()
    {
        var environmentId = Guid.NewGuid();
        return AppSettings.CreateDefault() with
        {
            DesktopSessions = new DesktopSessionSettings(
                true,
                [
                    new EnvironmentProfile(
                        environmentId,
                        "测试环境",
                        DesktopSessionBackendKind.LocalChild,
                        "local-child",
                        SessionStartupPolicy.EnvironmentDefault,
                        new SessionLimits(),
                        [])
                ],
                [
                    new DesktopSessionTask(
                        DesktopSessionFormat.CurrentTaskFormatVersion,
                        Guid.NewGuid(),
                        "测试任务",
                        environmentId,
                        [
                            new SessionTaskStep(
                                Guid.NewGuid(),
                                SessionCommandKind.Delay,
                                new Dictionary<string, string>
                                {
                                    ["milliseconds"] = "1000"
                                },
                                TimeoutSeconds: 30,
                                RetryCount: 0)
                        ],
                        new SessionTaskTrigger(
                            TaskTriggerKind.Manual,
                            null,
                            TimeZoneInfo.Local.Id),
                        Revision: 1,
                        Enabled: true)
                ])
        };
    }

    private sealed class FixedSettingsCoordinator(AppSettings settings)
        : ISettingsCoordinator
    {
        public AppSettings Current { get; } = settings;
        public event Action<AppSettings>? Changed
        {
            add { }
            remove { }
        }

        public Task InitializeAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<OperationResult> UpdateAsync(
            Func<AppSettings, AppSettings> update,
            CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Ok());
    }

    private sealed class NullCredentialStore : ITranslationCredentialStore
    {
        public Task<bool> HasAsync(
            TranslationProviderKind provider,
            string endpoint,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<string?> GetAsync(
            TranslationProviderKind provider,
            string endpoint,
            CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);

        public Task SaveAsync(
            TranslationProviderKind provider,
            string endpoint,
            string apiKey,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task DeleteAsync(
            TranslationProviderKind provider,
            string endpoint,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class NullTranslationProviderFactory
        : ITranslationProviderFactory
    {
        public ITranslationProvider Create(
            TranslationSettings settings,
            string? apiKey) =>
            new NullTranslationProvider();
    }

    private sealed class NullTranslationProvider : ITranslationProvider
    {
        public string Id => "test";
        public string DisplayName => "Test";
        public TranslationProviderCapabilities Capabilities { get; } =
            new(false, 1_000, 1_000, 1, 1, TimeSpan.Zero);

        public Task<TranslationProviderResult> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(TranslationProviderResult.Ok(""));
    }
}
