using FloatDesk.Core.Assistant;
using FloatDesk.Infrastructure.Assistant;

namespace FloatDesk.Infrastructure.Tests;

public sealed class AssistantAssetVersionTests
{
    [Fact]
    public async Task EditingCannotOverwriteOrResurrectAnOutdatedAsset()
    {
        var root = Path.Combine(Path.GetTempPath(), "YueYu-Asset-Version-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new AssistantStore(Path.Combine(root, "assistant.db")); var original = new AssistantAsset(Guid.NewGuid(), "skill", "skill", "old", "manual", new string('a',64));
            await store.SaveAssetVersionedAsync(original, null, CancellationToken.None);
            var changed = original with { Text = "new", Sha256 = new string('b',64) }; await store.SaveAssetVersionedAsync(changed, original.Sha256, CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAssetVersionedAsync(original, original.Sha256, CancellationToken.None));
            await store.DeleteAssetAsync(original.Id, CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAssetVersionedAsync(original, changed.Sha256, CancellationToken.None));
            Assert.Empty(await store.ListAssetsAsync(CancellationToken.None));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root,true); }
    }
}
