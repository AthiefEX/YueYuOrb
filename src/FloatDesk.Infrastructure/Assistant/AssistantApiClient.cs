using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using FloatDesk.Core.Assistant;

namespace FloatDesk.Infrastructure.Assistant;

public sealed class AssistantApiClient(HttpClient http) : IAssistantModelClient
{
    public static readonly HttpRequestOptionsKey<bool> AllowPrivateKey = new("YueYu.Assistant.AllowPrivate");
    public static bool IsAddressAllowed(IPAddress address, bool local, bool allowPrivate)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (local) return IPAddress.IsLoopback(address);
        var bytes = address.GetAddressBytes();
        var special = IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.IsIPv6Multicast ||
            (address.AddressFamily == AddressFamily.InterNetworkV6 ? (bytes[0] & 0xfe) == 0xfc :
            bytes[0] is 0 or 10 or 127 || bytes[0] >= 224 || bytes[0] == 169 && bytes[1] == 254 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
            bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 100 && bytes[1] is >= 64 and <= 127 || bytes[0] == 198 && bytes[1] is 18 or 19);
        return !special || allowPrivate;
    }

    public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseProxy = false,
        // Never pool across differently scoped endpoint permissions.
        PooledConnectionLifetime = TimeSpan.Zero,
        ConnectCallback = async (context, token) =>
        {
            var uri = context.InitialRequestMessage.RequestUri!;
            var local = AssistantEndpointPolicy.IsLocal(uri);
            var allowPrivate = context.InitialRequestMessage.Options.TryGetValue(AllowPrivateKey, out var permitted) && permitted;
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token).ConfigureAwait(false);
            foreach (var address in addresses.Where(address => IsAddressAllowed(address, local, allowPrivate)))
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(address, context.DnsEndPoint.Port, token).ConfigureAwait(false); return new NetworkStream(socket, ownsSocket: true); }
                catch (SocketException) { socket.Dispose(); }
                catch { socket.Dispose(); throw; }
            }
            throw new HttpRequestException("助手接口没有获准且可连接的地址；内网接口需在设置中明确允许。");
        }
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public async IAsyncEnumerable<AssistantChatDelta> StreamAsync(AssistantChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var settings = request.Settings;
        var errors = AssistantSettingsValidator.Validate(settings);
        if (errors.Count != 0) throw new ArgumentException("助手连接配置无效。");
        if (request.ApiKey?.Any(char.IsControl) == true) throw new ArgumentException("API密钥不能包含控制字符。");
        if (request.Messages.Count > 1000 || request.Messages.Sum(message => (long)message.Content.Length + JsonSerializer.Serialize(message.ToolCalls).Length) > settings.MaxContextCharacters)
            throw new InvalidOperationException("上下文过长，请新建会话或减少附加素材。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds)); var token = deadline.Token;
        var payloadBytes = CreatePayloadBytes(request);
        AssistantPrivacy.EnsureAllowed(request, payloadBytes);
        using var message = new HttpRequestMessage(HttpMethod.Post, AssistantEndpointPolicy.Resolve(settings.Endpoint, "chat/completions")) { Content = new ByteArrayContent(payloadBytes) };
        message.Options.Set(AllowPrivateKey, settings.AllowPrivateEndpoint);
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        if (!string.IsNullOrWhiteSpace(request.ApiKey)) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey.Trim());
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(settings.Stream ? "text/event-stream" : "application/json"));
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"助手接口返回HTTP {(int)response.StatusCode}，请检查连接、额度及模型设置。");
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var completed = false; long output = 0;
        if (!settings.Stream || response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            using var memory = new MemoryStream(); var buffer = new byte[8192]; int count;
            while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            { if (memory.Length + count > settings.MaxResponseCharacters * 8L + 65536) throw new InvalidDataException("助手响应超过限制。"); await memory.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false); }
            var text = Encoding.UTF8.GetString(memory.ToArray());
            foreach (var delta in Parse(text, false)) { CheckOutput(delta); yield return delta; }
            yield return new(IsComplete: true); yield break;
        }
        await foreach (var payloadText in ReadEventsAsync(stream, settings.MaxResponseCharacters * 8 + 65536, token).ConfigureAwait(false))
        {
            if (payloadText == "[DONE]") { completed = true; yield return new(IsComplete: true); break; }
            foreach (var delta in Parse(payloadText, true))
            { CheckOutput(delta); if (delta.IsComplete) completed = true; yield return delta; }
        }
        if (!completed) throw new InvalidDataException("响应在完成前中断，已保留收到的内容，可重新发送。");

        void CheckOutput(AssistantChatDelta delta)
        {
            output += delta.Text.Length + (delta.ToolCall?.Arguments?.Length ?? 0) + (delta.ToolCall?.Name?.Length ?? 0);
            if (output > settings.MaxResponseCharacters) throw new InvalidDataException("助手输出超过配置上限。");
        }
    }

    public static byte[] CreatePayloadBytes(AssistantChatRequest request)
    {
        var settings = request.Settings;
        var messages = request.Messages.Select(message => new Dictionary<string, object?>
        {
            ["role"] = message.Role, ["content"] = message.Content,
            ["tool_calls"] = message.ToolCalls.Count == 0 ? null : message.ToolCalls.Select(call => new { id = call.Id, type = "function", function = new { name = call.Name, arguments = call.Arguments } }).ToArray(),
            ["tool_call_id"] = message.ToolCallId
        }).Select(message => message.Where(pair => pair.Value is not null).ToDictionary(pair => pair.Key, pair => pair.Value)).ToArray();
        var payload = new Dictionary<string, object?> { ["model"] = settings.Model, ["messages"] = messages, ["stream"] = settings.Stream, ["max_tokens"] = Math.Clamp(settings.MaxResponseCharacters / 4, 256, 8192) };
        if (request.Tools is { Count: > 0 }) payload["tools"] = request.Tools.Select(tool => new { type = "function", function = new { name = tool.Name, description = tool.Description, parameters = tool.Parameters } }).ToArray();
        return JsonSerializer.SerializeToUtf8Bytes(payload);
    }

    private static List<AssistantChatDelta> Parse(string payload, bool streaming)
    {
        try
        {
            using var document = JsonDocument.Parse(payload, new() { MaxDepth = 32 });
            var root = document.RootElement; var result = new List<AssistantChatDelta>();
            var sources = new List<AssistantSource>();
            if (root.TryGetProperty("citations", out var citations) && citations.ValueKind == JsonValueKind.Array)
                foreach (var citation in citations.EnumerateArray().Take(20)) if (citation.ValueKind == JsonValueKind.String && AssistantSearchService.SafeSource(null, citation.GetString()) is { } source) sources.Add(source);
            var choice = root.GetProperty("choices")[0];
            var content = choice.GetProperty(streaming ? "delta" : "message");
            if (content.TryGetProperty("annotations", out var annotations) && annotations.ValueKind == JsonValueKind.Array)
                foreach (var annotation in annotations.EnumerateArray().Take(20))
                    if (annotation.TryGetProperty("url_citation", out var citation) && citation.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String &&
                        AssistantSearchService.SafeSource(citation.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String ? title.GetString() : null, url.GetString()) is { } source) sources.Add(source);
            if (sources.Count > 0) result.Add(new() { Sources = sources });
            if (content.TryGetProperty("content", out var text) && text.ValueKind == JsonValueKind.String && text.GetString() is { Length: > 0 } value) result.Add(new(value));
            if (content.TryGetProperty("tool_calls", out var calls))
            {
                if (calls.GetArrayLength() > 16) throw new InvalidDataException("工具调用过多。");
                var ordinal = 0;
                foreach (var call in calls.EnumerateArray())
                {
                    var index = call.TryGetProperty("index", out var ix) ? ix.GetInt32() : ordinal;
                    if (index is < 0 or > 15) throw new InvalidDataException("工具调用索引无效。");
                    var id = call.TryGetProperty("id", out var callId) ? callId.GetString() : null;
                    string? name = null, arguments = null;
                    if (call.TryGetProperty("function", out var function))
                    { name = function.TryGetProperty("name", out var n) ? n.GetString() : null; arguments = function.TryGetProperty("arguments", out var a) ? a.GetString() : null; }
                    if (id?.Length > 200 || name?.Length > 200) throw new InvalidDataException("工具调用字段过长。");
                    result.Add(new(ToolCall: new(index, id, name, arguments))); ordinal++;
                }
            }
            if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
            {
                if (finish.GetString() is "length" or "content_filter") throw new InvalidDataException("模型输出未完整结束，请调整上下文或输出限制。");
                result.Add(new(IsComplete: true));
            }
            return result;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException or FormatException)
        { throw new InvalidDataException("助手返回的数据格式无效，未执行任何其中的指令。"); }
    }

    private static async IAsyncEnumerable<string> ReadEventsAsync(Stream stream, int maximum, [EnumeratorCancellation] CancellationToken token)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true, 4096, leaveOpen: true);
        var line = new StringBuilder(); var data = new StringBuilder(); var buffer = new char[1024]; long received = 0; int count;
        while ((count = await reader.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            token.ThrowIfCancellationRequested(); received += count;
            if (received > maximum) throw new InvalidDataException("助手响应超过限制。");
            for (var index = 0; index < count; index++)
            {
                var character = buffer[index];
                if (character != '\n') { line.Append(character); continue; }
                var value = line.ToString().TrimEnd('\r'); line.Clear();
                if (value.Length == 0 && data.Length > 0)
                { var completed = data.ToString().TrimEnd('\n'); data.Clear(); yield return completed; }
                else if (value.StartsWith("data:", StringComparison.Ordinal))
                { var field = value.AsSpan(5); if (field.StartsWith(" ", StringComparison.Ordinal)) field = field[1..]; data.Append(field); data.Append('\n'); }
            }
        }
    }
}
