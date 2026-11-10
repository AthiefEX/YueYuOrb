using FloatDesk.Core.Assistant;

namespace FloatDesk.Infrastructure.Tests;

public sealed class AssistantLibraryPreferenceTests
{
    [Fact]
    public void DisablingModelDoesNotDisableOrReplaceTheKey()
    {
        var profile = new AssistantApiProfile(Guid.NewGuid(), "key", "https://example.test/v1", "chosen", true) { ModelEnabled = false };
        var settings = new AssistantSettings { Profiles = [profile], ActiveProfileId = profile.Id };
        Assert.True(profile.Enabled); Assert.Throws<InvalidOperationException>(() => AssistantConnections.Chat(settings));
        Assert.Equal(profile.Model, settings.Profiles[0].Model);
    }
}
