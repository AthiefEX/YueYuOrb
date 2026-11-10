using CommunityToolkit.Mvvm.ComponentModel;
using FloatDesk.Core.Assistant;
using FloatDesk.Core.Models;

namespace FloatDesk.App.ViewModels;

public partial class SettingsViewModel
{
    internal static bool TryParseAssistantSendKey(string text, out HotkeyChord chord) => TryParseHotkey(text, out chord);
    [ObservableProperty] private bool _assistantEnabled = false;
    [ObservableProperty] private string _assistantEndpoint = "";
    [ObservableProperty] private string _assistantModel = "";
    [ObservableProperty] private bool _assistantSearchEnabled = false;
    [ObservableProperty] private int _assistantSendMode;
    [ObservableProperty] private string _assistantSendShortcut = "Ctrl+Enter";
    [ObservableProperty] private string _assistantWindowTitle = "月语 AI助手";
    [ObservableProperty] private string _assistantDisplayName = "月语助手";
    [ObservableProperty] private string _assistantUserDisplayName = "你";
    [ObservableProperty] private bool _assistantStream = true;
    [ObservableProperty] private bool _assistantSupportsTools = false;
    [ObservableProperty] private bool _assistantSaveHistory = true;
    [ObservableProperty] private bool _assistantUseMemory = false;
    [ObservableProperty] private bool _assistantUseKnowledge = false;
    [ObservableProperty] private bool _assistantSearchHistory = false;
    [ObservableProperty] private bool _assistantEmbeddingEnabled = false;
    [ObservableProperty] private Guid? _assistantEmbeddingProfileId = null;
    [ObservableProperty] private bool _assistantExtractCandidates = false;
    [ObservableProperty] private Guid? _assistantExtractionProfileId = null;
    [ObservableProperty] private bool _assistantAllowComputerControl = false;
    [ObservableProperty] private bool _assistantAllowTaskManagement = false;
    [ObservableProperty] private bool _assistantAllowAssetEditing = false;
    [ObservableProperty] private bool _assistantAllowPrivateEndpoint = false;
    [ObservableProperty] private bool _assistantShowInWheel = true;
    [ObservableProperty] private string _assistantSystemPrompt = "";
    [ObservableProperty] private int _assistantTimeoutSeconds = 120;
    [ObservableProperty] private bool _assistantAlwaysOnTop = true;
    [ObservableProperty] private double _assistantOpacityPercent = 100;
    [ObservableProperty] private bool _assistantBackgroundEnabled = false;
    [ObservableProperty] private double _assistantBackgroundOpacityPercent = 25;
    [ObservableProperty] private bool _assistantVoiceEnabled = false;
    [ObservableProperty] private string _assistantVoiceEndpoint = "";
    [ObservableProperty] private string _assistantVoiceModel = "whisper-1";
    [ObservableProperty] private bool _assistantReadReplies = false;
    [ObservableProperty] private bool _assistantVoiceAutoSend = false;
    [ObservableProperty] private string _assistantHotkey = "";

    private void LoadAssistant(AppSettings settings)
    {
        AssistantEnabled = settings.Assistant.Enabled;
        AssistantEndpoint = settings.Assistant.Endpoint;
        AssistantModel = settings.Assistant.Model;
        AssistantSearchEnabled = settings.Assistant.SearchEnabled;
        AssistantWindowTitle = settings.Assistant.WindowTitle;
        AssistantDisplayName = settings.Assistant.DisplayName;
        AssistantUserDisplayName = settings.Assistant.UserDisplayName;
        AssistantStream = settings.Assistant.Stream;
        AssistantSupportsTools = settings.Assistant.SupportsTools;
        AssistantSaveHistory = settings.Assistant.SaveHistory;
        AssistantUseMemory = settings.Assistant.UseMemory;
        AssistantUseKnowledge = settings.Assistant.Library.UseKnowledge;
        AssistantSendMode = settings.Assistant.SendMode; AssistantSendShortcut = settings.Assistant.SendShortcut;
        AssistantAllowAssetEditing = settings.Assistant.AllowAssetEditing;
        AssistantSearchHistory = settings.Assistant.Library.SearchHistory;
        AssistantEmbeddingEnabled = settings.Assistant.Library.EmbeddingEnabled;
        AssistantEmbeddingProfileId = settings.Assistant.Library.EmbeddingProfileId;
        AssistantExtractCandidates = settings.Assistant.Library.ExtractCandidates;
        AssistantExtractionProfileId = settings.Assistant.Library.ExtractionProfileId;
        AssistantAllowComputerControl = settings.Assistant.AllowComputerControl;
        AssistantAllowTaskManagement = settings.Assistant.AllowTaskManagement;
        AssistantAllowPrivateEndpoint = settings.Assistant.AllowPrivateEndpoint;
        AssistantShowInWheel = settings.Assistant.ShowInWheel;
        AssistantSystemPrompt = settings.Assistant.SystemPrompt;
        AssistantTimeoutSeconds = settings.Assistant.TimeoutSeconds;
        AssistantAlwaysOnTop = settings.Assistant.Window.AlwaysOnTop;
        AssistantOpacityPercent = settings.Assistant.Window.Opacity * 100;
        AssistantBackgroundEnabled = settings.Assistant.Window.BackgroundEnabled;
        AssistantBackgroundOpacityPercent = settings.Assistant.Window.BackgroundOpacity * 100;
        AssistantVoiceEnabled = settings.Assistant.Voice.Enabled;
        AssistantVoiceEndpoint = settings.Assistant.Voice.Endpoint;
        AssistantVoiceModel = settings.Assistant.Voice.Model;
        AssistantReadReplies = settings.Assistant.Voice.ReadReplies;
        AssistantVoiceAutoSend = settings.Assistant.Voice.AutoSend;
        AssistantHotkey = FormatHotkey(settings.Hotkeys.GetValueOrDefault(HotkeyAction.Assistant));
    }

    private AssistantSettings MergeAssistant(AssistantSettings current)
    {
        return current with
        {
            Enabled = Pick(AssistantEnabled, _baseline.Assistant.Enabled, current.Enabled),
            Endpoint = Pick(AssistantEndpoint, _baseline.Assistant.Endpoint, current.Endpoint),
            Model = Pick(AssistantModel, _baseline.Assistant.Model, current.Model),
            SearchEnabled = Pick(AssistantSearchEnabled, _baseline.Assistant.SearchEnabled, current.SearchEnabled),
            SendMode = Pick(AssistantSendMode, _baseline.Assistant.SendMode, current.SendMode),
            SendShortcut = Pick(AssistantSendShortcut, _baseline.Assistant.SendShortcut, current.SendShortcut),
            WindowTitle = Pick(AssistantWindowTitle, _baseline.Assistant.WindowTitle, current.WindowTitle),
            DisplayName = Pick(AssistantDisplayName, _baseline.Assistant.DisplayName, current.DisplayName),
            UserDisplayName = Pick(AssistantUserDisplayName, _baseline.Assistant.UserDisplayName, current.UserDisplayName),
            Stream = Pick(AssistantStream, _baseline.Assistant.Stream, current.Stream),
            SupportsTools = Pick(AssistantSupportsTools, _baseline.Assistant.SupportsTools, current.SupportsTools),
            SaveHistory = Pick(AssistantSaveHistory, _baseline.Assistant.SaveHistory, current.SaveHistory),
            UseMemory = Pick(AssistantUseMemory, _baseline.Assistant.UseMemory, current.UseMemory),
            Library = current.Library with
            {
                UseKnowledge = Pick(AssistantUseKnowledge, _baseline.Assistant.Library.UseKnowledge, current.Library.UseKnowledge),
                SearchHistory = Pick(AssistantSearchHistory, _baseline.Assistant.Library.SearchHistory, current.Library.SearchHistory),
                EmbeddingEnabled = Pick(AssistantEmbeddingEnabled, _baseline.Assistant.Library.EmbeddingEnabled, current.Library.EmbeddingEnabled),
                EmbeddingProfileId = Pick(AssistantEmbeddingProfileId, _baseline.Assistant.Library.EmbeddingProfileId, current.Library.EmbeddingProfileId),
                ExtractCandidates = Pick(AssistantExtractCandidates, _baseline.Assistant.Library.ExtractCandidates, current.Library.ExtractCandidates),
                ExtractionProfileId = Pick(AssistantExtractionProfileId, _baseline.Assistant.Library.ExtractionProfileId, current.Library.ExtractionProfileId)
            },
            AllowComputerControl = Pick(AssistantAllowComputerControl, _baseline.Assistant.AllowComputerControl, current.AllowComputerControl),
            AllowTaskManagement = Pick(AssistantAllowTaskManagement, _baseline.Assistant.AllowTaskManagement, current.AllowTaskManagement),
            AllowAssetEditing = Pick(AssistantAllowAssetEditing, _baseline.Assistant.AllowAssetEditing, current.AllowAssetEditing),
            AllowPrivateEndpoint = Pick(AssistantAllowPrivateEndpoint, _baseline.Assistant.AllowPrivateEndpoint, current.AllowPrivateEndpoint),
            ShowInWheel = Pick(AssistantShowInWheel, _baseline.Assistant.ShowInWheel, current.ShowInWheel),
            SystemPrompt = Pick(AssistantSystemPrompt, _baseline.Assistant.SystemPrompt, current.SystemPrompt),
            TimeoutSeconds = Pick(AssistantTimeoutSeconds, _baseline.Assistant.TimeoutSeconds, current.TimeoutSeconds),
            Window = current.Window with
            {
            AlwaysOnTop = Pick(AssistantAlwaysOnTop, _baseline.Assistant.Window.AlwaysOnTop, current.Window.AlwaysOnTop),
            Opacity = Pick(AssistantOpacityPercent, _baseline.Assistant.Window.Opacity * 100, current.Window.Opacity * 100) / 100,
            BackgroundEnabled = Pick(AssistantBackgroundEnabled, _baseline.Assistant.Window.BackgroundEnabled, current.Window.BackgroundEnabled),
            BackgroundOpacity = Pick(AssistantBackgroundOpacityPercent, _baseline.Assistant.Window.BackgroundOpacity * 100, current.Window.BackgroundOpacity * 100) / 100,
            },
            Voice = current.Voice with
            {
            Enabled = Pick(AssistantVoiceEnabled, _baseline.Assistant.Voice.Enabled, current.Voice.Enabled),
            Endpoint = Pick(AssistantVoiceEndpoint, _baseline.Assistant.Voice.Endpoint, current.Voice.Endpoint),
            Model = Pick(AssistantVoiceModel, _baseline.Assistant.Voice.Model, current.Voice.Model),
            ReadReplies = Pick(AssistantReadReplies, _baseline.Assistant.Voice.ReadReplies, current.Voice.ReadReplies),
            AutoSend = Pick(AssistantVoiceAutoSend, _baseline.Assistant.Voice.AutoSend, current.Voice.AutoSend),
            }
        };

        static T Pick<T>(T value, T baseline, T latest) => EqualityComparer<T>.Default.Equals(value, baseline) ? latest : value;
    }
}
