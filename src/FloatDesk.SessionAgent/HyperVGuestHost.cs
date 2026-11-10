using System;
using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FloatDesk.Windows.DesktopSessions;
using Microsoft.Win32;

namespace FloatDesk.SessionAgent;

public sealed record HyperVGuestConfiguration(Guid VmId, Guid EnvironmentId, byte[] Secret);

internal static class HyperVGuestHost
{
    internal static string ConfigurationPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YueYuOrb", "guest", "pairing.dat");

    public static async Task<int> RunAsync(bool provision, bool configureOnly = false)
    {
        HyperVGuestConfiguration config;
        if (provision)
        {
            var line = await Console.In.ReadLineAsync().ConfigureAwait(false);
            config = JsonSerializer.Deserialize<HyperVGuestConfiguration>(line ?? string.Empty)
                ?? throw new ArgumentException("Guest configuration is missing.");
        }
        else
        {
            var encrypted = await File.ReadAllBytesAsync(ConfigurationPath).ConfigureAwait(false);
            var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            try { config = JsonSerializer.Deserialize<HyperVGuestConfiguration>(plain) ?? throw new ArgumentException("Invalid guest configuration."); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Virtual Machine\Guest\Parameters");
            if (!Guid.TryParse(key?.GetValue("VirtualMachineId") as string, out var actualVmId) || actualVmId != config.VmId ||
                config.EnvironmentId == Guid.Empty || config.Secret is not { Length: 32 })
                throw new InvalidOperationException("Guest configuration does not match this virtual machine.");

            using var process = Process.GetCurrentProcess();
            using var user = WindowsIdentity.GetCurrent();
            if (process.SessionId == 0 || user.User is null)
                throw new InvalidOperationException("Start the guest Agent in an interactive Windows session.");
            if (provision)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigurationPath)!);
                var plain = JsonSerializer.SerializeToUtf8Bytes(config);
                try
                {
                    var encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
                    await File.WriteAllBytesAsync(ConfigurationPath, encrypted).ConfigureAwait(false);
                }
                finally { CryptographicOperations.ZeroMemory(plain); }
            }
            if (configureOnly)
                return 0;

            var identity = new HyperVGuestIdentity(config.VmId, HyperVGuestDiscovery.ServiceId,
                (uint)process.SessionId, Guid.NewGuid(), Guid.NewGuid().ToString("N"), user.User.Value, DateTimeOffset.UtcNow);
            var binding = new SessionAgentBinding(config.EnvironmentId, Guid.NewGuid(), identity.LoginInstanceId, identity.WindowsSessionId, identity.UserSid);
            var options = new HyperVAgentListenerOptions(config.VmId, identity.ServiceId, binding, config.Secret)
            { Identity = identity, AcceptHostGeneration = true };
            using var runner = new JobObjectScriptRunner();
            var executor = new AutomationStepExecutor(new WindowsSessionAutomationBackend(), runner, new FloatDesk.Core.Contracts.SystemClock());
            await using var listener = new WindowsHyperVSocketListener(identity.ServiceId);
            listener.Start();
            await listener.AcceptLoopAsync((stream, token) => HyperVAgentListener.ServeAsync(stream, options, token, executor), CancellationToken.None).ConfigureAwait(false);
            return 0;
        }
        finally { if (config.Secret is not null) CryptographicOperations.ZeroMemory(config.Secret); }
    }
}
