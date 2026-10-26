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
