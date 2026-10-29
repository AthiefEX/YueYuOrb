using System.Runtime.InteropServices;

namespace FloatDesk.Windows.Audio;

// Keeps COM activation and ownership at the native boundary.
internal interface IAudioComRuntime
{
    object Create(Guid classId);
    void Release(object instance);
}

internal sealed class NativeAudioComRuntime : IAudioComRuntime
{
    public object Create(Guid classId) => AudioInterop.Create(classId);
    public void Release(object instance) => Marshal.FinalReleaseComObject(instance);
}
