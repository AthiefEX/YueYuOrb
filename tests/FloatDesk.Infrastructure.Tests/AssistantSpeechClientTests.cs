using System.Net;
using FloatDesk.Core.Assistant;
using FloatDesk.Infrastructure.Assistant;

namespace FloatDesk.Infrastructure.Tests;

public sealed class AssistantSpeechClientTests
{
    [Fact]
    public async Task UploadsBoundedWaveWithIndependentCredentialAndReturnsTranscript()
    {
        var called = false;
        using var http = new HttpClient(new Handler(async request =>
        {
            called = true;
            Assert.Equal("https://speech.test/v1/audio/transcriptions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("speech-secret", request.Headers.Authorization!.Parameter);
            var body = await request.Content!.ReadAsStringAsync();
            Assert.Contains("whisper-test", body); Assert.Contains("RIFF", body); Assert.DoesNotContain("speech-secret", body);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"text\":\"识别的文字\"}") };
        }));
        var wave = new byte[46]; "RIFF"u8.CopyTo(wave); "WAVE"u8.CopyTo(wave.AsSpan(8));
        var text = await new AssistantSpeechClient(http).TranscribeAsync(new() { Endpoint = "https://speech.test/v1", Model = "whisper-test" }, wave, "speech-secret", CancellationToken.None);
        Assert.True(called); Assert.Equal("识别的文字", text);
    }

    [Fact]
    public async Task RejectsAudioAndHttpErrorsWithoutExposingProviderBody()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("private-provider-detail") })));
        var client = new AssistantSpeechClient(http); var settings = new AssistantVoiceSettings { Endpoint = "https://speech.test/v1" };
        await Assert.ThrowsAsync<ArgumentException>(() => client.TranscribeAsync(settings, [], null, CancellationToken.None));
        var wave = new byte[46]; "RIFF"u8.CopyTo(wave); "WAVE"u8.CopyTo(wave.AsSpan(8));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.TranscribeAsync(settings, wave, null, CancellationToken.None));
        Assert.Contains("401", error.Message); Assert.DoesNotContain("private-provider-detail", error.Message);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request); }
}
