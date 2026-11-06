using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace FloatDesk.App.DesktopSessions;

public sealed class ChildSessionAgentBootstrapStore : IDisposable
{
    private readonly ConcurrentDictionary<Guid, Entry> _entries = [];
    private readonly object _sync = new();
    private bool _disposed;

    public void Set(
        SessionHandle handle,
        ChildSessionAgentBootstrap bootstrap)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(bootstrap);
        if (bootstrap.BootstrapSecret is not { Length: 32 })
        {
            throw new ArgumentException(
                "The session agent bootstrap secret must be 32 bytes.",
                nameof(bootstrap));
        }

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryRemove(handle.EnvironmentId, out var previous))
                Clear(previous);
            _entries[handle.EnvironmentId] = new Entry(handle, bootstrap);
        }
    }

    public ChildSessionAgentBootstrap Take(SessionHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(handle.EnvironmentId, out var entry))
            {
                if (entry.Handle != handle)
                {
                    throw new InvalidOperationException(
                        "The session agent bootstrap is bound to another generation.");
                }

                _entries.TryRemove(handle.EnvironmentId, out _);
                return entry.Bootstrap;
            }
        }

        throw new InvalidOperationException(
            "The session agent bootstrap is missing.");
    }

    public void Remove(SessionHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        lock (_sync)
        {
            if (_entries.TryGetValue(handle.EnvironmentId, out var entry) &&
                entry.Handle == handle &&
                _entries.TryRemove(handle.EnvironmentId, out entry))
            {
                Clear(entry);
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var entry in _entries.Values)
                Clear(entry);
            _entries.Clear();
        }
    }

    private static void Clear(Entry entry) =>
        CryptographicOperations.ZeroMemory(entry.Bootstrap.BootstrapSecret);

    private sealed record Entry(
        SessionHandle Handle,
        ChildSessionAgentBootstrap Bootstrap);
}
