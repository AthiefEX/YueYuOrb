using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using FloatDesk.App.Views;
using FloatDesk.Core.Assistant;
using FloatDesk.Infrastructure.Assistant;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;
using MenuItem = System.Windows.Controls.MenuItem;

namespace SettingsWindowProbe;

internal static partial class Program
{
    private static async Task VerifyAssistantWorkspaceUiAsync(SettingsWindow settingsWindow, AssistantWindow window, AssistantProbeCoordinator coordinator, AssistantStore store, string output, CancellationToken token)
    {
        var original = coordinator.Current;
        T Field<T>(object source, string name) => (T)source.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source)!;
        Button Icon(string name) => Descendants(window).OfType<Button>().Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == name);
        try
        {
            window.Show(); window.UpdateLayout();
            var sidebar = Field<ColumnDefinition>(window,"_sidebarWidth"); Icon("展开/收起会话列表").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if(sidebar.Width.Value != 0) throw new InvalidOperationException("Sidebar did not collapse.");
            CaptureProbeWindow(window,Path.Combine(output,"assistant-sidebar-collapsed.png"));
            Icon("展开/收起会话列表").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); if(sidebar.Width.Value == 0) throw new InvalidOperationException("Sidebar did not expand.");
            Icon("窗口透明度").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if(window.WindowOpacitySlider.Parent is not StackPanel) throw new InvalidOperationException("Opacity flyout has no slider.");
            window.WindowOpacitySlider.Value=90; if(Math.Abs(window.Opacity-.9)>.01) throw new InvalidOperationException("Opacity flyout did not apply.");
            window.WindowOpacitySlider.Value=100; window.Focus();
            var skill=new AssistantAsset(Guid.NewGuid(),"native-skill","skill","Use concise bullets.","manual",new string('a',64)); await store.SaveAssetAsync(skill,token);
            var at=Icon("@ 调用Skill（仅本次消息）"); at.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(50,token);
            var skillItem=at.ContextMenu!.Items.OfType<MenuItem>().Single(item=>item.Header?.ToString()==skill.Name); skillItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); at.ContextMenu.IsOpen=false;
            if(!Field<HashSet<Guid>>(window,"_mentionedSkills").Contains(skill.Id) || coordinator.Current.Assistant.EnabledAssetIds.Contains(skill.Id)) throw new InvalidOperationException("Mention did not stay transient.");
            Field<HashSet<Guid>>(window,"_mentionedSkills").Clear();
            settingsWindow.Show(); settingsWindow.SelectAssistantPage();
            var roleIds=new List<Guid>();
            foreach(var label in new[]{"native-role-a","native-role-b"})
            {
                Exception? failure=null;
                _=settingsWindow.Dispatcher.BeginInvoke(new Action(()=>
                {
                    try
                    {
                        var editor=System.Windows.Application.Current.Windows.OfType<Window>().Single(item=>item.Title=="手动添加角色卡");
                        var fields=Descendants(editor).OfType<TextBox>().ToArray(); fields.Single(item=>item.MaxLength==100).Text=label; fields.Single(item=>item.MaxLength==64000).Text="You are "+label;
                        Descendants(editor).OfType<Button>().Single(button=>button.Content?.ToString()=="保存").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    catch(Exception error){failure=error;System.Windows.Application.Current.Windows.OfType<Window>().FirstOrDefault(item=>item.Title=="手动添加角色卡")?.Close();}
                }));
                await (Task)typeof(SettingsWindow).GetMethod("EditAssistantAssetAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(settingsWindow,["role",null])!;
                if(failure is not null) throw new InvalidOperationException("Manual role editor failed.",failure);
                var asset=(await store.ListAssetsAsync(token)).Single(item=>item.Name==label); roleIds.Add(asset.Id);
                var rows=Field<StackPanel>(settingsWindow,"_assistantAssets"); var toggle=Descendants(rows).OfType<CheckBox>().Single(item=>item.ToolTip?.ToString()=="启用/关闭 "+label);
                toggle.IsChecked=true; toggle.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent)); await Task.Delay(60,token);
            }
            if(coordinator.Current.Assistant.EnabledAssetIds.Contains(roleIds[0]) || !coordinator.Current.Assistant.EnabledAssetIds.Contains(roleIds[1])) throw new InvalidOperationException("Role switch did not enforce one role.");
            var searchProfile=new AssistantApiProfile(Guid.NewGuid(),"duplicate-search-probe","https://example.test/v1","one",true);
            await coordinator.UpdateAsync(current=>current with{Assistant=current.Assistant with{Profiles=current.Assistant.Profiles.Append(searchProfile).ToArray(),SearchEnabled=true,SearchProfileId=searchProfile.Id,SearchModelId="one"}},token);
            var reminder=false;
            _=settingsWindow.Dispatcher.BeginInvoke(new Action(()=>
            {
                var dialog=System.Windows.Application.Current.Windows.OfType<Window>().Single(item=>item.Title=="联网搜索模型"); reminder=Descendants(dialog).OfType<Button>().Any(button=>button.Content?.ToString()=="关闭"); dialog.Close();
            }));
            await (Task)typeof(SettingsWindow).GetMethod("ChangeSearchRoleAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(settingsWindow,[searchProfile,"two",true])!;
            if(!reminder || coordinator.Current.Assistant.SearchModelId!="one") throw new InvalidOperationException("Duplicate search changed the selected model.");
            Console.WriteLine("assistant.realSidebarCollapseOpacityMention=PASS;realManualRoleEditorAndUniqueRole=PASS;realDuplicateSearchReminder=PASS");
        }
        finally { await coordinator.UpdateAsync(_=>original,CancellationToken.None); settingsWindow.Hide(); }
    }
}
