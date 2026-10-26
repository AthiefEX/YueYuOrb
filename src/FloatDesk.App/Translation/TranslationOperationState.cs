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
    private object? _active;

    public object Begin()
    {
        var operation = new object();
        Interlocked.Exchange(ref _active, operation);
        return operation;
    }

    public bool IsActive(object operation) =>
        ReferenceEquals(
            Volatile.Read(ref _active),
            operation);

    public bool TryComplete(object operation) =>
        ReferenceEquals(
            Interlocked.CompareExchange(
                ref _active,
                null,
                operation),
            operation);

    public void Clear() => Interlocked.Exchange(ref _active, null);
}
