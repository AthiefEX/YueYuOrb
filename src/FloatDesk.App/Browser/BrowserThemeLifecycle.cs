using System;
using System.Threading;
using System.Threading.Tasks;
using FloatDesk.Core.Models;

namespace FloatDesk.App.Browser;

public interface IBrowserThemeSurface
{
    void ApplyPreferredColorScheme(BrowserColorScheme scheme);

    Task<string> ReplaceDocumentScriptAsync(
        string? existingScriptId,
        BrowserColorScheme scheme);

    Task ApplyCurrentDocumentAsync(BrowserColorScheme scheme);
}

public sealed class BrowserThemeLifecycle : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly BrowserThemeRuntimeState _state = new();
    private BrowserColorScheme _requestedScheme;
    private long _requestRevision;
    private IBrowserThemeSurface? _surface;
    private long _surfaceGeneration;
    private BrowserColorScheme? _appliedScheme;
    private long _appliedRevision = -1;
    private bool _disposed;

    public BrowserThemeLifecycle(BrowserColorScheme initialScheme)
    {
        ValidateScheme(initialScheme);
        _requestedScheme = initialScheme;
    }

    public void Request(BrowserColorScheme scheme)
    {
        ValidateScheme(scheme);
        lock (_sync)
        {
            if (_disposed || _requestedScheme == scheme)
                return;

            _requestedScheme = scheme;
            _requestRevision++;
        }
    }

    public async Task AttachAsync(IBrowserThemeSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        await _gate.WaitAsync();
        try
        {
            lock (_sync)
            {
                if (_disposed)
                    return;

                _surface = surface;
                _surfaceGeneration++;
                ResetAppliedLocked();
            }

            await ApplyUntilStableAsync(
                applyCurrentDocument: false,
                applyDocumentWhenRevisionChanges: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ApplyAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
                return;

            await ApplyUntilStableAsync(
                applyCurrentDocument: false,
                applyDocumentWhenRevisionChanges: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReapplyCurrentDocumentAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
                return;

            await ApplyUntilStableAsync(
                applyCurrentDocument: true,
                applyDocumentWhenRevisionChanges: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Detach()
    {
        lock (_sync)
        {
            _surface = null;
            _surfaceGeneration++;
            ResetAppliedLocked();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            _surface = null;
            _surfaceGeneration++;
            ResetAppliedLocked();
        }

        // SemaphoreSlim is intentionally not disposed: an in-flight WebView
        // callback may still release it after the window has closed.
    }

    private async Task ApplyUntilStableAsync(
        bool applyCurrentDocument,
        bool applyDocumentWhenRevisionChanges)
    {
        var documentRequired = applyCurrentDocument;
        while (true)
        {
            OperationSnapshot snapshot;
            lock (_sync)
            {
                if (_disposed || _surface is null)
                    return;

                var revisionChanged =
                    _appliedRevision >= 0 &&
                    (_appliedScheme != _requestedScheme ||
                     _appliedRevision != _requestRevision);
                var needsWork =
                    documentRequired ||
                    revisionChanged ||
                    _state.NeedsReplacement(_requestedScheme);
                if (!needsWork)
                    return;

                documentRequired |= revisionChanged;
                snapshot = new OperationSnapshot(
                    _surface,
                    _surfaceGeneration,
                    _requestedScheme,
                    _requestRevision);
            }

            if (!await ApplySchemeAsync(snapshot, documentRequired))
                return;

            lock (_sync)
            {
                if (!IsSurfaceCurrentLocked(snapshot))
                    return;
                if (_requestedScheme == snapshot.Scheme &&
                    _requestRevision == snapshot.Revision)
                {
                    return;
                }
            }

            documentRequired = applyDocumentWhenRevisionChanges;
        }
    }

    private async Task<bool> ApplySchemeAsync(
        OperationSnapshot snapshot,
        bool applyCurrentDocument)
    {
        try
        {
            snapshot.Surface.ApplyPreferredColorScheme(snapshot.Scheme);
        }
        catch (Exception exception) when (
            WebViewThemeBridge.IsOptionalFailure(exception))
        {
            // Continue with the site adapter when native theming is absent.
        }

        if (!IsSurfaceCurrent(snapshot))
            return false;

        if (NeedsReplacement(snapshot.Scheme))
        {
            try
            {
                var scriptId =
                    await snapshot.Surface.ReplaceDocumentScriptAsync(
                        GetScriptId(),
                        snapshot.Scheme);
                lock (_sync)
                {
                    if (!IsSurfaceCurrentLocked(snapshot))
                        return false;

                    // Record even an immediately stale script so the next pass
                    // can remove it from the same WebView core.
                    _state.Record(scriptId, snapshot.Scheme);
                }
            }
            catch (Exception exception) when (
                WebViewThemeBridge.IsOptionalFailure(exception))
            {
                // Keep navigation usable if the adapter cannot be replaced.
            }
        }

        if (!IsSurfaceCurrent(snapshot))
            return false;

        if (applyCurrentDocument && IsRequestCurrent(snapshot))
        {
            try
            {
                await snapshot.Surface.ApplyCurrentDocumentAsync(
                    snapshot.Scheme);
            }
            catch (Exception exception) when (
                WebViewThemeBridge.IsOptionalFailure(exception))
            {
                // The registered script still applies to the next document.
            }
        }

        lock (_sync)
        {
            if (!IsSurfaceCurrentLocked(snapshot))
                return false;

            _appliedScheme = snapshot.Scheme;
            _appliedRevision = snapshot.Revision;
        }

        return true;
    }

    private bool IsSurfaceCurrent(OperationSnapshot snapshot)
    {
        lock (_sync)
            return IsSurfaceCurrentLocked(snapshot);
    }

    private bool IsSurfaceCurrentLocked(OperationSnapshot snapshot) =>
        !_disposed &&
        ReferenceEquals(_surface, snapshot.Surface) &&
        _surfaceGeneration == snapshot.SurfaceGeneration;

    private bool IsRequestCurrent(OperationSnapshot snapshot)
    {
        lock (_sync)
        {
            return !_disposed &&
                   ReferenceEquals(_surface, snapshot.Surface) &&
                   _surfaceGeneration == snapshot.SurfaceGeneration &&
                   _requestRevision == snapshot.Revision;
        }
    }

    private string? GetScriptId()
    {
        lock (_sync)
            return _state.ScriptId;
    }

    private bool NeedsReplacement(BrowserColorScheme scheme)
    {
        lock (_sync)
            return _state.NeedsReplacement(scheme);
    }

    private void ResetAppliedLocked()
    {
        _state.Reset();
        _appliedScheme = null;
        _appliedRevision = -1;
    }

    private static void ValidateScheme(BrowserColorScheme scheme)
    {
        if (!Enum.IsDefined(scheme))
        {
            throw new ArgumentOutOfRangeException(
                nameof(scheme),
                scheme,
                "Unknown browser color scheme.");
        }
    }

    private readonly record struct OperationSnapshot(
        IBrowserThemeSurface Surface,
        long SurfaceGeneration,
        BrowserColorScheme Scheme,
        long Revision);
}
