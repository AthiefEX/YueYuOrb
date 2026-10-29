using System.Runtime.InteropServices;
using FloatDesk.Windows.Audio;
namespace FloatDesk.Windows.Tests;

public sealed class AudioBackendTests
{
    [Fact]
    public void DefaultSwitchSetsAllRolesInOrderAndReleasesEveryComObject()
    {
        var native = new FakeRuntime();
        new CoreAudioBackend(native).SetDefaultDevice("a", CancellationToken.None);
        Assert.Equal(["a:Console", "a:Multimedia", "a:Communications"], native.Policy.Calls);
        Assert.Equal(native.Acquired.Count, native.Released.Count);
        Assert.Equal(native.Acquired.AsEnumerable().Reverse(), native.Released);
    }

    [Fact]
    public void PolicyFailurePreservesHresultAndStopsLaterRolesWhileReleasingObjects()
    {
        var native = new FakeRuntime();
        native.Policy.FailRole = AudioRole.Multimedia;
        var error = Assert.Throws<COMException>(() => new CoreAudioBackend(native).SetDefaultDevice("a", CancellationToken.None));
        Assert.Equal(unchecked((int)0x80070005), error.HResult);
        Assert.Contains("Multimedia", error.Message, StringComparison.Ordinal);
        Assert.Equal(["a:Console", "a:Multimedia"], native.Policy.Calls);
        Assert.Equal(native.Acquired.AsEnumerable().Reverse(), native.Released);
    }

    [Theory]
    [InlineData(2, AudioFlow.Render)]
    [InlineData(1, AudioFlow.Capture)]
    internal void InactiveOrCaptureEndpointNeverChangesAnyRole(uint state, AudioFlow flow)
    {
        var native = new FakeRuntime();
        native.Device.State = state;
        native.Device.Flow = flow;
        Assert.Throws<InvalidOperationException>(() => new CoreAudioBackend(native).SetDefaultDevice("a", CancellationToken.None));
        Assert.Empty(native.Policy.Calls);
        Assert.Equal(native.Acquired.AsEnumerable().Reverse(), native.Released);
    }

    [Fact]
    public void VolumeAndMuteAddressRequestedEndpointAndPreserveScalar()
    {
        var native = new FakeRuntime();
        var backend = new CoreAudioBackend(native);
        backend.SetVolume("speakers", .375, CancellationToken.None);
        backend.SetMute("headset", true, CancellationToken.None);
        Assert.Equal(["speakers", "headset"], native.Enumerator.RequestedIds);
        Assert.Equal(.375f, native.Volume.Scalar);
        Assert.True(native.Volume.Muted);
        Assert.Equal(native.Acquired.Count, native.Released.Count);
    }

    [Fact]
    public void CancellationAfterEndpointActivationPreventsNativeWriteAndCleansUp()
    {
        var native = new FakeRuntime();
        using var source = new CancellationTokenSource();
        native.Device.OnActivate = source.Cancel;
        Assert.ThrowsAny<OperationCanceledException>(() => new CoreAudioBackend(native).SetVolume("a", .9, source.Token));
        Assert.Equal(0, native.Volume.WriteCount);
        Assert.Equal(native.Acquired.AsEnumerable().Reverse(), native.Released);
    }

    [Fact]
    public void SnapshotUsesActiveRenderEnumerationMultimediaDefaultAndFriendlyName()
    {
        var native = new FakeRuntime();
        var snapshot = new CoreAudioBackend(native).GetSnapshot(CancellationToken.None);
        Assert.Equal("a", snapshot.DefaultDeviceId);
        Assert.Equal("Speakers", Assert.Single(snapshot.Devices).Name);
        Assert.Equal(.25, snapshot.Volume);
        Assert.True(snapshot.Muted);
        Assert.Equal(AudioFlow.Render, native.Enumerator.EnumerationFlow);
        Assert.Equal(1u, native.Enumerator.StateMask);
        Assert.Equal(AudioRole.Multimedia, native.Enumerator.DefaultRole);
        Assert.Equal(0, native.Volume.WriteCount);
        Assert.Equal(native.Acquired.Count, native.Released.Count);
    }

    [Fact]
    public void MissingDefaultProducesEmptyDefaultButOtherNativeErrorsSurface()
    {
        var native = new FakeRuntime();
        native.Enumerator.DefaultResult = AudioInterop.NotFound;
        var backend = new CoreAudioBackend(native);
        var snapshot = backend.GetSnapshot(CancellationToken.None);
        Assert.Null(snapshot.DefaultDeviceId);
        Assert.Equal(0, snapshot.Volume);
        Assert.False(snapshot.Muted);
        Assert.Single(snapshot.Devices);
        native.Enumerator.DefaultResult = unchecked((int)0x80070005);
        Assert.Equal(unchecked((int)0x80070005), Assert.Throws<COMException>(() => backend.GetSnapshot(CancellationToken.None)).HResult);
        Assert.Equal(native.Acquired.Count, native.Released.Count);
    }

    [Fact]
    public void PolicyQueryFailureStillReleasesCreatedObjectAndEndpoint()
    {
        var native = new FakeRuntime { WrongPolicyInterface = true };
        Assert.Throws<InvalidCastException>(() => new CoreAudioBackend(native).SetDefaultDevice("a", CancellationToken.None));
        Assert.Equal(native.Acquired.AsEnumerable().Reverse(), native.Released);
    }

    private sealed class FakeRuntime : IAudioComRuntime
    {
        internal FakeRuntime()
        {
            Device = new FakeDevice(this);
            Enumerator = new FakeEnumerator(this);
        }
        internal FakeDevice Device { get; }
        internal FakeEnumerator Enumerator { get; }
        internal FakeVolume Volume { get; } = new();
        internal FakePolicy Policy { get; } = new();
        internal List<object> Acquired { get; } = [];
        internal List<object> Released { get; } = [];
        internal bool WrongPolicyInterface { get; init; }
        internal T Acquire<T>(T instance) where T : class { Acquired.Add(instance); return instance; }
        public object Create(Guid classId) => Acquire(classId == AudioInterop.EnumeratorClass ? (object)Enumerator : WrongPolicyInterface ? new object() : Policy);
        public void Release(object instance) => Released.Add(instance);
    }

    private sealed class FakeEnumerator(FakeRuntime runtime) : IMMDeviceEnumerator, IMMDeviceCollection
    {
        internal List<string> RequestedIds { get; } = [];
        internal AudioFlow EnumerationFlow { get; private set; }
        internal uint StateMask { get; private set; }
        internal AudioRole DefaultRole { get; private set; }
        internal int DefaultResult { get; set; }
        public int EnumAudioEndpoints(AudioFlow flow, uint mask, out IMMDeviceCollection devices) { EnumerationFlow = flow; StateMask = mask; devices = runtime.Acquire<IMMDeviceCollection>(this); return 0; }
        public int GetDefaultAudioEndpoint(AudioFlow flow, AudioRole role, out IMMDevice device) { Assert.Equal(AudioFlow.Render, flow); DefaultRole = role; device = DefaultResult == 0 ? runtime.Acquire<IMMDevice>(runtime.Device) : null!; return DefaultResult; }
        public int GetDevice(string id, out IMMDevice device) { RequestedIds.Add(id); device = runtime.Acquire<IMMDevice>(runtime.Device); return 0; }
        public int GetCount(out uint count) { count = 1; return 0; }
        public int Item(uint index, out IMMDevice device) { Assert.Equal(0u, index); device = runtime.Acquire<IMMDevice>(runtime.Device); return 0; }
        public int RegisterEndpointNotificationCallback(IntPtr callback) => throw new NotSupportedException();
        public int UnregisterEndpointNotificationCallback(IntPtr callback) => throw new NotSupportedException();
    }

    private sealed class FakeDevice(FakeRuntime runtime) : IMMDevice, IMMEndpoint, IPropertyStore
    {
        internal uint State { get; set; } = 1;
        internal AudioFlow Flow { get; set; } = AudioFlow.Render;
        internal Action? OnActivate { get; set; }
        public int GetState(out uint state) { state = State; return 0; }
        public int GetDataFlow(out AudioFlow flow) { flow = Flow; return 0; }
        public int GetId(out string id) { id = "a"; return 0; }
        public int Activate(ref Guid iid, uint context, IntPtr parameters, out object instance) { Assert.Equal(typeof(IAudioEndpointVolume).GUID, iid); instance = runtime.Acquire(runtime.Volume); OnActivate?.Invoke(); return 0; }
        public int OpenPropertyStore(uint access, out IPropertyStore properties) { Assert.Equal(0u, access); properties = runtime.Acquire<IPropertyStore>(this); return 0; }
        public int GetValue(ref PropertyKey key, out PropVariant value) { Assert.Equal(14u, key.PropertyId); Assert.Equal(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), key.FormatId); value = new() { Type = 31, Pointer = Marshal.StringToCoTaskMemUni("Speakers") }; return 0; }
        public int GetCount(out uint count) => throw new NotSupportedException();
        public int GetAt(uint index, out PropertyKey key) => throw new NotSupportedException();
        public int SetValue(ref PropertyKey key, ref PropVariant value) => throw new NotSupportedException();
        public int Commit() => throw new NotSupportedException();
    }

    private sealed class FakeVolume : IAudioEndpointVolume
    {
        internal float Scalar { get; private set; } = .25f;
        internal bool Muted { get; private set; } = true;
        internal int WriteCount { get; private set; }
        public int GetMasterVolumeLevelScalar(out float level) { level = Scalar; return 0; }
        public int GetMute(out bool muted) { muted = Muted; return 0; }
        public int SetMasterVolumeLevelScalar(float level, IntPtr context) { Scalar = level; WriteCount++; return 0; }
        public int SetMute(bool muted, IntPtr context) { Muted = muted; WriteCount++; return 0; }
        public int RegisterControlChangeNotify(IntPtr callback) => throw new NotSupportedException();
        public int UnregisterControlChangeNotify(IntPtr callback) => throw new NotSupportedException();
        public int GetChannelCount(out uint count) => throw new NotSupportedException();
        public int SetMasterVolumeLevel(float level, IntPtr context) => throw new NotSupportedException();
        public int GetMasterVolumeLevel(out float level) => throw new NotSupportedException();
        public int SetChannelVolumeLevel(uint channel, float level, IntPtr context) => throw new NotSupportedException();
        public int SetChannelVolumeLevelScalar(uint channel, float level, IntPtr context) => throw new NotSupportedException();
        public int GetChannelVolumeLevel(uint channel, out float level) => throw new NotSupportedException();
        public int GetChannelVolumeLevelScalar(uint channel, out float level) => throw new NotSupportedException();
        public int GetVolumeStepInfo(out uint step, out uint count) => throw new NotSupportedException();
        public int VolumeStepUp(IntPtr context) => throw new NotSupportedException();
        public int VolumeStepDown(IntPtr context) => throw new NotSupportedException();
        public int QueryHardwareSupport(out uint mask) => throw new NotSupportedException();
        public int GetVolumeRange(out float minimum, out float maximum, out float increment) => throw new NotSupportedException();
    }

    private sealed class FakePolicy : IPolicyConfig
    {
        internal List<string> Calls { get; } = [];
        internal AudioRole? FailRole { get; set; }
        public int SetDefaultEndpoint(string id, AudioRole role) { Calls.Add($"{id}:{role}"); return role == FailRole ? unchecked((int)0x80070005) : 0; }
        public int GetMixFormat(string id, out IntPtr format) => throw new NotSupportedException();
        public int GetDeviceFormat(string id, int defaults, out IntPtr format) => throw new NotSupportedException();
        public int ResetDeviceFormat(string id) => throw new NotSupportedException();
        public int SetDeviceFormat(string id, IntPtr endpoint, IntPtr mix) => throw new NotSupportedException();
        public int GetProcessingPeriod(string id, int defaults, out long period, out long minimum) => throw new NotSupportedException();
        public int SetProcessingPeriod(string id, ref long period) => throw new NotSupportedException();
        public int GetShareMode(string id, IntPtr mode) => throw new NotSupportedException();
        public int SetShareMode(string id, IntPtr mode) => throw new NotSupportedException();
        public int GetPropertyValue(string id, ref PropertyKey key, out PropVariant value) => throw new NotSupportedException();
        public int SetPropertyValue(string id, ref PropertyKey key, ref PropVariant value) => throw new NotSupportedException();
        public int SetEndpointVisibility(string id, int visible) => throw new NotSupportedException();
    }
}
