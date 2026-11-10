using FloatDesk.Windows.DesktopSessions;

namespace FloatDesk.Windows.Tests;

public sealed class HyperVGuestDiscoveryTests
{
    [Fact]
    public void IdentityProofBindsGuestIdentityAndFreshChallenge()
    {
        var secret = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var identity = new HyperVGuestIdentity(Guid.NewGuid(), Guid.NewGuid(), 2, Guid.NewGuid(), "boot", "S-1-5-21-123", DateTimeOffset.UtcNow);
        var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var proof = HyperVGuestDiscovery.CreateProof(secret, nonce, identity);
        Assert.True(HyperVGuestDiscovery.VerifyProof(secret, nonce, identity, proof));
        Assert.False(HyperVGuestDiscovery.VerifyProof(secret, new byte[16], identity, proof));
        Assert.False(HyperVGuestDiscovery.VerifyProof(secret, nonce, identity with { WindowsSessionId = 3 }, proof));
        Assert.False(HyperVGuestDiscovery.VerifyProof(new byte[32], nonce, identity, proof));
    }
}
