using System.Net;
using FloatDesk.Infrastructure.Assistant;

namespace FloatDesk.Infrastructure.Tests;

public sealed class AssistantTransportTests
{
    [Theory]
    [InlineData("10.0.0.2", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("1.1.1.1", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void PublicEndpointsCannotResolveToPrivateAddresses(string ip, bool expected)
        => Assert.Equal(expected, AssistantApiClient.IsAddressAllowed(IPAddress.Parse(ip), false, false));

    [Fact]
    public void ExplicitLoopbackModelsStayLocalAndPrivateEndpointsRequireOptIn()
    {
        Assert.True(AssistantApiClient.IsAddressAllowed(IPAddress.Loopback, true, false));
        Assert.False(AssistantApiClient.IsAddressAllowed(IPAddress.Parse("1.1.1.1"), true, true));
        Assert.True(AssistantApiClient.IsAddressAllowed(IPAddress.Parse("192.168.1.4"), false, true));
    }
}
