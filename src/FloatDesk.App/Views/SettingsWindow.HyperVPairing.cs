using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using FloatDesk.Core.Models;
using FloatDesk.Windows.DesktopSessions;

namespace FloatDesk.App.Views;

public partial class SettingsWindow
{
    private async Task ExportGuestInstallerAsync()
    {
        try { await ExportGuestInstallerCoreAsync(); }
        catch (Exception exception) { _hyperVVmStatus.Text = $"导出初始化包失败：{exception.Message}"; }
    }

    private async Task ExportGuestInstallerCoreAsync()
    {
        if (_hyperVVmList.SelectedItem is not HyperVVirtualMachine vm)
        { _hyperVVmStatus.Text = "请先选择虚拟机。"; return; }
        var environment = _viewModel.DesktopSessionEnvironments.FirstOrDefault(item =>
            item.Backend == DesktopSessionBackendKind.HyperV && Guid.TryParse(item.BackendProfileId, out var id) && id == vm.Id);
        if (environment is null)
        { _hyperVVmStatus.Text = "请先添加并保存关联此 VM ID 的 Hyper-V 环境。"; return; }
        var payload = Path.Combine(AppContext.BaseDirectory, "desktop-sessions", "agent");
        var exe = Path.Combine(payload, "YueYuOrb.SessionAgent.exe");
        if (!File.Exists(exe) || !File.Exists(exe + ".sha256"))
        { _hyperVVmStatus.Text = "当前目录缺少已发布的 Agent 包及校验清单，请使用完整测试发布包。"; return; }
        var expected = (await File.ReadAllTextAsync(exe + ".sha256")).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
        using (var input = File.OpenRead(exe))
            if (!Convert.ToHexString(SHA256.HashData(input)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Agent 包校验失败。");
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择来宾初始化包导出目录" };
        if (dialog.ShowDialog(this) != true) return;
        if (MessageBox.Show(this, "初始化包包含专用于此虚拟机的配对密钥。请仅复制到该来宾系统运行，并在配对成功后删除导出包。继续？",
                "导出来宾初始化包", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes) return;
        var destination = Path.Combine(dialog.FolderName, "YueYu-Guest-" + Guid.NewGuid().ToString("N"));
        var secret = RandomNumberGenerator.GetBytes(32);
        var store = new DpapiHyperVPairingSecretStore();
        var existing = await store.LoadAsync(vm.Id, CancellationToken.None);
        if (existing is not null) { CryptographicOperations.ZeroMemory(secret); secret = existing; }
        try
        {
            Directory.CreateDirectory(destination);
            var entries = new List<object>();
            foreach (var file in Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(payload, file);
                var output = Path.Combine(destination, "agent", relative);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.Copy(file, output);
                using var input = File.OpenRead(output);
                entries.Add(new { Path = relative, Hash = Convert.ToHexString(SHA256.HashData(input)) });
            }
            await File.WriteAllTextAsync(Path.Combine(destination, "manifest.json"), JsonSerializer.Serialize(entries));
            await File.WriteAllTextAsync(Path.Combine(destination, "bootstrap.json"), JsonSerializer.Serialize(new { VmId = vm.Id, EnvironmentId = environment.Id, Secret = secret }));
            await File.WriteAllTextAsync(Path.Combine(destination, "Install.ps1"), HyperVGuestInstaller.BuildInstallScript(), new System.Text.UTF8Encoding(true));
            await File.WriteAllTextAsync(Path.Combine(destination, "Install.cmd"), "@echo off\r\npowershell.exe -NoProfile -ExecutionPolicy Bypass -File \"%~dp0Install.ps1\"\r\npause\r\n");
            await store.SaveAsync(vm.Id, secret, CancellationToken.None);
            _hyperVVmStatus.Text = $"已导出：{destination}。复制整个目录到来宾，双击 Install.cmd，完成后点击“测试 Agent 配对”。";
        }
        catch (Exception exception) { _hyperVVmStatus.Text = $"导出失败：{exception.Message}"; }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    private async Task TestGuestPairingAsync()
    {
        if (_hyperVVmList.SelectedItem is not HyperVVirtualMachine vm)
        { _hyperVVmStatus.Text = "请先选择虚拟机。"; return; }
        try
        {
            var identity = await new HyperVGuestAgentProbe(new DpapiHyperVPairingSecretStore()).ProbeAsync(vm.Id, CancellationToken.None);
            _hyperVVmStatus.Text = $"Agent 身份验证通过，来宾交互会话 {identity.WindowsSessionId}。可启动环境验证任务传输。";
        }
        catch (Exception exception) { _hyperVVmStatus.Text = $"Agent 配对未就绪：{exception.Message}"; }
    }

    private async Task RegisterGuestChannelAsync()
    {
        try
        {
            var script = $"$ErrorActionPreference='Stop'; $path='HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Virtualization\\GuestCommunicationServices\\{HyperVGuestDiscovery.ServiceId:D}'; New-Item -Path $path -Force | Out-Null; Set-ItemProperty -Path $path -Name ElementName -Value 'YueYu Guest Agent'";
            var command = new System.Diagnostics.ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                UseShellExecute = true, Verb = "runas", WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };
            command.ArgumentList.Add("-NoProfile");
            command.ArgumentList.Add("-NonInteractive");
            command.ArgumentList.Add("-EncodedCommand");
            command.ArgumentList.Add(Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)));
            using var process = System.Diagnostics.Process.Start(command) ?? throw new InvalidOperationException("无法启动通道注册。");
            await process.WaitForExitAsync();
            _hyperVVmStatus.Text = process.ExitCode == 0 ? "来宾通信通道已注册，可以测试 Agent 配对。" : "通道注册失败，请重试管理员授权。";
        }
        catch (Exception exception) { _hyperVVmStatus.Text = $"通道注册失败：{exception.Message}"; }
    }
}
