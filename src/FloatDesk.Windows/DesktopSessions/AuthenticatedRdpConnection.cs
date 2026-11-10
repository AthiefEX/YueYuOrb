using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Win32;

namespace FloatDesk.Windows.DesktopSessions;

public sealed record GuestRdpEndpoint(string Address, string HostAddress, string ComputerName, string CertificateHash, string CertificatePublicKey);

/// <summary>VM-bound loopback relay for an isolated Windows RDP control process.</summary>
public sealed class AuthenticatedRdpConnection : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _server;
    private readonly string _owner = "YueYuOrb.Rdp." + Guid.NewGuid().ToString("N");
    private Task? _relay;
    private bool _disposed;
    public ProcessStartInfo StartInfo { get; }
    public string CertificateFingerprint { get; }
    public string GuestComputerName { get; }

    private AuthenticatedRdpConnection(Guid vmId, GuestRdpEndpoint endpoint, GuestCredential credential, int width, int height, IReadOnlyList<string> drives, string clientExecutable)
    {
        if (!IPAddress.TryParse(endpoint.Address, out var guestAddress) || guestAddress.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(guestAddress) ||
            !IPAddress.TryParse(endpoint.HostAddress, out var hostAddress) || hostAddress.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(hostAddress) ||
            endpoint.CertificateHash.Length != 40 || !endpoint.CertificateHash.All(char.IsAsciiHexDigit))
            throw new InvalidOperationException("已认证来宾返回了无效的 RDP 端点。");
        var identityHash = System.Security.Cryptography.SHA256.HashData(vmId.ToByteArray());
        var loopback = new IPAddress(new byte[] { 127, (byte)(identityHash[0] % 254 + 1), identityHash[1], (byte)(identityHash[2] % 254 + 1) });
        _listener = new TcpListener(loopback, 0);
        _listener.Start(1);
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _server = loopback.ToString();
        CertificateFingerprint = endpoint.CertificateHash; GuestComputerName = endpoint.ComputerName;
        try
        {
            using var existing = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Terminal Server Client\Servers\" + _server);
            if (existing is not null && existing.GetValue("YueYuVmId") as string != vmId.ToString("D"))
                throw new InvalidOperationException("连接目标已有其他来源的证书配置，未覆盖。");
            using var pin = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Terminal Server Client\Servers\" + _server);
            pin.SetValue("YueYuOwner", _owner);
            pin.SetValue("YueYuVmId", vmId.ToString("D"));
            // Reference fingerprint for the user's Windows certificate dialog.
            // Windows alone writes CertHash after explicit user confirmation.
            pin.SetValue("YueYuExpectedCertificateHash", endpoint.CertificateHash);
            ValidateViewSettings(port, credential.UserName, endpoint.ComputerName, width, height, drives);
            if (!Path.IsPathFullyQualified(clientExecutable) || !File.Exists(clientExecutable)) throw new InvalidOperationException("缺少认证桌面客户端，请使用完整发布包。");
            StartInfo = new ProcessStartInfo
            {
                FileName = clientExecutable,
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in new[] { "--rdp-view", vmId.ToString("D"), _server, port.ToString(CultureInfo.InvariantCulture), endpoint.ComputerName,
                width.ToString(CultureInfo.InvariantCulture), height.ToString(CultureInfo.InvariantCulture), string.Join(';', drives), _owner })
                StartInfo.ArgumentList.Add(argument);
            _relay = RelayAsync(guestAddress, hostAddress);
        }
        catch { Dispose(); throw; }
    }

    public static async Task<AuthenticatedRdpConnection> CreateAsync(Guid vmId, int width, int height, IReadOnlyList<string> drives, CancellationToken cancellationToken, string? clientExecutable = null)
    {
        using var credential = new WindowsGuestCredentialStore().Load(vmId) ?? throw new InvalidOperationException("请先测试并保存此虚拟机的来宾凭据。");
        var text = await HyperVGuestAdministration.RunAsync(vmId, credential, GuestAdministrationOperation.PrepareAuthenticatedRdp, cancellationToken).ConfigureAwait(false);
        const string prefix = "GuestRdpEndpoint:";
        if (!text.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidOperationException("来宾 RDP 端点未准备好。");
        var endpoint = JsonSerializer.Deserialize<GuestRdpEndpoint>(text[prefix.Length..]) ?? throw new InvalidOperationException("来宾 RDP 端点缺失。");
        var scope = await HyperVRdpHostScope.VerifyAsync(vmId, cancellationToken).ConfigureAwait(false);
        if (endpoint.HostAddress != scope.HostAddress) throw new InvalidOperationException("主机接口地址发生变化，请重试。");
        return new AuthenticatedRdpConnection(vmId, endpoint, credential, width, height, drives,
            clientExecutable ?? Path.Combine(AppContext.BaseDirectory, "desktop-sessions", "client", "YueYuOrb.SessionClient.exe"));
    }

    public static void ValidateViewSettings(int port, string user, string computer, int width, int height, IReadOnlyList<string> drives)
    {
        static bool ValidName(string value) => value.Length is > 0 and <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ' ');
        if (port is < 1024 or > 65535 || !ValidName(user) || !ValidName(computer) || width is < 800 or > 3840 || height is < 600 or > 2160 ||
            drives.Any(d => d.Length != 3 || !char.IsAsciiLetter(d[0]) || d[1] != ':' || d[2] != '\\'))
            throw new ArgumentException("Invalid RDP display, drive or account metadata.");
    }

    // The installed RDP control includes a trailing NUL in drive-name BSTRs.
    // Strip only terminators; never accept a prefix or a directory as a drive.
    public static bool MatchesRedirectedDrive(string nativeName, string selectedRoot) =>
        nativeName.TrimEnd('\0').Length == 3 && selectedRoot.Length == 3 &&
        char.IsAsciiLetter(selectedRoot[0]) && selectedRoot[1] == ':' && selectedRoot[2] == '\\' &&
        string.Equals(nativeName.TrimEnd('\0'), selectedRoot, StringComparison.OrdinalIgnoreCase);

    private async Task RelayAsync(IPAddress guest, IPAddress host)
    {
        var token = _lifetime.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var local = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                using var remote = new TcpClient(AddressFamily.InterNetwork);
                remote.Client.Bind(new IPEndPoint(host, 0));
                await remote.ConnectAsync(guest, 3389, token).ConfigureAwait(false);
                using var input = local.GetStream(); using var output = remote.GetStream();
                var toGuest = input.CopyToAsync(output, token); var fromGuest = output.CopyToAsync(input, token);
                await Task.WhenAny(toGuest, fromGuest).ConfigureAwait(false);
                local.Close(); remote.Close();
                try { await Task.WhenAll(toGuest, fromGuest).ConfigureAwait(false); }
                catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException) { }
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel(); _listener.Stop();
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Terminal Server Client\Servers\" + _server))
        {
            if (key?.GetValue("YueYuOwner") as string == _owner)
            {
                using var writable = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Terminal Server Client\Servers\" + _server, writable: true);
                writable?.DeleteValue("YueYuOwner", throwOnMissingValue: false);
            }
        }
        if (_relay is null || _relay.IsCompleted) _lifetime.Dispose();
        else _ = _relay.ContinueWith(_ => _lifetime.Dispose(), TaskScheduler.Default);
    }
}
