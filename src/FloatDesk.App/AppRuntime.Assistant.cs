using System.Net.Http;
using FloatDesk.App.Assistant;
using FloatDesk.App.Views;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;
using FloatDesk.Infrastructure.Assistant;
using FloatDesk.Windows.Assistant;

namespace FloatDesk.App;

internal sealed partial class AppRuntime
{
    private AssistantStore? _assistantStore;
    private AssistantCredentialStore? _assistantCredentials;
    private AssistantRuntime? _assistantRuntime;
    private HttpClient? _assistantHttp;
    private FloatDesk.Core.Assistant.AssistantSettings? _assistantAppliedSettings;
    private AssistantLearningWorker? _assistantLearningWorker;
    private System.Threading.Timer? _assistantLearningTimer;

    private void InitializeAssistant()
    {
        var directory = Path.Combine(_paths.Root, "assistant");
        _assistantStore = new AssistantStore(Path.Combine(directory, "assistant.db"));
        _assistantCredentials = new AssistantCredentialStore(Path.Combine(directory, "secrets.dat"));
        _assistantHttp = AssistantApiClient.CreateHttpClient();
        var bridge = new AssistantTaskBridge(_settings, _desktopSessions.RunAsync, _desktopSessions.IsEnvironmentReady);
        var model = new AssistantApiClient(_assistantHttp);
        var retrieval = new AssistantRetrievalService(_assistantStore, new AssistantEmbeddingClient(_assistantHttp), _assistantCredentials);
        _assistantRuntime = new AssistantRuntime(() => _settings.Current.Assistant, _assistantStore, model, _assistantCredentials, bridge, retrieval);
        _assistantLearningWorker = new AssistantLearningWorker(_assistantStore, model, _assistantCredentials);
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        _assistantLearningWorker.CandidatesChanged += () => { if (!dispatcher.HasShutdownStarted) _ = dispatcher.BeginInvoke(() => { if (GetTool<SettingsWindow>() is { IsVisible: true } window) window.RefreshAssistantKnowledge(); }); };
        _assistantLearningTimer = new System.Threading.Timer(async _ =>
        {
            try { await _assistantLearningWorker.ProcessOnceAsync(_settings.Current.Assistant, CancellationToken.None); }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or IOException or Microsoft.Data.Sqlite.SqliteException) { }
        }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    private AssistantWindow CreateAssistantWindow()
    {
        var window = new AssistantWindow(_paths, _settings, _assistantStore!, _assistantRuntime!, OpenAssistantSettings);
        window.VoiceRequested += () => OnAssistantVoiceRequested(window);
        window.StopSpeechRequested += () => StopAssistantAudio();
        window.ReplyCompleted += text => SpeakAssistantReply(text);
        return window;
    }

    private void OpenAssistant()
    {
        _panel?.HideAnimated();
        if (!_settings.Current.Assistant.Enabled) { OpenAssistantSettings(); return; }
        ShowTool(ToolWindow.Assistant);
    }

    private void OpenAssistantSettings()
    {
        ShowTool(ToolWindow.Settings);
        GetTool<SettingsWindow>()?.SelectAssistantPage();
    }

    private void ApplyAssistantSettings(AppSettings settings)
    {
        if (_assistantAppliedSettings is { } oldLibrary && (oldLibrary.Library != settings.Assistant.Library || !oldLibrary.Profiles.SequenceEqual(settings.Assistant.Profiles))) _assistantLearningWorker?.Cancel();
        if (_assistantAppliedSettings is { } previous && (previous.Voice != settings.Assistant.Voice || previous.Endpoint != settings.Assistant.Endpoint || previous.Model != settings.Assistant.Model || previous.AllowPrivateEndpoint != settings.Assistant.AllowPrivateEndpoint)) StopAssistantAudio();
        _assistantAppliedSettings = settings.Assistant;
        if (!settings.Assistant.Enabled || !settings.Assistant.Library.ExtractCandidates) _assistantLearningWorker?.Cancel();
        _assistantRuntime?.ApplySettings(settings.Assistant);
        GetTool<AssistantWindow>()?.ApplySettings(settings);
        if (!settings.Assistant.Enabled || !settings.Assistant.Voice.Enabled) StopAssistantAudio();
        if (!settings.Assistant.Enabled) GetTool<SettingsWindow>()?.CancelAssistantLibraryWork();
    }

    private void DisposeAssistant()
    {
        _assistantLearningTimer?.Dispose(); _assistantLearningWorker?.Dispose();
        StopAssistantAudio(); GetTool<AssistantWindow>()?.Dispose(); _assistantRuntime?.Dispose();
        _assistantHttp?.Dispose(); _assistantStore?.Dispose(); _assistantCredentials?.Dispose();
    }

    partial void OnAssistantVoiceRequested(AssistantWindow window);
    partial void StopAssistantAudio();
    partial void SpeakAssistantReply(string text);
}
