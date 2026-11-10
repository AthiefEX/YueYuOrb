using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Diagnostics;

namespace FloatDesk.Windows.Network;

internal sealed record SteamUpstreamConnectLimits(
    TimeSpan TotalTimeout,
    TimeSpan AttemptTimeout,
    TimeSpan StaggerDelay,
    int MaximumCandidates,
    int MaximumConcurrentAttempts)
{
    internal static SteamUpstreamConnectLimits Default { get; } = new(
        TimeSpan.FromSeconds(24),
        // Measured on the target network: a TCP connect to a usable edge takes 0.3 s - 1.3 s but
        // the same SYN is dropped for whole seconds during a loss burst, and a no-SNI handshake on
        // that edge takes another 1.4 s - 4.1 s, while a blocked ClientHello is answered with a
        // reset in ~0.04 s. The attempt window therefore has to fit connect plus handshake once the
        // SYN finally lands; the reset path still retries several times inside it.
        TimeSpan.FromSeconds(7),
        TimeSpan.FromMilliseconds(200),
        6,
        // Filtered answers put several blackholed addresses in the same result set as the real
        // edge. Two at a time meant 18 s of six-second timeouts before the usable address was even
        // reached (measured: a 17 s store page); four at a time keeps that walk inside one window.
        4);
}

internal sealed class SteamUpstreamAttemptException : IOException
{
    internal string Stage { get; }
    internal string ErrorCode { get; }
    internal bool AllowsNoSniRetry { get; }

    internal SteamUpstreamAttemptException(string stage, string errorCode, bool allowsNoSniRetry, Exception? inner = null)
        : base("Steam 上游尝试失败。", inner)
    {
        Stage = stage;
        ErrorCode = errorCode;
        AllowsNoSniRetry = allowsNoSniRetry;
    }
}

internal sealed class SteamUpstreamConnectException : IOException
{
    internal string ErrorCode { get; }

    internal SteamUpstreamConnectException(string errorCode)
        : base("Steam 上游连接失败（" + errorCode + "）。") => ErrorCode = errorCode;
}

public static class SteamUpstreamTls
{
    public static bool ValidateCertificate(string originalHost, X509Certificate? certificate, SslPolicyErrors errors)
        => ValidateCertificate(originalHost, certificate, errors, null);

    internal static bool ValidateCertificate(string originalHost, X509Certificate? certificate, SslPolicyErrors errors, X509ChainPolicy? chainPolicy)
    {
        var allowedErrors = SslPolicyErrors.RemoteCertificateNameMismatch |
            (chainPolicy is null ? SslPolicyErrors.None : SslPolicyErrors.RemoteCertificateChainErrors);
        if (certificate is null || (errors & ~allowedErrors) != SslPolicyErrors.None) return false;
        try
        {
            using var copy = new X509Certificate2(certificate);
            var trusted = chainPolicy is null
                ? (errors & SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None
                : BuildsChain(copy, chainPolicy);
            return trusted && copy.NotBefore.ToUniversalTime() <= DateTime.UtcNow && copy.NotAfter.ToUniversalTime() > DateTime.UtcNow && copy.MatchesHostname(originalHost);
        }
        catch (System.Security.Cryptography.CryptographicException) { return false; }
        catch (ArgumentException) { return false; }
    }

    private static bool BuildsChain(X509Certificate2 certificate, X509ChainPolicy policy)
    {
        using var chain = new X509Chain { ChainPolicy = policy };
        return chain.Build(certificate);
    }

    public static Task<Stream> ConnectAsync(string host, SteamNetworkResolver resolver, CancellationToken token) =>
        ConnectAsync(host, resolver, false, null, token);

    internal static async Task<Stream> ConnectAsync(
        string host,
        SteamNetworkResolver resolver,
        bool allowNoSni,
        Action<SteamNetworkDiagnostic>? diagnostic,
        CancellationToken token,
        SteamUpstreamConnectLimits? limits = null,
        bool preferNoSni = false,
        Action<bool>? onHandshake = null)
    {
        var selectedLimits = limits ?? SteamUpstreamConnectLimits.Default;
        var timer = Stopwatch.StartNew();
        using var total = CancellationTokenSource.CreateLinkedTokenSource(token);
        total.CancelAfter(selectedLimits.TotalTimeout);
        for (var round = 0; ; round++)
        {
            SteamResolvedAddress[] candidates;
            try
            {
                candidates = await resolver.ResolveCandidatesAsync(host, true, total.Token, diagnostic).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                Notify(diagnostic, SteamNetworkDiagnostic.Create(host, null, "none", "budget", timer.ElapsedMilliseconds, "TotalTimeout", false));
                throw new SteamUpstreamConnectException("TotalTimeout");
            }

            if (candidates.Length == 0)
                throw new SteamUpstreamConnectException("NoPublicAddress");

            var remaining = selectedLimits.TotalTimeout - timer.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                Notify(diagnostic, SteamNetworkDiagnostic.Create(host, null, "none", "budget", timer.ElapsedMilliseconds, "TotalTimeout", false));
                throw new SteamUpstreamConnectException("TotalTimeout");
            }

            var attemptLimits = selectedLimits with { TotalTimeout = remaining };
            try
            {
                return await ConnectCandidatesAsync(host, candidates, allowNoSni, (candidate, noSni, attemptToken) => OpenCandidateAsync(host, candidate, noSni, attemptToken), diagnostic, total.Token, attemptLimits, connected => resolver.Prefer(host, connected.Address), preferNoSni, onHandshake).ConfigureAwait(false);
            }
            catch (SteamUpstreamConnectException) when (round < 4 && selectedLimits.TotalTimeout - timer.Elapsed > TimeSpan.FromSeconds(6))
            {
                // A filtered network hands out a mixture of genuine and blackholed answers for the
                // same name, and which one arrives changes between queries. Retrying with a fresh
                // query is therefore worth one attempt before this page load is reported as failed.
                resolver.Invalidate(host);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                Notify(diagnostic, SteamNetworkDiagnostic.Create(host, null, "none", "budget", timer.ElapsedMilliseconds, "TotalTimeout", false));
                throw new SteamUpstreamConnectException("TotalTimeout");
            }
        }
    }

    internal static async Task<Stream> ConnectCandidatesAsync(
        string host,
        IReadOnlyList<SteamResolvedAddress> candidates,
        bool allowNoSni,
        Func<SteamResolvedAddress, bool, CancellationToken, Task<Stream>> connectCandidate,
        Action<SteamNetworkDiagnostic>? diagnostic,
        CancellationToken token,
        SteamUpstreamConnectLimits? limits = null,
        Action<SteamResolvedAddress>? onConnected = null,
        bool preferNoSni = false,
        Action<bool>? onHandshake = null)
    {
        var selectedLimits = limits ?? SteamUpstreamConnectLimits.Default;
        var timer = Stopwatch.StartNew();
        using var total = CancellationTokenSource.CreateLinkedTokenSource(token);
        total.CancelAfter(selectedLimits.TotalTimeout);
        var selected = candidates.Take(selectedLimits.MaximumCandidates).ToArray();
        if (selected.Length == 0) throw new SteamUpstreamConnectException("NoPublicAddress");

        try
        {
            if (preferNoSni)
            {
                // The previous connection to this host only completed without SNI, which means the
                // path blocks the domain handshake rather than the edge. Starting there saves the
                // reset plus retry on every connection a page opens; SNI is still tried when the
                // remembered mode stops working, and the certificate is verified either way.
                var remembered = await RunPhaseAsync(host, selected, true, false, ResetRetries, connectCandidate, diagnostic, selectedLimits, total.Token).ConfigureAwait(false);
                if (remembered.Stream is not null)
                {
                    onConnected?.Invoke(remembered.Connected!.Candidate);
                    onHandshake?.Invoke(true);
                    return remembered.Stream;
                }
            }

            // A reset that arrives before any certificate is proof of a path that blocks the
            // handshake for this host name, and on this network the no-SNI handshake on that same
            // edge then completes with a valid certificate for the host (measured: reset in 0.04 s,
            // no-SNI handshake in 1.4 s, CN=steamcommunity.com). It therefore starts beside the
            // standard retries instead of waiting for the poisoned addresses to time out.
            var standard = await RunPhaseAsync(host, selected, false, true, StandardResetRetries, connectCandidate, diagnostic, selectedLimits, total.Token).ConfigureAwait(false);
            if (standard.Stream is not null)
            {
                onConnected?.Invoke(standard.Connected!.Candidate);
                onHandshake?.Invoke(false);
                return standard.Stream;
            }

            var resetEdges = standard.Failures
                .Where(failure => failure.Stage == "tls-handshake" && failure.AllowsNoSniRetry)
                .ToArray();

            // With the preference on, an edge that failed the handshake for any reason gets one
            // no-SNI try as well; with it off, only a proven reset (no certificate ever offered)
            // unlocks the fallback. Both modes still verify chain and original host name in full.
            var escalation = allowNoSni
                ? standard.Failures.Where(failure => failure.AllowsNoSniRetry).ToArray()
                : resetEdges;

            if (standard.FallbacksStarted == 0 && escalation.Length > 0)
            {
                var fallback = escalation
                    .Select(failure => failure.Candidate)
                    .DistinctBy(candidate => candidate.Address)
                    .Take(3)
                    .ToArray();
                var compatibility = await RunPhaseAsync(host, fallback, true, false, ResetRetries, connectCandidate, diagnostic, selectedLimits, total.Token).ConfigureAwait(false);
                if (compatibility.Stream is not null)
                {
                    onConnected?.Invoke(compatibility.Connected!.Candidate);
                    onHandshake?.Invoke(true);
                    return compatibility.Stream;
                }
                standard.Failures.AddRange(compatibility.Failures);
            }

            var finalCode = standard.Failures.LastOrDefault()?.ErrorCode ?? "ConnectFailed";
            if (resetEdges.Length > 0) finalCode = "SniBlocked";
            else if (standard.Failures.Count > 0 && standard.Failures.All(failure =>
                    failure.Stage == "tcp-connect" && failure.ErrorCode == "Timeout"))
            {
                // Every resolved address ignored the TCP handshake for the whole connect window.
                // The answers are routable-looking but blackholed, which is what filtered DNS
                // returns for a blocked host name; say so instead of a bare timeout.
                finalCode = "NoReachableAddress";
            }
            throw new SteamUpstreamConnectException(finalCode);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            Notify(diagnostic, SteamNetworkDiagnostic.Create(host, null, "none", "budget", timer.ElapsedMilliseconds, "TotalTimeout", false));
            throw new SteamUpstreamConnectException("TotalTimeout");
        }
    }

    private const int ResetRetries = 2;

    /// <summary>
    /// The reset that answers a ClientHello is decided per attempt, not per address: the same
    /// edge accepted the same ClientHello milliseconds later in measured runs, and every observed
    /// success of the standard phase needed a second attempt. A reset arrives in ~0.04 s, so the
    /// extra tries are cheap and run beside the no-SNI attempt that the same reset just unlocked.
    /// </summary>
    private const int StandardResetRetries = 2;

    private sealed record AttemptOutcome(
        SteamResolvedAddress Candidate,
        bool NoSni,
        Stream? Stream,
        string Stage,
        string ErrorCode,
        long ElapsedMilliseconds,
        bool AllowsNoSniRetry,
        bool CertificateValidated);

    private static async Task<(Stream? Stream, List<AttemptOutcome> Failures, int FallbacksStarted, AttemptOutcome? Connected)> RunPhaseAsync(
        string host,
        SteamResolvedAddress[] candidates,
        bool noSni,
        bool allowFallback,
        int resetRetries,
        Func<SteamResolvedAddress, bool, CancellationToken, Task<Stream>> connectCandidate,
        Action<SteamNetworkDiagnostic>? diagnostic,
        SteamUpstreamConnectLimits limits,
        CancellationToken token)
    {
        var failures = new List<AttemptOutcome>();
        var active = new List<(Task<AttemptOutcome> Task, CancellationTokenSource Cancellation)>();
        var timer = Stopwatch.StartNew();
        var lastStarted = TimeSpan.Zero;
        var next = 0;
        var fallbacksStarted = 0;
        var fallbackCandidates = new HashSet<IPAddress>();

        try
        {
            while (next < candidates.Length || active.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                while (next < candidates.Length && active.Count < limits.MaximumConcurrentAttempts)
                {
                    if (active.Count > 0)
                    {
                        var stagger = limits.StaggerDelay - (timer.Elapsed - lastStarted);
                        if (stagger > TimeSpan.Zero) break;
                    }

                    var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var task = RunAttemptWithRetryAsync(candidates[next++], noSni, connectCandidate, limits.AttemptTimeout, resetRetries, token, cancellation.Token);
                    active.Add((task, cancellation));
                    lastStarted = timer.Elapsed;
                }

                if (active.Count == 0) continue;

                Task completed;
                var delay = limits.StaggerDelay - (timer.Elapsed - lastStarted);
                if (next < candidates.Length && active.Count < limits.MaximumConcurrentAttempts && delay > TimeSpan.Zero)
                {
                    var staggerTask = Task.Delay(delay, token);
                    completed = await Task.WhenAny(active.Select(item => (Task)item.Task).Append(staggerTask)).ConfigureAwait(false);
                    if (ReferenceEquals(completed, staggerTask))
                    {
                        token.ThrowIfCancellationRequested();
                        continue;
                    }
                }
                else
                {
                    completed = await Task.WhenAny(active.Select(item => (Task)item.Task)).ConfigureAwait(false);
                }

                token.ThrowIfCancellationRequested();
                var index = active.FindIndex(item => ReferenceEquals(item.Task, completed));
                var finished = active[index];
                active.RemoveAt(index);
                finished.Cancellation.Dispose();
                var outcome = await finished.Task.ConfigureAwait(false);

                if (outcome.Stream is not null)
                {
                    Notify(diagnostic, SteamNetworkDiagnostic.Create(host, outcome.Candidate, noSni ? "none" : "domain", outcome.Stage, outcome.ElapsedMilliseconds, "None", true));
                    return (outcome.Stream, failures, fallbacksStarted, outcome);
                }

                if (outcome.ErrorCode != "Cancelled")
                {
                    failures.Add(outcome);
                    Notify(diagnostic, SteamNetworkDiagnostic.Create(host, outcome.Candidate, noSni ? "none" : "domain", outcome.Stage, outcome.ElapsedMilliseconds, outcome.ErrorCode, outcome.CertificateValidated));
                }

                // A reset before any certificate was offered is the signal this path blocks the
                // handshake for the host name. Retry the same edge without SNI right away instead
                // of waiting for every remaining edge to time out first.
                if (allowFallback && !noSni && fallbacksStarted < 2 && outcome.Stage == "tls-handshake" &&
                    outcome.AllowsNoSniRetry && fallbackCandidates.Add(outcome.Candidate.Address))
                {
                    fallbacksStarted++;
                    var fallbackCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var fallbackTask = RunAttemptWithRetryAsync(outcome.Candidate, true, connectCandidate, limits.AttemptTimeout, ResetRetries, token, fallbackCancellation.Token);
                    active.Add((fallbackTask, fallbackCancellation));
                }
            }

            return (null, failures, fallbacksStarted, null);
        }
        finally
        {
            foreach (var item in active) item.Cancellation.Cancel();
            foreach (var item in active)
            {
                try { (await item.Task.ConfigureAwait(false)).Stream?.Dispose(); }
                catch (Exception error) when (error is IOException or SocketException or AuthenticationException or OperationCanceledException or ObjectDisposedException) { }
                item.Cancellation.Dispose();
            }
        }
    }

    /// <summary>
    /// A reset during the handshake is transient on the target networks (measured: the same edge
    /// accepted the same ClientHello on a later try), so a bounded retry is used instead of
    /// reporting one unlucky reset as a permanent failure.
    /// </summary>
    private static async Task<AttemptOutcome> RunAttemptWithRetryAsync(
        SteamResolvedAddress candidate,
        bool noSni,
        Func<SteamResolvedAddress, bool, CancellationToken, Task<Stream>> connectCandidate,
        TimeSpan timeout,
        int retries,
        CancellationToken phaseToken,
        CancellationToken attemptToken)
    {
        var outcome = await RunAttemptAsync(candidate, noSni, connectCandidate, timeout, phaseToken, attemptToken).ConfigureAwait(false);
        for (var attempt = 0; attempt < retries; attempt++)
        {
            // A handshake timeout is retried: the edge accepted this ClientHello moments ago, so a
            // slow handshake is transient. A TCP connect timeout is not: a contended or blackholed
            // address answers the same way every time, and retrying it would burn the shared budget
            // that the remaining candidates need (measured: three dead answers cost 18 s instead of 6 s).
            if (outcome.Stream is not null) return outcome;
            var transient = outcome.ErrorCode is "ConnectionReset" or "HandshakeFailure"
                || (outcome.ErrorCode == "Timeout" && outcome.Stage != "tcp-connect");
            if (!transient) return outcome;
            if (phaseToken.IsCancellationRequested || attemptToken.IsCancellationRequested) return outcome;
            outcome = await RunAttemptAsync(candidate, noSni, connectCandidate, timeout, phaseToken, attemptToken).ConfigureAwait(false);
        }
        return outcome;
    }

    private static async Task<AttemptOutcome> RunAttemptAsync(
        SteamResolvedAddress candidate,
        bool noSni,
        Func<SteamResolvedAddress, bool, CancellationToken, Task<Stream>> connectCandidate,
        TimeSpan timeout,
        CancellationToken phaseToken,
        CancellationToken attemptToken)
    {
        var timer = Stopwatch.StartNew();
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(attemptToken);
        attempt.CancelAfter(timeout);
        try
        {
            var stream = await connectCandidate(candidate, noSni, attempt.Token).ConfigureAwait(false);
            return new(candidate, noSni, stream, "tls-handshake", "None", timer.ElapsedMilliseconds, false, true);
        }
        catch (SteamUpstreamAttemptException error)
        {
            return new(candidate, noSni, null, error.Stage, error.ErrorCode, timer.ElapsedMilliseconds, error.AllowsNoSniRetry, false);
        }
        catch (OperationCanceledException) when (phaseToken.IsCancellationRequested)
        {
            return new(candidate, noSni, null, "connect-or-handshake", "Cancelled", timer.ElapsedMilliseconds, false, false);
        }
        catch (OperationCanceledException)
        {
            return new(candidate, noSni, null, "connect-or-handshake", "Timeout", timer.ElapsedMilliseconds, true, false);
        }
        catch (SocketException error)
        {
            var reset = error.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted;
            return new(candidate, noSni, null, "tcp-connect", reset ? "ConnectionReset" : "ConnectFailed", timer.ElapsedMilliseconds, reset, false);
        }
        catch (AuthenticationException)
        {
            return new(candidate, noSni, null, "tls-handshake", "HandshakeFailure", timer.ElapsedMilliseconds, true, false);
        }
        catch (IOException error)
        {
            var reset = HasReset(error);
            return new(candidate, noSni, null, "tls-handshake", reset ? "ConnectionReset" : "HandshakeFailure", timer.ElapsedMilliseconds, true, false);
        }
        catch (ObjectDisposedException)
        {
            return new(candidate, noSni, null, "connect-or-handshake", "Cancelled", timer.ElapsedMilliseconds, false, false);
        }
    }

    private static bool HasReset(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is SocketException socket && socket.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted) return true;
        return false;
    }

    private static void Notify(Action<SteamNetworkDiagnostic>? diagnostic, SteamNetworkDiagnostic value)
    {
        try { diagnostic?.Invoke(value); }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException) { }
    }

    private static Task<Stream> OpenCandidateAsync(string host, SteamResolvedAddress candidate, bool noSni, CancellationToken token) =>
        OpenCandidateAsync(host, candidate, noSni, 443, null, token);

    internal static async Task<Stream> OpenCandidateAsync(
        string host,
        SteamResolvedAddress candidate,
        bool noSni,
        int port,
        X509ChainPolicy? chainPolicy,
        CancellationToken token,
        TimeSpan? connectTimeout = null)
    {
        var socket = new Socket(candidate.Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        SslStream? tls = null;
        var stage = "tcp-connect";
        var certificateRejected = false;
        try
        {
            // A dead edge must fail quickly so the remaining candidates still fit the total budget:
            // a usable edge answers the SYN in 0.3 s - 1.3 s, so four seconds is enough to tell a
            // lost SYN from a blackholed answer without paying the old six second wait.
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                connect.CancelAfter(connectTimeout ?? TimeSpan.FromSeconds(4));
                await socket.ConnectAsync(candidate.Address, port, connect.Token).ConfigureAwait(false);
            }
            stage = "tls-handshake";
            tls = new SslStream(new NetworkStream(socket, true), false, (_, certificate, _, errors) =>
            {
                var valid = ValidateCertificate(host, certificate, errors, chainPolicy);
                if (!valid) certificateRejected = true;
                return valid;
            });
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = noSni ? candidate.Address.ToString() : host,
                ApplicationProtocols = [SslApplicationProtocol.Http11]
            }, token).ConfigureAwait(false);
            return tls;
        }
        catch (Exception error) when (error is IOException or SocketException or AuthenticationException or OperationCanceledException or ObjectDisposedException)
        {
            tls?.Dispose();
            socket.Dispose();
            if (token.IsCancellationRequested) throw;
            if (certificateRejected) throw new SteamUpstreamAttemptException("tls-certificate", "CertificateRejected", false, error);
            if (error is OperationCanceledException && stage == "tcp-connect")
                // No no-SNI fallback here: changing the ClientHello cannot make an unroutable
                // address routable, it would only multiply attempts against a dead answer.
                throw new SteamUpstreamAttemptException("tcp-connect", "Timeout", false, error);
            if (error is OperationCanceledException) throw;
            if (error is SocketException socketError && socketError.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted)
                throw new SteamUpstreamAttemptException(stage, "ConnectionReset", true, error);
            if (error is IOException io && HasReset(io))
                throw new SteamUpstreamAttemptException(stage, "ConnectionReset", true, error);
            if (stage == "tls-handshake" && error is AuthenticationException)
                throw new SteamUpstreamAttemptException(stage, "HandshakeFailure", true, error);
            if (error is IOException && stage == "tls-handshake")
                throw new SteamUpstreamAttemptException(stage, "HandshakeFailure", true, error);
            throw new SteamUpstreamAttemptException(stage, "ConnectFailed", false, error);
        }
    }
}
