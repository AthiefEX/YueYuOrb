namespace FloatDesk.Windows.DesktopSessions;

public sealed class UnavailableHyperVGuestAgentProbe : IHyperVGuestAgentProbe
{
    public Task<HyperVGuestIdentity> ProbeAsync(Guid expectedVmId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("HyperVGuestAgentNotPaired");
    }
}
