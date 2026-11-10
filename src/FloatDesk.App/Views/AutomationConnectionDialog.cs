using FloatDesk.Core.Models;
using System.Windows;
using System.Windows.Controls;

namespace FloatDesk.App.Views;

internal static class AutomationConnectionDialog
{
    public static Task<bool> ShowAsync(AutomationClientTrust client,Window? owner)
    {
        var dialog=new Window{Title="外部 agent 连接",Width=570,SizeToContent=SizeToContent.Height,WindowStyle=WindowStyle.None,
            ResizeMode=ResizeMode.NoResize,ShowInTaskbar=owner is null,Owner=owner,WindowStartupLocation=owner is null?WindowStartupLocation.CenterScreen:WindowStartupLocation.CenterOwner};
        dialog.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("/YueYuOrb;component/Resources/SettingsWindowResources.xaml",UriKind.Relative)});
        dialog.SetResourceReference(Window.BackgroundProperty,"SettingsWindowBackgroundBrush");dialog.SetResourceReference(Window.ForegroundProperty,"SettingsInkBrush");
        var content=new StackPanel{Margin=new Thickness(24)};
        content.Children.Add(new TextBlock{Text="允许此 agent 控制月语？",FontSize=21,FontWeight=FontWeights.SemiBold});
        content.Children.Add(new TextBlock{Text=client.Name,FontSize=16,Margin=new Thickness(0,14,0,8),TextWrapping=TextWrapping.Wrap});
        content.Children.Add(new TextBlock{Text="信任后可操作主桌面、本地分身和虚拟机，并创建、启用、执行脚本与自动任务。仍受 Windows 当前账号权限和 UAC 限制。\n\n以下为实际启动程序，客户端名称由其自行提供；请核对后信任。",TextWrapping=TextWrapping.Wrap});
        content.Children.Add(new TextBlock{Text=client.ExecutablePath+"\nSHA256: "+client.Sha256,TextWrapping=TextWrapping.Wrap,FontSize=12,Opacity=0.75,Margin=new Thickness(0,12,0,18)});
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        var completion=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var accepted=false;
        var cancel=new Button{Content="暂不信任",Margin=new Thickness(0,0,10,0)};
        var allow=new Button{Content="信任并允许操作"};
        cancel.SetResourceReference(FrameworkElement.StyleProperty,"ActionButtonStyle");allow.SetResourceReference(FrameworkElement.StyleProperty,"ActionButtonStyle");
        allow.Click+=(_,_)=>{accepted=true;dialog.Close();};cancel.Click+=(_,_)=>dialog.Close();
        dialog.KeyDown+=(_,args)=>{if(args.Key==System.Windows.Input.Key.Escape)dialog.Close();};
        dialog.Closed+=(_,_)=>completion.TrySetResult(accepted);
        actions.Children.Add(cancel);actions.Children.Add(allow);content.Children.Add(actions);
        var shell=new Border{Child=content};shell.SetResourceReference(FrameworkElement.StyleProperty,"ToolWindowShellStyle");dialog.Content=shell;
        GlassWindowFrame.Prepare(dialog);
        dialog.Show();return completion.Task;
    }
}
