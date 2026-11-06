using FloatDesk.Windows.DesktopSessions;
using System.Security.Principal;

namespace FloatDesk.SessionAgent;

public sealed record SessionAgentOptions(
    string PipeName,
    SessionAgentBinding Binding)
{
    public static SessionAgentOptions Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var values = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < arguments.Count; index += 2)
        {
            if (index + 1 >= arguments.Count)
            {
                throw new ArgumentException(
                    "Agent options must use '--name value' pairs.");
            }

            var name = arguments[index];
            if (!name.StartsWith("--", StringComparison.Ordinal) ||
                name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                !values.TryAdd(name, arguments[index + 1]))
            {
                throw new ArgumentException(
                    "Secret values cannot be passed as agent arguments.");
            }
        }

        var pipeName = GetRequired(values, "--pipe");
        var environmentId = ParseGuid(values, "--environment");
        var generation = ParseGuid(values, "--generation");
        var loginInstanceId = ParseGuid(values, "--login");
        if (!uint.TryParse(
                GetRequired(values, "--session"),
                out var windowsSessionId) ||
            windowsSessionId == 0)
        {
            throw new ArgumentException(
                "The interactive session ID is invalid.");
        }

        var userSid = GetRequired(values, "--user-sid");
        try
        {
            _ = new SecurityIdentifier(userSid);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException(
                "The user SID is invalid.",
                exception);
        }

        var binding = new SessionAgentBinding(
            environmentId,
            generation,
            loginInstanceId,
            windowsSessionId,
            userSid);
        return new SessionAgentOptions(pipeName, binding);
    }

    private static Guid ParseGuid(
        Dictionary<string, string> values,
        string name) =>
        Guid.TryParse(GetRequired(values, name), out var value)
            ? value
            : throw new ArgumentException(
                $"Agent option {name} is not a GUID.");

    private static string GetRequired(
        Dictionary<string, string> values,
        string name)
    {
        if (!values.TryGetValue(name, out var value) ||
            string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                $"Agent option {name} is required.");
        }

        return value;
    }
}
