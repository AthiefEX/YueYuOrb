using FloatDesk.Core.Models;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace FloatDesk.Windows.Network;

public sealed class SteamNetworkController : IDisposable
{
    private static readonly string[] AllowedCodes =
    [
        "None", "NoPublicAddress", "DnsNoAnswer", "DnsUnreachable", "TotalTimeout", "CertificateRejected",
        "ConnectionReset", "ConnectFailed", "HandshakeFailure", "SniBlocked", "NoReachableAddress", "Timeout", "Cancelled",
        "InvalidSettings", "AuthorizationFailed", "CertificateSetupFailed", "ListenerFailed", "StartupFailed",
        "ClientHandshakeFailed", "TransferFailed", "UnknownFailure"
    ];

    private static readonly string[] AllowedStages =
    [
        "dns", "budget", "tcp-connect", "tls-handshake", "tls-certificate", "connect-or-handshake",
        "downstream-tls", "startup", "transfer", "none", "unknown"
    ];

    private static readonly string[] AllowedProviders = ["DNSPod", "360", "Cloudflare", "Google", "system", "custom", "none"];

    private readonly SteamNetworkService _service = new();
    private readonly SteamProxyLease _lease;
    private readonly string _dataDirectory;
    private readonly string _trustJournal;
    private readonly string _lastFailurePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _failureGate = new();
    private readonly Dictionary<string, SteamNetworkFailure> _failures = new(StringComparer.Ordinal);
    private SteamCertificateAuthority? _certificates;
    private SteamTlsBridge? _bridge;
    private SteamBackgroundClient? _backgroundLease;
    private int _generation;
    private string _status = "已停止，尚未进行连通检测。";
    public SteamNetworkSettings? ActiveSettings { get; private set; }
    public bool IsRunning => _service.IsRunning || _bridge is not null;
    public bool IsBusy => _gate.CurrentCount == 0;
    public Uri? PacUri => _service.IsRunning ? _service.PacUri : null;

    /// <summary>The most recent unresolved failure across all site groups.</summary>
    public SteamNetworkFailure? LastFailure
    {
        get
        {
            lock (_failureGate)
                return _failures.Count == 0 ? null : _failures.Values.MaxBy(failure => failure.AtUtc);
        }
    }

    public string Status
    {
        get
        {
            var status = _status + (_bridge is null ? "" : Environment.NewLine + _bridge.Counters);
            if (_backgroundLease is not null && !_backgroundLease.LeaseAlive) status += Environment.NewLine + "网络后台连接已断开，请停止后重新开启。";
            var failures = Failures();
            if (failures.Count == 0) return status;
            return status + Environment.NewLine + string.Join(Environment.NewLine, failures.Select(FormatFailure));
        }
    }

    public event Action? Changed;
    public event Action<SteamNetworkDiagnostic>? Diagnostic;

    public SteamNetworkController(string dataDirectory)
    {
        _dataDirectory = dataDirectory;
        _lease = new(Path.Combine(dataDirectory, "steam-network-proxy.json"), new WinInetProxyConfiguration());
        _trustJournal = Path.Combine(dataDirectory, "steam-network-trust.json");
        _lastFailurePath = Path.Combine(dataDirectory, "steam-network-last-failure.json");
        ReadFailures(_lastFailurePath, _failures);
        _service.Diagnostic += OnBridgeDiagnostic;
    }

    public void Start(SteamNetworkSettings settings) => StartAsync(settings, CancellationToken.None).GetAwaiter().GetResult();

    public async Task StartAsync(SteamNetworkSettings settings, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
        await _gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            if (SteamNetworkPolicy.Domains(settings).Length == 0) throw new InvalidOperationException("请至少选择一类 Steam 站点。");
            _status = "正在准备 Steam 网络服务…";
            Changed?.Invoke();
            if (settings.UseHostsCompatibility)
            {
                Interlocked.Increment(ref _generation);
                // The authority is reused across starts, so Windows asks to trust the root once
                // instead of on every start and stop. Each start still receives a fresh leaf set
                // whose private keys are deleted as soon as acceleration stops.
                var ready = await SteamBackgroundClient.RequestAsync(new("STATUS"), lifetime.Token).ConfigureAwait(false);
                if (!ready.Success || ready.Certificate is null) throw new IOException("请先在加速设置中安装证书和网络后台服务。");
                _certificates?.Dispose();
                using var backgroundRoot = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(Convert.FromBase64String(ready.Certificate));
                _certificates = new(settings, Path.Combine(_dataDirectory, "steam-network-leaves.json"), backgroundRoot);
                _bridge = new(_certificates, settings, OnBridgeDiagnostic, _dataDirectory);
                _bridge.Start();
                _backgroundLease = new();
                _status = "正在接入网络后台服务…";
                Changed?.Invoke();
                await _backgroundLease.AcquireAsync((settings.Store ? 1 : 0) | (settings.Community ? 2 : 0) | (settings.Content ? 4 : 0), lifetime.Token).ConfigureAwait(false);
                // No pre-warm here: opening the same edges the user is about to open doubles the
                // ClientHellos on one edge and the resets with them (measured: the pre-warm and the
                // first real page both failed while each alone reached the site).
                _status = "Steam 兼容模式已启动（证书 + hosts）。请重新打开 Steam 页面；接入数为 0 时请重启 Steam 清除 DNS 缓存。";
            }
            else
            {
                _service.Start(settings);
                if (settings.UseSystemPac) _lease.Acquire(_service.PacUri);
                _status = settings.UseSystemPac ? "PAC 模式已启动；不采用系统代理的 Steam 页面不会接入。" : "本地 PAC 服务已启动，未应用到系统。";
            }
            ActiveSettings = settings;
            Changed?.Invoke();
        }
        catch (Exception error)
        {
            if (!lifetime.IsCancellationRequested && error is not OperationCanceledException)
                RecordFailure(CreateStartupDiagnostic(error));
            await StopCoreAsync().ConfigureAwait(false);
            throw;
        }
        finally { _gate.Release(); }
    }

    public void Stop() => StopAsync().GetAwaiter().GetResult();
    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task StopCoreAsync()
    {
        Interlocked.Increment(ref _generation);
        Exception? failure = null;
        try { if (_backgroundLease is not null) await _backgroundLease.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException) { failure = error; }
        finally { _backgroundLease = null; }
        if (_bridge is not null) await _bridge.DisposeAsync().ConfigureAwait(false);
        _bridge = null;
        try { _certificates?.EndSession(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException) { failure ??= error; }
        try { _lease.Restore(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException) { failure ??= error; }
        finally { _service.Stop(); ActiveSettings = null; }
        _status = failure is null ? "已停止；hosts 已恢复，叶密钥已删除。" : "停止时未能完成恢复，请点击“恢复网络配置”。";
        Changed?.Invoke();
        if (failure is not null) throw new IOException(_status, failure);
    }

    public void Recover() => RecoverAsync(CancellationToken.None).GetAwaiter().GetResult();
    public async Task RecoverRuntimeAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            SteamCertificateAuthority.RecoverSession(_trustJournal);
            SteamCertificateAuthority.RecoverSession(Path.Combine(_dataDirectory, "steam-network-leaves.json"));
            if (SteamHostsPatch.Owners(await File.ReadAllBytesAsync(SteamHostsHelper.HostsPath, token).ConfigureAwait(false)).Count > 0)
            {
                await using var recovery = new SteamBackgroundClient();
                await recovery.AcquireAsync(0, token).ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task InstallCertificateAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            await SteamBackgroundClient.ConfigureAsync(true, token).ConfigureAwait(false);
            _status = "网络后台已安装；证书状态正在核对。";
            Changed?.Invoke();
        }
        finally { _gate.Release(); }
    }

    public async Task UninstallCertificateAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            _certificates?.Dispose(); _certificates = null;
            await SteamBackgroundClient.ConfigureAsync(false, token).ConfigureAwait(false);
            // Legacy per-user roots, if any, are removed only by this explicit uninstall action.
            SteamCertificateAuthority.RecoverTrust(_trustJournal);
            SteamCertificateAuthority.RecoverSession(Path.Combine(_dataDirectory, "steam-network-leaves.json"));
            _status = "网络后台和证书已卸载。";
            Changed?.Invoke();
        }
        finally { _gate.Release(); }
    }
    public async Task RecoverAsync(CancellationToken token)
    {
        await StopAsync().ConfigureAwait(false);
        // Releasing the authority first lets the removal actually delete its key container.
        _certificates?.Dispose();
        _certificates = null;
        try
        {
            await SteamHostsLease.RecoverAsync(token).ConfigureAwait(false);
            SteamCertificateAuthority.RecoverTrust(_trustJournal);
            _status = "网络恢复完成；月语根证书与叶密钥已移除。";
            Changed?.Invoke();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or System.ComponentModel.Win32Exception or OperationCanceledException)
        { _status = "恢复未完成：" + error.Message; Changed?.Invoke(); throw; }
    }

    public async Task<string> DiagnoseAsync(SteamNetworkSettings settings, bool accelerated, CancellationToken token) =>
        SteamCompatibilityDiagnostics.Format(await DiagnoseResultsAsync(settings, accelerated, token).ConfigureAwait(false));

    /// <summary>
    /// Structured diagnosis. The route decides the wording: a direct success is never reported
    /// as "accelerated", and a bridge route result requires proof that this instance carried bytes.
    /// </summary>
    public async Task<IReadOnlyList<SteamNetworkProbeResult>> DiagnoseResultsAsync(SteamNetworkSettings settings, bool accelerated, CancellationToken token)
    {
        if (accelerated)
        {
            if (_bridge is not null)
                return await SteamCompatibilityDiagnostics
                    .RunDetailedAsync(settings, SteamProbeRoute.LocalBridge, BridgeCounters, token)
                    .ConfigureAwait(false);
            if (_service.IsRunning)
                return await _service.DiagnoseDetailedAsync(settings, true, token).ConfigureAwait(false);
            throw new InvalidOperationException("请先开始加速。");
        }

        return await SteamCompatibilityDiagnostics
            .RunDetailedAsync(settings, SteamProbeRoute.SystemDirect, null, token)
            .ConfigureAwait(false);
    }

    private SteamBridgeCounters BridgeCounters() => _bridge is null
        ? new(0, 0, 0, 0)
        : new(_bridge.AcceptedConnections, _bridge.TransferredConnections, _bridge.BytesToUpstream, _bridge.BytesToDownstream);

    public void ClearLastFailure()
    {
        lock (_failureGate)
        {
            _failures.Clear();
            SaveFailures();
        }
        Changed?.Invoke();
    }

    internal void OnBridgeDiagnostic(SteamNetworkDiagnostic diagnostic)
    {
        if (diagnostic.ErrorCode == "None")
        {
            if (diagnostic.CertificateValidated)
            {
                try
                {
                    ClearGroup(GroupFor(diagnostic.Host));
                    ClearGroup(FailureGroups.Startup);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
            }
            Diagnostic?.Invoke(diagnostic);
            return;
        }
        RecordFailure(diagnostic);
    }

    private static class FailureGroups
    {
        internal const string Startup = "startup";
        internal const string Store = "store";
        internal const string Community = "community";
    }

    private static string GroupFor(string host)
    {
        if (host is "unknown" or "none" or "") return FailureGroups.Startup;
        if (host.Contains("steamcommunity", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("steamstatic", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("akamaihd", StringComparison.OrdinalIgnoreCase) ||
            host.Contains("eccdnx", StringComparison.OrdinalIgnoreCase)) return FailureGroups.Community;
        return FailureGroups.Store;
    }

    private void RecordFailure(SteamNetworkDiagnostic diagnostic)
    {
        var safe = Sanitize(diagnostic);
        var failure = new SteamNetworkFailure(DateTimeOffset.UtcNow, safe);
        lock (_failureGate)
        {
            _failures[GroupFor(safe.Host)] = failure;
            try { SaveFailures(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        }
        Diagnostic?.Invoke(safe);
        Changed?.Invoke();
    }

    private void ClearGroup(string group)
    {
        lock (_failureGate)
        {
            if (!_failures.Remove(group)) return;
            SaveFailures();
        }
        Changed?.Invoke();
    }

    private List<SteamNetworkFailure> Failures()
    {
        lock (_failureGate) return _failures.Values.OrderByDescending(failure => failure.AtUtc).ToList();
    }

    private static SteamNetworkDiagnostic CreateStartupDiagnostic(Exception error)
    {
        var errorCode = error switch
        {
            InvalidOperationException => "InvalidSettings",
            SocketException => "ListenerFailed",
            UnauthorizedAccessException or System.ComponentModel.Win32Exception => "AuthorizationFailed",
            System.Security.Cryptography.CryptographicException => "CertificateSetupFailed",
            _ => "StartupFailed"
        };
        return new("unknown", null, "none", "none", "startup", 0, errorCode, false);
    }

    private static void ReadFailures(string path, Dictionary<string, SteamNetworkFailure> target)
    {
        if (!File.Exists(path)) return;
        try
        {
            var state = JsonSerializer.Deserialize<PersistedFailure>(File.ReadAllText(path));
            if (state is null || state.IsCleared) return;
            if (state.Groups is not null)
            {
                foreach (var (group, failure) in state.Groups)
                    if (failure is not null) target[group] = failure with { Diagnostic = Sanitize(failure.Diagnostic) };
                return;
            }
            if (state.Failure is null) return;
            // Legacy single-record format: keep the failure under the group its host belongs to.
            var legacy = state.Failure with { Diagnostic = Sanitize(state.Failure.Diagnostic) };
            target[GroupFor(legacy.Diagnostic.Host)] = legacy;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private void SaveFailures()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_lastFailurePath))!);
        var temporary = _lastFailurePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new PersistedFailure(false, LastFailure, new Dictionary<string, SteamNetworkFailure>(_failures))));
        File.Move(temporary, _lastFailurePath, true);
    }

    private static SteamNetworkDiagnostic Sanitize(SteamNetworkDiagnostic value)
    {
        var allDomains = SteamNetworkPolicy.Domains(new SteamNetworkSettings());
        var host = allDomains.Contains(value.Host, StringComparer.OrdinalIgnoreCase) ? value.Host.ToLowerInvariant() : "unknown";
        var address = IPAddress.TryParse(value.Address, out var parsed) && SteamNetworkPolicy.IsPublicAddress(parsed) ? parsed.ToString() : null;
        var source = value.Source is "https-dns" or "system-dns" or "https-dns+system-dns" ? value.Source : "none";
        var sni = value.SniMode is "domain" or "none" ? value.SniMode : "none";
        var stage = AllowedStages.Contains(value.Stage) ? value.Stage : "unknown";
        var code = AllowedCodes.Contains(value.ErrorCode) ? value.ErrorCode : "UnknownFailure";
        var provider = value.Provider is not null && AllowedProviders.Contains(value.Provider) ? value.Provider : "none";
        var recordType = value.RecordType is "A" or "AAAA" ? value.RecordType : null;
        return new(host, address, source, sni, stage, Math.Clamp(value.ElapsedMilliseconds, 0, 30000), code, value.CertificateValidated)
        {
            Provider = provider,
            RecordType = recordType,
            Detail = SanitizeDetail(value.Detail)
        };
    }

    /// <summary>Only "Provider:Code" pairs are kept, so free text or URLs can never reach the log.</summary>
    private static string? SanitizeDetail(string? detail)
    {
        if (string.IsNullOrEmpty(detail)) return null;
        var pairs = detail.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(pair => pair.Split(':'))
            .Where(parts => parts.Length == 2 && AllowedProviders.Contains(parts[0]) &&
                (AllowedCodes.Contains(parts[1]) || parts[1].StartsWith("Http", StringComparison.Ordinal) && int.TryParse(parts[1][4..], out _)))
            .Select(parts => parts[0] + ":" + parts[1])
            .Take(8)
            .ToArray();
        return pairs.Length == 0 ? null : string.Join(',', pairs);
    }

    private static string FormatFailure(SteamNetworkFailure failure)
    {
        var diagnostic = failure.Diagnostic;
        var subject = diagnostic.Host == "unknown" ? "启动" : diagnostic.Host;
        var extra = string.IsNullOrEmpty(diagnostic.Detail) ? "" : "；" + diagnostic.Detail;
        return $"最近失败：{subject} / {diagnostic.Stage} / {diagnostic.ErrorCode}（{diagnostic.ElapsedMilliseconds} ms）{extra}";
    }

    private sealed record PersistedFailure(
        bool IsCleared,
        SteamNetworkFailure? Failure,
        Dictionary<string, SteamNetworkFailure>? Groups = null);

    public void Dispose()
    {
        _shutdown.Cancel();
        try { Stop(); }
        finally
        {
            // Leaves are gone; the trusted authority is kept on purpose so the next launch does
            // not need another confirmation. "恢复网络配置" is what removes it.
            _certificates?.Dispose();
            _certificates = null;
            _service.Dispose();
        }
    }
}
