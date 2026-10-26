using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;
using FloatDesk.Core.Translation;

namespace FloatDesk.App.Translation;

public sealed class TranslationSettingsApplier
{
    private readonly ISettingsCoordinator _settings;
    private readonly ITranslationCredentialStore _credentials;

    public TranslationSettingsApplier(
        ISettingsCoordinator settings,
        ITranslationCredentialStore credentials)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    }

    public async Task<OperationResult> ApplyAsync(
        Func<AppSettings, AppSettings> update,
        string? stagedApiKey,
        bool clearSavedKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        var settingsResult = await _settings.UpdateAsync(
            update,
            cancellationToken);
        if (!settingsResult.Success)
            return settingsResult;

        var translation = _settings.Current.Translation;
        var key = string.IsNullOrWhiteSpace(stagedApiKey)
            ? null
            : stagedApiKey.Trim();
        try
        {
            if (key is not null)
            {
                await _credentials.SaveAsync(
                    translation.Provider,
                    translation.Endpoint,
                    key,
                    cancellationToken);
                return OperationResult.Ok("设置与 API 密钥已保存。");
            }

            if (clearSavedKey)
            {
                await _credentials.DeleteAsync(
                    translation.Provider,
                    translation.Endpoint,
                    cancellationToken);
                return OperationResult.Ok("设置已保存，API 密钥已清除。");
            }

            return OperationResult.Ok("设置已保存。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return key is null
                ? OperationResult.Fail(
                    "TranslationKeyDeleteFailed",
                    "设置已保存，但 API 密钥未能清除。请检查系统凭据保护功能后重试。")
                : OperationResult.Fail(
                    "TranslationKeySaveFailed",
                    "设置已保存，但 API 密钥未保存。请检查系统凭据保护功能后重试。");
        }
    }
}

public sealed class TranslationConnectionTester
{
    private const string TestText = "Hello";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private readonly ITranslationCredentialStore _credentials;
    private readonly ITranslationProviderFactory _providers;

    public TranslationConnectionTester(
        ITranslationCredentialStore credentials,
        ITranslationProviderFactory providers)
    {
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
    }

    public async Task<OperationResult> TestAsync(
        TranslationSettings settings,
        string? stagedApiKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var key = string.IsNullOrWhiteSpace(stagedApiKey)
            ? null
            : stagedApiKey.Trim();
        try
        {
            if (settings.Provider != TranslationProviderKind.MyMemory &&
                key is null)
            {
                key = await _credentials.GetAsync(
                    settings.Provider,
                    settings.Endpoint,
                    cancellationToken);
            }

            if (RequiresKey(settings.Provider) && string.IsNullOrWhiteSpace(key))
            {
                return OperationResult.Fail(
                    "TranslationKeyMissing",
                    "请先输入或保存该服务所需的 API 密钥。");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(Timeout);
            var provider = _providers.Create(settings, key);
            var result = await provider.TranslateAsync(
                new TranslationRequest(
                    TestText,
                    TranslationLanguages.English,
                    settings.TargetLanguage,
                    TranslationPurpose.Interactive),
                timeout.Token);
            return result.Success
                ? OperationResult.Ok("连接成功。")
                : OperationResult.Fail(
                    "TranslationConnectionFailed",
                    FailureMessage(result.FailureKind));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return OperationResult.Fail(
                "TranslationConnectionTimeout",
                "连接测试超时，请检查网络或接口地址。");
        }
        catch
        {
            return OperationResult.Fail(
                "TranslationConnectionFailed",
                "无法创建翻译服务连接，请检查接口地址、模型与密钥配置。");
        }
    }

    private static bool RequiresKey(TranslationProviderKind provider) =>
        provider is TranslationProviderKind.AzureTranslator or
            TranslationProviderKind.DeepL or
            TranslationProviderKind.OpenAiCompatible;

    private static string FailureMessage(TranslationFailureKind kind) =>
        kind switch
        {
            TranslationFailureKind.Authentication =>
                "认证失败，请检查 API 密钥和区域配置。",
            TranslationFailureKind.Quota =>
                "服务额度已用尽或请求过于频繁，请稍后重试或更换服务。",
            TranslationFailureKind.UnsupportedLanguage =>
                "该服务不支持当前选择的语言。",
            TranslationFailureKind.Timeout =>
                "连接测试超时，请检查网络或接口地址。",
            TranslationFailureKind.Network =>
                "网络连接失败，请检查网络、代理和接口地址。",
            TranslationFailureKind.InvalidRequest =>
                "服务拒绝了测试请求，请检查接口地址与模型配置。",
            TranslationFailureKind.Cancelled =>
                "连接测试已取消。",
            _ => "翻译服务返回错误，请检查配置后重试。"
        };
}
