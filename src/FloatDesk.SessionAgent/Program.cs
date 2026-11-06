using FloatDesk.Windows.DesktopSessions;
using System.Security.Cryptography;

namespace FloatDesk.SessionAgent;

internal static class Program
{
    private static async Task<int> Main(string[] arguments)
    {
        try
        {
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
