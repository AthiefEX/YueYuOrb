using FloatDesk.Core.Audio;
using FloatDesk.Core.Models;
namespace FloatDesk.Core.Tests;
public sealed class AudioCycleSelectorTests
{
    [Theory]
    [InlineData("a", "c")]
    [InlineData("c", "b")]
    [InlineData("b", "a")]
    [InlineData("outside", "a")]
    [InlineData(null, "a")]
    public void PreservesGroupOrderSkipsDisconnectedAndWraps(string? current, string expected)
    {
        var group = new AudioCycleGroup(Guid.NewGuid(), "Desk", ["a", "offline", "c", "b"]);
        var snapshot = new AudioSnapshot([new("b", "B"), new("a", "A"), new("c", "C")], current, .5, false);
        Assert.Equal(expected, AudioCycleSelector.ChooseNext(group, snapshot));
    }
    [Theory]
    [InlineData("a", null)]
    [InlineData("other", "a")]
    [InlineData(null, "a")]
    public void SingleConnectedEndpointOnlySelectsWhenDifferent(string? current, string? expected)
    {
        var group = new AudioCycleGroup(Guid.NewGuid(), "Desk", ["offline", "a", "a"]);
        Assert.Equal(expected, AudioCycleSelector.ChooseNext(group, new([new("a", "A")], current, 0, false)));
    }
    [Fact]
    public void NoConnectedGroupMembersReturnsNull()
    {
        var snapshot = new AudioSnapshot([new("other", "Other")], "other", 0, false);
        Assert.Null(AudioCycleSelector.ChooseNext(new(Guid.NewGuid(), "Desk", ["gone"]), snapshot));
        Assert.Null(AudioCycleSelector.ChooseNext(new(Guid.NewGuid(), "Empty", []), snapshot));
        Assert.Null(AudioCycleSelector.ChooseNext(new(Guid.NewGuid(), "Desk", ["gone"]), new([], null, 0, false)));
    }
    [Fact]
    public void EndpointIdsAreOpaqueCaseSensitiveStrings()
    {
        Assert.Null(AudioCycleSelector.ChooseNext(new(Guid.NewGuid(), "Desk", ["A"]), new([new("a", "A")], null, 0, false)));
    }
}
