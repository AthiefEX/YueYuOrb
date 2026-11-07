using FloatDesk.Core.Models.DesktopAutomation;
using System.Diagnostics;

namespace FloatDesk.SessionAgent;

public interface IGuestOperationExecutor
{
    Task<GuestOperationResult> StartApplicationAsync(string path, CancellationToken cancellationToken);
    Task<GuestOperationResult> StopApplicationAsync(string path, CancellationToken cancellationToken);
    Task<GuestOperationResult> SetEnvironmentVariableAsync(string name, string value, CancellationToken cancellationToken);
}

public sealed record GuestOperationResult(bool Succeeded, string? ErrorCode = null);

public sealed class GuestOperationDispatcher
{
    private readonly IGuestOperationExecutor _executor;

    public GuestOperationDispatcher(IGuestOperationExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public Task<GuestOperationResult> DispatchAsync(
        GuestOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.Parameters is null)
            return Task.FromResult(new GuestOperationResult(false, "OperationParametersMissing"));
        return operation.Kind switch
        {
            GuestOperationKind.StartApplication => DispatchPathAsync(operation, true, cancellationToken),
            GuestOperationKind.StopApplication => DispatchPathAsync(operation, false, cancellationToken),
            GuestOperationKind.SetEnvironmentVariable => DispatchEnvironmentAsync(operation, cancellationToken),
            _ => Task.FromResult(new GuestOperationResult(false, "GuestOperationNotSupported"))
        };
    }

    private Task<GuestOperationResult> DispatchPathAsync(
        GuestOperation operation,
        bool start,
        CancellationToken cancellationToken)
    {
        if (!operation.Parameters.TryGetValue("path", out var path) ||
            !Path.IsPathFullyQualified(path) ||
            path.Contains('"') || path.Contains('\n') || path.Contains('\r'))
            return Task.FromResult(new GuestOperationResult(false, "GuestApplicationPathInvalid"));
        return start
            ? _executor.StartApplicationAsync(path, cancellationToken)
            : _executor.StopApplicationAsync(path, cancellationToken);
    }

    private Task<GuestOperationResult> DispatchEnvironmentAsync(
        GuestOperation operation,
        CancellationToken cancellationToken)
    {
        if (!operation.Parameters.TryGetValue("name", out var name) ||
            !operation.Parameters.TryGetValue("value", out var value) ||
            string.IsNullOrWhiteSpace(name) ||
            name.Contains('=') || name.Contains('\0'))
            return Task.FromResult(new GuestOperationResult(false, "EnvironmentVariableInvalid"));
        return _executor.SetEnvironmentVariableAsync(name, value, cancellationToken);
    }
}

public sealed class WindowsGuestOperationExecutor : IGuestOperationExecutor
{
    public Task<GuestOperationResult> StartApplicationAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return Task.FromResult(process is null
                ? new GuestOperationResult(false, "GuestApplicationStartFailed")
                : new GuestOperationResult(true));
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Task.FromResult(new GuestOperationResult(false, "GuestApplicationStartFailed"));
        }
    }

    public Task<GuestOperationResult> StopApplicationAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path);
        var processes = Process.GetProcesses();
        foreach (var process in processes)
        {
            try
            {
                if (string.Equals(process.MainModule?.FileName, fullPath, StringComparison.OrdinalIgnoreCase))
                    process.CloseMainWindow();
            }
            catch (Exception) { }
            finally { process.Dispose(); }
        }
        return Task.FromResult(new GuestOperationResult(true));
    }

    public Task<GuestOperationResult> SetEnvironmentVariableAsync(string name, string value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);
        return Task.FromResult(new GuestOperationResult(true));
    }
}
