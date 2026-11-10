using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FloatDesk.Core.Assistant;

namespace FloatDesk.Infrastructure.Assistant;

public sealed class AssistantEmbeddingClient(HttpClient http)
{
    public static string Fingerprint(AssistantApiProfile profile) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(new Uri(profile.Endpoint).AbsoluteUri.TrimEnd('/') + "\n" + profile.Model + "\n" + profile.Id)));
    public static string ContentHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public async Task<IReadOnlyList<float[]>> EmbedAsync(AssistantApiProfile profile, IReadOnlyList<string> texts, string? key, CancellationToken cancellationToken)
    {
        if (!profile.Enabled || !profile.ModelEnabled || !AssistantEndpointPolicy.IsValid(profile.Endpoint) || !AssistantEndpointPolicy.IsLocal(new Uri(profile.Endpoint))) throw new InvalidOperationException("语义索引只使用明确启用的回环本地模型，不向云端发送文档。");
        if (string.IsNullOrWhiteSpace(profile.Model) || texts.Count is < 1 or > 8 || texts.Sum(text => (long)text.Length) > 32000 || texts.Any(text => string.IsNullOrWhiteSpace(text) || AssistantPrivacy.SecretReason(text, key) is not null)) throw new ArgumentException("向量输入无效或包含凭据。");
        if (key?.Any(char.IsControl) == true) throw new ArgumentException("模型密钥无效。");
        var completions = AssistantEndpointPolicy.Resolve(profile.Endpoint, "chat/completions").AbsoluteUri;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(30)); var token = deadline.Token;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(completions[..^"chat/completions".Length] + "embeddings")) { Content = JsonContent.Create(new { model = profile.Model, input = texts, encoding_format = "float" }) };
        if (!string.IsNullOrWhiteSpace(key)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"本地向量接口返回HTTP {(int)response.StatusCode}，全文检索仍可使用。");
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false); using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0) { if (output.Length + count > 2 * 1024 * 1024) throw new InvalidDataException("向量响应超过2MiB。"); await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false); }
        try
        {
            using var json = JsonDocument.Parse(output.ToArray(), new() { MaxDepth = 12 }); var data = json.RootElement.GetProperty("data");
            if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() != texts.Count) throw new InvalidDataException("向量数量不匹配。");
            var result = new float[texts.Count][];
            foreach (var item in data.EnumerateArray())
            {
                var index = item.GetProperty("index").GetInt32(); var embedding = item.GetProperty("embedding");
                if (index < 0 || index >= texts.Count || result[index] is not null || embedding.ValueKind != JsonValueKind.Array || embedding.GetArrayLength() is < 1 or > 8192) throw new InvalidDataException("向量索引或维度无效。");
                var values = embedding.EnumerateArray().Select(value => value.GetSingle()).ToArray();
                if (values.Any(value => !float.IsFinite(value)) || values.All(value => value == 0)) throw new InvalidDataException("向量包含无效数值。"); result[index] = values;
            }
            if (result.Any(vector => vector is null || vector.Length != result[0].Length)) throw new InvalidDataException("模型返回不一致的向量维度。");
            return result;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException) { throw new InvalidDataException("向量响应格式无效。"); }
    }
}
