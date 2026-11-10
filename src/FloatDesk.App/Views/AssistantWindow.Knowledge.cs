using System.Windows;
using System.Windows.Controls;
using FloatDesk.Core.Assistant;

namespace FloatDesk.App.Views;

public sealed partial class AssistantWindow
{
    private readonly ComboBox _assistantChatProject = new() { Width = 170, DisplayMemberPath = "Name", SelectedValuePath = "Id", Margin = new Thickness(0, 5, 6, 0), IsEnabled = false, ToolTip = "新会话发送消息前选择资料项目，项目之间不共享文档；全局资料可用于所有项目。" };
    private readonly WrapPanel _assistantReferenceBar = new() { Margin = new Thickness(0, 4, 0, 0) };
    private bool _loadingAssistantProject;
    private void InitializeAssistantKnowledgeToolbar(Panel targetRow, Panel toolbar)
    {
        ApplyStyle(_assistantChatProject, "SettingsComboBoxStyle"); targetRow.Children.Add(_assistantChatProject); toolbar.Children.Add(_assistantReferenceBar);
        _assistantChatProject.SelectionChanged += async (_, _) =>
        {
            if (_loadingAssistantProject || _session is null || _store is not IAssistantKnowledgeStore library) return;
            var id = _session.Id; var scope = _assistantChatProject.SelectedValue is Guid selected && selected != Guid.Empty ? selected : (Guid?)null;
            await UiActionAsync(async () => { _runtime.RevokePermissions(); StopSpeechRequested?.Invoke(); await _runtime.SetProjectAsync(id, scope, CancellationToken.None); _assistantReferenceBar.Children.Clear(); });
            await LoadAssistantProjectAsync(id);
        };
        _runtime.ReferencesChanged += OnAssistantReferencesChanged;
    }
    private async Task LoadAssistantProjectAsync(Guid session)
    {
        if (_store is not IAssistantKnowledgeStore library) return;
        var projects = await library.ListProjectsAsync(CancellationToken.None); var project = await _runtime.GetProjectAsync(session, CancellationToken.None); var messages = await _runtime.LoadMessagesAsync(session, CancellationToken.None);
        if (_disposed || _session?.Id != session) return;
        _loadingAssistantProject = true;
        try { _assistantChatProject.ItemsSource = projects.Prepend(new AssistantProject(Guid.Empty, "全局资料", DateTimeOffset.MinValue)).ToArray(); _assistantChatProject.SelectedValue = project ?? Guid.Empty; _assistantChatProject.IsEnabled = messages.Count == 0 && !_runtime.IsBusy; }
        finally { _loadingAssistantProject = false; }
    }
    private void OnAssistantReferencesChanged(Guid session, IReadOnlyList<AssistantRecall> hits) => Dispatch(() =>
    {
        if (session != Guid.Empty && _session?.Id != session) return;
        _assistantReferenceBar.Children.Clear();
        foreach (var hit in hits)
        {
            var citation = new AssistantCitation(hit.Id, hit.Kind, hit.Title, hit.Locator, hit.Revision);
            _assistantReferenceBar.Children.Add(ActionButton("引用 · " + hit.Title, (_, _) => _ = OpenAssistantCitationAsync(citation)));
        }
    });
    private void AddAssistantCitationButtons(Panel panel, AssistantMessage message)
    {
        if (message.References.Count == 0) return;
        var row = new WrapPanel { Margin = new Thickness(10, 0, 10, 6) };
        foreach (var citation in message.References) { var button = ActionButton("来源 · " + citation.Title, (_, _) => _ = OpenAssistantCitationAsync(citation)); button.ToolTip = citation.Locator + " · 修订" + citation.Revision; row.Children.Add(button); }
        panel.Children.Add(row);
    }
    private Task OpenAssistantCitationAsync(AssistantCitation citation) => UiActionAsync(async () =>
    {
        if (_session is null || _store is not IAssistantKnowledgeStore library) return;
        var project = await _runtime.GetProjectAsync(_session.Id, CancellationToken.None);
        var source = citation.Kind == "chat" && _store is FloatDesk.Infrastructure.Assistant.AssistantStore concrete
            ? await concrete.ReadHistoryCitationAsync(citation.Id, project, CancellationToken.None)
            : (await library.ListRecallItemsAsync(project, true, true, CancellationToken.None)).FirstOrDefault(item => item.Id == citation.Id && item.Kind == citation.Kind && item.Revision == citation.Revision);
        if (source is null) { _status.Text = "引用来源已关闭、删除或更新；不会展示过时片段。"; return; }
        AssistantTextDialog.Show(this, "引用来源", $"{source.Title}\n{source.Locator}\n{source.Kind}:{source.Id}\n修订{source.Revision}\n\n{source.Content}", editable: false);
    });
}
