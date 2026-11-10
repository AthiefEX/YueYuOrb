using FloatDesk.Core.Models;
using FloatDesk.Windows.DesktopSessions;
using System.Text.Json;

namespace FloatDesk.SessionClient;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOutputOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static async Task<int> Main(string[] arguments)
    {
        if (arguments.Length > 0 && arguments[0] == "--rdp-view") return RdpViewProgram.Run(arguments);
        try
        {
            var command = ClientCommand.Parse(arguments);
            var response = await SendAsync(command).ConfigureAwait(false);
            if (!response.Success)
            {
                Console.Error.WriteLine(
                    $"{response.ErrorCode}: {response.Message}");
                return 1;
            }

            if (command.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    response,
                    JsonOutputOptions));
            }
            else
            {
                Print(response);
            }

            return 0;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or
            InvalidOperationException or UnauthorizedAccessException or
            TimeoutException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    private static async Task<DesktopSessionControlResponse> SendAsync(
        ClientCommand command)
    {
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(10));
        return await DesktopSessionControlClient.SendAsync(
            new DesktopSessionControlRequest(
                DesktopSessionControlProtocol.CurrentVersion,
                command.Operation,
                command.EnvironmentId,
                command.TaskId,
                command.RunId),
            timeout.Token).ConfigureAwait(false);
    }

    private static void Print(DesktopSessionControlResponse response)
    {
        if (response.Environments is not null)
        {
            foreach (var environment in response.Environments)
            {
                Console.WriteLine(
                    $"ENV  {environment.Id:D}  {environment.Name}  [{environment.Backend}]");
            }
        }

        if (response.Tasks is not null)
        {
            foreach (var task in response.Tasks)
            {
                Console.WriteLine(
                    $"TASK {task.Id:D}  {task.Name}  env={task.EnvironmentId:D}  enabled={task.Enabled}");
            }
        }

        if (response.RunId is { } runId)
        {
            Console.WriteLine(
                $"RUN  {runId:D}  {response.RunOutcome}  steps={response.CompletedSteps}");
            if (response.FailedStepId is { } failedStep)
                Console.WriteLine($"FAILED_STEP {failedStep:D}");
        }
    }

    private sealed record ClientCommand(
        string Operation,
        Guid? EnvironmentId,
        Guid? TaskId,
        Guid? RunId,
        bool Json)
    {
        public static ClientCommand Parse(string[] arguments)
        {
            if (arguments.Length == 0)
                throw new ArgumentException(Usage);
            var operation = arguments[0].ToLowerInvariant();
            Guid? environment = null;
            Guid? task = null;
            Guid? run = null;
            var json = false;
            for (var index = 1; index < arguments.Length; index++)
            {
                var name = arguments[index];
                if (string.Equals(
                        name,
                        "--json",
                        StringComparison.OrdinalIgnoreCase))
                {
                    json = true;
                    continue;
                }

                if (index + 1 >= arguments.Length)
                    throw new ArgumentException(Usage);
                var value = ParseGuid(arguments[++index], name);
                switch (name.ToLowerInvariant())
                {
                    case "--environment":
                        environment = value;
                        break;
                    case "--task":
                        task = value;
                        break;
                    case "--run":
                        run = value;
                        break;
                    default:
                        throw new ArgumentException(Usage);
                }
            }

            return operation switch
            {
                DesktopSessionControlProtocol.ListOperation
                    when environment is null && task is null && run is null =>
                    new(operation, null, null, null, json),
                DesktopSessionControlProtocol.RunOperation
                    when environment is not null && task is not null &&
                         run is null =>
                    new(operation, environment, task, null, json),
                DesktopSessionControlProtocol.CancelOperation
                    when environment is null && task is null &&
                         run is not null =>
                    new(operation, null, null, run, json),
                _ => throw new ArgumentException(Usage)
            };
        }

        private static Guid ParseGuid(string text, string option) =>
            Guid.TryParse(text, out var value)
                ? value
                : throw new ArgumentException(
                    $"{option} requires a GUID.");

        private const string Usage =
            "Usage: YueYuOrb.SessionClient list [--json] | run --environment <guid> --task <guid> [--json] | cancel --run <guid> [--json]";
    }
}
