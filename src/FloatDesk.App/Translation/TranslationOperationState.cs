using FloatDesk.Core.Models;
using FloatDesk.Core.Translation;

namespace FloatDesk.App.Translation;

public sealed record TranslationCredentialTarget
{
    public TranslationCredentialTarget(
        TranslationProviderKind provider,
        string endpoint)
    {
        Provider = provider;
        Endpoint = endpoint ?? "";
        Identity = TranslationCredentialKey.For(provider, Endpoint);
    }

    public TranslationProviderKind Provider { get; }
    public string Endpoint { get; }
    public string Identity { get; }

    public static TranslationCredentialTarget From(
        TranslationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new TranslationCredentialTarget(
            settings.Provider,
            settings.Endpoint);
    }

    public static TranslationCredentialTarget From(
        TranslationProviderKind provider,
        string endpoint) =>
        new(provider, endpoint);

    public bool Matches(TranslationSettings settings) =>
        settings is not null &&
        string.Equals(
            Identity,
            TranslationCredentialKey.For(
                settings.Provider,
                settings.Endpoint),
            StringComparison.Ordinal);
}

public sealed class TranslationCredentialIntentState
{
    public TranslationCredentialTarget? Target { get; private set; }

    public void Mark(TranslationCredentialTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (Target is not null &&
            !string.Equals(
                Target.Identity,
                target.Identity,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Credential intent cannot move to a different target.");
        }
        Target = target;
    }

    public bool ResetIfIdentityChanged(TranslationCredentialTarget current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (Target is null ||
            string.Equals(
                Target.Identity,
                current.Identity,
                StringComparison.Ordinal))
        {
            return false;
        }
        Target = null;
        return true;
    }

    public void Clear() => Target = null;
}

public sealed class TranslationOperationGate
{
    private sealed record ActiveOperation(
        object Token,
        string? Identity);

    private ActiveOperation? _active;

    public object Begin()
        => BeginCore(identity: null);

    public object Begin(TranslationCredentialTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return BeginCore(target.Identity);
    }

    private object BeginCore(string? identity)
    {
        var operation = new object();
        Interlocked.Exchange(
            ref _active,
            new ActiveOperation(operation, identity));
        return operation;
    }

    public bool IsActive(object operation)
    {
        var active = Volatile.Read(ref _active);
        return active is not null &&
            ReferenceEquals(active.Token, operation);
    }

    public bool IsActive(
        object operation,
        TranslationCredentialTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var active = Volatile.Read(ref _active);
        return active is not null &&
            ReferenceEquals(active.Token, operation) &&
            string.Equals(
                active.Identity,
                target.Identity,
                StringComparison.Ordinal);
    }

    public bool HasActiveIdentityDifferentFrom(
        TranslationCredentialTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var active = Volatile.Read(ref _active);
        return active?.Identity is not null &&
            !string.Equals(
                active.Identity,
                target.Identity,
                StringComparison.Ordinal);
    }

    public bool TryComplete(object operation)
        => TryCompleteCore(operation, identity: null, requireIdentity: false);

    public bool TryComplete(
        object operation,
        TranslationCredentialTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return TryCompleteCore(
            operation,
            target.Identity,
            requireIdentity: true);
    }

    private bool TryCompleteCore(
        object operation,
        string? identity,
        bool requireIdentity)
    {
        while (true)
        {
            var active = Volatile.Read(ref _active);
            if (active is null ||
                !ReferenceEquals(active.Token, operation) ||
                (requireIdentity &&
                 !string.Equals(
                     active.Identity,
                     identity,
                     StringComparison.Ordinal)))
            {
                return false;
            }

            if (ReferenceEquals(
                    Interlocked.CompareExchange(
                        ref _active,
                        null,
                        active),
                    active))
            {
                return true;
            }
        }
    }

    public void Clear() => Interlocked.Exchange(ref _active, null);
}

public sealed class TranslationConnectionTestOperation
{
    private readonly CancellationTokenSource _cancellation;

    internal TranslationConnectionTestOperation(
        object gateToken,
        CancellationTokenSource cancellation)
    {
        GateToken = gateToken;
        _cancellation = cancellation;
        Token = cancellation.Token;
    }

    internal object GateToken { get; }
    public CancellationToken Token { get; }
    internal CancellationTokenSource Cancellation => _cancellation;
}

public readonly record struct TranslationConnectionTestCompletion(
    bool WasActive,
    bool IdentityMatches)
{
    public bool ShouldReenableControls => WasActive;
}

public sealed class TranslationConnectionTestState
{
    private readonly object _sync = new();
    private readonly TranslationOperationGate _gate = new();
    private CancellationTokenSource? _cancellation;

    public TranslationConnectionTestOperation Start(
        TranslationCredentialTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        Cancel();

        var cancellation = new CancellationTokenSource();
        var gateToken = _gate.Begin(target);
        lock (_sync)
        {
            _cancellation = cancellation;
        }
        return new TranslationConnectionTestOperation(
            gateToken,
            cancellation);
    }

    public bool IsCurrent(
        TranslationConnectionTestOperation operation,
        TranslationCredentialTarget target)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(target);
        return _gate.IsActive(operation.GateToken, target);
    }

    public bool HasActiveIdentityDifferentFrom(
        TranslationCredentialTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return _gate.HasActiveIdentityDifferentFrom(target);
    }

    public bool InvalidateIfIdentityChanged(
        TranslationCredentialTarget target)
    {
        if (!HasActiveIdentityDifferentFrom(target))
            return false;
        Cancel();
        return true;
    }

    public TranslationConnectionTestCompletion Complete(
        TranslationConnectionTestOperation operation,
        TranslationCredentialTarget currentTarget)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(currentTarget);
        var identityMatches = _gate.IsActive(
            operation.GateToken,
            currentTarget);
        var wasActive = _gate.TryComplete(operation.GateToken);

        CancellationTokenSource? cancellation = null;
        if (wasActive)
        {
            lock (_sync)
            {
                if (ReferenceEquals(
                        _cancellation,
                        operation.Cancellation))
                {
                    cancellation = _cancellation;
                    _cancellation = null;
                }
            }
        }

        cancellation?.Dispose();
        return new TranslationConnectionTestCompletion(
            wasActive,
            identityMatches);
    }

    public void Cancel()
    {
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            cancellation = _cancellation;
            _cancellation = null;
            _gate.Clear();
        }

        if (cancellation is null)
            return;
        try
        {
            cancellation.Cancel();
        }
        finally
        {
            cancellation.Dispose();
        }
    }
}
