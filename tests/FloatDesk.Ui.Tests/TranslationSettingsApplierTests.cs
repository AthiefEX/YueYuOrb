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
    public async Task SavedCandidateFromUpdateDelegateTargetsCredentialDespiteCurrentRace()
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
                Endpoint = "https://saved.example.test"
            }
        };
        settings.CurrentAfterUpdate = AppSettings.CreateDefault() with
        {
            Translation = AppSettings.CreateDefault().Translation with
            {
                Provider = TranslationProviderKind.AzureTranslator,
                Endpoint = "https://raced.example.test"
            }
        };
        var intendedTarget = TranslationCredentialTarget.From(
            candidate.Translation);

        var result = await applier.ApplyAsync(
            _ => candidate,
            stagedApiKey: "saved-key",
            clearSavedKey: false,
            intendedTarget,
            CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal(
            "saved-key",
            await credentials.GetAsync(
                TranslationProviderKind.DeepL,
                "https://saved.example.test",
                CancellationToken.None));
        Assert.Null(await credentials.GetAsync(
            TranslationProviderKind.AzureTranslator,
            "https://raced.example.test",
            CancellationToken.None));
    }

    [Fact]
    public async Task StagedKeyIsNotSavedWhenCredentialTargetChanges()
    {
        var settings = new RecordingSettingsCoordinator(
            AppSettings.CreateDefault());
        var credentials = new RecordingCredentialStore();
        var applier = new TranslationSettingsApplier(settings, credentials);
        var intendedTarget = TranslationCredentialTarget.From(
            TranslationProviderKind.DeepL,
            "https://api-free.deepl.com");
        var candidate = AppSettings.CreateDefault() with
        {
            Translation = AppSettings.CreateDefault().Translation with
            {
                Provider = TranslationProviderKind.AzureTranslator,
                Endpoint = "https://api.cognitive.microsofttranslator.com"
            }
        };

        var result = await applier.ApplyAsync(
            _ => candidate,
            stagedApiKey: "must-not-move",
            clearSavedKey: false,
            intendedTarget,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TranslationCredentialTargetChanged", result.ErrorCode);
        Assert.Equal(0, credentials.SaveCount);
        Assert.Null(await credentials.GetAsync(
            TranslationProviderKind.AzureTranslator,
            "https://api.cognitive.microsofttranslator.com",
            CancellationToken.None));
    }

    [Fact]
    public async Task ClearOnlyDeletesCredentialAtTheIntentTarget()
    {
        var settings = new RecordingSettingsCoordinator(
            AppSettings.CreateDefault());
        var credentials = new RecordingCredentialStore();
        await credentials.SaveAsync(
            TranslationProviderKind.AzureTranslator,
            "https://api.cognitive.microsofttranslator.com",
            "azure-key",
            CancellationToken.None);
        credentials.ResetCounters();
        var applier = new TranslationSettingsApplier(settings, credentials);
        var intendedTarget = TranslationCredentialTarget.From(
            TranslationProviderKind.DeepL,
            "https://api-free.deepl.com");
        var candidate = AppSettings.CreateDefault() with
        {
            Translation = AppSettings.CreateDefault().Translation with
            {
                Provider = TranslationProviderKind.AzureTranslator,
                Endpoint = "https://api.cognitive.microsofttranslator.com"
            }
        };

        var result = await applier.ApplyAsync(
            _ => candidate,
            stagedApiKey: null,
            clearSavedKey: true,
            intendedTarget,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TranslationCredentialTargetChanged", result.ErrorCode);
        Assert.Equal(0, credentials.DeleteCount);
        Assert.Equal(
            "azure-key",
            await credentials.GetAsync(
                TranslationProviderKind.AzureTranslator,
                "https://api.cognitive.microsofttranslator.com",
                CancellationToken.None));
    }

    [Fact]
    public void CredentialIntentResetsWhenProviderOrEndpointIdentityChanges()
    {
        var intent = new TranslationCredentialIntentState();
        var original = TranslationCredentialTarget.From(
            TranslationProviderKind.DeepL,
            "https://api-free.deepl.com");
        var changedEndpoint = TranslationCredentialTarget.From(
            TranslationProviderKind.DeepL,
            "https://deepl.example.test");
        var changedProvider = TranslationCredentialTarget.From(
            TranslationProviderKind.AzureTranslator,
            "https://api-free.deepl.com");

        intent.Mark(original);

        Assert.True(intent.ResetIfIdentityChanged(changedEndpoint));
        Assert.Null(intent.Target);
        Assert.False(intent.ResetIfIdentityChanged(changedProvider));
        intent.Mark(changedProvider);
        Assert.Equal(changedProvider, intent.Target);
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
    public async Task ProviderCancellationAfterLinkedTimeoutIsReportedAsTimeout()
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
                TranslationFailureKind.Cancelled)
        };
        var tester = new TranslationConnectionTester(
            new RecordingCredentialStore(),
            new RecordingProviderFactory(provider),
            TimeSpan.Zero);

        var result = await tester.TestAsync(
            settings.Translation,
            stagedApiKey: "secret",
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("TranslationConnectionTimeout", result.ErrorCode);
        Assert.Contains("超时", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderCancellationFromUserTokenRemainsCancelled()
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
                TranslationFailureKind.Cancelled)
        };
        var tester = new TranslationConnectionTester(
            new RecordingCredentialStore(),
            new RecordingProviderFactory(provider),
            TimeSpan.FromMinutes(1));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await tester.TestAsync(
            settings.Translation,
            stagedApiKey: "secret",
            cancellation.Token);

        Assert.False(result.Success);
        Assert.Equal("TranslationConnectionCancelled", result.ErrorCode);
        Assert.Contains("已取消", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("超时", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StaleOperationCannotCompleteOrReplaceActiveOperation()
    {
        var gate = new TranslationOperationGate();
        var first = gate.Begin();
        var second = gate.Begin();

        Assert.False(gate.IsActive(first));
        Assert.True(gate.IsActive(second));
        Assert.False(gate.TryComplete(first));
        Assert.True(gate.IsActive(second));
        Assert.True(gate.TryComplete(second));
        Assert.False(gate.IsActive(second));
    }

    [Fact]
    public void CredentialIdentityChangeRejectsStaleConnectionOperationAndAllowsNextTest()
    {
        var gate = new TranslationOperationGate();
        var deepLTarget = TranslationCredentialTarget.From(
            TranslationProviderKind.DeepL,
            "https://api-free.deepl.com");
        var azureTarget = TranslationCredentialTarget.From(
            TranslationProviderKind.AzureTranslator,
            "https://api.cognitive.microsofttranslator.com");
        var staleOperation = gate.Begin(deepLTarget);

        Assert.True(gate.IsActive(staleOperation, deepLTarget));
        Assert.False(gate.IsActive(staleOperation, azureTarget));
        Assert.False(gate.TryComplete(staleOperation, azureTarget));
        Assert.True(gate.HasActiveIdentityDifferentFrom(azureTarget));

        gate.Clear();
        Assert.False(gate.IsActive(staleOperation, azureTarget));

        var nextOperation = gate.Begin(azureTarget);
        Assert.True(gate.IsActive(nextOperation, azureTarget));
        Assert.True(gate.TryComplete(nextOperation, azureTarget));
    }

    [Fact]
    public void StaleCompletionAfterIdentityChangeReleasesCancellationAndAllowsNextTest()
    {
        var state = new TranslationConnectionTestState();
        var deepLTarget = TranslationCredentialTarget.From(
            TranslationProviderKind.DeepL,
            "https://api-free.deepl.com");
        var azureTarget = TranslationCredentialTarget.From(
            TranslationProviderKind.AzureTranslator,
            "https://api.cognitive.microsofttranslator.com");
        var staleOperation = state.Start(deepLTarget);

        Assert.True(state.IsCurrent(staleOperation, deepLTarget));
        Assert.False(state.IsCurrent(staleOperation, azureTarget));

        var staleCompletion = state.Complete(
            staleOperation,
            azureTarget);

        Assert.True(staleCompletion.WasActive);
        Assert.False(staleCompletion.IdentityMatches);
        Assert.True(staleCompletion.ShouldReenableControls);
        Assert.Null(Record.Exception(state.Cancel));

        var nextOperation = state.Start(azureTarget);
        Assert.True(state.IsCurrent(nextOperation, azureTarget));
        var nextCompletion = state.Complete(
            nextOperation,
            azureTarget);
        Assert.True(nextCompletion.WasActive);
        Assert.True(nextCompletion.IdentityMatches);
    }

    [Fact]
    public void IdentityChangeCancelsActiveTestAndRejectsLateCompletion()
    {
        var state = new TranslationConnectionTestState();
        var deepLTarget = TranslationCredentialTarget.From(
            TranslationProviderKind.DeepL,
            "https://api-free.deepl.com");
        var azureTarget = TranslationCredentialTarget.From(
            TranslationProviderKind.AzureTranslator,
            "https://api.cognitive.microsofttranslator.com");
        var staleOperation = state.Start(deepLTarget);

        Assert.True(state.InvalidateIfIdentityChanged(azureTarget));
        Assert.True(staleOperation.Token.IsCancellationRequested);
        Assert.False(state.IsCurrent(staleOperation, azureTarget));

        var staleCompletion = state.Complete(
            staleOperation,
            azureTarget);
        Assert.False(staleCompletion.WasActive);
        Assert.False(staleCompletion.ShouldReenableControls);
        Assert.Null(Record.Exception(state.Cancel));

        var nextOperation = state.Start(azureTarget);
        Assert.True(state.IsCurrent(nextOperation, azureTarget));
        Assert.True(state.Complete(nextOperation, azureTarget).WasActive);
    }

    [Fact]
    public void StaleCompletionCannotReleaseNewerConnectionTest()
    {
        var state = new TranslationConnectionTestState();
        var deepLTarget = TranslationCredentialTarget.From(
            TranslationProviderKind.DeepL,
            "https://api-free.deepl.com");
        var azureTarget = TranslationCredentialTarget.From(
            TranslationProviderKind.AzureTranslator,
            "https://api.cognitive.microsofttranslator.com");
        var staleOperation = state.Start(deepLTarget);
        var currentOperation = state.Start(azureTarget);

        Assert.True(staleOperation.Token.IsCancellationRequested);
        Assert.False(currentOperation.Token.IsCancellationRequested);
        Assert.True(state.IsCurrent(currentOperation, azureTarget));

        var staleCompletion = state.Complete(
            staleOperation,
            azureTarget);
        Assert.False(staleCompletion.WasActive);
        Assert.True(state.IsCurrent(currentOperation, azureTarget));
        Assert.False(currentOperation.Token.IsCancellationRequested);
        Assert.True(state.Complete(currentOperation, azureTarget).WasActive);
        Assert.Null(Record.Exception(state.Cancel));
    }

    [Fact]
    public void TranslationPageExposesProviderLanguageSecretOcrAndSubtitleFields()
    {
        var document = XDocument.Load(FindRepoFile(
            "src/FloatDesk.App/Views/SettingsWindow.xaml"));
        var xaml = document.ToString();
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
        foreach (var phrase in new[]
                 {
                     "GET",
                     "第三方服务",
                     "查询字符串",
                     "网络中间",
                     "自定义翻译服务"
                 })
        {
            Assert.Contains(phrase, xaml, StringComparison.Ordinal);
        }
        Assert.Contains("PasswordBox", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "Style=\"{StaticResource SettingsPasswordBoxStyle}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Text=\"{Binding TranslationApiKey",
            xaml,
            StringComparison.Ordinal);

        var xName = XName.Get(
            "Name",
            "http://schemas.microsoft.com/winfx/2006/xaml");
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
                     "ClearSavedTranslationKeyCheckBox",
                     "TranslationKeyStatusText",
                     "TranslationTestStatusText",
                     "TranslationSelectionHotkeyTextBox",
                     "TranslationRegionHotkeyTextBox",
                     "SubtitlesHotkeyTextBox"
                 })
        {
            var element = document
                .Descendants()
                .Single(candidate =>
                    (string?)candidate.Attribute(xName) == name);
            Assert.False(string.IsNullOrWhiteSpace(
                (string?)element.Attribute("AutomationProperties.Name")));
        }
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
        public AppSettings? CurrentAfterUpdate { get; set; }

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
            if (CurrentAfterUpdate is not null)
                Current = CurrentAfterUpdate;
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
