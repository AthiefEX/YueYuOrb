using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace FloatDesk.Windows.DesktopSessions;

public sealed class HyperVAutomaticInstaller
{
    public static async Task<Guid> InstallAsync(HyperVVmCreateRequest request, int imageIndex, string userName,
        string password, string agentDirectory, IProgress<string> progress, bool startAfterDeploy = true)
    {
        HyperVVirtualMachineManager.ValidateCreate(request);
        if (request.TemplatePath is not null || imageIndex < 1) throw new ArgumentException("Choose an ISO image and valid image index.");
        var agentExe = Path.Combine(agentDirectory, "YueYuOrb.SessionAgent.exe");
        var expected = (await File.ReadAllTextAsync(agentExe + ".sha256").ConfigureAwait(false)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
        using (var file = File.OpenRead(agentExe))
            if (!Convert.ToHexString(SHA256.HashData(file)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Agent payload verification failed.");
        var xml = XDocument.Parse(HyperVUnattendedMediaBuilder.BuildAnswerFile(userName, password, "zh-CN"));
        XNamespace ns = "urn:schemas-microsoft-com:unattend";
        XNamespace wcm = "http://schemas.microsoft.com/WMIConfig/2002/State";
        var shell = xml.Descendants(ns + "settings").Single(e => (string?)e.Attribute("pass") == "oobeSystem")
            .Elements(ns + "component").Single(e => (string?)e.Attribute("name") == "Microsoft-Windows-Shell-Setup");
        shell.Add(new XElement(ns + "FirstLogonCommands", new XElement(ns + "SynchronousCommand", new XAttribute(wcm + "action", "add"),
            new XElement(ns + "Order", "1"), new XElement(ns + "Description", "Initialize YueYu guest Agent"),
            new XElement(ns + "CommandLine", @"powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File C:\ProgramData\YueYuBootstrap\Install.ps1"))));
        var secret = RandomNumberGenerator.GetBytes(32);
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false)
            };
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-EncodedCommand"); start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(Script)));
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start offline installation.");
            var errors = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new
            {
                request.Name, request.StorageDirectory, request.IsoPath, request.ProcessorCount,
                request.MemoryStartupMiB, request.DiskSizeGiB, request.SwitchName,
                ImageIndex = imageIndex, AnswerXml = xml.ToString(), AgentDirectory = agentDirectory,
                Installer = HyperVGuestInstaller.BuildInstallScript(), request.EnvironmentId, Secret = secret,
                StartAfterDeploy = startAfterDeploy
            })).ConfigureAwait(false);
            process.StandardInput.Close();
            Guid vmId = Guid.Empty;
            var lastStage = "初始化安装脚本";
            string? failure = null;
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.StartsWith("VM:", StringComparison.Ordinal) && Guid.TryParse(line.AsSpan(3), out var id)) vmId = id;
                else if (line.StartsWith("STAGE:", StringComparison.Ordinal)) { lastStage = line[6..]; progress.Report(lastStage); }
                else if (line.StartsWith("PROGRESS:", StringComparison.Ordinal)) progress.Report(line);
                else if (line.StartsWith("FAILURE:", StringComparison.Ordinal))
                {
                    using var detail = JsonDocument.Parse(line[8..]);
                    failure = $"阶段：{detail.RootElement.GetProperty("stage").GetString()}；错误代码：{detail.RootElement.GetProperty("code").GetString()}；脚本行：{detail.RootElement.GetProperty("line").GetInt32()}";
                }
            }
            await process.WaitForExitAsync().ConfigureAwait(false);
            _ = await errors.ConfigureAwait(false);
            if (process.ExitCode != 0 || vmId == Guid.Empty)
                throw new InvalidOperationException(failure ?? $"自动安装失败，最后阶段：{lastStage}；进程退出码：{process.ExitCode}。");
            await new DpapiHyperVPairingSecretStore().SaveAsync(vmId, secret, CancellationToken.None).ConfigureAwait(false);
            return vmId;
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    public const string Script = """
        $ErrorActionPreference='Stop'
        [Console]::InputEncoding = New-Object System.Text.UTF8Encoding($false)
        [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
        $ProgressPreference='SilentlyContinue'
        $config = [Console]::In.ReadToEnd() | ConvertFrom-Json
        Import-Module Hyper-V -ErrorAction Stop
        Import-Module Dism -ErrorAction Stop
        $vhd = Join-Path $config.StorageDirectory 'system.vhdx'
        $vm=$null; $mounted=$false; $isoMounted=$false; $diskCreated=$false; $success=$false
        $mountRoot=Join-Path $env:ProgramData ('YueYuOrb\Mounts\' + [guid]::NewGuid().ToString('N'))
        $osRoot=Join-Path $mountRoot 'WindowsVolume'
        $isoRoot=Join-Path $mountRoot 'IsoVolume'
        $isoMapped=$false
        $stage='初始化'
        function Report-Stage($text) { $script:stage=$text; Write-Output ('STAGE:' + $text) }
        try {
            Report-Stage '预检 ISO、交换机和目标目录'
            if (Test-Path -LiteralPath $config.StorageDirectory) { throw 'TargetDirectoryAlreadyExists' }
            Get-VMSwitch -Name $config.SwitchName -ErrorAction Stop | Out-Null
            $service='HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization\GuestCommunicationServices\901aa695-6edb-4ec7-b30b-e8d29b10c321'
            New-Item -Path $service -Force | Out-Null
            Set-ItemProperty -Path $service -Name ElementName -Value 'YueYu Guest Agent'
            if (Get-VM -Name $config.Name -ErrorAction SilentlyContinue) { throw 'VmAlreadyExists' }
            $iso = Get-DiskImage -ImagePath $config.IsoPath -ErrorAction Stop
            if (-not $iso.Attached) { Mount-DiskImage -ImagePath $config.IsoPath -NoDriveLetter -ErrorAction Stop | Out-Null; $isoMounted=$true }
            $volume = Get-DiskImage -ImagePath $config.IsoPath | Get-Volume
            New-Item -ItemType Directory -Path $isoRoot -Force | Out-Null
            & "$env:SystemRoot\System32\mountvol.exe" ($isoRoot + '\') $volume.Path
            if ($LASTEXITCODE -ne 0) { throw 'IsoDirectoryMountFailed' }
            $isoMapped=$true
            $sources = Join-Path $isoRoot 'sources'
            $image = Join-Path $sources 'install.wim'
            if (-not (Test-Path -LiteralPath $image)) { $image=Join-Path $sources 'install.esd' }
            Report-Stage '校验 Windows 镜像索引'
            Get-WindowsImage -ImagePath $image -Index $config.ImageIndex -ErrorAction Stop | Out-Null
            Report-Stage '创建并格式化新虚拟磁盘'
            New-Item -ItemType Directory -Path $config.StorageDirectory -ErrorAction Stop | Out-Null
            New-VHD -Path $vhd -SizeBytes ([int64]$config.DiskSizeGiB * 1GB) -Dynamic -ErrorAction Stop | Out-Null
            $diskCreated=$true
            $disk = Mount-VHD -Path $vhd -NoDriveLetter -Passthru -ErrorAction Stop | Get-Disk
            $mounted=$true
            if ($disk.PartitionStyle -ne 'RAW') { throw 'UnexpectedDiskLayout' }
            Initialize-Disk -Number $disk.Number -PartitionStyle GPT -ErrorAction Stop | Out-Null
            $efi=New-Partition -DiskNumber $disk.Number -Size 260MB -GptType '{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}'
            $efi | Format-Volume -FileSystem FAT32 -Confirm:$false -Force | Out-Null
            New-Partition -DiskNumber $disk.Number -Size 16MB -GptType '{e3c9e316-0b5c-4db8-817d-f92df00215ae}' | Out-Null
            $os=New-Partition -DiskNumber $disk.Number -UseMaximumSize
            $os | Format-Volume -FileSystem NTFS -Confirm:$false -Force | Out-Null
            New-Item -ItemType Directory -Path $osRoot -Force | Out-Null
            $os | Add-PartitionAccessPath -AccessPath ($osRoot + '\') -ErrorAction Stop
            Report-Stage '正在应用 Windows 镜像，此阶段可能需要较长时间'
            & "$env:SystemRoot\System32\dism.exe" /English /Apply-Image ("/ImageFile:" + $image) ("/Index:" + $config.ImageIndex) ("/ApplyDir:" + $osRoot) /CheckIntegrity 2>&1 | ForEach-Object {
                $matches=[regex]::Matches([string]$_,'(\d+(?:\.\d+)?)%')
                if ($matches.Count -gt 0) { Write-Output ('PROGRESS:' + $matches[$matches.Count-1].Groups[1].Value) }
            }
            if ($LASTEXITCODE -ne 0) { throw 'ImageApplicationFailed' }
            Report-Stage '写入 UEFI 启动文件'
            $efi | Add-PartitionAccessPath -AssignDriveLetter -ErrorAction Stop
            $efi = Get-Partition -DiskNumber $disk.Number -PartitionNumber $efi.PartitionNumber
            & "$env:SystemRoot\System32\bcdboot.exe" (Join-Path $osRoot 'Windows') /s ([string]$efi.DriveLetter + ':') /f UEFI | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'BootFilesFailed' }
            $efi | Remove-PartitionAccessPath -AccessPath ([string]$efi.DriveLetter + ':\') -ErrorAction Stop
            Report-Stage '创建虚拟机并配置首次登录'
            $vm=New-VM -Name $config.Name -Generation 2 -MemoryStartupBytes ([int64]$config.MemoryStartupMiB * 1MB) -VHDPath $vhd -Path $config.StorageDirectory -SwitchName $config.SwitchName
            Set-VM -VM $vm -ProcessorCount $config.ProcessorCount -AutomaticCheckpointsEnabled $false -Notes 'YueYuOrb:managed' -Confirm:$false
            Set-VMFirmware -VM $vm -FirstBootDevice (Get-VMHardDiskDrive -VM $vm | Select-Object -First 1)
            $bootstrap=Join-Path $osRoot 'ProgramData\YueYuBootstrap'
            New-Item -ItemType Directory -Path $bootstrap -Force | Out-Null
            $payload=Join-Path $bootstrap 'agent'
            Copy-Item -LiteralPath $config.AgentDirectory -Destination $payload -Recurse -ErrorAction Stop
            $manifest=@(Get-ChildItem -LiteralPath $payload -File -Recurse | ForEach-Object { [pscustomobject]@{ Path=$_.FullName.Substring($payload.Length).TrimStart('\'); Hash=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash } })
            ConvertTo-Json -InputObject $manifest -Compress | Set-Content -LiteralPath (Join-Path $bootstrap 'manifest.json') -Encoding UTF8
            [pscustomobject]@{ VmId=$vm.Id; EnvironmentId=$config.EnvironmentId; Secret=$config.Secret } | ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $bootstrap 'bootstrap.json') -Encoding UTF8
            $cleanup = @'
        $completion='HKLM:\SOFTWARE\YueYuOrb\Bootstrap'
        if ((Get-ItemProperty -Path $completion -Name Completed -ErrorAction SilentlyContinue).Completed -eq 1) { return }
        $panther = Join-Path $env:SystemRoot 'Panther'
        foreach ($file in @((Join-Path $panther 'unattend.xml'), (Join-Path $panther 'Unattend\unattend.xml'))) {
            if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }
        }
        $winlogon='HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
        Set-ItemProperty -Path $winlogon -Name AutoAdminLogon -Value '0'
        Remove-ItemProperty -Path $winlogon -Name DefaultPassword -ErrorAction SilentlyContinue
        Remove-ItemProperty -Path $winlogon -Name AutoLogonCount -ErrorAction SilentlyContinue
        New-Item -Path $completion -Force | Out-Null
        Set-ItemProperty -Path $completion -Name Completed -Value 1
        '@
            ('& {' + [Environment]::NewLine + $config.Installer + [Environment]::NewLine + '}' + [Environment]::NewLine + $cleanup) | Set-Content -LiteralPath (Join-Path $bootstrap 'Install.ps1') -Encoding UTF8
            $panther=Join-Path $osRoot 'Windows\Panther'
            New-Item -ItemType Directory -Path $panther -Force | Out-Null
            $config.AnswerXml | Set-Content -LiteralPath (Join-Path $panther 'unattend.xml') -Encoding UTF8
            Dismount-VHD -Path $vhd -ErrorAction Stop; $mounted=$false
            Report-Stage 'Windows 已部署，正在准备首次初始化'
            if ($config.StartAfterDeploy) { Start-VM -VM $vm -Confirm:$false -ErrorAction Stop }
            $success=$true
            Write-Output ('VM:' + $vm.Id)
        } catch {
            Write-Output ('FAILURE:' + ([pscustomobject]@{ stage=$stage; code=$_.FullyQualifiedErrorId; line=$_.InvocationInfo.ScriptLineNumber } | ConvertTo-Json -Compress))
            Write-Output 'STAGE:安装失败，正在清理本次新建资源'
            exit 1
        }
        finally {
            if ($mounted) { Dismount-VHD -Path $vhd -ErrorAction Stop }
            if (Test-Path -LiteralPath $osRoot) { [IO.Directory]::Delete($osRoot) }
            if ($isoMapped) { & "$env:SystemRoot\System32\mountvol.exe" $isoRoot /D; if ($LASTEXITCODE -ne 0) { throw 'IsoDirectoryUnmountFailed' } }
            if (Test-Path -LiteralPath $isoRoot) { [IO.Directory]::Delete($isoRoot) }
            if (Test-Path -LiteralPath $mountRoot) { [IO.Directory]::Delete($mountRoot) }
            if ($isoMounted) { Dismount-DiskImage -ImagePath $config.IsoPath -ErrorAction Stop }
            if (-not $success) {
                if ($null -ne $vm) { if ($vm.State -eq 'Off') { Remove-VM -VM $vm -Force -Confirm:$false -ErrorAction Stop } else { throw 'NewVmStillRunning' } }
                if ($diskCreated -and (Test-Path -LiteralPath $vhd)) { Remove-Item -LiteralPath $vhd -Force -ErrorAction Stop }
            }
        }
        """;
}
