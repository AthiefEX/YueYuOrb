using FloatDesk.Windows.Assistant;

namespace FloatDesk.Windows.Tests;

public sealed class AssistantAudioTests
{
    [Fact]
    public void PcmIsEncodedAsBoundedMono16KhzWave()
    {
        var bytes = AssistantMicrophone.EncodeWave(new byte[32000]);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
        Assert.Equal(16000, BitConverter.ToInt32(bytes, 24)); Assert.Equal(1, BitConverter.ToInt16(bytes, 22));
        Assert.Equal(32000, BitConverter.ToInt32(bytes, 40)); Assert.Equal(32044, bytes.Length);
        Assert.Throws<ArgumentException>(() => AssistantMicrophone.EncodeWave(new byte[3]));
    }
}
