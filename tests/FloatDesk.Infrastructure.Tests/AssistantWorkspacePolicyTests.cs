using FloatDesk.Core.Assistant;

namespace FloatDesk.Infrastructure.Tests;

public sealed class AssistantWorkspacePolicyTests
{
    [Fact]
    public void SameKeySupportsDistinctChatAndUniqueSearchModel()
    {
        var key = new AssistantApiProfile(Guid.NewGuid(), "key", "https://example.test/v1", "base", true);
        var settings = new AssistantSettings { Profiles = [key], ActiveProfileId = key.Id, ChatModelId = "chat", SearchEnabled = true, SearchProfileId = key.Id, SearchModelId = "search" };
        Assert.Equal("chat", AssistantConnections.Chat(settings).Model); Assert.Equal("search", AssistantConnections.Search(settings).Model);
        Assert.Throws<InvalidOperationException>(() => AssistantWorkspacePolicy.SetSearch(settings, key.Id, "second", true));
        Assert.Equal("search", AssistantConnections.Search(settings).Model);
    }

    [Fact]
    public void OnlyOneRoleAndMultipleSkillsCanBeEnabled()
    {
        var a = new AssistantAsset(Guid.NewGuid(), "A", "role", "A", "manual", "a"); var b = a with { Id = Guid.NewGuid(), Name = "B" };
        var skill = a with { Id = Guid.NewGuid(), Kind = "skill" };
        var settings = new AssistantSettings { EnabledAssetIds = [a.Id, skill.Id] };
        var enabled = AssistantWorkspacePolicy.SetAsset(settings, [a,b,skill], b.Id, true);
        Assert.DoesNotContain(a.Id, enabled); Assert.Contains(b.Id, enabled); Assert.Contains(skill.Id, enabled);
    }
}
