namespace FloatDesk.Windows.DesktopSessions;

public enum GuestAutoLogonState { Disabled, Enabled, Foreign }

public interface IHyperVAutoLogonBackend
{
    Task EnsureReadyAsync(Guid id, GuestCredential credential, CancellationToken token);
    Task<string> RunAsync(Guid id, GuestCredential credential, GuestAdministrationOperation operation, CancellationToken token);
}

public sealed class HyperVAutoLogonWorkflow(IGuestCredentialStore store, IHyperVAutoLogonBackend? backend = null)
{
    private readonly IHyperVAutoLogonBackend _backend = backend ?? new NativeBackend();

    public async Task<GuestAutoLogonState> PrepareAsync(Guid id, GuestCredential credential, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Validate(id, credential);
        await _backend.EnsureReadyAsync(id, credential, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        await _backend.RunAsync(id, credential, GuestAdministrationOperation.PrepareAuthenticatedRdp, token).ConfigureAwait(false);
        var state = await ReadStateAsync(id, credential, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        store.Save(id, credential.UserName, credential.Password);
        return state;
    }

    public async Task<GuestAutoLogonState> SetEnabledAsync(Guid id, GuestCredential credential, bool enabled, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Validate(id, credential);
        await _backend.EnsureReadyAsync(id, credential, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        await _backend.RunAsync(id, credential, enabled ? GuestAdministrationOperation.EnableAutoLogon : GuestAdministrationOperation.DisableAutoLogon, token).ConfigureAwait(false);
        var state = await ReadStateAsync(id, credential, token).ConfigureAwait(false);
        if (state != (enabled ? GuestAutoLogonState.Enabled : GuestAutoLogonState.Disabled))
            throw new InvalidOperationException("自动登录状态校验未通过，请重新读取状态。");
        return state;
    }

    public async Task<GuestAutoLogonState> ReadStateAsync(Guid id, GuestCredential credential, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Validate(id, credential);
        var result = await _backend.RunAsync(id, credential, GuestAdministrationOperation.QueryAutoLogon, token).ConfigureAwait(false);
        return result switch
        {
            "GuestAutoLogonState:Enabled" => GuestAutoLogonState.Enabled,
            "GuestAutoLogonState:Disabled" => GuestAutoLogonState.Disabled,
            "GuestAutoLogonState:Foreign" => GuestAutoLogonState.Foreign,
            _ => throw new InvalidOperationException("无法确认来宾自动登录状态。")
        };
    }

    private static void Validate(Guid id, GuestCredential credential)
    {
        if (id == Guid.Empty) throw new ArgumentException("VM identity required.", nameof(id));
        ArgumentNullException.ThrowIfNull(credential);
        WindowsGuestCredentialStore.ValidateCredential(credential.UserName, credential.Password);
    }

    private sealed class NativeBackend : IHyperVAutoLogonBackend
    {
        public async Task EnsureReadyAsync(Guid id, GuestCredential credential, CancellationToken token)
        {
            var manager = new HyperVVirtualMachineManager();
            var machine = (await manager.ListAsync(token).ConfigureAwait(false)).SingleOrDefault(vm => vm.Id == id)
                ?? throw new InvalidOperationException("所选虚拟机不存在。");
            var starting = machine.State is HyperVVirtualMachineState.Off or HyperVVirtualMachineState.Saved or HyperVVirtualMachineState.Paused;
            if (machine.State == HyperVVirtualMachineState.Paused)
                await new PowerShellHyperVHost().RunScriptForManagementAsyncCore($"Import-Module Hyper-V -ErrorAction Stop; Resume-VM -VM (Get-VM -Id ([guid]'{id:D}') -ErrorAction Stop) -Confirm:$false -ErrorAction Stop; 'ok'", token).ConfigureAwait(false);
            else if (starting) await manager.StartAsync(id, token).ConfigureAwait(false);
            else if (machine.State != HyperVVirtualMachineState.Running) throw new InvalidOperationException("虚拟机当前状态无法配置登录。");
            var deadline = DateTime.UtcNow.AddMinutes(2);
            while (true)
            {
                try { await RunAsync(id, credential, GuestAdministrationOperation.TestCredential, token).ConfigureAwait(false); return; }
                catch (InvalidOperationException) when (starting && DateTime.UtcNow < deadline)
                { await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false); }
            }
        }
        public Task<string> RunAsync(Guid id, GuestCredential credential, GuestAdministrationOperation operation, CancellationToken token) =>
            HyperVGuestAdministration.RunAsync(id, credential, operation, token);
    }
}
