using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using FloatDesk.Core.Models;
using Microsoft.Win32.SafeHandles;

namespace FloatDesk.Windows.Network;

public sealed class SteamBackgroundServer : IDisposable
{
    private readonly ConcurrentDictionary<NamedPipeServerStream, Task> _clients = new();
    private readonly object _authorityLock = new();
    private int _leases;
    private SteamBackgroundAuthority? _authority;
    private static string LeasePath => Path.Combine(SteamBackgroundInstallation.DirectoryPath, "active-lease.json");
    private sealed record LeaseRecord(Guid Owner);

    public async Task RunAsync(CancellationToken token)
    {
        var registration = JsonSerializer.Deserialize<SteamBackgroundInstallation.Registration>(File.ReadAllText(SteamBackgroundInstallation.ConfigurationPath)) ?? throw new IOException("后台登记缺失。");
        try
        {
            _authority = new();
            RecoverOutstandingLease();
            while (!token.IsCancellationRequested)
            {
                var pipe = CreatePipe(registration.Sid);
                try { await pipe.WaitForConnectionAsync(token).ConfigureAwait(false); }
                catch { pipe.Dispose(); throw; }
                if (_clients.Count >= 16) { pipe.Dispose(); continue; }
                _clients[pipe] = HandleAsync(pipe, registration, token);
                foreach (var entry in _clients.Where(entry => entry.Value.IsCompleted)) _clients.TryRemove(entry.Key, out _);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            foreach (var pipe in _clients.Keys) pipe.Dispose();
            await Task.WhenAll(_clients.Values).ConfigureAwait(false);
            _authority?.Dispose();
            _authority = null;
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, SteamBackgroundInstallation.Registration registration, CancellationToken token)
    {
        await Task.Yield();
        using (pipe)
        using (var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, true))
        using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true })
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var stage = "message";
            try
            {
                var message = await SteamBackgroundClient.ReadMessageAsync(reader, timeout.Token).ConfigureAwait(false);
                stage = "process";
                // Bind authorization to the OS-reported local pipe PID and its real process token.
                // Do not impersonate a low-privilege peer while lazily loading protected service assemblies.
                if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) || !SteamBackgroundInstallation.VerifyClient((int)pid, registration)) throw new UnauthorizedAccessException();
                var request = JsonSerializer.Deserialize<SteamBackgroundRequest>(message);
                if (request is null || !SteamBackgroundProtocol.IsAllowed(request.Command, request.Mask, request.Host)) throw new UnauthorizedAccessException();
                if (request.Command == "LEASE")
                {
                    timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                    await Task.Run(() => Lease(reader, writer, request.Mask, (int)pid, token), token).ConfigureAwait(false);
                    return;
                }
                SteamBackgroundReply reply;
                stage = "certificate";
                lock (_authorityLock)
                {
                    if (_leases == 0 && _authority!.Certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow.AddDays(7))
                    { _authority.Dispose(); _authority = new(); }
                    if (request.Command == "STATUS")
                        reply = new(_authority!.IsTrusted(), _authority.IsTrusted() ? "Ready" : "CertificateMissing", Convert.ToBase64String(_authority.Certificate.RawData), _authority.Certificate.Thumbprint);
                    else
                    {
                        if (request.Csr is null || request.Thumbprint != _authority!.Certificate.Thumbprint || !_authority.IsTrusted()) throw new IOException("证书状态已变化。");
                        using var certificate = _authority.Sign(request.Host!, request.Csr);
                        reply = new(true, "Signed", Convert.ToBase64String(certificate.RawData), _authority.Certificate.Thumbprint);
                    }
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(reply).AsMemory(), timeout.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or CryptographicException or OperationCanceledException or ObjectDisposedException or ArgumentException or AggregateException or System.ComponentModel.Win32Exception)
            {
                try { await writer.WriteLineAsync(JsonSerializer.Serialize(new SteamBackgroundReply(false, error is UnauthorizedAccessException ? "NotAuthorized" : "RequestFailed-" + stage + "-" + error.GetType().Name + "-" + error.HResult)).AsMemory(), token).ConfigureAwait(false); }
                catch (Exception sendError) when (sendError is IOException or ObjectDisposedException or OperationCanceledException) { }
            }
        }
    }

    private void Lease(StreamReader reader, StreamWriter writer, int mask, int pid, CancellationToken token)
    {
        using var mutex = new Mutex(false, "Global\\YueYuOrb.SteamHostsLease");
        var held = false; var ready = false; var owner = Guid.NewGuid();
        try
        {
            try { held = mutex.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
            if (!held) { writer.WriteLine(JsonSerializer.Serialize(new SteamBackgroundReply(false, "LeaseBusy"))); return; }
            lock (_authorityLock) _leases++;
            using var parent = Process.GetProcessById(pid);
            RetryHostUpdate(() => SteamHostsHelper.UpdateHosts(SteamHostsHelper.RemoveStaleLeases));
            File.WriteAllText(LeasePath + ".tmp", JsonSerializer.Serialize(new LeaseRecord(owner)));
            File.Move(LeasePath + ".tmp", LeasePath, true);
            if (mask != 0)
                RetryHostUpdate(() => SteamHostsHelper.UpdateHosts(bytes => SteamHostsPatch.Add(bytes, new SteamNetworkSettings { Store = (mask & 1) != 0, Community = (mask & 2) != 0, Content = (mask & 4) != 0 }, owner, pid, parent.StartTime.ToUniversalTime().Ticks)));
            writer.WriteLine(JsonSerializer.Serialize(new SteamBackgroundReply(true, "Ready")));
            ready = true;
            var command = SteamBackgroundClient.ReadMessageAsync(reader, token);
            while (!command.Wait(250, token)) if (parent.HasExited) break;
        }
        finally
        {
            if (held)
            {
                var restored = false;
                try
                {
                    RetryHostUpdate(() => SteamHostsHelper.UpdateHosts(bytes => SteamHostsPatch.Remove(bytes, owner)));
                    restored = true;
                    if (restored) File.Delete(LeasePath);
                    if (ready) writer.WriteLine(JsonSerializer.Serialize(new SteamBackgroundReply(restored, restored ? "Restored" : "RestoreFailed")));
                }
                finally { lock (_authorityLock) _leases--; mutex.ReleaseMutex(); }
            }
        }
    }

    private static NamedPipeServerStream CreatePipe(string sid)
    {
        var security = new PipeSecurity(); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        foreach (var account in new[] { new SecurityIdentifier(sid), new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            security.AddAccessRule(new PipeAccessRule(account, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(SteamBackgroundProtocol.PipeName, PipeDirection.InOut, 32, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
    }

    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);

    public void Dispose() { _authority?.Dispose(); _authority = null; }

    internal static void RecoverOutstandingLease()
    {
        if (!File.Exists(LeasePath)) return;
        using var mutex = new Mutex(false, "Global\\YueYuOrb.SteamHostsLease");
        var held = false;
        try
        {
            try { held = mutex.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
            if (!held) throw new IOException("其他加速租约仍在活动，暂不恢复。");
            var record = JsonSerializer.Deserialize<LeaseRecord>(File.ReadAllText(LeasePath)) ?? throw new IOException("后台恢复记录无效。");
            if (record.Owner == Guid.Empty) throw new IOException("后台恢复标记无效。");
            RetryHostUpdate(() => SteamHostsHelper.UpdateHosts(bytes => SteamHostsPatch.Remove(bytes, record.Owner)));
            File.Delete(LeasePath);
        }
        finally { if (held) mutex.ReleaseMutex(); }
    }

    internal static void RetryHostUpdate(Action update, Action<int>? delay = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { update(); return; }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 && attempt < 9)
            { (delay ?? Thread.Sleep)(Math.Min(100 + attempt * 50, 400)); }
        }
    }
}
