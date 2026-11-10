using FloatDesk.Core.Assistant;

namespace FloatDesk.Infrastructure.Tests;

public sealed class AssistantProfileTests
{
    [Fact]
    public void SelectedProfileOverridesLegacyAndDisablingItDoesNotFallBack()
    {
        var profile = new AssistantApiProfile(Guid.NewGuid(), "second key", "https://second.test/v1", "chosen", true);
        var settings = new AssistantSettings { Endpoint = "https://old.test/v1", Model = "old", Profiles = [profile], ActiveProfileId = profile.Id };
        var resolved = AssistantConnections.Chat(settings);
        Assert.Equal(profile.Endpoint, resolved.Endpoint); Assert.Equal(profile.Model, resolved.Model);
        Assert.Throws<InvalidOperationException>(() => AssistantConnections.Chat(settings with { Profiles = [profile with { Enabled = false }] }));
        Assert.Equal("old", AssistantConnections.Chat(new() { Endpoint = "https://old.test/v1", Model = "old" }).Model);
    }

    [Fact]
    public void SearchMustUseExplicitEnabledProfileAndNamesAreValidated()
    {
        Assert.Throws<InvalidOperationException>(() => AssistantConnections.Search(new() { SearchEnabled = true }));
        Assert.NotEmpty(AssistantSettingsValidator.Validate(new() { DisplayName = "bad\nname" }));
        Assert.NotEmpty(AssistantSettingsValidator.Validate(new() { WindowTitle = "" }));
    }
}
