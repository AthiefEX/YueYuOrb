using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FloatDesk.Core.Assistant;

namespace FloatDesk.Infrastructure.Assistant;

public static partial class AssistantPrivacy
{
    public static async Task CheckStoredSecretsAsync(string text, AssistantSettings settings, IAssistantCredentialStore credentials, CancellationToken token)
    {
        if (SecretReason(text) is { } reason) throw new InvalidOperationException(reason);
        foreach (var (purpose, endpoint) in new[] { ("chat", settings.Endpoint), ("asr", settings.Voice.Endpoint) })
        {
            if (!AssistantEndpointPolicy.IsValid(endpoint)) continue;
            var key = await credentials.GetAsync(purpose, endpoint, token).ConfigureAwait(false);
            if (SecretReason(text, key) is { } knownReason) throw new InvalidOperationException(knownReason);
        }
        foreach (var profile in settings.Profiles)
        {
            var key = await credentials.GetAsync(AssistantConnections.CredentialPurpose(profile), profile.Endpoint, token).ConfigureAwait(false);
            if (SecretReason(text, key) is { } profileReason) throw new InvalidOperationException(profileReason);
        }
    }

    public static string? SecretReason(string text, string? knownKey = null)
    {
        if (!string.IsNullOrEmpty(knownKey) && text.Contains(knownKey, StringComparison.Ordinal)) return "内容包含当前API密钥，请改在连接设置的密码框中保存。";
        return Secrets().IsMatch(text) ? "内容可能包含密钥、密码或认证令牌，请移除后再发送或保存记忆。" : null;
    }

    public static string Fingerprint(AssistantChatRequest request, byte[]? payload = null)
    {
        var endpoint = AssistantEndpointPolicy.Resolve(request.Settings.Endpoint, "chat/completions").AbsoluteUri;
        var bytes = payload ?? AssistantApiClient.CreatePayloadBytes(request);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(endpoint + "\n")); hash.AppendData(bytes);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static AssistantRequestApproval Approve(AssistantChatRequest request, DateTimeOffset expiresAt) =>
        new(AssistantEndpointPolicy.Resolve(request.Settings.Endpoint, "chat/completions").AbsoluteUri, request.Settings.Model, Fingerprint(request), expiresAt);

    public static bool RequiresApproval(AssistantChatRequest request) => request.ContainsSensitiveContext &&
        !AssistantEndpointPolicy.IsLocal(new Uri(request.Settings.Endpoint));

    public static void EnsureAllowed(AssistantChatRequest request, byte[]? payload = null)
    {
        CheckSecrets(request);
        if (request.Approval is { } approval)
        {
            var endpoint = AssistantEndpointPolicy.Resolve(request.Settings.Endpoint, "chat/completions").AbsoluteUri;
            if (approval.ExpiresAt <= DateTimeOffset.UtcNow || approval.Endpoint != endpoint || approval.Model != request.Settings.Model || approval.Fingerprint != Fingerprint(request, payload))
                throw new InvalidOperationException("外发授权已过期或内容发生变化，请重新确认。");
        }
        else if (RequiresApproval(request)) throw new InvalidOperationException("发送敏感上下文前需要确认本次外发内容。");
    }

    public static void CheckSecrets(AssistantChatRequest request)
    {
        foreach (var message in request.Messages)
        {
            var reason = SecretReason(message.Content, request.ApiKey);
            if (reason is not null) throw new InvalidOperationException(reason);
            foreach (var call in message.ToolCalls)
                if (SecretReason(call.Arguments, request.ApiKey) is { } argumentReason) throw new InvalidOperationException(argumentReason);
        }
    }

    [GeneratedRegex("(?:\\bsk-[A-Za-z0-9_-]{12,}|\\bBearer\\s+[A-Za-z0-9._~-]{12,}|(?:api[_ -]?key|password|passwd|secret|密码|口令)\\s*[:=：]\\s*[^\\s,;，；]{4,}|-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Secrets();
}
