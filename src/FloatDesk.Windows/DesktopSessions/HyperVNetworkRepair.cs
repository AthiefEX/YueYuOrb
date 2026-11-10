using System.Collections.Concurrent;
using FloatDesk.Core.Models;

namespace FloatDesk.Windows.DesktopSessions;

public sealed record HyperVNetworkStatus(bool Route, bool Dns, bool Http, bool Internet, bool ProbeDns = true)
{
    public bool Reachable => Route && Dns && Http;
    public bool Healthy => Reachable && Internet && ProbeDns;
    public string Summary => Healthy ? "网络可访问，Windows 联网状态正常。" :
        Reachable && !ProbeDns ? "网络可访问，但联网探针 DNS 不符；请在主机代理的 Fake-IP 例外中加入 +.msftncsi.com 和 +.msftconnecttest.com。" :
        Reachable ? "网络可访问，但 Windows 仍报告无 Internet；请检查代理联网探针例外或来宾策略。" :
        $"尚未恢复：路由 {(Route ? "正常" : "失败")}，DNS {(Dns ? "正常" : "失败")}，HTTP {(Http ? "正常" : "失败")}。";
}

public interface IHyperVNetworkOperations
{
    Task<HyperVNetworkStatus> CheckAsync(Guid vmId, Guid environmentId, CancellationToken cancellationToken);
    Task FlushDnsAsync(Guid vmId, Guid environmentId, CancellationToken cancellationToken);
    Task ReconnectAsync(Guid vmId, CancellationToken cancellationToken);
}

public sealed class HyperVNetworkRepair(IHyperVNetworkOperations? operations = null)
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Gates = new();
    private readonly IHyperVNetworkOperations _operations = operations ?? new HyperVNetworkOperations();

    public async Task<HyperVNetworkStatus> RepairAsync(Guid vmId, Guid environmentId,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (vmId == Guid.Empty || environmentId == Guid.Empty) throw new ArgumentException("请选择已配对的虚拟机环境。");
        var gate = Gates.GetOrAdd(vmId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("该虚拟机正在修复网络。");
        try
        {
            progress?.Report("正在检查路由、DNS、HTTP 和 Windows 联网状态…");
            var status = await _operations.CheckAsync(vmId, environmentId, cancellationToken).ConfigureAwait(false);
            if (status.Healthy) return status;
            progress?.Report("正在清理来宾 DNS 缓存…");
            await _operations.FlushDnsAsync(vmId, environmentId, cancellationToken).ConfigureAwait(false);
            status = await _operations.CheckAsync(vmId, environmentId, cancellationToken).ConfigureAwait(false);
            if (status.Healthy) return status;
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report("正在重连此虚拟机网卡，将短暂断网；请稍候…");
            // Finish restoration even if the caller closes its window mid-operation.
            await _operations.ReconnectAsync(vmId, CancellationToken.None).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report("正在重新验证联网状态…");
            return await _operations.CheckAsync(vmId, environmentId, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
}

internal sealed class HyperVNetworkOperations : IHyperVNetworkOperations
{
    public async Task<HyperVNetworkStatus> CheckAsync(Guid vmId, Guid environmentId, CancellationToken cancellationToken)
    {
        var values = await HyperVGuestChecks.RunAsync(vmId, environmentId,
        [
            "if (@(Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction Stop).Count -eq 0) { exit 1 }",
            "if (@((Resolve-DnsName www.msftconnecttest.com -Type A -DnsOnly -ErrorAction Stop).IPAddress).Count -eq 0) { exit 1 }",
            "$p=Get-ItemProperty 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\NlaSvc\\Parameters\\Internet'; $r=Invoke-WebRequest ('http://'+$p.ActiveWebProbeHost+'/'+$p.ActiveWebProbePath) -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop; if ($r.StatusCode -ne 200 -or $r.Content.Trim() -ne $p.ActiveWebProbeContent) { exit 1 }",
            "for($i=0;$i -lt 8;$i++){if (@(Get-NetConnectionProfile -ErrorAction Stop | Where-Object {$_.IPv4Connectivity -eq 'Internet' -or $_.IPv6Connectivity -eq 'Internet'}).Count -gt 0){exit 0}; Start-Sleep -Seconds 1}; exit 1",
            "$p=Get-ItemProperty 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\NlaSvc\\Parameters\\Internet'; if (@((Resolve-DnsName $p.ActiveDnsProbeHost -Type A -DnsOnly -ErrorAction Stop).IPAddress) -notcontains $p.ActiveDnsProbeContent) { exit 1 }"
        ], cancellationToken).ConfigureAwait(false);
        return new(values[0], values[1], values[2], values[3], values[4]);
    }

    public async Task FlushDnsAsync(Guid vmId, Guid environmentId, CancellationToken cancellationToken)
    {
        var results = await HyperVGuestChecks.RunAsync(vmId, environmentId, ["Clear-DnsClientCache -ErrorAction Stop"], cancellationToken).ConfigureAwait(false);
        if (!results[0]) throw new InvalidOperationException("来宾 DNS 缓存清理失败；未更改网卡。");
    }

    public async Task ReconnectAsync(Guid vmId, CancellationToken cancellationToken)
    {
        var host = new PowerShellHyperVHost();
        using var preparation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var json = await host.RunScriptForManagementAsyncCore($$"""
            $ErrorActionPreference='Stop'; Import-Module Hyper-V
            $vm=Get-VM -Id ([guid]'{{vmId:D}}') -ErrorAction Stop
            $a=@(Get-VMNetworkAdapter -VM $vm -ErrorAction Stop)
            if ($a.Count -ne 1) { throw '自动修复仅支持单网卡虚拟机，请手动检查交换机。' }
            $s=Get-VMSwitch -Id $a[0].SwitchId -ErrorAction Stop
            [pscustomobject]@{switchId=$s.Id;adapterId=[string]$a[0].Id} | ConvertTo-Json -Compress
            """, preparation.Token).ConfigureAwait(false);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var switchId = document.RootElement.GetProperty("switchId").GetGuid();
        var adapterId = document.RootElement.GetProperty("adapterId").GetString() ?? throw new InvalidOperationException("网卡身份缺失。");
        var select = $$"""
            $ErrorActionPreference='Stop'; Import-Module Hyper-V
            $vm=Get-VM -Id ([guid]'{{vmId:D}}') -ErrorAction Stop
            $a=@(Get-VMNetworkAdapter -VM $vm | Where-Object {$_.Id -eq '{{adapterId.Replace("'", "''", StringComparison.Ordinal)}}'})
            if ($a.Count -ne 1) { throw '网卡身份已变化，请手动检查。' }
            $adapter=$a[0]
            $switch=Get-VMSwitch -Id ([guid]'{{switchId:D}}') -ErrorAction Stop
            """;
        var restore = $$"""
            $fresh=@(Get-VMNetworkAdapter -VM $vm | Where-Object {$_.Id -eq '{{adapterId.Replace("'", "''", StringComparison.Ordinal)}}'})
            if ($fresh.Count -ne 1) { throw '网卡身份已变化，请手动恢复。' }
            $adapter=$fresh[0]
            if ($null -ne $adapter.SwitchId -and $adapter.SwitchId -ne [guid]::Empty -and $adapter.SwitchId -ne $switch.Id) { throw '网卡已被其他操作连接到另一交换机，未覆盖其配置。' }
            if ($adapter.SwitchId -ne $switch.Id) { Connect-VMNetworkAdapter -VMNetworkAdapter $adapter -SwitchName $switch.Name -Confirm:$false -ErrorAction Stop }
            """;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await host.RunScriptForManagementAsyncCore(select + "\ntry { Disconnect-VMNetworkAdapter -VMNetworkAdapter $adapter -Confirm:$false -ErrorAction Stop; Start-Sleep -Seconds 2 } finally { " + restore + "\n}\n'ok'", timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            // A killed PowerShell process cannot run its finally. Restore again
            // from the independently captured identities in a fresh process.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await host.RunScriptForManagementAsyncCore(select + "\n" + restore + "\n'ok'", cleanup.Token).ConfigureAwait(false);
        }
    }
}

public static class HyperVGuestChecks
{
    public static async Task<bool[]> RunAsync(Guid vmId, Guid environmentId, IReadOnlyList<string> checks, CancellationToken cancellationToken)
    {
        var identity = await new HyperVGuestAgentProbe(new DpapiHyperVPairingSecretStore()).ProbeAsync(vmId, cancellationToken).ConfigureAwait(false);
        var endpoints = new HyperVAgentEndpointRegistry(); endpoints.Set(environmentId, identity);
        await using var transport = new HyperVSocketTransport(new WindowsHyperVSocketConnector(), endpoints, new DpapiHyperVPairingSecretStore());
        var handle = new SessionHandle(environmentId, Guid.NewGuid(), identity.WindowsSessionId, identity.LoginInstanceId, true);
        var connected = await transport.ConnectAsync(handle, cancellationToken).ConfigureAwait(false);
        if (!connected.Succeeded) throw new InvalidOperationException("来宾身份验证失败，未执行网络操作。");
        var result = new bool[checks.Count];
        try
        {
            for (var i = 0; i < checks.Count; i++)
            {
                await transport.AcquireInputLeaseAsync(handle, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop'; " + checks[i]));
                var command = new SessionCommand(Guid.NewGuid(), handle, SessionCommandKind.RunScript,
                    new Dictionary<string, string> { ["scriptPath"] = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
                        ["arguments"] = System.Text.Json.JsonSerializer.Serialize(new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded }) },
                    DateTimeOffset.UtcNow.AddSeconds(16));
                var pending = transport.SendAsync(handle, command, cancellationToken);
                while (!pending.IsCompleted)
                {
                    if (await Task.WhenAny(pending, Task.Delay(1000, cancellationToken)).ConfigureAwait(false) == pending) break;
                    await transport.RenewInputLeaseAsync(handle, cancellationToken).ConfigureAwait(false);
                }
                var response = await pending.ConfigureAwait(false);
                if (response.OutcomeUnknown) throw new InvalidOperationException("来宾检测结果未知，请检查 Agent 状态。");
                result[i] = response.Succeeded;
            }
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await transport.ReleaseInputLeaseAsync(handle, cleanup.Token).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or InvalidOperationException) { }
        }
        return result;
    }
}
