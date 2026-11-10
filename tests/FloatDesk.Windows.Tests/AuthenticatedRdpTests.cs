using FloatDesk.Windows.DesktopSessions;

namespace FloatDesk.Windows.Tests;

public sealed class AuthenticatedRdpTests
{
    [Theory]
    [InlineData("C:\\\0", "C:\\", true)]
    [InlineData("z:\\\0", "Z:\\", true)]
    [InlineData("Z:\\", "Z:\\", true)]
    [InlineData("C:\\\0other", "C:\\", false)]
    [InlineData("C:\\other", "C:\\", false)]
    [InlineData("D:\\\0", "C:\\", false)]
    public void DriveMatchingAcceptsOnlyExactRootWithOptionalNativeTerminator(string nativeName, string selectedRoot, bool expected)
    {
        Assert.Equal(expected, AuthenticatedRdpConnection.MatchesRedirectedDrive(nativeName, selectedRoot));
    }

    [Fact]
    public void ValidDisplayAndExplicitDriveRootsAreAccepted()
    {
        AuthenticatedRdpConnection.ValidateViewSettings(49152, "YueYu", "GUEST", 1280, 720, ["X:\\"]);
    }

    [Theory]
    [InlineData("YueYu\r\nfull address:s:other")]
    [InlineData("DOMAIN\\User")]
    public void ConnectionMetadataCannotInjectAnotherRdpTarget(string user)
    {
        Assert.Throws<ArgumentException>(() => AuthenticatedRdpConnection.ValidateViewSettings(49152, user, "GUEST", 1280, 720, []));
    }
}
