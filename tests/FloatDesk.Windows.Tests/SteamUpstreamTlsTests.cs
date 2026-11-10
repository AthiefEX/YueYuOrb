using System.Diagnostics;
using System.Net;
using FloatDesk.Windows.Network;

namespace FloatDesk.Windows.Tests;

public sealed class SteamUpstreamTlsTests
{
    [Fact]
    public void DefaultBudgetMatchesSteamConnectionLimits()
    {
        var limits = SteamUpstreamConnectLimits.Default;
        Assert.Equal(TimeSpan.FromSeconds(24), limits.TotalTimeout);
        Assert.Equal(TimeSpan.FromSeconds(7), limits.AttemptTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(200), limits.StaggerDelay);
        Assert.Equal(6, limits.MaximumCandidates);
        Assert.Equal(4, limits.MaximumConcurrentAttempts);
    }

    [Fact]
    public async Task ResetHandshakeStartsNoSniRetryWithoutWaitingForOtherCandidates()
    {
        var candidates = Candidates(3);
        var attempts = new List<(IPAddress Address, bool NoSni)>();

        var stream = await SteamUpstreamTls.ConnectCandidatesAsync(
            "steamcommunity.com",
            candidates,
            true,
            (candidate, noSni, _) =>
            {
                attempts.Add((candidate.Address, noSni));
                if (noSni) return Task.FromResult<Stream>(new MemoryStream());
                throw new SteamUpstreamAttemptException("tls-handshake", "HandshakeFailure", true);
            },
            null,
            CancellationToken.None,
            FastLimits());

        using (stream)
        {
            Assert.NotNull(stream);
            Assert.InRange(attempts.Count(attempt => attempt.NoSni), 1, 3);
            Assert.Contains(attempts, attempt => attempt.NoSni && attempt.Address.Equals(candidates[0].Address));
            Assert.Contains(attempts, attempt => !attempt.NoSni);
        }
    }

    [Fact]
    public async Task SniBlockedHostEscalatesToNoSniEvenWhenPreferenceIsOff()
    {
        var candidates = Candidates(2);
        var noSniAttempts = 0;

        var stream = await SteamUpstreamTls.ConnectCandidatesAsync(
            "steamcommunity.com",
            candidates,
            false,
            (_, noSni, _) =>
            {
                if (noSni)
                {
                    noSniAttempts++;
                    return Task.FromResult<Stream>(new MemoryStream());
                }
                throw new SteamUpstreamAttemptException("tls-handshake", "ConnectionReset", true);
            },
            null,
            CancellationToken.None,
            FastLimits());

        // Measured on the target network: a blocked ClientHello is answered with a reset in 0.04 s
        // while the no-SNI handshake on the same edge completes in 1.4 s with a valid certificate
        // for the host. Refusing the fallback when the preference is off leaves the site
        // unreachable for the users whose stored preference predates it.
        using (stream) Assert.NotEqual(0, noSniAttempts);
    }

    [Fact]
    public async Task RememberedNoSniHostSkipsTheSniAttempt()
    {
        var attempts = new List<(IPAddress Address, bool NoSni)>();
        bool? handshakeMode = null;

        var stream = await SteamUpstreamTls.ConnectCandidatesAsync(
            "steamcommunity.com",
            Candidates(1),
            false,
            (candidate, noSni, _) =>
            {
                attempts.Add((candidate.Address, noSni));
                return Task.FromResult<Stream>(new MemoryStream());
            },
            null,
            CancellationToken.None,
            FastLimits(),
            null,
            preferNoSni: true,
            onHandshake: mode => handshakeMode = mode);

        // A host whose last verified connection only completed without SNI is asked that way
        // first: every extra connection a page opens would otherwise pay the reset again.
        using (stream)
        {
            Assert.True(Assert.Single(attempts).NoSni);
            Assert.True(handshakeMode);
        }
    }

    [Fact]
    public async Task RememberedNoSniHostFallsBackToSniWhenThatHandshakeFails()
    {
        var attempts = new List<(IPAddress Address, bool NoSni)>();
        bool? handshakeMode = null;

        var stream = await SteamUpstreamTls.ConnectCandidatesAsync(
            "steamcommunity.com",
            Candidates(1),
            false,
            (candidate, noSni, _) =>
            {
                attempts.Add((candidate.Address, noSni));
                return noSni
                    ? Task.FromException<Stream>(new SteamUpstreamAttemptException("tls-handshake", "HandshakeFailure", true))
                    : Task.FromResult<Stream>(new MemoryStream());
            },
            null,
            CancellationToken.None,
            FastLimits(),
            null,
            preferNoSni: true,
            onHandshake: mode => handshakeMode = mode);

        // The remembered mode is a hint, not a rule: when the edge stops accepting it the original
        // domain handshake is tried before the page is reported as failed.
        using (stream)
        {
            Assert.Contains(attempts, attempt => !attempt.NoSni);
            Assert.False(handshakeMode);
        }
    }

    [Fact]
    public async Task CertificateRejectionNeverEnablesNoSniFallback()
    {
        var noSniAttempts = 0;

        var error = await Assert.ThrowsAsync<SteamUpstreamConnectException>(() => SteamUpstreamTls.ConnectCandidatesAsync(
            "steamcommunity.com",
            Candidates(1),
            true,
            (_, noSni, _) =>
            {
                if (noSni) noSniAttempts++;
                throw new SteamUpstreamAttemptException("tls-certificate", "CertificateRejected", false);
            },
            null,
            CancellationToken.None,
            FastLimits()));

        Assert.Equal("CertificateRejected", error.ErrorCode);
        Assert.Equal(0, noSniAttempts);
    }

    [Fact]
    public async Task CandidateRaceStaggersSecondAttemptAndReleasesLoserAfterWinner()
    {
        var candidates = Candidates(2);
        var started = new List<long>();
        var timer = Stopwatch.StartNew();
        var active = 0;
        var maximumActive = 0;
        var loserCancelled = false;

        var stream = await SteamUpstreamTls.ConnectCandidatesAsync(
            "steamcommunity.com",
            candidates,
            false,
            async (candidate, _, token) =>
            {
                lock (started) started.Add(timer.ElapsedMilliseconds);
                var current = Interlocked.Increment(ref active);
                int observed;
                do { observed = Volatile.Read(ref maximumActive); }
                while (current > observed && Interlocked.CompareExchange(ref maximumActive, current, observed) != observed);
                try
                {
                    if (candidate.Address.Equals(candidates[0].Address))
                    {
                        await Task.Delay(500, token);
                        return new MemoryStream();
                    }
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    throw new InvalidOperationException("The losing attempt should be cancelled.");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    loserCancelled = true;
                    throw;
                }
                finally { Interlocked.Decrement(ref active); }
            },
            null,
            CancellationToken.None,
            FastLimits() with { StaggerDelay = TimeSpan.FromMilliseconds(250), AttemptTimeout = TimeSpan.FromSeconds(2) });

        using (stream)
        {
            Assert.Equal(2, maximumActive);
            Assert.True(started[1] - started[0] >= 200);
            Assert.True(loserCancelled);
            Assert.Equal(0, active);
        }
    }

    [Fact]
    public async Task ParentCancellationCancelsAndAwaitsCandidateAttempts()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var parent = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connecting = SteamUpstreamTls.ConnectCandidatesAsync(
            "steamcommunity.com",
            Candidates(2),
            false,
            async (_, _, token) =>
            {
                entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    throw new InvalidOperationException("The cancelled candidate must not complete normally.");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    released.TrySetResult();
                    throw;
                }
            },
            null,
            parent.Token,
            FastLimits() with { StaggerDelay = TimeSpan.Zero });

        await entered.Task.WaitAsync(parent.Token);
        parent.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await connecting.WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.True(released.Task.IsCompleted);
    }

    [Fact]
    public async Task StandardRetriesStayBoundedAndNoSniTriesTwoEligibleAddresses()
    {
        var attempts = new List<(IPAddress Address, bool NoSni)>();
        await Assert.ThrowsAsync<SteamUpstreamConnectException>(() => SteamUpstreamTls.ConnectCandidatesAsync(
            "steamcommunity.com",
            Candidates(7),
            true,
            (candidate, noSni, _) =>
            {
                attempts.Add((candidate.Address, noSni));
                throw new SteamUpstreamAttemptException("tls-handshake", "HandshakeFailure", true);
            },
            null,
            CancellationToken.None,
            FastLimits()));

        // Six candidates are tried, each gets two extra attempts after a handshake reset, and two
        // eligible edges get an in-phase no-SNI retry with three attempts each: 18 + 6.
        Assert.Equal(24, attempts.Count);
        Assert.Equal(18, attempts.Count(attempt => !attempt.NoSni));
        Assert.Equal(6, attempts.Count(attempt => attempt.NoSni));
    }

    [Fact]
    public async Task AttemptDiagnosticsContainControlledFieldsOnly()
    {
        var diagnostics = new List<SteamNetworkDiagnostic>();
        await Assert.ThrowsAsync<SteamUpstreamConnectException>(() => SteamUpstreamTls.ConnectCandidatesAsync(
            "steamcommunity.com",
            Candidates(1),
            false,
            (_, _, _) => throw new IOException("https://steamcommunity.com/?password=not-for-logs"),
            diagnostics.Add,
            CancellationToken.None,
            FastLimits()));

        // A handshake failure is retried, so more than one attempt is recorded; every one of them
        // must stay inside the controlled field set and must never carry the raw text of the
        // underlying error.
        var serialized = System.Text.Json.JsonSerializer.Serialize(diagnostics);
        Assert.DoesNotContain("password", serialized);
        Assert.DoesNotContain("steamcommunity.com/?", serialized);
        Assert.All(diagnostics, item => Assert.Equal("steamcommunity.com", item.Host));
        var diagnostic = diagnostics[0];
        Assert.Equal("https-dns", diagnostic.Source);
        Assert.Equal("HandshakeFailure", diagnostic.ErrorCode);
    }

    [Fact]
    public async Task DeadAddressesAreNotRetriedAndReportNoReachableAddress()
    {
        var attempts = new List<(IPAddress Address, bool NoSni)>();
        var error = await Assert.ThrowsAsync<SteamUpstreamConnectException>(() => SteamUpstreamTls.ConnectCandidatesAsync(
            "steamcommunity.com",
            Candidates(3),
            true,
            (candidate, noSni, _) =>
            {
                attempts.Add((candidate.Address, noSni));
                throw new SteamUpstreamAttemptException("tcp-connect", "Timeout", false);
            },
            null,
            CancellationToken.None,
            FastLimits()));

        // A blackholed answer answers the same way every time, so retrying it only burns the
        // shared budget: one attempt per address, no no-SNI amplification.
        Assert.Equal("NoReachableAddress", error.ErrorCode);
        Assert.Equal(3, attempts.Count);
        Assert.DoesNotContain(attempts, attempt => attempt.NoSni);
    }

    private static SteamResolvedAddress[] Candidates(int count) => Enumerable.Range(1, count)
        .Select(index => new SteamResolvedAddress(IPAddress.Parse($"8.8.8.{index}"), SteamDnsSource.HttpsDns))
        .ToArray();

    private static SteamUpstreamConnectLimits FastLimits() => new(
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(2),
        TimeSpan.Zero,
        6,
        2);
}
