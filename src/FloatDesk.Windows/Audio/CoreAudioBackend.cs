using System.Runtime.InteropServices;
using FloatDesk.Core.Models;

namespace FloatDesk.Windows.Audio;

// All methods run synchronously on AudioComDispatcher; no RCW escapes an operation.
internal sealed class CoreAudioBackend : IAudioBackend
{
    private readonly IAudioComRuntime runtime;
    public CoreAudioBackend() : this(new NativeAudioComRuntime()) { }
    internal CoreAudioBackend(IAudioComRuntime runtime) => this.runtime = runtime;
    private const uint Active = 1;

    public AudioSnapshot GetSnapshot(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var scope = new ComScope(runtime);
        var enumerator = scope.Own<IMMDeviceEnumerator>(runtime.Create(AudioInterop.EnumeratorClass));
        AudioInterop.Check(enumerator.EnumAudioEndpoints(AudioFlow.Render, Active, out var collection), "Enumerate active playback endpoints");
        scope.Own<IMMDeviceCollection>(collection);
        AudioInterop.Check(collection.GetCount(out var count), "Read playback endpoint count");
        var devices = new List<AudioDevice>();
        for (uint i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            using var deviceScope = new ComScope(runtime);
            AudioInterop.Check(collection.Item(i, out var device), "Read playback endpoint");
            deviceScope.Own<IMMDevice>(device);
            AudioInterop.Check(device.GetId(out var id), "Read playback endpoint ID");
            devices.Add(new AudioDevice(id, ReadFriendlyName(device, deviceScope)));
        }
        token.ThrowIfCancellationRequested();
        var hr = enumerator.GetDefaultAudioEndpoint(AudioFlow.Render, AudioRole.Multimedia, out var defaultDevice);
        // ERROR_NOT_FOUND is the documented absence of a default endpoint, not a native failure.
        if (hr == AudioInterop.NotFound)
            return new AudioSnapshot(devices.AsReadOnly(), null, 0, false);
        AudioInterop.Check(hr, "Read multimedia playback default");
        scope.Own<IMMDevice>(defaultDevice);
        AudioInterop.Check(defaultDevice.GetId(out var defaultId), "Read multimedia default endpoint ID");
        var volume = ActivateVolume(defaultDevice, scope);
        AudioInterop.Check(volume.GetMasterVolumeLevelScalar(out var scalar), "Read multimedia default volume");
        AudioInterop.Check(volume.GetMute(out var muted), "Read multimedia default mute");
        token.ThrowIfCancellationRequested();
        return new AudioSnapshot(devices.AsReadOnly(), defaultId, scalar, muted);
    }

    public void SetDefaultDevice(string deviceId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var scope = new ComScope(runtime);
        GetActiveRenderEndpoint(deviceId, scope);
        var policy = scope.Own<IPolicyConfig>(runtime.Create(AudioInterop.PolicyClass));
        token.ThrowIfCancellationRequested();
        // Once started, finish all roles without cancellation between them. Windows has no atomic
        // three-role operation or rollback; a native failure can leave earlier roles changed.
        foreach (var role in new[] { AudioRole.Console, AudioRole.Multimedia, AudioRole.Communications })
            AudioInterop.Check(policy.SetDefaultEndpoint(deviceId, role), $"Set {role} playback default (earlier roles may already have changed)");
    }

    public void SetVolume(string deviceId, double volume, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var scope = new ComScope(runtime);
        var endpoint = ActivateVolume(GetActiveRenderEndpoint(deviceId, scope), scope);
        token.ThrowIfCancellationRequested();
        AudioInterop.Check(endpoint.SetMasterVolumeLevelScalar((float)volume, IntPtr.Zero), "Set playback endpoint volume");
    }

    public void SetMute(string deviceId, bool muted, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var scope = new ComScope(runtime);
        var endpoint = ActivateVolume(GetActiveRenderEndpoint(deviceId, scope), scope);
        token.ThrowIfCancellationRequested();
        AudioInterop.Check(endpoint.SetMute(muted, IntPtr.Zero), "Set playback endpoint mute");
    }

    private IMMDevice GetActiveRenderEndpoint(string id, ComScope scope)
    {
        var enumerator = scope.Own<IMMDeviceEnumerator>(runtime.Create(AudioInterop.EnumeratorClass));
        AudioInterop.Check(enumerator.GetDevice(id, out var device), "Find playback endpoint");
        scope.Own<IMMDevice>(device);
        AudioInterop.Check(device.GetState(out var state), "Read playback endpoint state");
        AudioInterop.Check(((IMMEndpoint)device).GetDataFlow(out var flow), "Read endpoint direction");
        if (state != Active || flow != AudioFlow.Render)
            throw new InvalidOperationException("The selected endpoint is not an active playback device.");
        return device;
    }

    private static IAudioEndpointVolume ActivateVolume(IMMDevice device, ComScope scope)
    {
        var iid = typeof(IAudioEndpointVolume).GUID;
        AudioInterop.Check(device.Activate(ref iid, 23, IntPtr.Zero, out var endpoint), "Activate playback endpoint volume"); // CLSCTX_ALL
        return scope.Own<IAudioEndpointVolume>(endpoint);
    }

    private static string ReadFriendlyName(IMMDevice device, ComScope scope)
    {
        AudioInterop.Check(device.OpenPropertyStore(0, out var properties), "Open endpoint properties"); // STGM_READ
        scope.Own<IPropertyStore>(properties);
        var key = new PropertyKey { FormatId = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), PropertyId = 14 }; // PKEY_Device_FriendlyName
        var value = default(PropVariant);
        try
        {
            AudioInterop.Check(properties.GetValue(ref key, out value), "Read playback endpoint friendly name");
            if (value.Type != 31 || value.Pointer == IntPtr.Zero) // VT_LPWSTR
                throw new InvalidOperationException("Playback endpoint friendly name is not a Unicode string.");
            return Marshal.PtrToStringUni(value.Pointer)!;
        }
        finally
        {
            AudioInterop.Check(AudioInterop.PropVariantClear(ref value), "Release endpoint friendly name");
        }
    }

    private sealed class ComScope(IAudioComRuntime runtime) : IDisposable
    {
        private readonly List<object> owned = [];
        internal T Own<T>(object instance)
        {
            owned.Add(instance); // Own before casting so failed QueryInterface also releases the RCW.
            return (T)instance;
        }
        public void Dispose()
        {
            List<Exception>? errors = null;
            for (var i = owned.Count - 1; i >= 0; i--)
            {
                try { runtime.Release(owned[i]); }
                catch (Exception error) { (errors ??= []).Add(error); }
            }
            owned.Clear();
            if (errors is not null)
                throw new AggregateException("Core Audio COM cleanup failed.", errors);
        }
    }
}


