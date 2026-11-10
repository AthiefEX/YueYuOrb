using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FloatDesk.App.Views;
using FloatDesk.Infrastructure.Assistant;
using FloatDesk.Windows.Assistant;

namespace FloatDesk.App;

internal sealed partial class AppRuntime
{
    private AssistantMicrophone? _assistantMicrophone;
    private AssistantSpeaker? _assistantSpeaker;
    private CancellationTokenSource? _assistantVoiceRequest;
    private int _assistantVoiceVersion;

    async partial void OnAssistantVoiceRequested(AssistantWindow window)
    {
        if (_assistantMicrophone is not null) { await FinishAssistantRecordingAsync(window, _assistantVoiceVersion); return; }
        var settings = _settings.Current.Assistant;
        if (!settings.Enabled || !settings.Voice.Enabled || _assistantVoiceRequest is not null) return;
        if (!FloatDesk.Core.Assistant.AssistantEndpointPolicy.IsValid(settings.Voice.Endpoint)) { window.SetVoiceState(false, "请先配置语音识别接口并应用。"); return; }
        if (!AssistantTextDialog.Confirm(window, "开始语音输入", $"点击后开始使用麦克风，再次点击停止。录音最长{settings.Voice.MaxRecordingSeconds}秒，仅在内存暂存；停止后发送至 {new Uri(settings.Voice.Endpoint).Host} 的 {settings.Voice.Model}。识别结果{(settings.Voice.AutoSend ? "将自动发送给聊天模型" : "先填入输入框，可编辑后发送")}。")) return;
        if (!_settings.Current.Assistant.Enabled || _settings.Current.Assistant.Voice != settings.Voice) return;
        StopAssistantAudio();
        var version = _assistantVoiceVersion;
        try
        {
            _assistantMicrophone = new AssistantMicrophone();
            _assistantMicrophone.LimitReached += () => _ = window.Dispatcher.BeginInvoke(async () => await FinishAssistantRecordingAsync(window, version));
            _assistantMicrophone.Start(settings.Voice.MaxRecordingSeconds);
            window.SetVoiceState(true, "正在录音，再次点击停止并识别。");
        }
        catch (Exception error) when (error is InvalidOperationException or IOException or ExternalException)
        { StopAssistantAudio(); window.SetVoiceState(false, "无法开始录音，请检查麦克风设备与Windows权限。"); }
    }

    private async Task FinishAssistantRecordingAsync(AssistantWindow window, int version)
    {
        if (version != _assistantVoiceVersion || _assistantMicrophone is not { } microphone || _assistantVoiceRequest is not null) return;
        using var request = new CancellationTokenSource(); _assistantVoiceRequest = request;
        var session = window.CurrentSessionId;
        byte[]? wave = null;
        try
        {
            window.SetVoiceState(false, "正在识别语音…");
            wave = await microphone.StopAsync();
            if (version != _assistantVoiceVersion) return;
            microphone.Dispose(); _assistantMicrophone = null;
            var settings = _settings.Current.Assistant;
            if (!settings.Enabled || !settings.Voice.Enabled) return;
            var key = await _assistantCredentials!.GetAsync("asr", settings.Voice.Endpoint, request.Token);
            var text = await new AssistantSpeechClient(_assistantHttp!).TranscribeAsync(settings.Voice, wave, key, request.Token, settings.AllowPrivateEndpoint);
            if (version != _assistantVoiceVersion || request.IsCancellationRequested || !_settings.Current.Assistant.Enabled) return;
            if (AssistantPrivacy.SecretReason(text, key) is not null) throw new InvalidOperationException("识别内容疑似包含凭据，已阻止填入。");
            window.SetRecognizedText(text, settings.Voice.AutoSend, session); window.SetVoiceState(false, "语音识别完成。");
        }
        catch (OperationCanceledException) { if (version == _assistantVoiceVersion) window.SetVoiceState(false, "语音已停止。"); }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or System.Net.Http.HttpRequestException or ExternalException)
        { if (version == _assistantVoiceVersion) window.SetVoiceState(false, "语音识别未完成，请检查设备、识别接口与额度；仍可文字聊天。"); }
        finally
        {
            if (wave is not null) CryptographicOperations.ZeroMemory(wave);
            if (ReferenceEquals(_assistantVoiceRequest, request))
            {
                _assistantVoiceRequest = null;
                try { _assistantMicrophone?.Dispose(); }
                catch (InvalidOperationException) { window.SetVoiceState(false, "麦克风驱动未能正常释放，请检查设备。"); }
                finally { _assistantMicrophone = null; }
            }
        }
    }

    partial void StopAssistantAudio()
    {
        _assistantVoiceVersion++; _assistantVoiceRequest?.Cancel();
        try { _assistantMicrophone?.Dispose(); }
        catch (InvalidOperationException) { }
        finally { _assistantMicrophone = null; }
        try { _assistantSpeaker?.Dispose(); }
        catch (Exception error) when (error is ExternalException or InvalidOperationException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        finally { _assistantSpeaker = null; }
        GetTool<AssistantWindow>()?.SetVoiceState(false, "语音已停止。");
    }

    partial void SpeakAssistantReply(string text)
    {
        var settings = _settings.Current.Assistant;
        if (!settings.Enabled || !settings.Voice.Enabled || !settings.Voice.ReadReplies || _assistantMicrophone is not null) return;
        try { _assistantSpeaker ??= new AssistantSpeaker(); _assistantSpeaker.Speak(text); }
        catch (Exception error) when (error is ExternalException or InvalidOperationException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        { GetTool<AssistantWindow>()?.SetVoiceState(false, "系统朗读不可用，文字回复不受影响。"); }
    }
}
