using FloatDesk.Windows.DesktopSessions;

namespace FloatDesk.SessionBroker;

internal static class Program
{
    private const string ServiceName = "YueYuOrbSessionBroker";

    private static async Task<int> Main(string[] arguments)
    {
        try
        {
            if (arguments is ["--install"])
                return SessionBrokerInstallation.Install();
            if (arguments is ["--uninstall"])
                return SessionBrokerInstallation.Uninstall();
            if (arguments is ["--service"])
                return RunService();
            if (arguments.Length == 0 || arguments is ["--console"])
                return await RunConsoleAsync().ConfigureAwait(false);
            return 2;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or
            IOException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static async Task<int> RunConsoleAsync()
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        await CreateServer().RunAsync(cancellation.Token)
            .ConfigureAwait(false);
        return 0;
    }

    private static int RunService() =>
        WindowsServiceHost.Run(
            ServiceName,
            token => CreateServer().RunAsync(token));

    private static SessionBrokerServer CreateServer() =>
        new(new WindowsChildSessionAgentLauncher());

}
