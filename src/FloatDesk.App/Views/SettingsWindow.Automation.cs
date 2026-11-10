using FloatDesk.Core.Models;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace FloatDesk.App.Views;

public partial class SettingsWindow
{
    private TabItem _automationSettingsTab=null!;
    private StackPanel _automationClientRows=null!;
    private ListBox? _automationActivityList;
    public void SelectAutomationPage()=>SettingsTabControl.SelectedItem=_automationSettingsTab;

    private void InitializeAutomationSettings()
    {
        var taskFold=_desktopFoldedSections[1];
        var taskContent=(FrameworkElement)taskFold.Content;
        taskFold.Content=null;
        ((Panel)taskFold.Parent).Children.Remove(taskFold);
        var automationRoot=new StackPanel{Margin=new Thickness(28,16,28,24),MaxWidth=1080};
        var clientCard=Card();var clients=new StackPanel();
        clients.Children.Add(SectionTitle("外部 agent 连接"));
        clients.Children.Add(Description("信任后可控制主桌面、分身和虚拟机，并保存可直接生效的任务。未确认或取消的连接可在此开启；关闭只撤销后续外部操作，不停用已保存任务。"));
        _automationClientRows=new StackPanel();clients.Children.Add(new ScrollViewer{Content=_automationClientRows,MaxHeight=220,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});clientCard.Child=clients;
        automationRoot.Children.Add(clientCard);automationRoot.Children.Add(taskContent);
        _automationSettingsTab=new TabItem{Header="自动任务",Style=(Style)FindResource("NavigationTabItemStyle"),
            Content=new ScrollViewer{Content=automationRoot,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled}};
        AutomationProperties.SetName(_automationSettingsTab,"自动任务");
        SettingsTabControl.Items.Insert(SettingsTabControl.Items.IndexOf(DesktopSessionsSettingsTab)+1,_automationSettingsTab);
        ConvertExpanderGroups(taskContent);
        if(taskContent is Panel taskPanel&&taskPanel.Children.OfType<TabControl>().LastOrDefault() is {} editorTabs)
        {
            var activity=new StackPanel();
            var status=Description("");
            status.SetBinding(TextBlock.TextProperty,new System.Windows.Data.Binding(nameof(TextBlock.Text)){Source=_desktopSessionStatus});
            activity.Children.Add(status);activity.Children.Add(Description("最近运行记录（所有任务目标）"));
            _automationActivityList=new ListBox{Height=180,Style=(Style)FindResource("ToolWindowListBoxStyle")};
            activity.Children.Add(_automationActivityList);AddCategory(editorTabs,"运行记录",activity);
        }
        RefreshAutomationClients();
        _settings.Changed+=OnAutomationSettingsChanged;
    }

    private void OnAutomationSettingsChanged(AppSettings settings)
    {
        _=Dispatcher.InvokeAsync(()=>
        {
            var selected=SelectedTask();
            var dirty=selected is not null&&HasPendingTaskEdits(selected);
            var inputs=new[]{_desktopTaskRules,_desktopTaskJson,_desktopTaskName,_desktopScriptName,_desktopScriptContent,
                _desktopTaskTime,_desktopTaskTimeZone,_desktopStepTimeout,_desktopStepRetry};
            var values=inputs.Select(input=>input.Text).ToArray();
            var parameters=_stepParameterInputs.ToDictionary(pair=>pair.Key,pair=>pair.Value.Text);
            var trigger=_desktopTaskTrigger.SelectedItem;var enabled=_desktopTaskEnabled.IsChecked;var kind=_desktopStepKind.SelectedItem;
            _viewModel.MergeExternalTaskUpdates(settings.DesktopSessions,dirty?selected!.Id:null);
            RefreshDesktopTaskScope();
            if(dirty&&SelectedTask()?.Id==selected!.Id)
            {
                _desktopTaskTrigger.SelectedItem=trigger;_desktopTaskEnabled.IsChecked=enabled;_desktopStepKind.SelectedItem=kind;
                for(var index=0;index<inputs.Length;index++)inputs[index].Text=values[index];
                foreach(var pair in parameters)if(_stepParameterInputs.TryGetValue(pair.Key,out var input))input.Text=pair.Value;
            }
            RefreshAutomationClients();
            if(dirty&&settings.DesktopSessions.Tasks.FirstOrDefault(task=>task.Id==selected!.Id) is {} external&&external.Revision!=selected!.Revision)
                SetDesktopSessionStatus("正在编辑的任务有外部更新；已保留输入，请重新加载并合并后保存。",false);
        });
    }
    private bool HasPendingTaskEdits(DesktopSessionTask task)
    {
        var script=task.Scripts.Count>0?task.Scripts[0]:null;
        if(_desktopTaskRules.Text!=task.Rules||_desktopTaskJson.Text!=DesktopTaskPackagePolicy.Serialize(task)||_desktopTaskName.Text!=task.Name||
            _desktopScriptContent.Text!=(script?.Content??"")||_desktopScriptName.Text!=(script?.Name??"task.ps1")||
            _desktopTaskEnabled.IsChecked!=task.Enabled||!Equals(_desktopTaskTrigger.SelectedItem,task.Trigger.Kind)||
            _desktopTaskTime.Text!=(task.Trigger.LocalTime??"")||_desktopTaskTimeZone.Text!=(task.Trigger.TimeZoneId??TimeZoneInfo.Local.Id))return true;
        return _desktopStepList.SelectedItem is SessionTaskStep step&&(_stepParameterInputs.Any(pair=>pair.Value.Text!=(step.Parameters.GetValueOrDefault(pair.Key)??""))||
            !Equals(_desktopStepKind.SelectedItem,step.Kind)||_desktopStepTimeout.Text!=step.TimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)||
            _desktopStepRetry.Text!=step.RetryCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
    private void RefreshAutomationClients()
    {
        _automationClientRows.Children.Clear();
        foreach(var client in _settings.Current.DesktopSessions.AutomationClients.OrderByDescending(item=>item.LastConnectedUtc))
        {
            var row=new DockPanel{Margin=new Thickness(0,12,0,0)};
            var toggle=new CheckBox{IsChecked=client.Enabled,ToolTip=client.ExecutablePath+"\nSHA256: "+client.Sha256};
            toggle.SetResourceReference(FrameworkElement.StyleProperty,"ToggleSwitchStyle");
            AutomationProperties.SetName(toggle,"信任连接："+client.Name);
            toggle.Click+=async(_,_)=>
            {
                var enabled=toggle.IsChecked==true;
                var result=await _settings.UpdateAsync(current=>current with{DesktopSessions=current.DesktopSessions with
                {AutomationClients=current.DesktopSessions.AutomationClients.Select(item=>item.Id==client.Id?item with{Enabled=enabled}:item).ToArray()}},CancellationToken.None);
                if(!result.Success){toggle.IsChecked=!enabled;SetDesktopSessionStatus("连接权限未保存，请重试。",false);}
            };
            DockPanel.SetDock(toggle,Dock.Right);row.Children.Add(toggle);
            var description=new StackPanel();description.Children.Add(new TextBlock{Text=client.Name+(client.Enabled?" · 已信任":" · 未开启"),FontWeight=FontWeights.SemiBold});
            description.Children.Add(new TextBlock{Text=client.ExecutablePath,TextTrimming=TextTrimming.CharacterEllipsis,Opacity=0.7,ToolTip=client.ExecutablePath});
            row.Children.Add(description);_automationClientRows.Children.Add(row);
        }
        if(_automationClientRows.Children.Count==0)_automationClientRows.Children.Add(Description("暂无连接。外部 agent 连接月语 MCP 后会出现在这里。"));
    }

    private TabControl CategoryTabs()=>new(){Style=(Style)FindResource("SettingsTabControlStyle"),Margin=new Thickness(0,8,0,0)};
    private void AddCategory(TabControl tabs,string title,UIElement content)
    {
        tabs.Items.Add(new TabItem{Header=title,Style=(Style)FindResource("NavigationTabItemStyle"),
            Content=new ScrollViewer{Content=content,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Padding=new Thickness(0,10,4,0)}});
    }
    private void ConvertExpanderGroups(DependencyObject root)
    {
        if(root is Panel panel)
        {
            var folds=panel.Children.OfType<Expander>().ToArray();
            if(folds.Length>0)
            {
                var tabs=CategoryTabs();var index=panel.Children.IndexOf(folds[0]);
                foreach(var fold in folds)
                {
                    var content=fold.Content as UIElement;fold.Content=null;panel.Children.Remove(fold);
                    if(content is not null)AddCategory(tabs,fold.Header?.ToString()??"设置",content);
                }
                panel.Children.Insert(index,tabs);
            }
        }
        foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>().ToArray())ConvertExpanderGroups(child);
    }
    private void OrganizeSettingsNavigation()
    {
        var orb=SettingsTabControl.Items.OfType<TabItem>().First(tab=>AutomationProperties.GetName(tab)=="悬浮球");
        var orbContent=(UIElement)orb.Content;var wheelContent=(UIElement)WheelSettingsTab.Content;
        orb.Content=null;WheelSettingsTab.Content=null;SettingsTabControl.Items.Remove(WheelSettingsTab);
        var merged=CategoryTabs();AddCategory(merged,"悬浮球",orbContent);
        if(wheelContent is ScrollViewer wheelScroll&&wheelScroll.Content is StackPanel wheelSections)
        {
            var sections=wheelSections.Children.OfType<FrameworkElement>().ToArray();
            var wheelCombined=new StackPanel{Margin=new Thickness(28,16,28,24),MaxWidth=1080};
            for(var index=0;index<sections.Length;index++)
            {
                var section=sections[index];wheelSections.Children.Remove(section);
                wheelCombined.Children.Add(section);
            }
            AddCategory(merged,"轮盘与快捷应用",wheelCombined);
        }
        else AddCategory(merged,"轮盘与快捷应用",wheelContent);
        orb.Content=merged;
        if(orb.Header is Panel header)header.Children.OfType<TextBlock>().Last().Text="悬浮球与轮盘";
        AutomationProperties.SetName(orb,"悬浮球与轮盘");
        foreach(var tab in SettingsTabControl.Items.OfType<TabItem>().ToArray())
        {
            if(tab==_automationSettingsTab||tab==DesktopSessionsSettingsTab||tab==orb)continue;
            if(tab.Content is not ScrollViewer scroll||scroll.Content is not StackPanel stack)continue;
            var sections=stack.Children.OfType<FrameworkElement>().ToArray();if(sections.Length<2)continue;
            var categories=CategoryTabs();categories.Margin=new Thickness(28,16,28,24);
            foreach(var section in sections)
            {
                var title=LogicalDescendants(section).OfType<TextBlock>().FirstOrDefault(text=>text.Style==FindResource("SettingsSectionTitleStyle"))?.Text??"设置";
                stack.Children.Remove(section);AddCategory(categories,title,section);
            }
            scroll.Content=null;tab.Content=categories;
        }
        foreach(var title in new[]{"悬浮浏览器","实时翻译","音频","亮度"})
        {
            var moved=SettingsTabControl.Items.OfType<TabItem>().FirstOrDefault(tab=>AutomationProperties.GetName(tab)==title);
            if(moved?.Content is not UIElement content)continue;
            moved.Content=null;SettingsTabControl.Items.Remove(moved);AddCategory(merged,title,content);
        }
        // Keep the desktop's primary enable controls visible; replace its old
        // install/diagnostic folds with horizontal categories.
        if(DesktopSessionsPage.Children[0] is StackPanel desktopRoot)
        {
            var categories=CategoryTabs();
            foreach(var fold in _desktopFoldedSections.Where(fold=>fold.Parent==desktopRoot).ToArray())
            {
                var content=fold.Content as UIElement;fold.Content=null;desktopRoot.Children.Remove(fold);
                if(content is not null)AddCategory(categories,fold.Header?.ToString()??"设置",content);
            }
            if(desktopRoot.Children.OfType<Grid>().FirstOrDefault() is {} environments)
            {
                desktopRoot.Children.Remove(environments);AddCategory(categories,"本地分身与虚拟机",environments);
                var first=categories.Items[categories.Items.Count-1];categories.Items.RemoveAt(categories.Items.Count-1);categories.Items.Insert(0,first);categories.SelectedIndex=0;
            }
            desktopRoot.Children.Insert(1,categories);
        }
        ConvertExpanderGroups(DesktopSessionsPage);
    }
    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject root)
    {
        yield return root;
        foreach(var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach(var descendant in LogicalDescendants(child))yield return descendant;
    }
}
