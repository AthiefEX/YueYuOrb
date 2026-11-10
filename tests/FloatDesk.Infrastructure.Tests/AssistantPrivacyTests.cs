using FloatDesk.Core.Assistant;
using FloatDesk.Infrastructure.Assistant;

namespace FloatDesk.Infrastructure.Tests;

public sealed class AssistantPrivacyTests
{
    private static AssistantChatRequest Request(string content = "hello") => new(new() { Endpoint = "https://example.test/v1", Model = "test" },
        [new(Guid.NewGuid(), Guid.NewGuid(), "user", content, DateTimeOffset.UtcNow)], "not-for-message-context");

    [Fact]
    public void ApiKeyInMessageIsRejectedBeforeNetworkOrStorage()
    {
        var request = Request("the key is not-for-message-context");
        Assert.NotNull(AssistantPrivacy.SecretReason(request.Messages[0].Content, request.ApiKey));
        Assert.Throws<InvalidOperationException>(() => AssistantPrivacy.EnsureAllowed(request));
    }

    [Fact]
    public void SensitiveApprovalCannotBeReusedForAnotherModelOrPayload()
    {
        var request = Request() with { ContainsSensitiveContext = true };
        Assert.Throws<InvalidOperationException>(() => AssistantPrivacy.EnsureAllowed(request));
        var approved = request with { Approval = AssistantPrivacy.Approve(request, DateTimeOffset.UtcNow.AddMinutes(1)) };
        AssistantPrivacy.EnsureAllowed(approved);
        Assert.Throws<InvalidOperationException>(() => AssistantPrivacy.EnsureAllowed(approved with { Settings = approved.Settings with { Model = "different" } }));
        Assert.Throws<InvalidOperationException>(() => AssistantPrivacy.EnsureAllowed(approved with { Messages = Request("changed").Messages }));
        Assert.Throws<InvalidOperationException>(() => AssistantPrivacy.EnsureAllowed(approved with { Approval = AssistantPrivacy.Approve(request, DateTimeOffset.UtcNow.AddMinutes(-1)) }));
    }
}
