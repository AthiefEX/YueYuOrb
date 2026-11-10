using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using FloatDesk.Windows.DesktopSessions;
using Microsoft.Win32;

namespace FloatDesk.SessionClient;

internal static class RdpViewProgram
{
    internal static int Run(string[] arguments)
    {
        var result = 1;
        var thread = new Thread(() =>
        {
            try
            {
                if (arguments.Length != 9 || !Guid.TryParse(arguments[1], out var vmId) ||
                    !IPAddress.TryParse(arguments[2], out var address) || !IPAddress.IsLoopback(address) ||
                    !int.TryParse(arguments[3], out var port) || port is < 1024 or > 65535 ||
                    !int.TryParse(arguments[5], out var width) || !int.TryParse(arguments[6], out var height)) return;
                var computer = arguments[4]; var drives = arguments[7].Split(';', StringSplitOptions.RemoveEmptyEntries);
                using var credential = new WindowsGuestCredentialStore().Load(vmId) ?? throw new InvalidOperationException("GuestCredentialMissing");
                AuthenticatedRdpConnection.ValidateViewSettings(port, credential.UserName, computer, width, height, drives);
                using var pin = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Terminal Server Client\Servers\" + address);
                if (pin?.GetValue("YueYuOwner") as string != arguments[8] || pin.GetValue("YueYuVmId") as string != vmId.ToString("D"))
                    throw new InvalidOperationException("RdpEndpointIdentityRejected");
                System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
                System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.CatchException);
                System.Windows.Forms.Application.EnableVisualStyles();
                using var form = new RdpViewForm(address.ToString(), port, computer, credential, width, height, drives);
                // AxHost can report a late native-window callback during teardown.
                // The default WinForms exception dialog would outlive the shell
                // and prevent the parent from observing a completed close.
                System.Windows.Forms.Application.ThreadException += (_, error) => form.HandleUiException(error.Exception);
                System.Windows.Forms.Application.Run(form);
                result = form.ExitCode;
            }
            catch (Exception exception)
            {
                // Types/codes only. No credential, source or raw COM exception text.
                Console.Error.WriteLine("RdpViewFailed:" + exception.GetType().Name + ":" + RdpViewForm.Stage + ":" + exception.GetBaseException().HResult.ToString("X8", CultureInfo.InvariantCulture));
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        return result;
    }

    private sealed class RdpViewForm : System.Windows.Forms.Form
    {
        internal static string Stage { get; private set; } = "Arguments";
        private readonly RdpHost _host = new();
        private readonly object _ocx;
        private bool _closing;
        public int ExitCode { get; private set; } = 1;
        internal void HandleUiException(Exception exception)
        {
            Console.Error.WriteLine((_closing ? "RdpCloseCallback:" : "RdpUiFailed:") + exception.GetType().Name);
            if (_closing) return;
            ExitCode = 1;
            BeginInvoke(new Action(Close));
        }
        public RdpViewForm(string address, int port, string computer, GuestCredential credential, int width, int height, string[] drives)
        {
            Text = "月语认证桌面";
            Stage = "CreateControl";
            ClientSize = new System.Drawing.Size(width, height + 2);
            var viewport = new System.Windows.Forms.Panel { Dock = System.Windows.Forms.DockStyle.Fill };
            Controls.Add(viewport);
            Controls.Add(new System.Windows.Forms.Panel { Dock = System.Windows.Forms.DockStyle.Top, Height = 1 });
            Controls.Add(new System.Windows.Forms.Panel { Dock = System.Windows.Forms.DockStyle.Bottom, Height = 1 });
            _host.Size = new System.Drawing.Size(width, height); viewport.Controls.Add(_host);
            _ = Handle;
            if (!SetProp(Handle, "YueYu.Rdp.Client", 1)) throw new InvalidOperationException("RdpWindowIdentityFailed");
            foreach (System.Windows.Forms.Control child in Controls) _ = child.Handle;
            _ = _host.Handle;
            CreateControl(); _host.CreateControl(); _ocx = _host.ActiveX;
            Stage = "DisplayProperties";
            Set(_ocx, "Server", address); Set(_ocx, "UserName", credential.UserName); Set(_ocx, "Domain", computer);
            Set(_ocx, "DesktopWidth", width); Set(_ocx, "DesktopHeight", height); Set(_ocx, "FullScreen", false);
            var advanced = Get(_ocx, "AdvancedSettings9");
            Set(advanced, "RDPPort", port); Set(advanced, "EnableCredSspSupport", true);
            // Standard Windows certificate validation and explicit first-use
            // user confirmation; never ignore certificate errors.
            Set(advanced, "AuthenticationLevel", 2u); Set(advanced, "SmartSizing", false);
            Set(advanced, "EnableAutoReconnect", false); Set(advanced, "RedirectClipboard", true);
            Set(advanced, "RedirectPrinters", false); Set(advanced, "RedirectSmartCards", false);
            Set(advanced, "RedirectDrives", drives.Length != 0);
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", "assembly", "GAC_64", "Microsoft.Virtualization.Client.RdpClientInterop");
            Stage = "WindowsInterop";
            var files = Directory.GetFiles(path, "Microsoft.Virtualization.Client.RdpClientInterop.dll", SearchOption.AllDirectories);
            if (files.Length != 1) throw new InvalidOperationException("WindowsRdpInteropAmbiguous");
            var interop = Assembly.LoadFrom(files[0]);
            var nonScript = interop.GetType("Microsoft.Virtualization.Client.Interop.IMsRdpClientNonScriptable4", true)!;
            void SetNative(string name, object value) => nonScript.GetProperty(name)!.SetValue(_ocx, value);
            SetNative("PromptForCredentials", false); SetNative("PromptForCredsOnClient", false);
            SetNative("AllowCredentialSaving", false); SetNative("RedirectDynamicDrives", false);
            SetNative("ShowRedirectionWarningDialog", false); SetNative("MarkRdpSettingsSecure", true);
            SetNative("WarnAboutClipboardRedirection", false);
            Stage = "DriveRedirection";
            var driveCollection = nonScript.GetProperty("DriveCollection")!.GetValue(_ocx)!;
            var collectionType = interop.GetType("Microsoft.Virtualization.Client.Interop.IMsRdpDriveCollection", true)!;
            var driveType = interop.GetType("Microsoft.Virtualization.Client.Interop.IMsRdpDrive", true)!;
            bool ConfigureDrives()
            {
                var count = (uint)collectionType.GetProperty("DriveCount")!.GetValue(driveCollection)!;
                var redirected = 0;
                for (uint i = 0; i < count; i++)
                {
                    var drive = collectionType.GetProperty("DriveByIndex")!.GetValue(driveCollection, [i])!;
                    var name = (string)driveType.GetProperty("Name")!.GetValue(drive)!;
                    var selected = drives.Any(root => AuthenticatedRdpConnection.MatchesRedirectedDrive(name, root));
                    driveType.GetProperty("RedirectionState")!.SetValue(drive, selected);
                    if (selected) redirected++;
                }
                return redirected == drives.Length;
            }
            var pointer = Marshal.SecureStringToBSTR(credential.Password);
            Stage = "Credential";
            try { SetNative("ClearTextPassword", Marshal.PtrToStringBSTR(pointer)); }
            finally { Marshal.ZeroFreeBSTR(pointer); }
            var events = new Guid("336d5562-efa8-482e-8cb3-c5c0fc7a7db6");
            Stage = "Events";
            ComEventsHelper.Combine(_ocx, events, 3, new Action(() => Console.WriteLine("RdpLoginComplete")));
            ComEventsHelper.Combine(_ocx, events, 4, new Action<int>(reason =>
            {
                if (_closing) return;
                Console.Error.WriteLine("RdpDisconnected:" + reason.ToString(CultureInfo.InvariantCulture));
                ExitCode = 1; BeginInvoke(new Action(Close));
            }));
            Shown += async (_, _) =>
            {
                try
                {
                    Stage = "DriveRedirection";
                    collectionType.GetMethod("RescanDrives")!.Invoke(driveCollection, [false]);
                    var ready = drives.Length == 0;
                    for (var attempt = 0; attempt < 50 && !ready && !_closing; attempt++)
                    { await Task.Delay(100); ready = ConfigureDrives(); }
                    if (_closing) return;
                    if (!ready) throw new InvalidOperationException("SelectedDriveUnavailable");
                    Console.WriteLine("RdpRedirectedDrives:" + drives.Length.ToString(CultureInfo.InvariantCulture));
                    if (!SetProp(Handle, "YueYu.Rdp.Ready", 1)) throw new InvalidOperationException("RdpWindowReadinessFailed");
                    Stage = "Connect"; Invoke(_ocx, "Connect");
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine("RdpConnectFailed:" + exception.GetType().Name + (exception.Message == "SelectedDriveUnavailable" ? ":SelectedDriveUnavailable" : ""));
                    ExitCode = 1; BeginInvoke(new Action(Close));
                }
            };
            FormClosing += (_, _) =>
            {
                _closing = true;
                if ((short)Get(_ocx, "Connected") != 0) { try { Invoke(_ocx, "Disconnect"); } catch (COMException) { } }
                if (ExitCode != 1) ExitCode = 0;
            };
            // A normal parent WM_CLOSE must be distinguished from server failure.
            ExitCode = 0;
        }
        private static object Get(object target, string name) => target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null, CultureInfo.InvariantCulture)!;
        private static void Set(object target, string name, object value) => target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, [value], CultureInfo.InvariantCulture);
        private static void Invoke(object target, string name) => target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, null, CultureInfo.InvariantCulture);
        private sealed class RdpHost() : System.Windows.Forms.AxHost("8B918B82-7985-4C24-89DF-C33AD2BBFBCD")
        { public object ActiveX => GetOcx() ?? throw new InvalidOperationException("RdpControlMissing"); }
        [DllImport("user32.dll", EntryPoint = "SetPropW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetProp(nint window, string name, nint value);
    }
}
