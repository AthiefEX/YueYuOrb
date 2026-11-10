using System.Windows;
using FloatDesk.Core.Assistant;
using FloatDesk.Infrastructure.Assistant;

namespace FloatDesk.App.Views;

public partial class SettingsWindow
{
    private async Task EditAssistantAssetAsync(string kind, AssistantAsset? previous = null)
    {
        if (_assistantStore is not AssistantStore store) return;
        var edited = AssistantAssetEditor.Edit(this, kind, previous); if (edited is null) return;
        await CheckAssistantStoredSecretsAsync(edited.Name + "\n" + edited.Text);
        await store.SaveAssetVersionedAsync(edited, previous?.Sha256, CancellationToken.None); await RefreshAssistantLibraryAsync();
    }
}
