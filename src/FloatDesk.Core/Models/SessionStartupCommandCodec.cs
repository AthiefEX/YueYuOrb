using System.Text.Json;

namespace FloatDesk.Core.Models;

public static class SessionStartupCommandCodec
{
    public const string ParameterName = "startupEntry";
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static IReadOnlyDictionary<string, string> ToParameters(
        SessionStartupEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var payload = new StartupEntryPayload(
            entry.Id,
            entry.DisplayName,
            entry.ExecutablePath,
            entry.Arguments.ToArray(),
            entry.WorkingDirectory,
            entry.Order,
            entry.DelaySeconds,
            entry.Enabled,
            entry.WaitForExit,
            entry.Revision);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ParameterName] = JsonSerializer.Serialize(payload, Options)
        };
    }

    public static bool TryDecode(
        IReadOnlyDictionary<string, string> parameters,
        out SessionStartupEntry? entry)
    {
        entry = null;
        ArgumentNullException.ThrowIfNull(parameters);
        if (!parameters.TryGetValue(ParameterName, out var json) ||
            string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<StartupEntryPayload>(
                json,
                Options);
            if (payload is null ||
                payload.Id == Guid.Empty ||
                string.IsNullOrWhiteSpace(payload.DisplayName) ||
                string.IsNullOrWhiteSpace(payload.ExecutablePath) ||
                payload.Arguments is null ||
                payload.DelaySeconds is < 0 or > 300 ||
                payload.Revision < 1)
            {
                return false;
            }

            entry = new SessionStartupEntry(
                payload.Id,
                payload.DisplayName,
                payload.ExecutablePath,
                payload.Arguments,
                payload.WorkingDirectory,
                payload.Order,
                payload.DelaySeconds,
                payload.Enabled,
                payload.WaitForExit,
                payload.Revision);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record StartupEntryPayload(
        Guid Id,
        string DisplayName,
        string ExecutablePath,
        string[]? Arguments,
        string? WorkingDirectory,
        int Order,
        int DelaySeconds,
        bool Enabled,
        bool WaitForExit,
        int Revision);
}
