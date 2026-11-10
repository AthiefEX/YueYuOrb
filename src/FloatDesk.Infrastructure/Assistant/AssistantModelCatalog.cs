using System.Net.Http.Headers;
using System.Text.Json;
using FloatDesk.Core.Assistant;

namespace FloatDesk.Infrastructure.Assistant;

public sealed class AssistantModelCatalog(HttpClient http)
{
    public async Task<IReadOnlyList<string>> ListAsync(string endpoint, string? key, bool allowPrivate, CancellationToken cancellationToken)
    {
        if (key?.Any(char.IsControl) == true) throw new ArgumentException("密钥含无效字符。");
        var completions = AssistantEndpointPolicy.Resolve(endpoint, "chat/completions").AbsoluteUri;
        var uri = new Uri(completions[..^"chat/completions".Length] + "models");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri); request.Options.Set(AssistantApiClient.AllowPrivateKey, allowPrivate);
        if (!string.IsNullOrWhiteSpace(key)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"模型发现返回HTTP {(int)response.StatusCode}；服务可能不支持模型列表，可手动填写模型。");
        using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
        { if (output.Length + count > 1024 * 1024) throw new InvalidDataException("模型列表超过1MiB。"); await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token).ConfigureAwait(false); }
        try
        {
            using var json = JsonDocument.Parse(output.ToArray(), new() { MaxDepth = 24 });
            if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() > 5000) throw new InvalidDataException("服务未返回兼容的模型列表，请手动填写。");
            return data.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                .Select(item => item.GetProperty("id").GetString()!).Where(id => id.Length is > 0 and <= 200 && !id.Any(char.IsControl))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        }
        catch (JsonException) { throw new InvalidDataException("模型列表格式无效，请手动填写模型。"); }
    }

    public async Task<string> TestAsync(AssistantSettings settings, string? key, CancellationToken token)
    {
        var probe = settings with { Enabled = true, SupportsTools = false, TimeoutSeconds = 15, MaxResponseCharacters = 1000 };
        var reply = new System.Text.StringBuilder();
        await foreach (var delta in new AssistantApiClient(http).StreamAsync(new(probe,
            [new(Guid.NewGuid(), Guid.NewGuid(), "user", "Reply only OK. Connection availability test.", DateTimeOffset.UtcNow)], key), token).ConfigureAwait(false)) reply.Append(delta.Text);
        if (reply.Length == 0) throw new InvalidOperationException("模型未返回正文，不能确认可用。");
        if (AssistantPrivacy.SecretReason(reply.ToString(), key) is { } reason) throw new InvalidOperationException(reason);
        return reply.ToString();
    }
}
