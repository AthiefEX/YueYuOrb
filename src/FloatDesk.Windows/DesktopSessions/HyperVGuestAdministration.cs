using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FloatDesk.Windows.DesktopSessions;

public enum GuestAdministrationOperation { TestCredential, EnableAutoLogon, DisableAutoLogon, InspectSessions, PrepareAuthenticatedRdp, DisableAuthenticatedRdp, QueryAutoLogon, QueryBootstrapState, RunTaskScript }

public static class HyperVGuestAdministration
{
    public static async Task<string> RunAsync(Guid vmId, GuestCredential credential, GuestAdministrationOperation operation, CancellationToken cancellationToken, string? taskScript = null, IReadOnlyList<string>? taskArguments = null)
    {
        if (vmId == Guid.Empty || !Enum.IsDefined(operation)) throw new ArgumentException("Invalid guest operation.");
        ArgumentNullException.ThrowIfNull(credential);
        if (operation == GuestAdministrationOperation.RunTaskScript && (string.IsNullOrWhiteSpace(taskScript) || taskScript.Length > 65536))
            throw new ArgumentException("TaskScriptInvalid", nameof(taskScript));
        if (taskArguments is not null && (taskArguments.Count > 128 || taskArguments.Any(argument => argument is null || argument.Length > 8192)))
            throw new ArgumentException("TaskArgumentsInvalid", nameof(taskArguments));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(operation == GuestAdministrationOperation.RunTaskScript ? 600 : 60));
        var scope = operation == GuestAdministrationOperation.PrepareAuthenticatedRdp
            ? await HyperVRdpHostScope.VerifyAsync(vmId, timeout.Token).ConfigureAwait(false) : null;
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand"); start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(Bootstrap)));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动来宾管理。");
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        var pointer = Marshal.SecureStringToCoTaskMemUnicode(credential.Password);
        var chars = new char[credential.Password.Length];
        try
        {
            for (var i = 0; i < chars.Length; i++) chars[i] = (char)Marshal.ReadInt16(pointer, i * 2);
            using var data = new MemoryStream();
            using (var writer = new Utf8JsonWriter(data))
            {
                writer.WriteStartObject(); writer.WriteString("vmId", vmId); writer.WriteString("user", credential.UserName);
                writer.WriteString("script", Script);
                if (operation == GuestAdministrationOperation.RunTaskScript) writer.WriteString("taskScript", taskScript);
                writer.WriteString("taskArgumentsJson", JsonSerializer.Serialize(taskArguments ?? []));
                writer.WriteString("clientHostName", Environment.MachineName);
                if (scope is not null) { writer.WriteString("verifiedHostAddress", scope.HostAddress); writer.WriteString("expectedGuestMac", scope.GuestMacAddress); }
                writer.WriteString("password", chars.AsSpan()); writer.WriteString("operation", operation.ToString()); writer.WriteEndObject();
            }
            try { await process.StandardInput.BaseStream.WriteAsync(data.GetBuffer().AsMemory(0, checked((int)data.Length)), timeout.Token).ConfigureAwait(false); }
            finally { CryptographicOperations.ZeroMemory(data.GetBuffer()); }
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var text = await output.ConfigureAwait(false); _ = await errors.ConfigureAwait(false);
            if (process.ExitCode != 0 || (text.Trim() != "GuestOperationSucceeded" &&
                !(operation == GuestAdministrationOperation.InspectSessions && text.Trim().StartsWith("GuestSessionInventory:", StringComparison.Ordinal)) &&
                !(operation == GuestAdministrationOperation.QueryAutoLogon && text.Trim().StartsWith("GuestAutoLogonState:", StringComparison.Ordinal)) &&
                !(operation == GuestAdministrationOperation.QueryBootstrapState && text.Trim().StartsWith("GuestBootstrapState:", StringComparison.Ordinal)) &&
                !(operation == GuestAdministrationOperation.PrepareAuthenticatedRdp && text.Trim().StartsWith("GuestRdpEndpoint:", StringComparison.Ordinal))))
                throw new InvalidOperationException(text.Trim() == "ForeignAutoLogon" ?
                    "来宾已有其他来源或其他账户的自动登录配置；月语未覆盖它。" :
                    "来宾操作未完成：请确认虚拟机正在运行、本地账号密码正确且具有管理员权限，PowerShell Direct 服务可用。" +
                    (text.Trim().StartsWith("GuestOperationFailed:", StringComparison.Ordinal) && text.Trim().Length < 160 ? "（" + text.Trim() + "）" : ""));
            return text.Trim();
        }
        finally
        {
            Array.Clear(chars); Marshal.ZeroFreeCoTaskMemUnicode(pointer);
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            try { _ = await output.ConfigureAwait(false); _ = await errors.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    public const string Bootstrap = """
        $ErrorActionPreference='Stop'
        [Console]::InputEncoding=[Text.UTF8Encoding]::new($false)
        [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
        $packet=[Console]::In.ReadToEnd() | ConvertFrom-Json
        & ([scriptblock]::Create($packet.script)) $packet
        """;

    public const string Script = """
        param($config)
        $ErrorActionPreference='Stop'
        [Console]::InputEncoding=New-Object Text.UTF8Encoding($false)
        [Console]::OutputEncoding=New-Object Text.UTF8Encoding($false)
        $stage='HostVmLookup'
        try {
            Import-Module Hyper-V -ErrorAction Stop
            $vm=Get-VM -Id ([guid]$config.vmId) -ErrorAction Stop
            if ($vm.State -ne 'Running') { throw 'GuestNotRunning' }
            $stage='CredentialConversion'
            $secure=[Security.SecureString]::new()
            foreach($character in $config.password.ToCharArray()) { $secure.AppendChar($character) }
            $secure.MakeReadOnly()
            $credential=[Management.Automation.PSCredential]::new($config.user,$secure)
            $action={
                param($operation,$user,$password,$clientHostName,$verifiedHostAddress,$expectedGuestMac,$taskScript,$taskArgumentsJson)
                $ErrorActionPreference='Stop'
                if (-not (Test-Path 'HKLM:\SOFTWARE\Microsoft\Virtual Machine\Guest\Parameters')) { throw 'NotHyperVGuest' }
                $admin=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
                if (-not $admin) { throw 'GuestAdministratorRequired' }
                if ($operation -eq 'TestCredential') { return }
                if ($operation -eq 'RunTaskScript') {
                    if([string]::IsNullOrWhiteSpace($taskScript) -or $taskScript.Length -gt 65536) { throw 'TaskScriptInvalid' }
                    $global:LASTEXITCODE=0
                    $taskArguments=@(ConvertFrom-Json -InputObject $taskArgumentsJson)
                    & ([scriptblock]::Create($taskScript)) @taskArguments *> $null
                    if($LASTEXITCODE -ne 0) { throw 'TaskScriptExitCode' }
                    return 'GuestTaskScriptCompleted'
                }
                if ($operation -eq 'QueryBootstrapState') {
                    $completed=(Get-ItemProperty -Path 'HKLM:\SOFTWARE\YueYuOrb\Bootstrap' -Name Completed -ErrorAction SilentlyContinue).Completed
                    if($completed -eq 1) { return 'GuestBootstrapState:Complete' }
                    return 'GuestBootstrapState:Pending'
                }
                if ($operation -eq 'QueryAutoLogon') {
                    $current=Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
                    $owner='HKLM:\SOFTWARE\YueYuOrb\AutoLogon'
                    if($current.AutoAdminLogon -ne '1') { return 'GuestAutoLogonState:Disabled' }
                    if((Test-Path $owner) -and (Get-ItemPropertyValue $owner -Name User) -eq $user -and
                        $current.DefaultUserName -eq $user -and $current.DefaultDomainName -eq $env:COMPUTERNAME) {
                        return 'GuestAutoLogonState:Enabled'
                    }
                    return 'GuestAutoLogonState:Foreign'
                }
                if ($operation -eq 'DisableAuthenticatedRdp') {
                    $managed='HKLM:\SOFTWARE\YueYuOrb\RemoteDesktop'
                    if (-not (Test-Path $managed)) { return }
                    $settings='HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server'
                    $original=[int](Get-ItemPropertyValue -Path $managed -Name OriginalDenyConnections)
                    foreach($name in @('YueYuOrb-HostOnly-RDP','YueYuOrb-HostOnly-RDP-OtherHosts')) {
                        $rule=Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue
                        if($null -ne $rule -and $rule.Group -ne 'YueYuOrb Managed Guest'){throw 'ForeignFirewallRule'}
                    }
                    Set-ItemProperty -Path $settings -Name fDenyTSConnections -Value 1
                    foreach($name in @('YueYuOrb-HostOnly-RDP','YueYuOrb-HostOnly-RDP-OtherHosts')) {
                        Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction Stop
                    }
                    Set-ItemProperty -Path $settings -Name fDenyTSConnections -Value $original
                    Remove-Item -LiteralPath $managed -Force
                    return
                }
                if ($operation -eq 'PrepareAuthenticatedRdp') {
                    # Scope comes from host Get-VMNetworkAdapter -> management MAC
                    # -> Get-NetAdapter -> Get-NetIPAddress, never from a guessed gateway.
                    $adapter=@(Get-NetAdapter | Where-Object {([string]$_.MacAddress).Replace('-','').Replace(':','') -eq $expectedGuestMac})
                    if ($adapter.Count -ne 1) { throw 'GuestAdapterIdentityMismatch' }
                    $address=@(Get-NetIPAddress -InterfaceIndex $adapter[0].ifIndex -AddressFamily IPv4 -ErrorAction Stop | Where-Object {$_.AddressState -eq 'Preferred' -and $_.IPAddress -notlike '169.254.*'})
                    if ($address.Count -ne 1) { throw 'GuestAddressAmbiguous' }
                    $profiles=@(Get-NetFirewallProfile -PolicyStore ActiveStore)
                    if (@($profiles | Where-Object {$_.Enabled -ne 'True' -or $_.DefaultInboundAction -eq 'Allow'}).Count -gt 0) { throw 'GuestFirewallMustBlockUnsolicitedInbound' }
                    # Refuse foreign rules that would widen this service beyond
                    # the host, rather than disabling/modifying another app's rules.
                    foreach($other in @(Get-NetFirewallRule -PolicyStore ActiveStore -Enabled True -Direction Inbound -Action Allow | Where-Object Group -ne 'YueYuOrb Managed Guest')) {
                        $port=$other | Get-NetFirewallPortFilter
                        if ([string]$port.Protocol -notin @('TCP','6','Any','256')) { continue }
                        $applies=$false
                        foreach($value in @($port.LocalPort)) {
                            if($value -eq 'Any' -or $value -eq '3389') { $applies=$true }
                            elseif($value -match '^(\d+)-(\d+)$' -and [int]$Matches[1] -le 3389 -and [int]$Matches[2] -ge 3389) { $applies=$true }
                        }
                        if(-not $applies){continue}
                        $application=$other | Get-NetFirewallApplicationFilter
                        if($application.Program -notin @('Any','%SystemRoot%\system32\svchost.exe',($env:SystemRoot+'\system32\svchost.exe'))){continue}
                        if($application.Package -and $application.Package -ne 'Any'){continue}
                        $service=$other | Get-NetFirewallServiceFilter
                        if($service.Service -notin @('Any','TermService')){continue}
                        $addresses=@(($other | Get-NetFirewallAddressFilter).RemoteAddress)
                        if($addresses.Count -ne 1 -or $addresses[0] -notin @($verifiedHostAddress,($verifiedHostAddress+'/32'))){throw 'ExistingRdpRuleBroaderThanHost'}
                    }
                    $rule=Get-NetFirewallRule -Name 'YueYuOrb-HostOnly-RDP' -ErrorAction SilentlyContinue
                    if ($null -ne $rule -and $rule.Group -ne 'YueYuOrb Managed Guest') { throw 'ForeignFirewallRule' }
                    $block=Get-NetFirewallRule -Name 'YueYuOrb-HostOnly-RDP-OtherHosts' -ErrorAction SilentlyContinue
                    if ($null -ne $block -and $block.Group -ne 'YueYuOrb Managed Guest') { throw 'ForeignFirewallRule' }
                    $settings='HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server'
                    $managed='HKLM:\SOFTWARE\YueYuOrb\RemoteDesktop'
                    $original=[int](Get-ItemProperty -Path $settings).fDenyTSConnections
                    if (-not (Test-Path $managed)) {
                        New-Item -Path $managed -Force | Out-Null
                        New-ItemProperty -Path $managed -Name OriginalDenyConnections -Value $original -PropertyType DWord -Force | Out-Null
                    }
                    try {
                        Set-ItemProperty -Path $settings -Name fDenyTSConnections -Value 1
                        if ($null -ne $rule) { $rule | Remove-NetFirewallRule -ErrorAction Stop }
                        if ($null -ne $block) { $block | Remove-NetFirewallRule -ErrorAction Stop }
                        New-NetFirewallRule -Name 'YueYuOrb-HostOnly-RDP' -DisplayName 'YueYu host-only desktop' -Group 'YueYuOrb Managed Guest' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 3389 -LocalAddress $address[0].IPAddress -RemoteAddress $verifiedHostAddress -Service TermService -Profile Any -ErrorAction Stop | Out-Null
                        Set-ItemProperty -Path $settings -Name fDenyTSConnections -Value 0
                        Start-Service TermService -ErrorAction Stop
                        $hash=''
                        for($i=0;$i -lt 15;$i++) {
                            $listener=Get-CimInstance -Namespace 'root\cimv2\TerminalServices' -ClassName Win32_TSGeneralSetting -Filter "TerminalName='RDP-tcp'" -ErrorAction Stop
                            $hash=[string]$listener.SSLCertificateSHA1Hash
                            if ($hash -match '^[0-9A-Fa-f]{40}$') { break }
                            Start-Sleep -Seconds 1
                        }
                        if ($hash -notmatch '^[0-9A-Fa-f]{40}$') { throw 'GuestRdpCertificateUnavailable' }
                        $certificate=Get-Item -LiteralPath ('Cert:\LocalMachine\Remote Desktop\'+$hash) -ErrorAction Stop
                        $publicKey=[Convert]::ToBase64String($certificate.GetPublicKey())
                        return 'GuestRdpEndpoint:'+([pscustomobject]@{Address=$address[0].IPAddress;HostAddress=$verifiedHostAddress;ComputerName=$env:COMPUTERNAME;CertificateHash=$hash;CertificatePublicKey=$publicKey} | ConvertTo-Json -Compress)
                    } catch {
                        Get-NetFirewallRule -Name 'YueYuOrb-HostOnly-RDP' -ErrorAction SilentlyContinue | Where-Object Group -eq 'YueYuOrb Managed Guest' | Remove-NetFirewallRule -ErrorAction SilentlyContinue
                        Set-ItemProperty -Path $settings -Name fDenyTSConnections -Value 1 -ErrorAction SilentlyContinue
                        throw
                    }
                }
                if ($operation -eq 'InspectSessions') {
                    Add-Type -TypeDefinition @'
        using System;
        using System.Collections.Generic;
        using System.Runtime.InteropServices;
        public static class YueYuSessionInventory {
            [StructLayout(LayoutKind.Sequential)] struct SI { public int Id; public IntPtr Name; public int State; }
            [DllImport("wtsapi32.dll", CharSet=CharSet.Unicode)] static extern bool WTSEnumerateSessionsW(IntPtr server,int reserved,int version,out IntPtr entries,out int count);
            [DllImport("wtsapi32.dll", CharSet=CharSet.Unicode)] static extern bool WTSQuerySessionInformationW(IntPtr server,int id,int type,out IntPtr data,out int size);
            [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr memory);
            static string User(int id) { IntPtr data; int size; if(!WTSQuerySessionInformationW(IntPtr.Zero,id,5,out data,out size)) return ""; try{return Marshal.PtrToStringUni(data)??"";} finally{WTSFreeMemory(data);} }
            static string Client(int id) { IntPtr data;int size; if(!WTSQuerySessionInformationW(IntPtr.Zero,id,10,out data,out size)) return "";try{return Marshal.PtrToStringUni(data)??"";}finally{WTSFreeMemory(data);} }
            static int Value(int id,int type,int offset) { IntPtr data;int size; if(!WTSQuerySessionInformationW(IntPtr.Zero,id,type,out data,out size)) return -1; try{return type==16&&size>=2?Marshal.ReadInt16(data):size>=offset+4?Marshal.ReadInt32(data,offset):-1;}finally{WTSFreeMemory(data);} }
            public static string Read(string user,string clientHostName) {
                IntPtr entries;int count; if(!WTSEnumerateSessionsW(IntPtr.Zero,0,1,out entries,out count)) throw new InvalidOperationException("SessionEnumerationFailed");
                var rows=new List<string>();
                try { for(int i=0;i<count;i++){var item=(SI)Marshal.PtrToStructure(IntPtr.Add(entries,i*Marshal.SizeOf(typeof(SI))),typeof(SI));
                    bool match=string.Equals(User(item.Id),user,StringComparison.OrdinalIgnoreCase);
                    bool clientMatch=string.Equals(Client(item.Id),clientHostName,StringComparison.OrdinalIgnoreCase);
                    int protocol=Value(item.Id,16,0);
                    rows.Add(item.Id+","+item.State+","+protocol+","+match+","+Value(item.Id,25,16)+","+clientMatch);
                } }
                finally {WTSFreeMemory(entries);}
                return "GuestSessionInventory:"+string.Join(";",rows);
            }
        }
        '@
                    return [YueYuSessionInventory]::Read($user,$clientHostName)
                }
                $winlogon='HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
                $owner='HKLM:\SOFTWARE\YueYuOrb\AutoLogon'
                $existing=Get-ItemProperty -Path $winlogon
                $owned=Test-Path -LiteralPath $owner
                if ($operation -eq 'DisableAutoLogon' -and -not $owned) { throw 'ForeignAutoLogon' }
                if ($operation -eq 'EnableAutoLogon' -and $existing.AutoAdminLogon -eq '1' -and -not $owned) { throw 'ForeignAutoLogon' }
                if ($owned -and (Get-ItemPropertyValue -Path $owner -Name User) -ne $user) { throw 'ForeignAutoLogon' }
                if ($owned -and $existing.AutoAdminLogon -eq '1' -and
                    ($existing.DefaultUserName -ne $user -or $existing.DefaultDomainName -ne $env:COMPUTERNAME)) { throw 'ForeignAutoLogon' }
                Add-Type -TypeDefinition @'
        using System;
        using System.ComponentModel;
        using System.Runtime.InteropServices;
        public static class YueYuAutoLogonSecret {
            [StructLayout(LayoutKind.Sequential)] struct US { public ushort Length, MaximumLength; public IntPtr Buffer; }
            [StructLayout(LayoutKind.Sequential)] struct OA { public uint Length; public IntPtr Root, Name; public uint Attributes; public IntPtr Descriptor, Qos; }
            [DllImport("advapi32.dll")] static extern uint LsaOpenPolicy(IntPtr system, ref OA attributes, uint access, out IntPtr policy);
            [DllImport("advapi32.dll")] static extern uint LsaStorePrivateData(IntPtr policy, ref US key, IntPtr data);
            [DllImport("advapi32.dll")] static extern uint LsaClose(IntPtr policy);
            [DllImport("advapi32.dll")] static extern uint LsaNtStatusToWinError(uint status);
            static US Make(string value) { return new US { Length=checked((ushort)(value.Length*2)), MaximumLength=checked((ushort)(value.Length*2+2)), Buffer=Marshal.StringToHGlobalUni(value) }; }
            public static void Delete() { Store(null); }
            public static void Store(string password) {
                var attributes=new OA { Length=(uint)Marshal.SizeOf(typeof(OA)) };
                IntPtr policy;
                var status=LsaOpenPolicy(IntPtr.Zero,ref attributes,0x20,out policy);
                if(status!=0) throw new Win32Exception((int)LsaNtStatusToWinError(status));
                var key=Make("DefaultPassword"); var secret=new US(); IntPtr data=IntPtr.Zero;
                try {
                    if(password!=null) { secret=Make(password); data=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(US))); Marshal.StructureToPtr(secret,data,false); }
                    status=LsaStorePrivateData(policy,ref key,data);
                    if(status!=0 && !(password==null && LsaNtStatusToWinError(status)==2)) throw new Win32Exception((int)LsaNtStatusToWinError(status));
                } finally {
                    if(secret.Buffer!=IntPtr.Zero) { for(int i=0;i<secret.MaximumLength;i++) Marshal.WriteByte(secret.Buffer,i,0); Marshal.FreeHGlobal(secret.Buffer); }
                    if(data!=IntPtr.Zero) Marshal.FreeHGlobal(data);
                    Marshal.FreeHGlobal(key.Buffer); LsaClose(policy);
                }
            }
        }
        '@
                if ($operation -eq 'EnableAutoLogon') {
                    # Never configure a different user or overwrite foreign active auto-logon.
                    $secretPointer=[Runtime.InteropServices.Marshal]::SecureStringToBSTR($password)
                    try { [YueYuAutoLogonSecret]::Store([Runtime.InteropServices.Marshal]::PtrToStringBSTR($secretPointer)) }
                    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($secretPointer) }
                    try {
                        New-Item -Path $owner -Force | Out-Null
                        Set-ItemProperty -Path $owner -Name User -Value $user
                        Set-ItemProperty -Path $winlogon -Name DefaultUserName -Value $user
                        Set-ItemProperty -Path $winlogon -Name DefaultDomainName -Value $env:COMPUTERNAME
                        Remove-ItemProperty -Path $winlogon -Name AutoLogonCount -ErrorAction SilentlyContinue
                        Remove-ItemProperty -Path $winlogon -Name DefaultPassword -ErrorAction SilentlyContinue
                        Set-ItemProperty -Path $winlogon -Name AutoAdminLogon -Value '1'
                    } catch {
                        Set-ItemProperty -Path $winlogon -Name AutoAdminLogon -Value '0' -ErrorAction SilentlyContinue
                        [YueYuAutoLogonSecret]::Delete()
                        throw
                    }
                } elseif ($operation -eq 'DisableAutoLogon') {
                    Set-ItemProperty -Path $winlogon -Name AutoAdminLogon -Value '0'
                    [YueYuAutoLogonSecret]::Delete()
                    Remove-Item -LiteralPath $owner -Force
                } else { throw 'UnknownGuestOperation' }
            }
            $stage='PowerShellDirect'
            $result=Invoke-Command -VMId ([guid]$config.vmId) -Credential $credential -ScriptBlock $action -ArgumentList $config.operation,$config.user,$secure,$config.clientHostName,$config.verifiedHostAddress,$config.expectedGuestMac,$config.taskScript,$config.taskArgumentsJson -ErrorAction Stop
            if($config.operation -eq 'RunTaskScript' -and [string]$result -ne 'GuestTaskScriptCompleted') { throw 'TaskScriptIncomplete' }
            if ($config.operation -in @('InspectSessions','PrepareAuthenticatedRdp','QueryAutoLogon','QueryBootstrapState')) { [Console]::WriteLine([string]$result) }
            else { [Console]::WriteLine('GuestOperationSucceeded') }
        } catch {
            if($config.operation -eq 'RunTaskScript') { [Console]::WriteLine('GuestOperationFailed:TaskScript'); exit 1 }
            if ($_.Exception.Message -like '*ForeignAutoLogon*') { [Console]::WriteLine('ForeignAutoLogon') }
            else {
                $kind=$_.Exception.GetType().Name
                $code=($_.FullyQualifiedErrorId -split ',')[0] -replace '[^A-Za-z0-9_.-]',''
                if($code.Length -gt 60){$code=$code.Substring(0,60)}
                [Console]::WriteLine('GuestOperationFailed:'+$stage+':'+$kind+':'+$code)
            }
            exit 1
        } finally {
            $config.password=$null
            if ($null -ne $secure) { $secure.Dispose() }
        }
        """;
}
