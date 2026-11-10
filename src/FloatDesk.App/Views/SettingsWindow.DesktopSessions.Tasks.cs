using FloatDesk.Core.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Text.Json;

namespace FloatDesk.App.Views;

public partial class SettingsWindow
{
    private TabControl _desktopTaskBackendTabs = null!;
    private TextBox _desktopTaskRules = null!;
    private TextBox _desktopTaskJson = null!;
    private TextBox _desktopScriptName = null!;
    private TextBox _desktopScriptContent = null!;
    private CheckBox _desktopScriptAdministrator = null!;
    private CheckBox _desktopMcpEnvironmentGrant = null!;
    private TextBlock _desktopTaskApprovalStatus = null!;
    private DesktopSessionBackendKind SelectedTaskBackend => _desktopTaskBackendTabs?.SelectedIndex==1?DesktopSessionBackendKind.HyperV:DesktopSessionBackendKind.LocalChild;
    private bool SelectedTaskGlobal => _desktopTaskBackendTabs?.SelectedIndex==2;
    private EnvironmentProfile ResolveTaskEnvironment(Guid id)=>GlobalTaskTarget.Find(_viewModel.BuildDesktopSessionDraft(),id)??throw new InvalidOperationException("未找到任务目标。");

    private Expander BuildTaskPackageEditor()
    {
        var panel=new StackPanel();
        panel.Children.Add(ButtonRow(Button("导入任务 JSON",ImportDesktopTask),Button("导出任务 JSON",ExportDesktopTask),
            Button("操作说明",ShowTaskAuthoringGuide),Button("复制 MCP 配置",CopyTaskMcpConfiguration)));
        _desktopMcpEnvironmentGrant=new CheckBox {Content="允许外部 MCP 实时操作所选环境",Style=(Style)FindResource("ToggleSwitchStyle"),Margin=new Thickness(0,12,0,8)};
        _desktopMcpEnvironmentGrant.Click+=(_,_)=>
        {
            if(_desktopTaskEnvironment.SelectedItem is not EnvironmentProfile env) return;
            _viewModel.SetMcpControlAllowed(env.Id,_desktopMcpEnvironmentGrant.IsChecked==true);
            SetDesktopSessionStatus("环境 MCP 实时控制偏好已更新，点击应用后生效。脚本任务仍需单独审核。");
        };
        panel.Children.Add(_desktopMcpEnvironmentGrant);
        _desktopMcpEnvironmentGrant.Visibility=Visibility.Collapsed;
        _desktopTaskEnvironment.SelectionChanged+=(_,_)=>
        {
            var env=_desktopTaskEnvironment.SelectedItem as EnvironmentProfile;
            _desktopMcpEnvironmentGrant.IsChecked=env is not null&&_viewModel.IsMcpControlAllowed(env.Id);
            _desktopMcpEnvironmentGrant.IsEnabled=env is not null;
        };
        panel.Children.Add(Description("本地分身与主桌面共用操作系统；自定义脚本可访问当前账号的资源。仅对信任的 agent 和脚本授权，系统配置任务建议使用虚拟机。"));
        _desktopTaskApprovalStatus=Description("请选择任务。");panel.Children.Add(_desktopTaskApprovalStatus);
        panel.Children.Add(Description("已信任的 agent 可直接保存和执行任务。手动开启任务并应用后，按任务的触发方式自动生效。"));
        panel.Children.Add(SectionTitle("规则说明（保存为任务数据，不自动解释执行）"));
        _desktopTaskRules=CodeInput(100);panel.Children.Add(_desktopTaskRules);
        panel.Children.Add(ButtonRow(Button("保存规则",()=>EditSelectedTask(task=>task with {Rules=_desktopTaskRules.Text,McpApprovalHash=null})),Button("导入规则文本",ImportDesktopRules)));
        panel.Children.Add(SectionTitle("内嵌 PowerShell 脚本"));
        _desktopScriptName=AddVmInput(panel,"脚本名称（例如 task.ps1）","task.ps1");
        _desktopScriptContent=CodeInput(170);panel.Children.Add(_desktopScriptContent);
        _desktopScriptAdministrator=new CheckBox{Content="在虚拟机内以管理员运行",Style=(Style)FindResource("ToggleSwitchStyle"),Margin=new Thickness(0,8,0,8)};
        panel.Children.Add(_desktopScriptAdministrator);
        panel.Children.Add(ButtonRow(Button("导入 .ps1 脚本",ImportDesktopScript),Button("保存脚本并添加步骤",SaveEmbeddedDesktopScript)));
        panel.Children.Add(Description("脚本内容随任务保存，运行时按摘要校验并投递到目标分身；虚拟机管理员脚本需要已验证保存的来宾管理员凭据。不要在规则或脚本中保存密码。"));
        panel.Children.Add(SectionTitle("任务 JSON（完整编写与导入格式）"));
        _desktopTaskJson=CodeInput(220);panel.Children.Add(_desktopTaskJson);
        panel.Children.Add(ButtonRow(Button("刷新 JSON",()=>RefreshTaskPackageEditor(SelectedTask())),Button("保存 JSON 为任务",SaveDesktopTaskJson)));
        return new Expander{Header="规则、脚本、导入导出与 MCP",Content=panel,IsExpanded=false,Style=(Style)FindResource("DesktopSessionExpanderStyle"),Margin=new Thickness(0,12,0,0)};
    }

    private TextBox CodeInput(double height)=>new(){AcceptsReturn=true,AcceptsTab=true,Height=height,TextWrapping=TextWrapping.NoWrap,
        VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,
        FontFamily=new FontFamily("Cascadia Mono, Consolas"),Style=(Style)FindResource("SettingsTextBoxStyle"),Margin=new Thickness(0,6,0,8)};

    private void RefreshTaskPackageEditor(DesktopSessionTask? task)
    {
        if(_desktopTaskJson is null)return;
        _desktopTaskJson.Text=task is null?"":DesktopTaskPackagePolicy.Serialize(task);
        _desktopTaskRules.Text=task?.Rules??"";
        var firstScript=task?.Scripts.Count>0?task.Scripts[0]:null;
        _desktopScriptName.Text=firstScript?.Name??"task.ps1";
        _desktopScriptContent.Text=firstScript?.Content??"";
        _desktopScriptAdministrator.Visibility=SelectedTaskBackend==DesktopSessionBackendKind.HyperV?Visibility.Visible:Visibility.Collapsed;
        if(SelectedTaskBackend!=DesktopSessionBackendKind.HyperV)_desktopScriptAdministrator.IsChecked=false;
        _desktopTaskApprovalStatus.Text=task is null?"请选择或添加此标签下的任务。":
            $"来源：{task.Source} · 修订 {task.Revision} · {(task.Enabled?"已开启":"已关闭")}";
        if(task is null){_desktopTaskName.Clear();_desktopTaskEnabled.IsChecked=false;}
    }

    private void EditSelectedTask(Func<DesktopSessionTask,DesktopSessionTask> edit)
    {
        try
        {
            var current=SelectedTask()??throw new InvalidOperationException("请先选择任务。");
            var changed=edit(current);
            var environment=ResolveTaskEnvironment(changed.EnvironmentId);
            DesktopTaskPackagePolicy.Validate(changed,environment.Backend);
            _viewModel.UpdateDesktopSessionTask(current.Id,_=>changed);RefreshDesktopSessionsPage();
            SetDesktopSessionStatus("任务已更新，点击应用保存。");
        }
        catch(Exception exception){SetDesktopSessionStatus(exception.Message,false);}
    }

    private void ApproveDesktopTask(bool approve)=>EditSelectedTask(task=>
    {
        if(!approve)return task with {McpApprovalHash=null,McpEnvironmentBinding=null};
        var environment=ResolveTaskEnvironment(task.EnvironmentId);
        var candidate=task with {Enabled=true,Revision=checked(task.Revision+1),McpApprovalHash=null,
            McpEnvironmentBinding=DesktopTaskPackagePolicy.EnvironmentBinding(environment)};
        return candidate with {McpApprovalHash=DesktopTaskPackagePolicy.DefinitionHash(candidate)};
    });

    private void SaveEmbeddedDesktopScript()=>EditSelectedTask(task=>
    {
        var script=DesktopTaskScript.Create(_desktopScriptName.Text.Trim(),_desktopScriptContent.Text);
        var step=new SessionTaskStep(Guid.NewGuid(),_desktopScriptAdministrator.IsChecked==true?SessionCommandKind.RunSystemScript:SessionCommandKind.RunScript,
            new Dictionary<string,string>{{"scriptName",script.Name},{"arguments","[]"}},120,0);
        return task with {Scripts=task.Scripts.Where(asset=>!asset.Name.Equals(script.Name,StringComparison.OrdinalIgnoreCase)).Append(script).ToArray(),
            Steps=[..task.Steps,step],McpApprovalHash=null};
    });

    private void ImportDesktopScript()
    {
        var dialog=new Microsoft.Win32.OpenFileDialog{Title="导入脚本（不会自动执行）",Filter="PowerShell|*.ps1"};
        if(dialog.ShowDialog(this)!=true)return;
        try{if(new FileInfo(dialog.FileName).Length>65536)throw new InvalidOperationException("脚本文件不得超过 64 KiB。");
            _desktopScriptName.Text=Path.GetFileName(dialog.FileName);_desktopScriptContent.Text=File.ReadAllText(dialog.FileName);}
        catch(Exception exception){SetDesktopSessionStatus(exception.Message,false);}
    }
    private void ImportDesktopRules()
    {
        var dialog=new Microsoft.Win32.OpenFileDialog{Title="导入规则说明",Filter="规则文本|*.md;*.txt"};
        if(dialog.ShowDialog(this)!=true)return;
        try{if(new FileInfo(dialog.FileName).Length>32768)throw new InvalidOperationException("规则文件不得超过 32 KiB。");
            _desktopTaskRules.Text=File.ReadAllText(dialog.FileName);EditSelectedTask(task=>task with{Rules=_desktopTaskRules.Text,McpApprovalHash=null});}
        catch(Exception exception){SetDesktopSessionStatus(exception.Message,false);}
    }
    private void ImportDesktopTask()
    {
        var dialog=new Microsoft.Win32.OpenFileDialog{Title="导入任务到所选环境（停用草稿）",Filter="任务 JSON|*.json"};
        if(dialog.ShowDialog(this)!=true)return;
        try
        {
            if(new FileInfo(dialog.FileName).Length>524288)throw new InvalidOperationException("任务文件不得超过 512 KiB。");
            var environment=_desktopTaskEnvironment.SelectedItem as EnvironmentProfile??
                _desktopTaskScope.SelectedItem as EnvironmentProfile??throw new InvalidOperationException("请先选择此标签对应的分身。");
            var task=DesktopTaskPackagePolicy.AsExternalDraft(DesktopTaskPackagePolicy.Parse(File.ReadAllText(dialog.FileName)),"import") with {Id=Guid.NewGuid(),EnvironmentId=environment.Id,Revision=1};
            DesktopTaskPackagePolicy.Validate(task,environment.Backend);
            _viewModel.AddDesktopSessionTask(task);_selectedDesktopTaskId=task.Id;RefreshDesktopSessionsPage();
            SetDesktopSessionStatus("已导入为停用草稿，检查内容后启用／授权并应用。");
        }
        catch(Exception exception){SetDesktopSessionStatus(exception.Message,false);}
    }
    private void SaveDesktopTaskJson()
    {
        try
        {
            var task=DesktopTaskPackagePolicy.Parse(_desktopTaskJson.Text);
            var environment=_desktopTaskScope.SelectedItem as EnvironmentProfile;
            if(environment is null || environment.Id!=task.EnvironmentId)
                throw new InvalidOperationException("JSON 目标必须是当前下拉列表选中的分身。");
            if(_viewModel.DesktopSessionTasks.Any(existing=>existing.Id==task.Id&&existing.EnvironmentId!=environment.Id))
                throw new InvalidOperationException("此任务 ID 属于其他分身，请使用新 ID 或导入为新任务。");
            task=task with{McpApprovalHash=null,Scripts=task.Scripts.Select(script=>DesktopTaskScript.Create(script.Name,script.Content)).ToArray()};
            DesktopTaskPackagePolicy.Validate(task,environment.Backend);
            if(_viewModel.DesktopSessionTasks.Any(existing=>existing.Id==task.Id))_viewModel.UpdateDesktopSessionTask(task.Id,_=>task);
            else _viewModel.AddDesktopSessionTask(task);
            _selectedDesktopTaskId=task.Id;RefreshDesktopSessionsPage();SetDesktopSessionStatus("JSON 已写入任务，点击应用保存。MCP 授权已清除。");
        }
        catch(Exception exception){SetDesktopSessionStatus(exception.Message,false);}
    }
    private void ExportDesktopTask()
    {
        if(SelectedTask() is not {} task)return;
        var dialog=new Microsoft.Win32.SaveFileDialog{Title="导出任务、规则和脚本",Filter="任务 JSON|*.json",FileName="desktop-task.json"};
        try
        {
            if(dialog.ShowDialog(this)==true)File.WriteAllText(dialog.FileName,DesktopTaskPackagePolicy.Serialize(task with{McpApprovalHash=null,McpEnvironmentBinding=null}));
        }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException)
        { SetDesktopSessionStatus("导出失败："+exception.Message,false); }
    }
    private void CopyTaskMcpConfiguration()
    {
        Clipboard.SetText(JsonSerializer.Serialize(new{mcpServers=new Dictionary<string,object>{{"yueyu",new{command=Path.Combine(AppContext.BaseDirectory,"desktop-sessions","mcp","YueYuOrb.McpServer.exe"),args=Array.Empty<string>()}}}},DesktopTaskPackagePolicy.JsonOptions));
        SetDesktopSessionStatus("MCP 配置已复制。请保持月语运行，并在外部客户端添加此 stdio 服务。");
    }
    private void ShowTaskAuthoringGuide()
    {
        var path=Path.Combine(AppContext.BaseDirectory,"docs","desktop-automation-guide.md");
        var text=File.Exists(path)?File.ReadAllText(path):"先选本地分身或虚拟机标签，再添加任务。步骤、规则和脚本均可手动编写与导入。MCP 写入先保存为草稿，审核当前版本并应用后可运行；脱离 agent 后可手动或定时执行。完整说明位于 docs/desktop-automation-guide.md。";
        var viewer=CodeInput(520);viewer.Text=text;viewer.IsReadOnly=true;
        var dialog=new Window{Owner=this,Title="自动任务编写与执行说明",Width=900,Height=620,Content=viewer,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        dialog.SetResourceReference(Window.BackgroundProperty,"SettingsCardBrush");dialog.SetResourceReference(Window.ForegroundProperty,"SettingsInkBrush");
        dialog.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("/YueYuOrb;component/Resources/SettingsWindowResources.xaml",UriKind.Relative)});
        GlassWindowFrame.WrapStandardWindow(dialog);dialog.ShowDialog();
    }
}
