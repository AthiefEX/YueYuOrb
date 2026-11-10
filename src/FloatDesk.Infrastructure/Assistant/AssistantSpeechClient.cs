using System.Net.Http.Headers;
using System.Text.Json;
using FloatDesk.Core.Assistant;

namespace FloatDesk.Infrastructure.Assistant;

public sealed class AssistantSpeechClient(HttpClient http)
{
    public async Task<string> TranscribeAsync(AssistantVoiceSettings settings, byte[] wave, string? apiKey, CancellationToken cancellationToken, bool allowPrivateEndpoint = false)
    {
        if (wave.Length is < 44 or > 3840044 || !wave.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !wave.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            throw new ArgumentException("录音格式或长度无效。", nameof(wave));
        if (string.IsNullOrWhiteSpace(settings.Model) || settings.Model.Length > 200 || apiKey?.Any(char.IsControl) == true) throw new ArgumentException("语音模型或凭据无效。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(120));
        using var message = new HttpRequestMessage(HttpMethod.Post, AssistantEndpointPolicy.Resolve(settings.Endpoint, "audio/transcriptions"));
        message.Options.Set(AssistantApiClient.AllowPrivateKey, allowPrivateEndpoint);
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(settings.Model), "model");
        var audio = new ByteArrayContent(wave); audio.Headers.ContentType = new("audio/wav"); content.Add(audio, "file", "recording.wav"); message.Content = content;
        if (!string.IsNullOrWhiteSpace(apiKey)) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"语音识别接口返回HTTP {(int)response.StatusCode}，请检查配置。");
        using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        using var output = new MemoryStream(); var buffer = new byte[4096]; int count;
        while ((count = await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
        { if (output.Length + count > 256000) throw new InvalidDataException("语音识别结果过长。"); await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token).ConfigureAwait(false); }
        try
        {
            using var json = JsonDocument.Parse(output.ToArray(), new() { MaxDepth = 8 });
            if (!json.RootElement.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String || text.GetString() is not { Length: > 0 and <= 32000 } value)
                throw new InvalidDataException("语音接口未返回有效文字。");
            return value;
        }
        catch (JsonException) { throw new InvalidDataException("语音接口返回格式无效。"); }
    }
}
