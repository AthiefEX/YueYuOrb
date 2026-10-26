using FloatDesk.Core.Models;

namespace FloatDesk.App.Browser;

public sealed class BrowserThemeRuntimeState
{
    private BrowserColorScheme? _scheme;

    public string? ScriptId { get; private set; }

    public bool NeedsReplacement(BrowserColorScheme scheme) =>
        ScriptId is null || _scheme != scheme;

    public void Record(string scriptId, BrowserColorScheme scheme)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptId);
        ScriptId = scriptId;
        _scheme = scheme;
    }

    public void Reset()
    {
        ScriptId = null;
        _scheme = null;
    }
}
