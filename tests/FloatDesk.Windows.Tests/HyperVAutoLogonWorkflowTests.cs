using System.Security;
using FloatDesk.Windows.DesktopSessions;

namespace FloatDesk.Windows.Tests;

public sealed class HyperVAutoLogonWorkflowTests
{
    private static readonly string[] PreparationCalls = ["ready", "PrepareAuthenticatedRdp", "QueryAutoLogon", "save"];
    [Fact]
    public async Task CancelledPreparationDoesNotStartGuestOrWriteCredential()
    {
        var calls = new List<string>(); using var credential = Credential();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new HyperVAutoLogonWorkflow(new Store(calls), new Backend(calls))
            .PrepareAsync(Guid.NewGuid(), credential, cancellation.Token));
        Assert.Empty(calls);
    }

    [Theory]
    [InlineData("GuestAutoLogonState:Enabled", GuestAutoLogonState.Enabled)]
    [InlineData("GuestAutoLogonState:Disabled", GuestAutoLogonState.Disabled)]
    [InlineData("GuestAutoLogonState:Foreign", GuestAutoLogonState.Foreign)]
    public async Task ReadStateUsesGuestEvidenceWithoutChangingGuest(string reply, GuestAutoLogonState expected)
    {
        var calls = new List<string>(); using var credential = Credential();
        var state = await new HyperVAutoLogonWorkflow(new Store(calls), new Backend(calls) { StateReply = reply })
            .ReadStateAsync(Guid.NewGuid(), credential, CancellationToken.None);
        Assert.Equal(expected, state); Assert.Equal("QueryAutoLogon", Assert.Single(calls));
    }
    [Fact]
    public async Task PrepareValidatesAndConfiguresBeforeSavingWithoutEnabling()
    {
        var calls = new List<string>();
        var backend = new Backend(calls); var store = new Store(calls);
        using var credential = Credential(); var id = Guid.NewGuid();
        var state = await new HyperVAutoLogonWorkflow(store, backend).PrepareAsync(id, credential, CancellationToken.None);
        Assert.Equal(GuestAutoLogonState.Disabled, state);
        Assert.Equal(PreparationCalls, calls);
        Assert.Equal(id, store.SavedVm);
        Assert.All(backend.Targets, target => Assert.Equal(id, target));
    }

    [Fact]
    public async Task PreparationFailureDoesNotReplaceStoredCredentials()
    {
        var calls = new List<string>();
        var backend = new Backend(calls) { FailPrepare = true }; var store = new Store(calls);
        using var credential = Credential();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new HyperVAutoLogonWorkflow(store, backend)
            .PrepareAsync(Guid.NewGuid(), credential, CancellationToken.None));
        Assert.DoesNotContain("save", calls);
    }

    [Theory]
    [InlineData(true, GuestAutoLogonState.Enabled, "EnableAutoLogon")]
    [InlineData(false, GuestAutoLogonState.Disabled, "DisableAutoLogon")]
    public async Task SwitchUsesExplicitOperationAndConfirmsActualState(bool enabled, GuestAutoLogonState expected, string operation)
    {
        var calls = new List<string>(); var backend = new Backend(calls);
        using var credential = Credential();
        var state = await new HyperVAutoLogonWorkflow(new Store(calls), backend)
            .SetEnabledAsync(Guid.NewGuid(), credential, enabled, CancellationToken.None);
        Assert.Equal(expected, state);
        Assert.Equal(new[] { "ready", operation, "QueryAutoLogon" }, calls);
    }

    [Fact]
    public async Task ReadbackMismatchCannotReportSuccess()
    {
        var calls = new List<string>(); var backend = new Backend(calls) { IgnoreChange = true };
        using var credential = Credential();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new HyperVAutoLogonWorkflow(new Store(calls), backend)
            .SetEnabledAsync(Guid.NewGuid(), credential, true, CancellationToken.None));
    }

    private static GuestCredential Credential()
    { var secret = new SecureString(); foreach (var c in "synthetic-only") secret.AppendChar(c); return new("TestGuest", secret); }

    private sealed class Store(List<string> calls) : IGuestCredentialStore
    {
        public Guid SavedVm { get; private set; }
        public void Save(Guid vmId, string userName, SecureString password) { SavedVm = vmId; calls.Add("save"); }
        public GuestCredential? Load(Guid vmId) => null;
        public void Delete(Guid vmId) => throw new InvalidOperationException("Must not delete existing credentials.");
    }
    private sealed class Backend(List<string> calls) : IHyperVAutoLogonBackend
    {
        public bool FailPrepare { get; init; }
        public bool IgnoreChange { get; init; }
        public string? StateReply { get; init; }
        private bool _enabled;
        public List<Guid> Targets { get; } = [];
        public Task EnsureReadyAsync(Guid id, GuestCredential credential, CancellationToken token)
        { calls.Add("ready"); Targets.Add(id); return Task.CompletedTask; }
        public Task<string> RunAsync(Guid id, GuestCredential credential, GuestAdministrationOperation operation, CancellationToken token)
        {
            calls.Add(operation.ToString()); Targets.Add(id);
            if (operation == GuestAdministrationOperation.PrepareAuthenticatedRdp && FailPrepare) throw new InvalidOperationException("synthetic");
            if (!IgnoreChange && operation == GuestAdministrationOperation.EnableAutoLogon) _enabled = true;
            if (!IgnoreChange && operation == GuestAdministrationOperation.DisableAutoLogon) _enabled = false;
            return Task.FromResult(operation == GuestAdministrationOperation.QueryAutoLogon ? StateReply ?? "GuestAutoLogonState:" + (_enabled ? "Enabled" : "Disabled") : "GuestOperationSucceeded");
        }
    }
}
