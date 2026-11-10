using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;
using FloatDesk.Windows.DesktopSessions;

namespace FloatDesk.App.DesktopSessions;

internal sealed class TaskGuestTransport(EnvironmentProfile environment, IDesktopSessionTransport inner) : IDesktopSessionTransport
{
    public Task<SessionTransportHandshake> ConnectAsync(SessionHandle handle, CancellationToken token) => inner.ConnectAsync(handle, token);
    public Task AcquireInputLeaseAsync(SessionHandle handle, TimeSpan duration, CancellationToken token) => inner.AcquireInputLeaseAsync(handle, duration, token);
    public Task RenewInputLeaseAsync(SessionHandle handle, CancellationToken token) => inner.RenewInputLeaseAsync(handle, token);
    public Task ReleaseInputLeaseAsync(SessionHandle handle, CancellationToken token) => inner.ReleaseInputLeaseAsync(handle, token);
    public Task CancelAsync(SessionHandle handle, Guid commandId, CancellationToken token) => inner.CancelAsync(handle, commandId, token);
    public async Task<CommandResult> SendAsync(SessionHandle handle, SessionCommand command, CancellationToken token)
    {
        if (command.Kind != SessionCommandKind.RunSystemScript) return await inner.SendAsync(handle, command, token).ConfigureAwait(false);
        if (handle != command.Handle || handle.EnvironmentId != environment.Id || environment.Backend != DesktopSessionBackendKind.HyperV ||
            !Guid.TryParse(environment.BackendProfileId, out var vmId)) return new(command.CommandId, false, false, "GuestAdminRequiresBoundHyperV");
        if (!command.Parameters.TryGetValue("scriptContent", out var content) || !command.Parameters.TryGetValue("scriptSha256", out var hash) ||
            DesktopTaskScript.Create("task.ps1", content).Sha256 != hash) return new(command.CommandId, false, false, "ScriptAssetInvalid");
        using var credential = new WindowsGuestCredentialStore().Load(vmId);
        if (credential is null) return new(command.CommandId, false, false, "GuestAdministratorCredentialRequired");
        try
        {
            var arguments = command.Parameters.TryGetValue("arguments", out var encoded)
                ? System.Text.Json.JsonSerializer.Deserialize<string[]>(encoded) ?? [] : [];
            await HyperVGuestAdministration.RunAsync(vmId, credential, GuestAdministrationOperation.RunTaskScript, token, content, arguments).ConfigureAwait(false);
            return new(command.CommandId, true, false, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return new(command.CommandId, false, false, "GuestAdministratorScriptFailed"); }
    }
}
