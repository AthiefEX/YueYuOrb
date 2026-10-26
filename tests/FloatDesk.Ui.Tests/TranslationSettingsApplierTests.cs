using System.Text.Json;
using System.Xml.Linq;
using FloatDesk.App.Translation;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;
using FloatDesk.Core.Translation;

namespace FloatDesk.Ui.Tests;

public sealed class TranslationSettingsApplierTests
{
    [Fact]
    public async Task SavingSettingsNeverWritesApiKeyIntoAppSettings()
    {
        var settings = new RecordingSettingsCoordinator(
            AppSettings.CreateDefault());
        var credentials = new RecordingCredentialStore();
        var applier = new TranslationSettingsApplier(settings, credentials);
        var candidate = AppSettings.CreateDefault() with
        {
            Translation = AppSettings.CreateDefault().Translation with
            {
                Provider = TranslationProviderKind.DeepL,
                Endpoint = "https://api-free.deepl.com"
            }
        };

        var result = await applier.ApplyAsync(
            _ => candidate,
            stagedApiKey: "secret",
            clearSavedKey: false,
            CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal(
            "secret",
            await credentials.GetAsync(
                TranslationProviderKind.DeepL,
                "https://api-free.deepl.com",
                CancellationToken.None));
        Assert.DoesNotContain(
            "secret",
            JsonSerializer.Serialize(settings.Current),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplySavesSettingsBeforeTheCredential()
    {
        var events = new List<string>();
        var settings = new RecordingSettingsCoordinator(
            AppSettings.CreateDefault(),
            events);
        var credentials = new RecordingCredentialStore(events);
        var applier = new TranslationSettingsApplier(settings, credentials);
        var candidate = AppSettings.CreateDefault() with
        {
            Translation = AppSettings.CreateDefault().Translation with
            {
                Provider = TranslationProviderKind.OpenAiCompatible,
                Endpoint = "https://translator.example.test/v1/chat/completions",
                Model = "translation-model"
            }
        };

        var result = await applier.ApplyAsync(
            _ => candidate,
            stagedApiKey: "new-key",
            clearSavedKey: false,
            CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal(["settings", "credential-save"], events);
    }

    [Fact]
    public async Task KeySaveFailureReportsThatSettingsWereSaved()
    {
        var settings = new RecordingSettingsCoordinator(
            AppSettings.CreateDefault());
        var credentials = new RecordingCredentialStore
        {
            FailWrites = true
        };
        var applier = new TranslationSettingsApplier(settings, credentials);
        var candidate = AppSettings.CreateDefault() with
        {
            Translation = AppSettings.CreateDefault().Translation with
            {
                Provider = TranslationProviderKind.DeepL,
                Endpoint = "https://api-free.deepl.com"
            }
        };

        var result = await applier.ApplyAsync(
            _ => candidate,
            stagedApiKey: "must-not-leak",
            clearSavedKey: false,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TranslationKeySaveFailed", result.ErrorCode);
        Assert.Equal(1, settings.UpdateCount);
        Assert.Equal(TranslationProviderKind.DeepL, settings.Current.Translation.Provider);
        Assert.Contains("设置已保存", result.Message, StringComparison.Ordinal);
        Assert.Contains("密钥未保存", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("must-not-leak", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClearOnlyDeletesTheResultingCredential()
    {
        var settings = new RecordingSettingsCoordinator(
            AppSettings.CreateDefault());
        var credentials = new RecordingCredentialStore();
        await credentials.SaveAsync(
            TranslationProviderKind.DeepL,
            "https://api-free.deepl.com",
            "stored-key",
            CancellationToken.None);
        credentials.ResetCounters();
        var applier = new TranslationSettingsApplier(settings, credentials);
        var candidate = AppSettings.CreateDefault() with
        {
            Translation = AppSettings.CreateDefault().Translation with
            {
                Provider = TranslationProviderKind.DeepL,
                Endpoint = "https://api-free.deepl.com"
            }
        };

        var result = await applier.ApplyAsync(
            _ => candidate,
            stagedApiKey: null,
            clearSavedKey: true,
            CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal(0, credentials.SaveCount);
        Assert.Equal(1, credentials.DeleteCount);
        Assert.Null(await credentials.GetAsync(
            TranslationProviderKind.DeepL,
            "https://api-free.deepl.com",
            CancellationToken.None));
    }

    [Fact]
    public async Task StagedKeyTakesPrecedenceOverClear()
    {
        var settings = new RecordingSettingsCoordinator(
            AppSettings.CreateDefault());
        var credentials = new RecordingCredentialStore();
        await credentials.SaveAsync(
            TranslationProviderKind.DeepL,
            "https://api-free.deepl.com",
            "stored-key",
            CancellationToken.None);
        credentials.ResetCounters();
        var applier = new TranslationSettingsApplier(settings, credentials);
        var candidate = AppSettings.CreateDefault() with
        {
            Translation = AppSettings.CreateDefault().Translation with
            {
                Provider = TranslationProviderKind.DeepL,
                Endpoint = "https://api-free.deepl.com"
            }
        };

        var result = await applier.ApplyAsync(
            _ => candidate,
            stagedApiKey: "replacement-key",
            clearSavedKey: true,
            CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, credentials.SaveCount);
        Assert.Equal(0, credentials.DeleteCount);
        Assert.Equal(
            "replacement-key",
            await credentials.GetAsync(
                TranslationProviderKind.DeepL,
                "https://api-free.deepl.com",
                CancellationToken.None));
    }

    [Fact]
    public async Task SettingsFailurePreventsCredentialChanges()
    {
        var settings = new RecordingSettingsCoordinator(
            AppSettings.CreateDefault())
        {
            UpdateFailure = OperationResult.Fail(
                "SettingsSaveFailed",
                "settings failed")
        };
        var credentials = new RecordingCredentialStore();
        var applier = new TranslationSettingsApplier(settings, credentials);

        var result = await applier.ApplyAsync(
            current => current,
            stagedApiKey: "not-saved",
            clearSavedKey: true,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("SettingsSaveFailed", result.ErrorCode);
        Assert.Equal(0, credentials.SaveCount);
        Assert.Equal(0, credentials.DeleteCount);
    }

    [Fact]
    public async Task CancellationFromSettingsIsPropagated()
    {
        var settings = new RecordingSettingsCoordinator(
            AppSettings.CreateDefault())
        {
            UpdateException = new OperationCanceledException()
        };
        var applier = new TranslationSettingsApplier(
            settings,
            new RecordingCredentialStore());

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            applier.ApplyAsync(
                current => current,
                stagedApiKey: null,
                clearSavedKey: false,
                CancellationToken.None));
    }

    [Fact]
    public async Task ConnectionTestUsesStagedKeyAndDoesNotPersistAnything()
    {
        var settings = AppSettings.CreateDefault() with
        {
            Translation = AppSettings.CreateDefault().Translation with
            {
                Provider = TranslationProviderKind.DeepL,
                Endpoint = "https://api-free.deepl.com"
            }
        };
        var credentials = new RecordingCredentialStore
        {
            ThrowOnRead = true
        };
        var provider = new RecordingTranslationProvider();
        var factory = new RecordingProviderFactory(provider);
        var tester = new TranslationConnectionTester(credentials, factory);

        var result = await tester.TestAsync(
            settings.Translation,
            stagedApiKey: "staged-key",
            CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal("staged-key", factory.LastApiKey);
        Assert.Equal(0, credentials.ReadCount);
        Assert.Equal(0, credentials.SaveCount);
        Assert.Equal(0, credentials.DeleteCount);
        Assert.Equal("Hello", provider.LastRequest?.Text);
        Assert.Equal(TranslationLanguages.English, provider.LastRequest?.SourceLanguage);
    }

    [Fact]
    public async Task ConnectionTestUsesStoredKeyOnlyWhenNoKeyIsStaged()
    {
        var settings = AppSettings.CreateDefault() with
        {
            Translation = AppSettings.CreateDefault().Translation with
            {
                Provider = TranslationProviderKind.DeepL,
                Endpoint = "https://api-free.deepl.com"
            }
        };
        var credentials = new RecordingCredentialStore();
        await credentials.SaveAsync(
            TranslationProviderKind.DeepL,
            "https://api-free.deepl.com",
            "stored-key",
            CancellationToken.None);
        var provider = new RecordingTranslationProvider();
        var factory = new RecordingProviderFactory(provider);
        var tester = new TranslationConnectionTester(credentials, factory);

        var result = await tester.TestAsync(
            settings.Translation,
            stagedApiKey: " ",
            CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal("stored-key", factory.LastApiKey);
        Assert.Equal(1, credentials.ReadCount);
    }

    [Fact]
    public async Task ConnectionFailureDoesNotExposeProviderMessageOrKey()
    {
        var settings = AppSettings.CreateDefault() with
        {
            Translation = AppSettings.CreateDefault().Translation with
            {
                Provider = TranslationProviderKind.DeepL,
                Endpoint = "https://api-free.deepl.com"
            }
        };
        var provider = new RecordingTranslationProvider
        {
            Result = TranslationProviderResult.Fail(
                TranslationFailureKind.Authentication,
                "AuthenticationFailed",
                "request failed for super-secret-key")
        };
        var tester = new TranslationConnectionTester(
            new RecordingCredentialStore(),
            new RecordingProviderFactory(provider));

        var result = await tester.TestAsync(
            settings.Translation,
            stagedApiKey: "super-secret-key",
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.DoesNotContain("super-secret-key", result.Message, StringComparison.Ordinal);
        Assert.Contains("认证失败", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TranslationPageExposesProviderLanguageSecretOcrAndSubtitleFields()
    {
        var xaml = XDocument.Load(FindRepoFile(
            "src/FloatDesk.App/Views/SettingsWindow.xaml")).ToString();
        foreach (var name in new[]
                 {
                     "TranslationProviderComboBox",
                     "TranslationSourceLanguageComboBox",
                     "TranslationTargetLanguageComboBox",
                     "TranslationEndpointTextBox",
                     "TranslationApiKeyBox",
                     "TranslationModelTextBox",
                     "TranslationAzureRegionTextBox",
                     "TranslationOcrLanguageComboBox",
                     "SubtitleRefreshIntervalTextBox",
                     "SubtitleOverlayOpacityTextBox",
                     "SubtitleFontSizeTextBox",
                     "TestTranslationConnectionButton",
                     "TranslationSelectionHotkeyTextBox",
                     "TranslationRegionHotkeyTextBox",
                     "SubtitlesHotkeyTextBox"
                 })
        {
            Assert.Contains($"x:Name=\"{name}\"", xaml);
        }

        Assert.Contains("实时翻译", xaml, StringComparison.Ordinal);
        Assert.Contains("免费服务", xaml, StringComparison.Ordinal);
        Assert.Contains("截图不会上传", xaml, StringComparison.Ordinal);
        Assert.Contains("PasswordBox", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "Style=\"{StaticResource SettingsPasswordBoxStyle}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Text=\"{Binding TranslationApiKey",
            xaml,
            StringComparison.Ordinal);
    }

    private static string FindRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not find repository file '{relativePath}'.");
    }

    private sealed class RecordingSettingsCoordinator : ISettingsCoordinator
    {
        private readonly List<string>? _events;

        public RecordingSettingsCoordinator(
            AppSettings current,
            List<string>? events = null)
        {
            Current = current;
            _events = events;
        }

        public AppSettings Current { get; private set; }
        public event Action<AppSettings>? Changed;
        public int UpdateCount { get; private set; }
        public OperationResult? UpdateFailure { get; init; }
        public Exception? UpdateException { get; init; }

        public Task InitializeAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<OperationResult> UpdateAsync(
            Func<AppSettings, AppSettings> update,
            CancellationToken cancellationToken)
        {
            _events?.Add("settings");
            UpdateCount++;
            if (UpdateException is not null)
                return Task.FromException<OperationResult>(UpdateException);
            if (UpdateFailure is not null)
                return Task.FromResult(UpdateFailure);
            Current = update(Current);
            Changed?.Invoke(Current);
            return Task.FromResult(OperationResult.Ok());
        }
    }

    private sealed class RecordingCredentialStore : ITranslationCredentialStore
    {
        private readonly Dictionary<(TranslationProviderKind Provider, string Endpoint), string> _keys = [];
        private readonly List<string>? _events;

        public RecordingCredentialStore(List<string>? events = null)
        {
            _events = events;
        }

        public bool FailWrites { get; init; }
        public bool ThrowOnRead { get; init; }
        public int ReadCount { get; private set; }
        public int SaveCount { get; private set; }
        public int DeleteCount { get; private set; }

        public void ResetCounters()
        {
            ReadCount = 0;
            SaveCount = 0;
            DeleteCount = 0;
        }

        public Task<bool> HasAsync(
            TranslationProviderKind provider,
            string endpoint,
            CancellationToken cancellationToken) =>
            Task.FromResult(_keys.ContainsKey((provider, endpoint)));

        public Task<string?> GetAsync(
            TranslationProviderKind provider,
            string endpoint,
            CancellationToken cancellationToken)
        {
            ReadCount++;
            if (ThrowOnRead)
                throw new InvalidOperationException("read must not be needed");
            return Task.FromResult(
                _keys.GetValueOrDefault((provider, endpoint)));
        }

        public Task SaveAsync(
            TranslationProviderKind provider,
            string endpoint,
            string apiKey,
            CancellationToken cancellationToken)
        {
            SaveCount++;
            _events?.Add("credential-save");
            if (FailWrites)
                throw new InvalidOperationException("store rejected secret");
            _keys[(provider, endpoint)] = apiKey;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(
            TranslationProviderKind provider,
            string endpoint,
            CancellationToken cancellationToken)
        {
            DeleteCount++;
            _events?.Add("credential-delete");
            _keys.Remove((provider, endpoint));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingProviderFactory(
        ITranslationProvider provider) : ITranslationProviderFactory
    {
        public string? LastApiKey { get; private set; }

        public ITranslationProvider Create(
            TranslationSettings settings,
            string? apiKey)
        {
            LastApiKey = apiKey;
            return provider;
        }
    }

    private sealed class RecordingTranslationProvider : ITranslationProvider
    {
        public string Id => "recording";
        public string DisplayName => "Recording";
        public TranslationProviderCapabilities Capabilities { get; } =
            new(false, 1000, 1000, 1, 1, TimeSpan.Zero);
        public TranslationRequest? LastRequest { get; private set; }
        public TranslationProviderResult Result { get; init; } =
            TranslationProviderResult.Ok("你好");

        public Task<TranslationProviderResult> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(Result);
        }
    }
}
