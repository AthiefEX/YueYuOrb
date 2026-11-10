using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace FloatDesk.App.Views;

/// <summary>Keeps one glass surface without changing HWND/WebView/RDP hosting mode.</summary>
public static class GlassWindowFrame
{
    private static readonly ConditionalWeakTable<Window,FrameState> Frames=new();
    public static readonly DependencyProperty EnabledProperty=DependencyProperty.RegisterAttached(
        "Enabled",typeof(bool),typeof(GlassWindowFrame),new PropertyMetadata(false,OnEnabledChanged));
    public static bool GetEnabled(DependencyObject value)=>(bool)value.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject value,bool enabled)=>value.SetValue(EnabledProperty,enabled);
    public static void Prepare(Window window)
    {
        if(window.Content is Border root&&GetEnabled(root))Attach(root);
    }
    public static void WrapStandardWindow(Window window)
    {
        if(window.Content is not UIElement content)return;
        window.Content=null;
        window.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("/YueYuOrb;component/Resources/SettingsWindowResources.xaml",UriKind.Relative)});
        var layout=new Grid();layout.RowDefinitions.Add(new RowDefinition{Height=new GridLength(48)});layout.RowDefinitions.Add(new RowDefinition());
        var title=new DockPanel{Background=Brushes.Transparent};title.SetResourceReference(Panel.BackgroundProperty,"ToolWindowTitleBarBrush");
        var actions=new StackPanel{Orientation=Orientation.Horizontal};WindowChrome.SetIsHitTestVisibleInChrome(actions,true);
        Button Add(string glyph,string label,Action action,bool close=false)
        {
            var button=new Button{Content=new TextBlock{Text=glyph,FontFamily=(FontFamily)window.FindResource("SettingsIconFont")},ToolTip=label};
            button.SetResourceReference(FrameworkElement.StyleProperty,close?"CloseCaptionButtonStyle":"CaptionButtonStyle");
            System.Windows.Automation.AutomationProperties.SetName(button,label);button.Click+=(_,_)=>action();actions.Children.Add(button);
            return button;
        }
        if(window.ResizeMode!=ResizeMode.NoResize)Add("\uE921","最小化",()=>window.WindowState=WindowState.Minimized);
        if(window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip)
        {
            var maximize=Add("\uE922","最大化或还原",()=>window.WindowState=window.WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized);
            window.StateChanged+=(_,_)=>((TextBlock)maximize.Content).Text=window.WindowState==WindowState.Maximized?"\uE923":"\uE922";
        }
        Add("\uE8BB","关闭",window.Close,true);DockPanel.SetDock(actions,Dock.Right);title.Children.Add(actions);
        var caption=new TextBlock{Margin=new Thickness(18,0,10,0),VerticalAlignment=VerticalAlignment.Center,FontWeight=FontWeights.SemiBold,TextTrimming=TextTrimming.CharacterEllipsis};
        caption.SetBinding(TextBlock.TextProperty,new System.Windows.Data.Binding(nameof(Window.Title)){Source=window});title.Children.Add(caption);
        layout.Children.Add(title);Grid.SetRow(content,1);layout.Children.Add(content);
        var shell=new Border{Child=layout};shell.SetResourceReference(FrameworkElement.StyleProperty,"ToolWindowShellStyle");window.Content=shell;
        WindowChrome.SetWindowChrome(window,new WindowChrome{CaptionHeight=48,ResizeBorderThickness=new Thickness(window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip?7:0),GlassFrameThickness=new Thickness(0),UseAeroCaptionButtons=false});
        if(double.IsFinite(window.Height)&&window.SizeToContent==SizeToContent.Manual)window.Height=Math.Min(window.Height+16,Math.Max(window.MinHeight,SystemParameters.WorkArea.Height));
        Prepare(window);
    }

    private static void OnEnabledChanged(DependencyObject value,DependencyPropertyChangedEventArgs args)
    {
        if(value is not Border border)return;
        border.Loaded-=OnLoaded;
        if(args.NewValue is true)
        {border.Loaded+=OnLoaded;if(border.IsLoaded)Attach(border);}
    }
    private static void OnLoaded(object sender,RoutedEventArgs args)=>Attach((Border)sender);
    private static void Attach(Border root)
    {
        if(Window.GetWindow(root) is not {} window)return;
        root.SetCurrentValue(FrameworkElement.MarginProperty,new Thickness(0));
        root.SetCurrentValue(Border.BorderThicknessProperty,new Thickness(0));
        window.WindowStyle=WindowStyle.None;
        window.Background=Brushes.Transparent;
        if(!window.AllowsTransparency&&WindowChrome.GetWindowChrome(window) is null)
            WindowChrome.SetWindowChrome(window,new WindowChrome{CaptionHeight=0,GlassFrameThickness=new Thickness(0),
                ResizeBorderThickness=new Thickness(window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip?7:0),UseAeroCaptionButtons=false});
        var state=Frames.GetValue(window,key=>new FrameState(key,root));
        state.Refresh();
    }

    private sealed class FrameState
    {
        private readonly Window _window;
        private readonly Border _root;
        private HwndSource? _source;
        private (int Width,int Height,int Radius,bool Maximized) _last;
        private bool _hasRegion;
        private readonly System.ComponentModel.DependencyPropertyDescriptor _cornerDescriptor;
        public FrameState(Window window,Border root)
        {
            _window=window;_root=root;
            _cornerDescriptor=System.ComponentModel.DependencyPropertyDescriptor.FromProperty(Border.CornerRadiusProperty,typeof(Border));
            _cornerDescriptor.AddValueChanged(root,OnChanged);
            window.SourceInitialized+=OnChanged;window.StateChanged+=OnChanged;window.SizeChanged+=OnSizeChanged;
            root.SizeChanged+=OnSizeChanged;window.Closed+=OnClosed;
        }
        private void OnChanged(object? sender,EventArgs args)=>Refresh();
        private void OnSizeChanged(object sender,SizeChangedEventArgs args)=>Refresh();
        private void OnClosed(object? sender,EventArgs args)
        {
            _window.SourceInitialized-=OnChanged;_window.StateChanged-=OnChanged;_window.SizeChanged-=OnSizeChanged;
            _root.SizeChanged-=OnSizeChanged;_window.Closed-=OnClosed;
            _cornerDescriptor.RemoveValueChanged(_root,OnChanged);
            if(_source is {IsDisposed:false})_source.RemoveHook(OnMessage);
        }
        private nint OnMessage(nint window,int message,nint wParam,nint lParam,ref bool handled)
        {
            if(message is 0x0005 or 0x0047 or 0x02E0)Refresh(); // size, position, DPI
            return 0;
        }
        public void Refresh()
        {
            var handle=new WindowInteropHelper(_window).Handle;if(handle==0)return;
            if(_source is null)
            {
                _source=HwndSource.FromHwnd(handle);_source?.AddHook(OnMessage);
                uint disabled=1;_ = DwmSetWindowAttribute(handle,2,ref disabled,4);
                uint noBorder=0xFFFFFFFE;_ = DwmSetWindowAttribute(handle,34,ref noBorder,4);
            }
            var maximized=IsZoomed(handle);
            var radius=maximized?0:Math.Max(0,_root.CornerRadius.TopLeft);
            if(_window.AllowsTransparency)
            {
                var bounds=new Rect(0,0,Math.Max(0,_root.ActualWidth),Math.Max(0,_root.ActualHeight));
                if(_root.Clip is RectangleGeometry clip&&clip.Rect==bounds&&clip.RadiusX==radius&&clip.RadiusY==radius)return;
                _root.Clip=new RectangleGeometry(bounds,radius,radius);
                return;
            }
            // Native region clips both WPF and hosted child HWNDs. Do not enable
            // layered transparency, which breaks WebView2/ActiveX/RDP surfaces.
            if(!GetWindowRect(handle,out var rect))return;
            var width=rect.Right-rect.Left;var height=rect.Bottom-rect.Top;
            var dpi=GetDpiForWindow(handle);if(dpi==0)dpi=96;
            var pixels=(int)Math.Round(radius*dpi/96.0);
            var current=(width,height,pixels,maximized);
            if(_hasRegion&&current==_last)return;
            _hasRegion=true;_last=current; // SetWindowRgn may reenter WINDOWPOSCHANGED.
            if(maximized){if(SetWindowRgn(handle,0,true)==0)_hasRegion=false;return;}
            if(width<1||height<1)return;
            var region=CreateRoundRectRgn(0,0,width+1,height+1,Math.Min(width,pixels*2),Math.Min(height,pixels*2));
            if(region!=0&&SetWindowRgn(handle,region,true)==0){DeleteObject(region);_hasRegion=false;}
        }
    }
    [StructLayout(LayoutKind.Sequential)]private struct RectI{public int Left,Top,Right,Bottom;}
    [DllImport("user32.dll")]private static extern bool GetWindowRect(nint window,out RectI rectangle);
    [DllImport("user32.dll")]private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")]private static extern bool IsZoomed(nint window);
    [DllImport("user32.dll")]private static extern int SetWindowRgn(nint window,nint region,bool redraw);
    [DllImport("gdi32.dll")]private static extern nint CreateRoundRectRgn(int left,int top,int right,int bottom,int width,int height);
    [DllImport("gdi32.dll")]private static extern bool DeleteObject(nint value);
    [DllImport("dwmapi.dll")]private static extern int DwmSetWindowAttribute(nint window,int attribute,ref uint value,int size);
}
