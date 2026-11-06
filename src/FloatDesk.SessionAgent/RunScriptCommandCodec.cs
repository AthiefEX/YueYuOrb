using System.Text.Json;

namespace FloatDesk.SessionAgent;

internal static class RunScriptCommandCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static bool TryDecode(
        IReadOnlyDictionary<string, string> parameters,
        out string? scriptPath,
        out string[] arguments)
    {
        scriptPath = null;
        arguments = [];
        if (!parameters.TryGetValue("scriptPath", out var path) ||
            string.IsNullOrWhiteSpace(path) ||
            !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        if (parameters.TryGetValue("arguments", out var json) &&
            !string.IsNullOrWhiteSpace(json))
        {
            try
            {
                arguments = JsonSerializer.Deserialize<string[]>(
                    json,
                    Options) ?? [];
            }
            catch (JsonException)
            {
                return false;
            }
        }

        scriptPath = path;
        return true;
    }
}
