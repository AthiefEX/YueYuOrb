using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using FloatDesk.Core.Assistant;
using FloatDesk.Infrastructure.Assistant;

namespace FloatDesk.App.Views;

public partial class SettingsWindow
{
    private ComboBox? _assistantKnowledgeProject;
    private ComboBox? _assistantEmbeddingModels;
    private ComboBox? _assistantExtractionModels;
    private ComboBox? _assistantLearningSession;
    private ComboBox? _assistantDocumentPolicy;
    private StackPanel? _assistantKnowledgeDocuments;
    private StackPanel? _assistantCandidateRows;
    private TextBlock? _assistantKnowledgeStatus;
    private TextBox? _assistantKnowledgeQuery;
    private TextBox? _assistantKnowledgeOutput;
    private ProgressBar? _assistantKnowledgeProgress;
    private TextBlock? _assistantLearningStatus;
    private readonly Dictionary<Guid, string> _assistantProjectNames = [];
    private CancellationTokenSource? _assistantLibraryWork;
    private Guid? _assistantKnowledgeProjectId;
    private bool _refreshingKnowledge;
    private int _knowledgeRefreshVersion;
    private sealed record KnowledgeModelChoice(Guid Id, string Name);
    private sealed record KnowledgePolicy(string Id, string Name);

    private void InitializeAssistantKnowledgeSettings(TabControl categories, Panel memory)
    {
        memory.Children.Add(SectionTitle("候选记忆与本地提取"));
        AssistantToggle(memory, "自动从新用户消息提取候选（仅本地模型，仍需逐条审核）", "AssistantExtractCandidates");
        memory.Children.Add(Description("候选不会直接成为事实；显示来源、置信度与独立会话证据数。相同分类键冲突必须审核替换。没有本地模型时仍可手动保存记忆。"));
        _assistantLearningSession = KnowledgeCombo(); memory.Children.Add(_assistantLearningSession);
        memory.Children.Add(ButtonRow(Button("从所选会话生成候选", (_, _) => _ = LibraryActionAsync(ExtractAssistantCandidatesAsync)), Button("刷新候选", (_, _) => RefreshAssistantKnowledge())));
        memory.Children.Add(ButtonRow(Button("重置失败提取任务", (_, _) => _ = LibraryActionAsync(async () =>
        { if (_assistantStore is not AssistantStore store) return; var profile = AssistantLibraryPolicy.LocalProfile(_viewModel.BuildCandidate(_settings.Current).Assistant, _viewModel.AssistantExtractionProfileId); await store.RetryFailedLearningAsync(AssistantLearningWorker.Fingerprint(profile), CancellationToken.None); await RefreshAssistantKnowledgeAsync(); }))));
        _assistantLearningStatus = Description("尚无提取任务。"); memory.Children.Add(_assistantLearningStatus);
        _assistantCandidateRows = new StackPanel(); memory.Children.Add(_assistantCandidateRows);
        var panel = new StackPanel(); panel.Children.Add(SectionTitle("项目知识库"));
        AssistantToggle(panel, "回答时使用相关知识片段", "AssistantUseKnowledge");
        panel.Children.Add(Description("文档留在本机SQLite，按会话项目隔离；全局资料可用于所有项目。默认仅本地，允许云端时仍只附加相关片段。导入不执行宏或代码。"));
        _assistantKnowledgeProject = KnowledgeCombo(); panel.Children.Add(_assistantKnowledgeProject);
        _assistantKnowledgeProject.SelectionChanged += (_, _) => { if (!_refreshingKnowledge) { _assistantKnowledgeProjectId = (_assistantKnowledgeProject.SelectedItem as AssistantProject)?.Id is { } id && id != Guid.Empty ? id : null; RefreshAssistantKnowledge(); } };
        panel.Children.Add(ButtonRow(Button("新增项目", (_, _) => _ = LibraryActionAsync(async () =>
        { if (_assistantStore is not IAssistantKnowledgeStore store) return; var name = AssistantTextDialog.Show(this, "项目名称", ""); if (string.IsNullOrWhiteSpace(name)) return; var project = await store.CreateProjectAsync(name.Trim(), CancellationToken.None); _assistantKnowledgeProjectId = project.Id; await RefreshAssistantKnowledgeAsync(); })),
            Button("删除项目", (_, _) => _ = LibraryActionAsync(async () =>
            { if (_assistantStore is not IAssistantKnowledgeStore store || _assistantKnowledgeProjectId is not { } id) return; if (!AssistantTextDialog.Confirm(this, "删除项目与派生资料", "删除此项目的知识文档、分块、记忆、候选和索引；保留原始文件与聊天记录。之后旧会话不再获得这些资料。")) return; await store.DeleteProjectAsync(id, CancellationToken.None); _assistantKnowledgeProjectId = null; await RefreshAssistantKnowledgeAsync(); }))));
        _assistantDocumentPolicy = new ComboBox { ItemsSource = new[] { new KnowledgePolicy("deny", "仅本地，禁止云端"), new KnowledgePolicy("ask", "云端发送前逐次确认"), new KnowledgePolicy("allow", "允许发送相关片段") }, DisplayMemberPath = "Name", SelectedValuePath = "Id", SelectedIndex = 0, Margin = new Thickness(0, 6, 0, 8) }; _assistantDocumentPolicy.SetResourceReference(StyleProperty, "SettingsComboBoxStyle"); panel.Children.Add(_assistantDocumentPolicy);
        panel.Children.Add(ButtonRow(Button("导入文档", (_, _) => _ = LibraryActionAsync(ImportAssistantDocumentAsync)), Button("刷新", (_, _) => RefreshAssistantKnowledge())));
        _assistantKnowledgeStatus = Description("全文检索无需模型。TXT/Markdown/代码、DOCX与PDF文字文档最大20MiB；扫描PDF需先OCR。"); panel.Children.Add(_assistantKnowledgeStatus);
        _assistantKnowledgeProgress = new ProgressBar { Height = 5, Minimum = 0, Maximum = 100, Margin = new Thickness(0, 5, 0, 10) }; panel.Children.Add(_assistantKnowledgeProgress);
        _assistantKnowledgeDocuments = new StackPanel(); panel.Children.Add(_assistantKnowledgeDocuments);
        panel.Children.Add(SectionTitle("检索预览")); _assistantKnowledgeQuery = ProfileInput(panel, "输入问题或关键词（本地全文/已配置的本地语义检索）");
        panel.Children.Add(ButtonRow(Button("检索", (_, _) => _ = LibraryActionAsync(PreviewAssistantRecallAsync))));
        _assistantKnowledgeOutput = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 100, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; _assistantKnowledgeOutput.SetResourceReference(StyleProperty, "SettingsTextBoxStyle"); panel.Children.Add(_assistantKnowledgeOutput);
        panel.Children.Add(SectionTitle("本地模型与语义索引"));
        AssistantToggle(panel, "启用本地向量与全文混合召回", "AssistantEmbeddingEnabled");
        panel.Children.Add(Description("向量连接需提供兼容embeddings接口；候选提取连接需提供聊天接口。均只允许回环本地地址，不下载模型，不自动改用云端。更换向量模型需重建；索引完成前使用全文检索。"));
        panel.Children.Add(Description("向量模型连接")); _assistantEmbeddingModels = KnowledgeCombo(); panel.Children.Add(_assistantEmbeddingModels);
        _assistantEmbeddingModels.SelectionChanged += (_, _) => { if (!_refreshingKnowledge) _viewModel.AssistantEmbeddingProfileId = (_assistantEmbeddingModels.SelectedItem as KnowledgeModelChoice)?.Id; };
        panel.Children.Add(Description("候选记忆提取连接")); _assistantExtractionModels = KnowledgeCombo(); panel.Children.Add(_assistantExtractionModels);
        _assistantExtractionModels.SelectionChanged += (_, _) => { if (!_refreshingKnowledge) _viewModel.AssistantExtractionProfileId = (_assistantExtractionModels.SelectedItem as KnowledgeModelChoice)?.Id; };
        panel.Children.Add(ButtonRow(Button("重建语义索引", (_, _) => _ = LibraryActionAsync(RebuildAssistantVectorsAsync)), Button("停止当前处理", (_, _) => CancelAssistantLibraryWork())));
        AddCategory(categories, "知识库", panel);
        categories.SelectionChanged += (_, e) => { if (ReferenceEquals(e.Source, categories) && (categories.SelectedItem as TabItem)?.Header?.ToString() is "知识库" or "记忆与隐私") RefreshAssistantKnowledge(); };
        Loaded += (_, _) => RefreshAssistantKnowledge(); Closing += (_, _) => CancelAssistantLibraryWork();
    }

    private static ComboBox KnowledgeCombo()
    { var combo = new ComboBox { DisplayMemberPath = "Name", SelectedValuePath = "Id", Margin = new Thickness(0, 6, 0, 8) }; combo.SetResourceReference(StyleProperty, "SettingsComboBoxStyle"); return combo; }
    internal void CancelAssistantLibraryWork() => _assistantLibraryWork?.Cancel();
    public void RefreshAssistantKnowledge() { if (_assistantKnowledgeProject is not null) _ = LibraryActionAsync(RefreshAssistantKnowledgeAsync); }
    private async Task LibraryActionAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { if (_assistantKnowledgeStatus is not null) _assistantKnowledgeStatus.Text = "已停止处理。"; }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or JsonException or Microsoft.Data.Sqlite.SqliteException or System.Net.Http.HttpRequestException)
        { var text = error is InvalidOperationException or InvalidDataException ? error.Message : "本地资料操作未完成，请检查格式、模型连接和数据目录。"; _assistantStatus.Text = text; if (_assistantKnowledgeStatus is not null) _assistantKnowledgeStatus.Text = text; }
    }
    private async Task RefreshAssistantKnowledgeAsync()
    {
        if (_assistantStore is not IAssistantKnowledgeStore store || _assistantKnowledgeProject is null) return;
        var version = ++_knowledgeRefreshVersion; var scope = _assistantKnowledgeProjectId;
        var projects = await store.ListProjectsAsync(CancellationToken.None); var documents = await store.ListDocumentsAsync(scope, CancellationToken.None); var candidates = await store.ListCandidatesAsync(CancellationToken.None); var sessions = await _assistantStore.ListSessionsAsync(CancellationToken.None);
        if (version != _knowledgeRefreshVersion) return;
        _assistantProjectNames.Clear(); foreach (var project in projects) _assistantProjectNames[project.Id] = project.Name;
        if (_assistantStore is AssistantStore concrete) { var summary = await concrete.LearningSummaryAsync(CancellationToken.None); _assistantLearningStatus!.Text = $"候选提取：待处理{summary.Queued}，处理中{summary.Running}，完成{summary.Done}，失败{summary.Failed}{(summary.LastFailure is null ? "" : "；最近失败类型：" + summary.LastFailure)}"; }
        _refreshingKnowledge = true;
        try
        {
            _assistantKnowledgeProject.ItemsSource = projects.Prepend(new AssistantProject(Guid.Empty, "全局知识库", DateTimeOffset.MinValue)).ToArray(); _assistantKnowledgeProject.SelectedValue = scope ?? Guid.Empty;
            var local = AssistantConnections.WithLegacyProfile(_settings.Current.Assistant).Profiles.Where(profile => AssistantEndpointPolicy.IsValid(profile.Endpoint) && AssistantEndpointPolicy.IsLocal(new Uri(profile.Endpoint))).Select(profile => new KnowledgeModelChoice(profile.Id, profile.Name + " / " + profile.Model + (!profile.Enabled || !profile.ModelEnabled ? "（已停用）" : ""))).ToArray();
            _assistantEmbeddingModels!.ItemsSource = local; _assistantEmbeddingModels.SelectedValue = _viewModel.AssistantEmbeddingProfileId;
            _assistantExtractionModels!.ItemsSource = local; _assistantExtractionModels.SelectedValue = _viewModel.AssistantExtractionProfileId;
            var selectedSession = (_assistantLearningSession!.SelectedItem as AssistantSession)?.Id; _assistantLearningSession.DisplayMemberPath = "Title"; _assistantLearningSession.ItemsSource = sessions; _assistantLearningSession.SelectedItem = sessions.FirstOrDefault(item => item.Id == selectedSession) ?? (sessions.Count > 0 ? sessions[0] : null);
        }
        finally { _refreshingKnowledge = false; }
        _assistantKnowledgeDocuments!.Children.Clear();
        foreach (var document in documents)
        {
            var row = new StackPanel { Margin = new Thickness(0, 5, 0, 12) }; var toggle = new CheckBox { Content = document.Name + $" · 修订{document.Revision} · {document.CloudPolicy}", IsChecked = document.Enabled }; toggle.SetResourceReference(StyleProperty, "ToggleSwitchStyle");
            toggle.Click += (_, _) => _ = LibraryActionAsync(async () => { await store.SetDocumentEnabledAsync(document.Id, toggle.IsChecked == true, CancellationToken.None); }); row.Children.Add(toggle);
            row.Children.Add(ButtonRow(Button("查看来源", (_, _) => _ = LibraryActionAsync(async () => { var chunks = await store.ReadDocumentPassagesAsync(document.Id, CancellationToken.None); AssistantTextDialog.Show(this, document.Name, string.Join("\n\n", chunks.Take(12).Select(chunk => chunk.Locator + "\n" + chunk.Content)), editable: false); })),
                Button("删除", (_, _) => _ = LibraryActionAsync(async () => { if (!AssistantTextDialog.Confirm(this, "删除知识文档", "删除保存的正文、分块和索引，保留磁盘原始文件。")) return; await store.DeleteDocumentAsync(document.Id, CancellationToken.None); await RefreshAssistantKnowledgeAsync(); })))); _assistantKnowledgeDocuments.Children.Add(row);
        }
        if (documents.Count == 0) _assistantKnowledgeDocuments.Children.Add(Description("当前项目尚无文档。"));
        _assistantCandidateRows!.Children.Clear();
        foreach (var candidate in candidates)
        {
            var row = new StackPanel { Margin = new Thickness(0, 5, 0, 12) }; row.Children.Add(new TextBlock { Text = candidate.Content, TextWrapping = TextWrapping.Wrap }); row.Children.Add(Description($"{candidate.Kind} / {candidate.Key} · 置信度{candidate.Confidence:P0} · {candidate.EvidenceCount}个独立会话证据 · {(candidate.ConflictId is null ? "待确认，不参与回答" : "存在冲突，需审核替换")}"));
            row.Children.Add(ButtonRow(Button(candidate.ConflictId is null ? "采纳" : "审核替换", (_, _) => _ = LibraryActionAsync(async () =>
            {
                var source = await store.ReadSourceMessageAsync(candidate.SourceMessageId, CancellationToken.None);
                var old = candidate.ConflictId is { } id ? (await _assistantStore.ListMemoriesAsync(CancellationToken.None)).FirstOrDefault(memory => memory.Id == id) : null;
                if (!AssistantTextDialog.Confirm(this, "审核候选记忆", $"候选：{candidate.Content}\n分类：{candidate.Kind} / {candidate.Key}\n置信度：{candidate.Confidence:P0}；独立会话证据：{candidate.EvidenceCount}\n来源原文：\n{source?.Content ?? "来源已删除"}\n\n{(old is null ? "确认后才参与召回。" : "将替代以下旧记忆（旧版本保留但不再召回）：\n" + old.Content)}")) return;
                await CheckAssistantStoredSecretsAsync(candidate.Content); await store.ApproveCandidateAsync(candidate.Id, candidate.ConflictId, candidate.ConflictRevision, CancellationToken.None); await RefreshAssistantLibraryAsync(); await RefreshAssistantKnowledgeAsync();
            })), Button("拒绝并抑制", (_, _) => _ = LibraryActionAsync(async () => { await store.RejectCandidateAsync(candidate.Id, CancellationToken.None); await RefreshAssistantKnowledgeAsync(); })))); _assistantCandidateRows.Children.Add(row);
        }
        if (candidates.Count == 0) _assistantCandidateRows.Children.Add(Description("没有待审核候选。"));
    }

    private CancellationTokenSource BeginLibraryWork()
    {
        if (_assistantLibraryWork is not null) throw new InvalidOperationException("已有资料处理正在进行，可先停止。");
        return _assistantLibraryWork = new CancellationTokenSource();
    }
    private async Task ImportAssistantDocumentAsync()
    {
        if (_assistantStore is not IAssistantKnowledgeStore store) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "知识文档|*.txt;*.md;*.markdown;*.docx;*.pdf;*.cs;*.py;*.js;*.ts;*.json;*.xml;*.yaml;*.yml;*.sql;*.cpp;*.h;*.rs;*.java;*.csv|所有文件|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        using var work = BeginLibraryWork(); _assistantKnowledgeProgress!.IsIndeterminate = true;
        try
        {
            var scope = _assistantKnowledgeProjectId; var imported = await AssistantDocumentImporter.LoadAsync(dialog.FileName, scope, _assistantDocumentPolicy!.SelectedValue as string ?? "deny", work.Token);
            await CheckAssistantStoredSecretsAsync(imported.ExtractedText);
            var preview = string.Join("\n\n", imported.Passages.Take(4).Select(chunk => chunk.Locator + "\n" + chunk.Content));
            if (!AssistantTextDialog.Confirm(this, "导入本地知识文档", $"{imported.Document.Name}\n{imported.Passages.Count}个分块；云端策略{imported.Document.CloudPolicy}\n以下为前几块预览，完整内容将留在本机：\n\n{preview}")) return;
            await store.SaveDocumentAsync(imported.Document, imported.Passages, work.Token); _assistantKnowledgeStatus!.Text = "文档已导入并建立全文索引；启用语义检索时可重建向量。"; await RefreshAssistantKnowledgeAsync();
        }
        finally { _assistantKnowledgeProgress.IsIndeterminate = false; _assistantLibraryWork = null; }
    }
    private AssistantRetrievalService CreateRetrieval(System.Net.Http.HttpClient http)
    { if (_assistantStore is not IAssistantKnowledgeStore store || _assistantCredentials is null) throw new InvalidOperationException("资料或凭据服务尚未初始化。"); return new(store, new AssistantEmbeddingClient(http), _assistantCredentials); }
    private async Task PreviewAssistantRecallAsync()
    {
        if (_assistantStore is not IAssistantKnowledgeStore) return;
        using var work = BeginLibraryWork();
        try
        {
            using var http = AssistantApiClient.CreateHttpClient(); var retrieval = CreateRetrieval(http); var settings = _viewModel.BuildCandidate(_settings.Current).Assistant with { UseMemory = true, Library = _viewModel.BuildCandidate(_settings.Current).Assistant.Library with { UseKnowledge = true } };
            string? fallbackStatus = null; retrieval.StatusChanged += status => fallbackStatus = status;
            var hits = await retrieval.RecallAsync(settings, _assistantKnowledgeQuery!.Text, _assistantKnowledgeProjectId, 10000, work.Token);
            _assistantKnowledgeOutput!.Text = (fallbackStatus is null ? "" : fallbackStatus + "\n\n") + (hits.Count == 0 ? "未找到相关资料。" : string.Join("\n\n", hits.Select(hit => $"[{hit.Kind}:{hit.Id}] {hit.Title} / {hit.Locator} / 修订{hit.Revision}\n{hit.Content}")));
        }
        finally { _assistantLibraryWork = null; }
    }
    private async Task RebuildAssistantVectorsAsync()
    {
        if (!_settings.Current.Assistant.Enabled) throw new InvalidOperationException("请先启用助手并应用，再调用本地模型。");
        using var work = BeginLibraryWork();
        try
        {
            using var http = AssistantApiClient.CreateHttpClient(); var progress = new Progress<(int Done, int Total)>(value => { _assistantKnowledgeProgress!.Value = value.Total == 0 ? 100 : value.Done * 100d / value.Total; _assistantKnowledgeStatus!.Text = $"本地向量索引：{value.Done}/{value.Total}"; });
            var count = await CreateRetrieval(http).RebuildAsync(_viewModel.BuildCandidate(_settings.Current).Assistant, progress, work.Token); _assistantKnowledgeStatus!.Text = $"已建立{count}个当前模型向量；过时或删除的片段不会写回。";
        }
        finally { _assistantLibraryWork = null; }
    }
    private async Task ExtractAssistantCandidatesAsync()
    {
        if (_assistantStore is not AssistantStore store || _assistantCredentials is null || _assistantLearningSession!.SelectedItem is not AssistantSession session) return;
        if (!_settings.Current.Assistant.Enabled) throw new InvalidOperationException("请先启用助手并应用，再调用本地模型。");
        var settings = _viewModel.BuildCandidate(_settings.Current).Assistant; settings = settings with { Library = settings.Library with { ExtractCandidates = true } };
        var profile = AssistantLibraryPolicy.LocalProfile(settings, settings.Library.ExtractionProfileId);
        if (!AssistantTextDialog.Confirm(this, "向本地模型提取候选", $"将所选会话的用户消息发送至本地接口{profile.Endpoint}，模型{profile.Model}。只生成待审核候选，不自动采纳。")) return;
        using var work = BeginLibraryWork();
        try
        {
            foreach (var source in (await store.ReadMessagesAsync(session.Id, work.Token)).Where(message => message.Role == "user")) await store.QueueLearningAsync(source.Id, AssistantLearningWorker.Fingerprint(profile), work.Token);
            using var http = AssistantApiClient.CreateHttpClient(); using var worker = new AssistantLearningWorker(store, new AssistantApiClient(http), _assistantCredentials); var processed = 0;
            while (processed < 100 && await worker.ProcessOnceAsync(settings, work.Token)) { processed++; _assistantKnowledgeStatus!.Text = $"已处理{processed}条本地提取任务。"; }
            await RefreshAssistantKnowledgeAsync(); _assistantKnowledgeStatus!.Text = "本地候选处理已结束；失败任务最多尝试3次，未确认候选不进入回答。";
        }
        finally { _assistantLibraryWork = null; }
    }

    private StackPanel CreateAssistantMemoryRow(AssistantMemory memory)
    {
        var row = new StackPanel { Margin = new Thickness(0, 6, 0, 12) };
        row.Children.Add(new TextBlock { Text = memory.Content, TextWrapping = TextWrapping.Wrap });
        row.Children.Add(Description($"{memory.Kind} / {memory.Key} · {(memory.ProjectId is null ? "全局" : _assistantProjectNames.GetValueOrDefault(memory.ProjectId.Value, "项目资料"))} · 修订{memory.Revision} · {(memory.State == "active" ? "已确认" : "已替代")} · 置信度{memory.Confidence:P0} · {(memory.CloudPolicy == "deny" ? "仅本地" : memory.CloudPolicy == "ask" ? "云端确认" : "允许相关片段")} · {(memory.Enabled ? "启用" : "停用")}"));
        var toggle = Button(memory.Enabled ? "停用" : "启用", (_, _) => _ = LibraryActionAsync(async () => { await _assistantStore!.SaveMemoryAsync(memory with { Enabled = !memory.Enabled }, CancellationToken.None); await RefreshAssistantLibraryAsync(); })); toggle.IsEnabled = memory.State == "active";
        row.Children.Add(ButtonRow(Button("编辑属性", (_, _) => _ = LibraryActionAsync(async () =>
        { if (_assistantStore is not IAssistantKnowledgeStore knowledge) return; var edited = AssistantMemoryEditor.Edit(this, memory, await knowledge.ListProjectsAsync(CancellationToken.None)); if (edited is null) return; await CheckAssistantStoredSecretsAsync(edited.Content + "\n" + edited.Key); await _assistantStore.SaveMemoryAsync(edited, CancellationToken.None); await RefreshAssistantLibraryAsync(); })), toggle,
            Button("查看来源", (_, _) => _ = LibraryActionAsync(async () =>
            { if (_assistantStore is not IAssistantKnowledgeStore knowledge || memory.SourceMessageId is not { } id) { AssistantTextDialog.Show(this, "来源", "此记忆由用户手动添加。", editable: false); return; } var source = await knowledge.ReadSourceMessageAsync(id, CancellationToken.None); AssistantTextDialog.Show(this, "记忆来源", source is null ? "来源已删除。" : $"消息{source.Id}\n会话{source.SessionId}\n{source.CreatedAt:g}\n\n{source.Content}", editable: false); })),
            Button("修订历史", (_, _) => _ = LibraryActionAsync(async () => { if (_assistantStore is not IAssistantKnowledgeStore knowledge) return; var revisions = await knowledge.ReadMemoryRevisionsAsync(memory.Id, CancellationToken.None); AssistantTextDialog.Show(this, "记忆修订", string.Join("\n\n", revisions.Select(item => $"修订{item.Revision} · {item.UpdatedAt:g}\n{item.Content}")), editable: false); })),
            Button("删除", (_, _) => _ = LibraryActionAsync(async () => { if (!AssistantTextDialog.Confirm(this, "删除记忆与索引", "删除记忆、修订、索引与派生候选，抑制从旧来源再次提取。")) return; await _assistantStore!.DeleteMemoryAsync(memory.Id, CancellationToken.None); await RefreshAssistantLibraryAsync(); await RefreshAssistantKnowledgeAsync(); }))));
        return row;
    }
}
