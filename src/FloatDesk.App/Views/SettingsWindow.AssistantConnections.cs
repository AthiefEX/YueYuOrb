using System.Windows;
using System.Windows.Controls;
using FloatDesk.Core.Assistant;
using FloatDesk.Infrastructure.Assistant;

namespace FloatDesk.App.Views;

public partial class SettingsWindow
{
    private ComboBox _assistantProfiles = null!;
    private ComboBox _assistantModels = null!;
    private TextBox _assistantManualModel = null!;
    private TextBox _assistantProfileName = null!;
    private TextBox _assistantProfileEndpoint = null!;
    private CheckBox _assistantProfileEnabled = null!;
    private ListBox _assistantProfileRows = null!;
    private TextBlock _assistantProfileSummary = null!;
    private AssistantApiProfile? _editingAssistantProfile;
    private bool _refreshingAssistantProfiles;
    private int _assistantDiscoveryVersion;
    private readonly Dictionary<(Guid Id, string Endpoint), IReadOnlyList<string>> _assistantDiscoveredModels = [];
    private ListBox? _assistantAvailableModels;
    private CheckBox? _assistantModelEnabled;
    private TextBox? _assistantModelResult;

    private void InitializeAssistantConnectionManager(Panel panel, Action? openAssistant) => InitializeCompactConnections(panel, openAssistant);

    private static TextBox ProfileInput(Panel panel, string label)
    {
        panel.Children.Add(Description(label)); var input = new TextBox { MaxLength = 2048, Margin = new Thickness(0, 6, 0, 8) }; input.SetResourceReference(StyleProperty, "SettingsTextBoxStyle"); panel.Children.Add(input); return input;
    }

    private void ShowAssistantProfile(AssistantApiProfile profile)
    {
        var wasRefreshing = _refreshingAssistantProfiles; _refreshingAssistantProfiles = true;
        _assistantProfiles.SelectedItem = (_assistantProfiles.ItemsSource as IEnumerable<AssistantApiProfile>)?.FirstOrDefault(item => item.Id == profile.Id);
        _refreshingAssistantProfiles = wasRefreshing;
        _assistantDiscoveryVersion++; _editingAssistantProfile = profile; _assistantKey.Clear(); _assistantProfileName.Text = profile.Name;
        _assistantProfileEndpoint.Text = profile.Endpoint; _assistantModels.ItemsSource = _assistantDiscoveredModels.GetValueOrDefault((profile.Id, profile.Endpoint)); _assistantModels.Text = profile.Model; _assistantManualModel.Text = profile.Model; _assistantProfileEnabled.IsChecked = profile.Enabled;
        RefreshConnectionModels();
    }

    private void RefreshAssistantProfiles(Guid? select = null)
    {
        var settings = AssistantConnections.WithLegacyProfile(_settings.Current.Assistant);
        var selected = select ?? _editingAssistantProfile?.Id ?? settings.ActiveProfileId;
        _refreshingAssistantProfiles = true; _assistantProfiles.ItemsSource = settings.Profiles; _assistantProfiles.SelectedItem = settings.Profiles.FirstOrDefault(item => item.Id == selected); _refreshingAssistantProfiles = false;
        BuildAssistantProfileRows(settings);
        if (_assistantProfiles.SelectedItem is AssistantApiProfile profile) ShowAssistantProfile(profile);
        UpdateConnectionRoleSwitches();
    }
    private async Task CommitAssistantProfilesAsync(Func<AssistantSettings, AssistantSettings> update)
    {
        var result = await _settings.UpdateAsync(current => current with { Assistant = update(AssistantConnections.WithLegacyProfile(current.Assistant)) }, CancellationToken.None);
        if (!result.Success) throw new InvalidOperationException("连接设置未保存，请检查API地址和模型。");
    }

    private async Task<AssistantApiProfile> SaveAssistantProfileAsync()
    {
        if (_assistantCredentials is null) throw new InvalidOperationException("凭据服务未初始化。");
        var profile = new AssistantApiProfile(_editingAssistantProfile?.Id ?? Guid.NewGuid(), _assistantProfileName.Text.Trim(), _assistantProfileEndpoint.Text.Trim(), _assistantModels.Text.Trim(), _assistantProfileEnabled.IsChecked == true) { UsesLegacyKey = _editingAssistantProfile?.UsesLegacyKey == true, ModelEnabled = _editingAssistantProfile?.ModelEnabled ?? true };
        if (AssistantSettingsValidator.Validate(new() { Profiles = [profile] }).Count != 0) throw new InvalidOperationException("请填写有效连接名称、API地址与模型名。");
        var purpose = AssistantConnections.CredentialPurpose(profile); var previous = await _assistantCredentials.GetAsync(purpose, profile.Endpoint, CancellationToken.None); var key = _assistantKey.Password;
        if (key.Length == 0 && previous is null && !AssistantEndpointPolicy.IsLocal(new Uri(profile.Endpoint))) throw new InvalidOperationException("请先输入该连接的密钥。");
        if (key.Length > 0) { await _assistantCredentials.SaveAsync(purpose, profile.Endpoint, key, CancellationToken.None); _assistantDiscoveredModels.Remove((profile.Id, profile.Endpoint)); foreach (var health in _assistantModelHealth.Keys.Where(item => item.Key == profile.Id && item.Endpoint == profile.Endpoint).ToArray()) _assistantModelHealth.Remove(health); }
        try
        {
            await CommitAssistantProfilesAsync(current => current with { Profiles = current.Profiles.Where(item => item.Id != profile.Id).Append(profile).ToArray(),
                Endpoint = current.ActiveProfileId == profile.Id ? profile.Endpoint : current.Endpoint, Model = current.ActiveProfileId == profile.Id ? profile.Model : current.Model });
        }
        catch
        {
            if (key.Length > 0) { if (previous is null) await _assistantCredentials.DeleteAsync(purpose, profile.Endpoint, CancellationToken.None); else await _assistantCredentials.SaveAsync(purpose, profile.Endpoint, previous, CancellationToken.None); }
            throw;
        }
        _assistantKey.Clear(); RefreshAssistantProfiles(profile.Id); _assistantStatus.Text = "密钥已加密保存。"; return profile;
    }

    private async Task DiscoverAssistantModelsAsync(AssistantApiProfile profile)
    {
        if (!profile.Enabled) throw new InvalidOperationException("此连接已停用，请先启用。");
        var version = ++_assistantDiscoveryVersion; _assistantTest?.Cancel(); _assistantTest = new CancellationTokenSource(TimeSpan.FromSeconds(22)); var token = _assistantTest.Token;
        var key = await _assistantCredentials!.GetAsync(AssistantConnections.CredentialPurpose(profile), profile.Endpoint, token);
        _assistantStatus.Text = "正在发现模型…"; using var http = AssistantApiClient.CreateHttpClient();
        var models = await new AssistantModelCatalog(http).ListAsync(profile.Endpoint, key, _viewModel.AssistantAllowPrivateEndpoint, token);
        if (version != _assistantDiscoveryVersion || _editingAssistantProfile?.Id != profile.Id) return;
        _assistantDiscoveredModels[(profile.Id, profile.Endpoint)] = models;
        _assistantModels.ItemsSource = models; _assistantModels.Text = profile.Model.Length == 0 ? models.Count > 0 ? models[0] : "" : profile.Model;
        RefreshConnectionModels();
        _assistantManualModel.Text = _assistantModels.Text;
        _assistantStatus.Text = $"发现{models.Count}个模型。请选择后保存或用于聊天/搜索；仍需检测实际可用性。";
    }

    private Task TestAssistantProfileModelAsync() => TestAssistantModelsAsync(false);

    private async Task UseAssistantProfileAsync(bool search)
    {
        if (_editingAssistantProfile is not { Enabled: true, ModelEnabled: true } profile || string.IsNullOrWhiteSpace(_assistantManualModel.Text)) throw new InvalidOperationException("请选择模型并启用此连接。");
        var model = _assistantManualModel.Text.Trim();
        await CommitAssistantProfilesAsync(current => search
            ? AssistantWorkspacePolicy.SetSearch(current, profile.Id, model, true)
            : current with { ActiveProfileId = profile.Id, ChatModelId = model, ChatModelEnabled = true, Endpoint = profile.Endpoint, Model = model, Profiles = current.Profiles.Select(item => item.Id == profile.Id && string.IsNullOrWhiteSpace(item.Model) ? item with { Model = model } : item).ToArray() });
        if (search) _viewModel.AssistantSearchEnabled = true;
        else { _viewModel.AssistantEndpoint = profile.Endpoint; _viewModel.AssistantModel = model; }
        RefreshAssistantProfiles(profile.Id); _assistantManualModel.Text = model; RefreshConnectionModels();
    }
    private async Task DeleteAssistantProfileAsync()
    {
        if (_editingAssistantProfile is not { } profile || !AssistantTextDialog.Confirm(this, "删除密钥连接", $"删除“{profile.Name}”及其保存密钥？其他连接不受影响。")) return;
        await CommitAssistantProfilesAsync(current => current with { Profiles = current.Profiles.Where(item => item.Id != profile.Id).ToArray(),
            Enabled = current.ActiveProfileId == profile.Id ? false : current.Enabled, ActiveProfileId = current.ActiveProfileId == profile.Id ? null : current.ActiveProfileId,
            ChatModelId = current.ActiveProfileId == profile.Id ? "" : current.ChatModelId, SearchModelId = current.SearchProfileId == profile.Id ? "" : current.SearchModelId,
            Endpoint = current.ActiveProfileId == profile.Id || current.Profiles.Count == 1 ? "" : current.Endpoint, Model = current.ActiveProfileId == profile.Id || current.Profiles.Count == 1 ? "" : current.Model,
            SearchEnabled = current.SearchProfileId == profile.Id ? false : current.SearchEnabled, SearchProfileId = current.SearchProfileId == profile.Id ? null : current.SearchProfileId });
        if (_assistantCredentials is not null) await _assistantCredentials.DeleteAsync(AssistantConnections.CredentialPurpose(profile), profile.Endpoint, CancellationToken.None);
        _editingAssistantProfile = null; _assistantKey.Clear(); _assistantProfileName.Clear(); _assistantModels.Text = ""; _assistantManualModel.Text = ""; _assistantAvailableModels!.ItemsSource = null;
        _viewModel.AssistantEnabled = _settings.Current.Assistant.Enabled; _viewModel.AssistantEndpoint = _settings.Current.Assistant.Endpoint; _viewModel.AssistantModel = _settings.Current.Assistant.Model;
        _viewModel.AssistantSearchEnabled = _settings.Current.Assistant.SearchEnabled; RefreshAssistantProfiles(); _assistantStatus.Text = "连接已删除。";
    }
}
