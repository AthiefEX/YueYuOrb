using FloatDesk.Core.Assistant;

namespace FloatDesk.Infrastructure.Assistant;

public static class AssistantProfileCredentials
{
    public static Task<string?> ChatKeyAsync(AssistantSettings settings, IAssistantCredentialStore store, CancellationToken token)
    {
        var effective = AssistantConnections.Chat(settings);
        var profile = settings.ActiveProfileId is { } id ? settings.Profiles.Single(profile => profile.Id == id) : null;
        return store.GetAsync(profile is null ? "chat" : AssistantConnections.CredentialPurpose(profile), effective.Endpoint, token);
    }
}
