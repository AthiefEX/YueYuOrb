using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace FloatDesk.Windows.DesktopSessions;

public sealed record HyperVRdpHostScope(string HostAddress, string GuestMacAddress)
{
    public static async Task<HyperVRdpHostScope> VerifyAsync(Guid vmId, CancellationToken cancellationToken)
    {
        if (vmId == Guid.Empty) throw new ArgumentException("VM identity required.", nameof(vmId));
        var json = await new PowerShellHyperVHost().RunScriptForManagementAsyncCore($$"""
            $ErrorActionPreference='Stop'; Import-Module Hyper-V
            $vm=Get-VM -Id ([guid]'{{vmId:D}}') -ErrorAction Stop
            $a=@(Get-VMNetworkAdapter -VM $vm -ErrorAction Stop)
            if ($a.Count -ne 1) { throw '自动登录暂只支持单网卡 Default Switch 虚拟机。' }
            $switch=Get-VMSwitch -Id $a[0].SwitchId -ErrorAction Stop
            if ($switch.Name -ne 'Default Switch' -or $switch.SwitchType -ne 'Internal') { throw '自动登录暂只支持 Default Switch；可以使用原生控制台。' }
            $management=@(Get-VMNetworkAdapter -ManagementOS | Where-Object {$_.SwitchId -eq $switch.Id})
            if ($management.Count -ne 1) { throw '无法唯一确认主机虚拟网卡。' }
            $mac=([string]$management[0].MacAddress).Replace('-','').Replace(':','')
            $interface=@(Get-NetAdapter -IncludeHidden | Where-Object {([string]$_.MacAddress).Replace('-','').Replace(':','') -eq $mac})
            if ($interface.Count -ne 1) { throw '主机虚拟网卡身份不明确。' }
            $ip=@(Get-NetIPAddress -InterfaceIndex $interface[0].ifIndex -AddressFamily IPv4 -ErrorAction Stop | Where-Object {$_.AddressState -eq 'Preferred' -and $_.IPAddress -notlike '169.254.*'})
            if ($ip.Count -ne 1) { throw '主机虚拟网卡地址不明确。' }
            [pscustomobject]@{HostAddress=$ip[0].IPAddress;GuestMacAddress=[string]$a[0].MacAddress} | ConvertTo-Json -Compress
            """, cancellationToken).ConfigureAwait(false);
        var scope = JsonSerializer.Deserialize<HyperVRdpHostScope>(json) ?? throw new InvalidOperationException("主机接口身份缺失。");
        if (!IPAddress.TryParse(scope.HostAddress, out var address) || address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address) ||
            scope.GuestMacAddress.Length != 12 || !scope.GuestMacAddress.All(char.IsAsciiHexDigit))
            throw new InvalidOperationException("主机接口身份无效。");
        return scope;
    }
}
