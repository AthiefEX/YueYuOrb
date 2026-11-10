using FloatDesk.App;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Brushes=System.Windows.Media.Brushes;

namespace SettingsWindowProbe;

internal static class GlassFrameProbe
{
    public static void VerifyScrollBarHit(Window window,System.Windows.Controls.Primitives.ScrollBar bar)
    {
        bar.Visibility=Visibility.Visible;window.UpdateLayout();
        var point=bar.PointToScreen(new System.Windows.Point(bar.ActualWidth/2,bar.ActualHeight/2));
        var packed=((int)point.Y<<16)|((int)point.X&0xffff);
        var result=SendMessage(new WindowInteropHelper(window).Handle,0x0084,0,packed);
        if(result!=1)throw new InvalidOperationException($"The guest scrollbar is intercepted by the resize edge: hit={result}.");
    }
    public static void Run()
    {
        foreach(var theme in new[]{FloatDesk.Core.Models.AppTheme.Light,FloatDesk.Core.Models.AppTheme.Dark})
        foreach(var layered in new[]{false,true})
        {
            ThemeService.Apply(theme);
            var window=new Window{Width=480,Height=320,WindowStyle=layered?WindowStyle.None:WindowStyle.SingleBorderWindow,
                AllowsTransparency=layered,ResizeMode=layered?ResizeMode.NoResize:ResizeMode.CanResize,Background=Brushes.Black,Topmost=true};
            window.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri("/YueYuOrb;component/Resources/SettingsWindowResources.xaml",UriKind.Relative)});
            var shell=new Border{Style=(Style)window.FindResource("ToolWindowShellStyle"),Child=new Grid{Background=Brushes.SteelBlue}};
            window.Content=shell;window.Show();window.UpdateLayout();
            try
            {
                if(shell.Margin!=new Thickness(0)||shell.BorderThickness!=new Thickness(0)||window.WindowStyle!=WindowStyle.None)
                    throw new InvalidOperationException("Glass frame must not retain an opaque inset or outer stroke.");
                if(window.AllowsTransparency!=layered)throw new InvalidOperationException("Native HWND hosting mode must be preserved.");
                var handle=new WindowInteropHelper(window).Handle;
                if(!layered)
                {
                    VerifyRegion(handle);
                    VerifyEdgePixels(window,handle);
                    window.Width=640;window.Height=420;window.UpdateLayout();VerifyRegion(handle);
                    window.WindowState=WindowState.Maximized;window.UpdateLayout();
                    window.WindowState=WindowState.Normal;window.UpdateLayout();VerifyRegion(handle);
                }
                else if(shell.Clip is not RectangleGeometry geometry||geometry.RadiusX<=0)
                    throw new InvalidOperationException("Transparent panels need a real rounded content clip.");
            }
            finally{window.Close();}
        }
        Console.WriteLine("glass.frame.nativeAndLayered=PASS;themes=Light,Dark;resizeAndRestore=PASS");
    }
    private static void VerifyEdgePixels(Window window,nint handle)
    {
        for(var attempt=0;attempt<30;attempt++)
        {
            window.Dispatcher.Invoke(()=>{},System.Windows.Threading.DispatcherPriority.Render);
            _=DwmFlush();GetWindowRect(handle,out var rect);
            var dc=GetDC(0);
            try
            {
                var left=GetPixel(dc,rect.Left,(rect.Top+rect.Bottom)/2);
                var top=GetPixel(dc,(rect.Left+rect.Right)/2,rect.Top);
                if(left==0xB48246&&top==0xB48246)return; // SteelBlue, no black/white NC stripe.
                if(attempt==29)throw new InvalidOperationException($"Native frame has a visible edge stripe: left=0x{left:X}, top=0x{top:X}.");
            }
            finally{_=ReleaseDC(0,dc);}
            Thread.Sleep(10);
        }
    }
    private static void VerifyRegion(nint window)
    {
        var region=CreateRectRgn(0,0,0,0);
        try
        {
            if(GetWindowRgn(window,region)==0||PtInRegion(region,0,0))throw new InvalidOperationException("Native window still has square opaque corners.");
            GetWindowRect(window,out var rect);
            if(!PtInRegion(region,(rect.Right-rect.Left)/2,(rect.Bottom-rect.Top)/2))throw new InvalidOperationException("Glass content was clipped out.");
        }
        finally{DeleteObject(region);}
    }
    [StructLayout(LayoutKind.Sequential)]private struct Rect{public int Left,Top,Right,Bottom;}
    [DllImport("gdi32.dll")]private static extern nint CreateRectRgn(int left,int top,int right,int bottom);
    [DllImport("gdi32.dll")]private static extern bool DeleteObject(nint region);
    [DllImport("gdi32.dll")]private static extern bool PtInRegion(nint region,int x,int y);
    [DllImport("user32.dll")]private static extern int GetWindowRgn(nint window,nint region);
    [DllImport("user32.dll")]private static extern bool GetWindowRect(nint window,out Rect rect);
    [DllImport("user32.dll")]private static extern nint GetDC(nint window);
    [DllImport("user32.dll")]private static extern int ReleaseDC(nint window,nint dc);
    [DllImport("gdi32.dll")]private static extern uint GetPixel(nint dc,int x,int y);
    [DllImport("dwmapi.dll")]private static extern int DwmFlush();
    [DllImport("user32.dll")]private static extern nint SendMessage(nint window,uint message,nint wParam,nint lParam);
}
