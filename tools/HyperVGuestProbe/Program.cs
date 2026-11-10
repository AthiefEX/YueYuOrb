using FloatDesk.Windows.DesktopSessions;
using FloatDesk.Core.Models;

if (args is ["--apply-resources", var resourceVm, var cpuText, var memoryText] &&
    Guid.TryParse(resourceVm, out var resourceId) && int.TryParse(cpuText, out var cpuCount) && long.TryParse(memoryText, out var memoryMiB))
{
    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var applied = await new HyperVResourceManager().ApplyAsync(resourceId, cpuCount, memoryMiB, limit.Token);
    Console.WriteLine($"Actual CPU={applied.CpuCount}; memoryMiB={applied.MemoryStartupMiB}; fixedForGpu={applied.FixedMemoryForGpu}");
    return 0;
}

if (args is ["--write-install-script", var scriptPath])
{
    await File.WriteAllTextAsync(scriptPath, HyperVAutomaticInstaller.Script, new System.Text.UTF8Encoding(true));
    return 0;
}
if (args is ["--write-installer", var outputPath])
{
    await File.WriteAllTextAsync(outputPath, HyperVGuestInstaller.BuildInstallScript(), new System.Text.UTF8Encoding(true));
    return 0;
}
if (args is ["--write-admin-script", var adminScriptPath])
{
    await File.WriteAllTextAsync(adminScriptPath, HyperVGuestAdministration.Script, new System.Text.UTF8Encoding(true));
    return 0;
}
if (args.Length is < 1 or > 3 || !Guid.TryParse(args[0], out var vmId)) return 2;
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(args.Length == 3 && args[2] == "--authenticated-rdp-pilot" ? 180 : 65));
try
{
    var identity = await new HyperVGuestAgentProbe(new DpapiHyperVPairingSecretStore()).ProbeAsync(vmId, timeout.Token);
    Console.WriteLine($"Guest proof valid. VM={identity.VmId:D}; session={identity.WindowsSessionId}");
    if (args.Length >= 2)
    {
        if (!Guid.TryParse(args[1], out var environmentId)) return 2;
        var endpoints = new HyperVAgentEndpointRegistry();
        endpoints.Set(environmentId, identity);
        await using var transport = new HyperVSocketTransport(new WindowsHyperVSocketConnector(), endpoints, new DpapiHyperVPairingSecretStore());
        var handle = new SessionHandle(environmentId, Guid.NewGuid(), identity.WindowsSessionId, identity.LoginInstanceId, true);
        var result = await transport.ConnectAsync(handle, timeout.Token);
        Console.WriteLine($"Transport handshake: succeeded={result.Succeeded}; error={result.ErrorCode ?? "none"}");
        if (!result.Succeeded) return 1;
        if (args.Length == 3 && args[2] == "--enable-saved-autologon")
        {
            using var saved = new WindowsGuestCredentialStore().Load(vmId) ?? throw new InvalidOperationException("SavedGuestCredentialMissing");
            await HyperVGuestAdministration.RunAsync(vmId, saved, GuestAdministrationOperation.EnableAutoLogon, timeout.Token);
            Console.WriteLine("Owned guest auto-logon configuration applied; no restart performed.");
            return 0;
        }
        if (args.Length == 3 && args[2] == "--authenticated-rdp-pilot")
        {
            var clientExecutable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "FloatDesk.SessionClient", "bin", "Release", "net10.0-windows10.0.19041.0", "YueYuOrb.SessionClient.exe"));
            using var connection = await AuthenticatedRdpConnection.CreateAsync(vmId, 1280, 720, [], timeout.Token, clientExecutable);
            Console.WriteLine("Expected guest=" + connection.GuestComputerName + "; certificate fingerprint=" + connection.CertificateFingerprint);
            connection.StartInfo.WindowStyle = System.Diagnostics.ProcessWindowStyle.Normal;
            using var process = System.Diagnostics.Process.Start(connection.StartInfo) ?? throw new InvalidOperationException("RdpClientNotStarted");
            Console.WriteLine($"RDP client PID={process.Id}");
            process.OutputDataReceived += (_, entry) => { if (entry.Data?.StartsWith("Rdp", StringComparison.Ordinal) == true) Console.WriteLine(entry.Data); };
            process.ErrorDataReceived += (_, entry) => { if (entry.Data?.StartsWith("Rdp", StringComparison.Ordinal) == true) Console.WriteLine(entry.Data); };
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            try
            {
                using var saved = new WindowsGuestCredentialStore().Load(vmId)!;
                for (var i = 0; i < 40; i++)
                {
                    await Task.Delay(2000, timeout.Token);
                    if (process.HasExited) return 1;
                    var inventory = await HyperVGuestAdministration.RunAsync(vmId, saved, GuestAdministrationOperation.InspectSessions, timeout.Token);
                    var unlocked = inventory["GuestSessionInventory:".Length..].Split(';')
                        .Any(row => { var fields = row.Split(','); return fields.Length == 6 && fields[1] == "0" && fields[2] == "2" && fields[3] == "True" && fields[4] == "1" && fields[5] == "True"; });
                    Console.WriteLine($"Authenticated RDP unlocked desktop={unlocked}");
                    if (Environment.GetEnvironmentVariable("YUEYU_RDP_PILOT_SCREENSHOT") is { } capture)
                        Console.WriteLine($"Pilot window captured={PilotWindowCapture.Capture(process, capture)}; processExited={process.HasExited}");
                    if (unlocked) return 0;
                }
                return 1;
            }
            finally { if (!process.HasExited) { process.CloseMainWindow(); if (!process.WaitForExit(3000)) process.Kill(); } }
        }
        if (args.Length == 3 && args[2] == "--inspect-login-sessions")
        {
            using var saved = new WindowsGuestCredentialStore().Load(vmId) ?? throw new InvalidOperationException("SavedGuestCredentialMissing");
            Console.WriteLine(await HyperVGuestAdministration.RunAsync(vmId, saved, GuestAdministrationOperation.InspectSessions, timeout.Token));
            return 0;
        }
        if (args.Length == 3 && args[2] == "--check-saved-login")
        {
            using var saved = new WindowsGuestCredentialStore().Load(vmId);
            Console.WriteLine($"Saved guest credential present={saved is not null}");
            if (saved is null) return 1;
            var names = new[] { "auto-logon-enabled", "product-owned-auto-logon", "no-plaintext-password" };
            var checks = await HyperVGuestChecks.RunAsync(vmId, environmentId,
            [
                "if ((Get-ItemPropertyValue 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon' -Name AutoAdminLogon -ErrorAction Stop) -ne '1') { exit 1 }",
                "if (-not (Test-Path 'HKLM:\\SOFTWARE\\YueYuOrb\\AutoLogon')) { exit 1 }",
                "$p=Get-ItemProperty 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon'; if ($null -ne $p.DefaultPassword) { exit 1 }"
            ], timeout.Token);
            for (var i = 0; i < names.Length; i++) Console.WriteLine($"Guest check {names[i]}={checks[i]}");
            await HyperVGuestAdministration.RunAsync(vmId, saved, GuestAdministrationOperation.TestCredential, timeout.Token);
            Console.WriteLine("PowerShell Direct credential verified=True");
            return 0;
        }
        if (args.Length == 3 && args[2] == "--exercise-network-reconnect")
        {
            var operations = (IHyperVNetworkOperations)typeof(HyperVNetworkRepair).Assembly.CreateInstance(
                "FloatDesk.Windows.DesktopSessions.HyperVNetworkOperations")!;
            await operations.ReconnectAsync(vmId, timeout.Token);
            var status = await operations.CheckAsync(vmId, environmentId, timeout.Token);
            Console.WriteLine($"Production reconnect completed; healthy={status.Healthy}");
            return status.Healthy ? 0 : 1;
        }
        if (args.Length == 3 && args[2] == "--repair-network")
        {
            var status = await new HyperVNetworkRepair().RepairAsync(vmId, environmentId,
                new Progress<string>(message => Console.WriteLine(message)), timeout.Token);
            Console.WriteLine(status.Summary);
            return status.Healthy ? 0 : 1;
        }
        if (args.Length == 3 && args[2] == "--refresh-guest-ui")
        {
            await transport.AcquireInputLeaseAsync(handle, TimeSpan.FromSeconds(5), timeout.Token);
            try
            {
                var entry = new SessionStartupEntry(Guid.NewGuid(), "Refresh Windows connectivity",
                    @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
                    ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", @"C:\Users\Public\YueYu-Network-Refresh\refresh-guest-connectivity.ps1"],
                    null, 0, 0, true, false);
                var command = new SessionCommand(Guid.NewGuid(), handle, SessionCommandKind.LaunchApplication,
                    SessionStartupCommandCodec.ToParameters(entry), DateTimeOffset.UtcNow.AddSeconds(15));
                var response = await transport.SendAsync(handle, command, timeout.Token);
                Console.WriteLine($"Guest refresh launched={response.Succeeded}; error={response.ErrorCode ?? "none"}");
                return response.Succeeded ? 0 : 1;
            }
            finally { await transport.ReleaseInputLeaseAsync(handle, timeout.Token); }
        }
        if (args.Length == 3 && args[2] is "--network" or "--gpu" or "--health" or "--ncsi-refresh" or "--refresh-status")
        {
            await transport.AcquireInputLeaseAsync(handle, TimeSpan.FromSeconds(5), timeout.Token);
            try
            {
            var checks = args[2] == "--gpu" ? new Dictionary<string, string>
            {
                ["gpu-device"] = "if (@(Get-CimInstance Win32_VideoController | Where-Object { $_.Name -like '*RTX 4070 SUPER*' -and $_.ConfigManagerErrorCode -eq 0 }).Count -eq 0) { exit 1 }",
                ["gpu-driver"] = "if (@(Get-CimInstance Win32_VideoController | Where-Object { $_.Name -like '*RTX 4070 SUPER*' -and $_.DriverVersion -eq '32.0.16.2112' }).Count -eq 0) { exit 1 }",
                ["gpu-direct3d-nvidia"] = GpuHardwareProbe.Script,
                ["gpu-performance-counters"] = "if (@(Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine -OperationTimeoutSec 3 -ErrorAction Stop).Count -eq 0) { exit 1 }",
                ["agent-startup"] = "$v=Get-ItemPropertyValue 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Run' -Name YueYuGuestAgent -ErrorAction Stop; if ($v -notlike '*YueYuOrb.SessionAgent.exe*--hyperv-guest') { exit 1 }"
            } : args[2] == "--refresh-status" ? new Dictionary<string, string>
            {
                ["refresh-script-exists"] = "if (-not (Test-Path -LiteralPath 'C:\\Users\\Public\\YueYu-Network-Refresh\\refresh-guest-connectivity.ps1')) { exit 1 }",
                ["refresh-process-running"] = "if (@(Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'powershell.exe' -and $_.CommandLine -like '*-File*YueYu-Network-Refresh*refresh-guest-connectivity.ps1*' }).Count -eq 0) { exit 1 }",
                ["refresh-result-exists"] = "if (-not (Test-Path -LiteralPath 'C:\\Users\\Public\\YueYu-Network-Refresh\\result.json')) { exit 1 }",
                ["refresh-service-succeeded"] = "$r=Get-Content -LiteralPath 'C:\\Users\\Public\\YueYu-Network-Refresh\\result.json' -Raw | ConvertFrom-Json; if (-not $r.networkRefresh) { exit 1 }",
                ["refresh-internet"] = "$r=Get-Content -LiteralPath 'C:\\Users\\Public\\YueYu-Network-Refresh\\result.json' -Raw | ConvertFrom-Json; if (-not $r.internet) { exit 1 }",
                ["refresh-taskmanager-restarted"] = "$r=Get-Content -LiteralPath 'C:\\Users\\Public\\YueYu-Network-Refresh\\result.json' -Raw | ConvertFrom-Json; if (-not $r.taskManagerRestart) { exit 1 }",
                ["refresh-service-error"] = "$r=Get-Content -LiteralPath 'C:\\Users\\Public\\YueYu-Network-Refresh\\result.json' -Raw | ConvertFrom-Json; if ($r.errorType -notlike '*Service*') { exit 1 }",
                ["nla-can-stop"] = "if (-not (Get-Service NlaSvc).CanStop) { exit 1 }",
                ["network-list-running"] = "if ((Get-Service netprofm).Status -ne 'Running') { exit 1 }",
                ["network-profile-present"] = "if (@(Get-NetConnectionProfile -ErrorAction Stop).Count -eq 0) { exit 1 }"
            } : args[2] == "--ncsi-refresh" ? new Dictionary<string, string>
            {
                ["guest-dns-flush"] = "Clear-DnsClientCache -ErrorAction Stop",
                ["ncsi-dns-content"] = "$p=Get-ItemProperty 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\NlaSvc\\Parameters\\Internet'; if (@((Resolve-DnsName $p.ActiveDnsProbeHost -Type A -DnsOnly).IPAddress) -notcontains $p.ActiveDnsProbeContent) { exit 1 }",
                ["ncsi-http-content"] = "$p=Get-ItemProperty 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\NlaSvc\\Parameters\\Internet'; $r=Invoke-WebRequest ('http://'+$p.ActiveWebProbeHost+'/'+$p.ActiveWebProbePath) -UseBasicParsing -TimeoutSec 5; if ($r.StatusCode -ne 200 -or $r.Content.Trim() -ne $p.ActiveWebProbeContent) { exit 1 }",
                ["internet-profile"] = "for($i=0;$i -lt 12;$i++){ if (@(Get-NetConnectionProfile | Where-Object { $_.IPv4Connectivity -eq 'Internet' -or $_.IPv6Connectivity -eq 'Internet' }).Count -gt 0){exit 0}; Start-Sleep -Seconds 1 }; exit 1"
            } : args[2] == "--health" ? new Dictionary<string, string>
            {
                ["ncsi-active-probing"] = "$p=Get-ItemProperty 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\NlaSvc\\Parameters\\Internet'; if ($p.EnableActiveProbing -ne 1) { exit 1 }",
                ["ncsi-policy-allows"] = "$p=Get-ItemProperty 'HKLM:\\SOFTWARE\\Policies\\Microsoft\\Windows\\NetworkConnectivityStatusIndicator' -ErrorAction SilentlyContinue; if ($p.NoActiveProbe -eq 1) { exit 1 }",
                ["nla-service"] = "if ((Get-Service NlaSvc).Status -ne 'Running') { exit 1 }",
                ["internet-profile"] = "if (@(Get-NetConnectionProfile | Where-Object { $_.IPv4Connectivity -eq 'Internet' -or $_.IPv6Connectivity -eq 'Internet' }).Count -eq 0) { exit 1 }",
                ["ncsi-http-content"] = "$p=Get-ItemProperty 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\NlaSvc\\Parameters\\Internet'; $r=Invoke-WebRequest ('http://'+$p.ActiveWebProbeHost+'/'+$p.ActiveWebProbePath) -UseBasicParsing -TimeoutSec 5; if ($r.StatusCode -ne 200 -or $r.Content.Trim() -ne $p.ActiveWebProbeContent) { exit 1 }",
                ["ncsi-dns-content"] = "$p=Get-ItemProperty 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\NlaSvc\\Parameters\\Internet'; if (@((Resolve-DnsName $p.ActiveDnsProbeHost -Type A -DnsOnly).IPAddress) -notcontains $p.ActiveDnsProbeContent) { exit 1 }",
                ["ncsi-dns-defaults"] = "$p=Get-ItemProperty 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\NlaSvc\\Parameters\\Internet'; if ($p.ActiveDnsProbeHost -ne 'dns.msftncsi.com' -or $p.ActiveDnsProbeContent -ne '131.107.255.255') { exit 1 }",
                ["ncsi-dns-is-fakeip"] = "$p=Get-ItemProperty 'HKLM:\\SYSTEM\\CurrentControlSet\\Services\\NlaSvc\\Parameters\\Internet'; if (@((Resolve-DnsName $p.ActiveDnsProbeHost -Type A -DnsOnly).IPAddress | Where-Object { $_ -match '^198\\.(18|19)\\.' }).Count -eq 0) { exit 1 }",
                ["ncsi-dns-direct"] = "if (@((Resolve-DnsName dns.msftncsi.com -Type A -DnsOnly -Server 1.1.1.1 -QuickTimeout -ErrorAction Stop).IPAddress) -notcontains '131.107.255.255') { exit 1 }",
                ["gpu-perf-library"] = "if (@(Get-ChildItem \"$env:SystemRoot\\System32\" -Filter '*gpu*perf*.dll' -ErrorAction Stop).Count -eq 0) { exit 1 }",
                ["gpu-counter-class"] = "Get-CimClass -ClassName Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine -OperationTimeoutSec 3 -ErrorAction Stop | Out-Null",
                ["cpu-counters"] = "if (@(Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter \"Name='_Total'\" -OperationTimeoutSec 3).Count -eq 0) { exit 1 }",
                ["guest-admin-token"] = "if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { exit 1 }"
            } : new Dictionary<string, string>
            {
                ["route"] = "if (@(Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction Stop).Count -eq 0) { exit 1 }",
                ["dns"] = "Resolve-DnsName www.msftconnecttest.com -DnsOnly -ErrorAction Stop | Out-Null",
                ["http"] = "$r=Invoke-WebRequest 'http://www.msftconnecttest.com/connecttest.txt' -UseBasicParsing -TimeoutSec 3 -ErrorAction Stop; if ($r.StatusCode -ne 200) { exit 1 }"
            };
            foreach (var (name, script) in checks)
            {
                await transport.AcquireInputLeaseAsync(handle, TimeSpan.FromSeconds(5), timeout.Token);
                var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes("$ErrorActionPreference='Stop'; " + script));
                var command = new SessionCommand(Guid.NewGuid(), handle, SessionCommandKind.RunScript,
                    new Dictionary<string, string> { ["scriptPath"] = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", ["arguments"] = System.Text.Json.JsonSerializer.Serialize(new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded }) }, DateTimeOffset.UtcNow.AddSeconds(18));
                var pending = transport.SendAsync(handle, command, timeout.Token);
                while (!pending.IsCompleted)
                {
                    if (await Task.WhenAny(pending, Task.Delay(1000, timeout.Token)) == pending) break;
                    await transport.RenewInputLeaseAsync(handle, timeout.Token);
                }
                var response = await pending;
                Console.WriteLine($"Guest check {name}: succeeded={response.Succeeded}; error={response.ErrorCode ?? "none"}");
            }
            return 0;
            }
            finally { await transport.ReleaseInputLeaseAsync(handle, timeout.Token); }
        }
        await transport.AcquireInputLeaseAsync(handle, TimeSpan.FromSeconds(5), timeout.Token);
        try
        {
            var command = new SessionCommand(Guid.NewGuid(), handle, SessionCommandKind.Delay,
                new Dictionary<string, string> { ["milliseconds"] = "125" }, DateTimeOffset.UtcNow.AddSeconds(5));
            var response = await transport.SendAsync(handle, command, timeout.Token);
            Console.WriteLine($"Guest delay command: succeeded={response.Succeeded}; unknown={response.OutcomeUnknown}; error={response.ErrorCode ?? "none"}");
            return response.Succeeded && !response.OutcomeUnknown ? 0 : 1;
        }
        finally { await transport.ReleaseInputLeaseAsync(handle, timeout.Token); }
    }
    return 0;
}
catch (Exception exception)
{
    Console.WriteLine($"Guest probe failed: {exception}");
    return 1;
}
