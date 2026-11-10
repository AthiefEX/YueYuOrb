using FloatDesk.Windows.DesktopSessions;
using System.Security.Cryptography;

namespace FloatDesk.SessionAgent;

internal static class Program
{
    private static async Task<int> Main(string[] arguments)
    {
        try
        {
            if (arguments is ["--hyperv-guest"])
                return await HyperVGuestHost.RunAsync(provision: false);
            if (arguments is ["--hyperv-provision"])
                return await HyperVGuestHost.RunAsync(provision: true);
            if (arguments is ["--hyperv-configure"])
                return await HyperVGuestHost.RunAsync(provision: true, configureOnly: true);
            if (arguments.Length >= 3 && arguments[0] == "--guard-startup-app" && arguments[2] == "--")
                return StartupApplicationGuard.Run(arguments[1], arguments.Skip(3).ToArray());
            if (arguments is ["--probe-child", var pipeName])
            {
                if (!pipeName.StartsWith("YueYuOrb.ParentProbe.", StringComparison.Ordinal))
                    return 2;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await using var pipe = new System.IO.Pipes.NamedPipeClientStream(
                    ".", pipeName, System.IO.Pipes.PipeDirection.Out, System.IO.Pipes.PipeOptions.Asynchronous);
                await pipe.ConnectAsync(timeout.Token);
                var native = new ChildSessionNative();
                var present = native.TryGetChildSessionId(out var childId);
                var bytes = new byte[4];
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes, present ? childId : uint.MaxValue);
                await pipe.WriteAsync(bytes, timeout.Token);
                return 0;
            }
            var options = SessionAgentOptions.Parse(arguments);
            var encodedSecret = await Console.In.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(encodedSecret))
                return 2;
            var secret = Convert.FromBase64String(encodedSecret);
            if (secret.Length != SessionProtocol.BootstrapSecretSize)
            {
                CryptographicOperations.ZeroMemory(secret);
                return 2;
            }

            try
            {
                using var scriptRunner = new JobObjectScriptRunner();
                var executor = new AutomationStepExecutor(
                    new WindowsSessionAutomationBackend(),
                    scriptRunner,
                    new FloatDesk.Core.Contracts.SystemClock());
                await SessionAgentServer.RunAsync(
                    options,
                    secret,
                    executor);
                return 0;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secret);
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            return 2;
        }
    }
}
