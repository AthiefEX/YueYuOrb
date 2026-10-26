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
        CancellationToken cancellationToken) =>
        await ApplyAsync(
            update,
            stagedApiKey,
            clearSavedKey,
            intendedCredentialTarget: null,
            cancellationToken);

    public async Task<OperationResult> ApplyAsync(
        Func<AppSettings, AppSettings> update,
        string? stagedApiKey,
        bool clearSavedKey,
        TranslationCredentialTarget? intendedCredentialTarget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        AppSettings? savedCandidate = null;
        var settingsResult = await _settings.UpdateAsync(
            current =>
            {
                savedCandidate = update(current);
                return savedCandidate;
            },
            cancellationToken);
        if (!settingsResult.Success)
            return settingsResult;
        if (savedCandidate is null)
        {
            return OperationResult.Fail(
                "SettingsUpdateContract",
                "设置已保存，但无法确认保存内容。请重新打开设置后再试。");
        }

        var translation = savedCandidate.Translation;
        var key = string.IsNullOrWhiteSpace(stagedApiKey)
            ? null
            : stagedApiKey.Trim();
        if ((key is not null || clearSavedKey) &&
            intendedCredentialTarget is not null &&
            !intendedCredentialTarget.Matches(translation))
        {
            return OperationResult.Fail(
                "TranslationCredentialTargetChanged",
                key is not null
                    ? "设置已保存，但翻译服务或接口已变化，API 密钥未保存。请重新输入密钥。"
                    : "设置已保存，但翻译服务或接口已变化，未清除任何 API 密钥。");
        }
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
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    private readonly ITranslationCredentialStore _credentials;
    private readonly ITranslationProviderFactory _providers;
    private readonly TimeSpan _timeout;

    public TranslationConnectionTester(
        ITranslationCredentialStore credentials,
        ITranslationProviderFactory providers,
        TimeSpan? timeout = null)
    {
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _timeout = timeout ?? DefaultTimeout;
        if (_timeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
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
            if (_timeout == TimeSpan.Zero)
                timeout.Cancel();
            else
                timeout.CancelAfter(_timeout);
            var provider = _providers.Create(settings, key);
            var result = await provider.TranslateAsync(
                new TranslationRequest(
                    TestText,
                    TranslationLanguages.English,
                    settings.TargetLanguage,
                    TranslationPurpose.Interactive),
                timeout.Token);
            if (result.Success)
                return OperationResult.Ok("连接成功。");
            if (timeout.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
            {
                return TimeoutResult();
            }
            if (result.FailureKind == TranslationFailureKind.Timeout)
                return TimeoutResult();
            if (result.FailureKind == TranslationFailureKind.Cancelled)
            {
                return OperationResult.Fail(
                    "TranslationConnectionCancelled",
                    "连接测试已取消。");
            }
            return OperationResult.Fail(
                "TranslationConnectionFailed",
                FailureMessage(result.FailureKind));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return TimeoutResult();
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

    private static OperationResult TimeoutResult() =>
        OperationResult.Fail(
            "TranslationConnectionTimeout",
            "连接测试超时，请检查网络或接口地址。");

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
