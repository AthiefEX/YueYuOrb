using System.Security;
using FloatDesk.Windows.DesktopSessions;

namespace FloatDesk.Windows.Tests;

public sealed class WindowsGuestCredentialTests
{
    [Fact]
    public void DiagnosticDoesNotAttemptToUnlockOrAttachSessions()
    {
        var script = HyperVGuestAdministration.Script;
        Assert.Contains("GuestSessionInventory:", script);
        Assert.DoesNotContain("WTSConnectSessionW", script);
        Assert.DoesNotContain("ConvertTo-SecureString", script);
        Assert.Contains("[Security.SecureString]::new()", script);
    }
    [Fact]
    public void CredentialsRoundTripIsScopedToVmAndDeleteIsIdempotent()
    {
        var store = new WindowsGuestCredentialStore();
        var vm = Guid.NewGuid(); var other = Guid.NewGuid();
        using var secret = new SecureString();
        foreach (var c in "test-only-not-a-real-password") secret.AppendChar(c);
        try
        {
            store.Save(vm, "TestGuest", secret);
            using var loaded = store.Load(vm);
            Assert.NotNull(loaded);
            Assert.Equal("TestGuest", loaded.UserName);
            Assert.Equal(secret.Length, loaded.Password.Length);
            var pointer = System.Runtime.InteropServices.Marshal.SecureStringToGlobalAllocUnicode(loaded.Password);
            try { Assert.Equal("test-only-not-a-real-password", System.Runtime.InteropServices.Marshal.PtrToStringUni(pointer)); }
            finally { System.Runtime.InteropServices.Marshal.ZeroFreeGlobalAllocUnicode(pointer); }
            Assert.Null(store.Load(other));
            store.Delete(vm); store.Delete(vm);
            Assert.Null(store.Load(vm));
        }
        finally { store.Delete(vm); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("DOMAIN\\User")]
    [InlineData("user@example.com")]
    [InlineData("bad\nuser")]
    public void InvalidLocalUsernameDoesNotSaveAnything(string user)
    {
        using var secret = new SecureString(); secret.AppendChar('x');
        Assert.Throws<ArgumentException>(() => new WindowsGuestCredentialStore().Save(Guid.NewGuid(), user, secret));
    }

    [Fact]
    public void LoginScriptKeepsPasswordOutOfWinlogonAndChecksGuestOwnership()
    {
        var script = HyperVGuestAdministration.Script;
        Assert.Contains("LsaStorePrivateData", script);
        Assert.Contains("YueYuOrb\\AutoLogon", script);
        Assert.Contains("Invoke-Command -VMId", script);
        Assert.Contains("[Console]::In.ReadToEnd()", HyperVGuestAdministration.Bootstrap);
        Assert.True(HyperVGuestAdministration.Bootstrap.Length < 2000);
        Assert.DoesNotContain("Set-ItemProperty -Path $winlogon -Name DefaultPassword", script);
        Assert.Contains("ForeignAutoLogon", script);
    }
}
