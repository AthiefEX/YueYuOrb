using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;

namespace FloatDesk.App.DesktopSessions;

public sealed class SessionStartupCoordinator
{
    private readonly IStartupExecutionLedger _ledger;
    private readonly IStartupApplicationRunner _runner;
    private readonly IClock _clock;
    private readonly SessionStartupPolicy _policy;
    private readonly SessionCapabilities _capabilities;

    public SessionStartupCoordinator(
        IStartupExecutionLedger ledger,
        IStartupApplicationRunner runner,
        IClock clock,
        SessionStartupPolicy policy,
        SessionCapabilities capabilities)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _capabilities = capabilities ??
            throw new ArgumentNullException(nameof(capabilities));
        _policy = policy;
    }

    public async Task RunOnceAsync(
        SessionHandle handle,
        IReadOnlyList<SessionStartupEntry> entries,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(entries);
        EnsurePolicySupported();
        var ordered = entries
            .Where(entry => entry is { Enabled: true })
            .OrderBy(entry => entry.Order)
            .ThenBy(entry => entry.Id)
            .ToArray();
        foreach (var entry in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.DelaySeconds > 0)
            {
                await _clock.DelayAsync(
                    TimeSpan.FromSeconds(entry.DelaySeconds),
                    cancellationToken).ConfigureAwait(false);
            }

            var key = new StartupExecutionKey(
                handle.EnvironmentId,
                handle.LoginInstanceId,
                entry.Id,
                entry.Revision);
            if (!await _ledger.TryBeginAsync(
                    key,
                    cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var result = await _runner.RunAsync(
                handle,
                entry,
                cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                throw new SessionStartupException(
                    result.ErrorCode ?? "StartupApplicationFailed",
                    entry,
                    result.Detail);
            }
        }
    }

    private void EnsurePolicySupported()
    {
        if (_policy == SessionStartupPolicy.ManagedOnly)
        {
            throw new SessionStartupException(
                "ManagedOnlyUnsupported",
                entry: null,
                detail: "ManagedOnly startup suppression is unavailable.");
        }

        if (_policy != SessionStartupPolicy.EnvironmentDefault)
        {
            throw new SessionStartupException(
                "StartupPolicyUnsupported",
                entry: null,
                detail: "The requested startup policy is invalid.");
        }

        if (!_capabilities.SupportedStartupSources.Contains(
                "EnvironmentDefault",
                StringComparer.Ordinal))
        {
            throw new SessionStartupException(
                "EnvironmentDefaultUnsupported",
                entry: null,
                detail: "The environment cannot run a managed startup list.");
        }
    }
}

public sealed class SessionStartupException : InvalidOperationException
{
    public SessionStartupException(
        string errorCode,
        SessionStartupEntry? entry,
        string? detail)
        : base(BuildMessage(errorCode, entry, detail))
    {
        ErrorCode = errorCode;
        Entry = entry;
        Detail = detail;
    }

    public string ErrorCode { get; }
    public SessionStartupEntry? Entry { get; }
    public string? Detail { get; }

    private static string BuildMessage(
        string errorCode,
        SessionStartupEntry? entry,
        string? detail)
    {
        var message = entry is null
            ? errorCode
            : $"{errorCode}: {entry.DisplayName} ({entry.ExecutablePath})";
        return string.IsNullOrWhiteSpace(detail)
            ? message
            : $"{message} - {detail}";
    }
}
