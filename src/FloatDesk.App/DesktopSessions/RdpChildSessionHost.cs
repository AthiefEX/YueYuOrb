using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FloatDesk.App.DesktopSessions;

public interface IRdpControl : IDisposable
{
    Task ConfigureAsync(
        int width,
        int height,
        CancellationToken cancellationToken);

    void Connect();

    void Disconnect();
}

public interface IRdpControlView
{
    Control View { get; }
}

public interface IRdpControlFactory
{
    IRdpControl Create();
}

public sealed class RdpChildSessionHost : IAsyncDisposable
{
    private readonly IRdpControl _control;
    private readonly object _sync = new();
    private bool _connected;
    private bool _disposed;

    public RdpChildSessionHost(IRdpControlFactory factory)
        : this((factory ?? throw new ArgumentNullException(nameof(factory)))
            .Create())
    {
    }

    public RdpChildSessionHost(IRdpControl control)
    {
        _control = control ?? throw new ArgumentNullException(nameof(control));
    }

    public bool IsConnected
    {
        get
        {
            lock (_sync)
            {
                return _connected;
            }
        }
    }

    public async Task ConnectAsync(
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (width is < 800 or > 3840 || height is < 600 or > 2160)
        {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                "The guest resolution must be between 800x600 and 3840x2160.");
        }

        lock (_sync)
        {
            if (_connected)
            {
                throw new InvalidOperationException(
                    "The RDP control is already connected.");
            }
        }

        try
        {
            await _control.ConfigureAsync(
                width,
                height,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _control.Connect();
            lock (_sync)
            {
                _connected = true;
            }
        }
        catch
        {
            DisposeControl();
            throw;
        }
    }

    public void Disconnect()
    {
        lock (_sync)
        {
            if (!_connected)
                return;
            _connected = false;
        }

        try
        {
            _control.Disconnect();
        }
        catch
        {
            DisposeControl();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed)
                return ValueTask.CompletedTask;
            _disposed = true;
            _connected = false;
        }

        DisposeControl();
        return ValueTask.CompletedTask;
    }

    private void DisposeControl()
    {
        try
        {
            _control.Dispose();
        }
        catch
        {
        }
    }
}

public sealed class RdpControlFactory(string? initialProgram = null) : IRdpControlFactory
{
    public IRdpControl Create() => new MsRdpControl(initialProgram);
}

public interface IRdpDisplayControl
{
    Task SetResolutionAsync(int width, int height, CancellationToken cancellationToken);
}

internal sealed class MsRdpControl : IRdpControl, IRdpControlView, IRdpDisplayControl
{
    private const string ChildSessionClsid =
        "8B918B82-7985-4C24-89DF-C33AD2BBFBCD";
    private readonly RdpAxHost _host = new();
    private readonly Panel _viewport = new() { AutoScroll = true, Dock = DockStyle.Fill };
    private readonly string? _initialProgram;
    private object? _ocx;
    private bool _disposed;

    public MsRdpControl(string? initialProgram = null)
    {
        _initialProgram = initialProgram;
        _viewport.Controls.Add(_host);
    }

    public Control View => _viewport;

    public async Task ConfigureAsync(
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        InvokeOnView(() =>
        {
            try
            {
                _host.CreateControl();
                var ocx = _host.GetActiveXObject();
                _ocx = ocx;
                SetRequiredProperty(ocx, "Server", "localhost");
                SetRequiredProperty(ocx, "DesktopWidth", width);
                SetRequiredProperty(ocx, "DesktopHeight", height);
                SetRequiredProperty(ocx, "FullScreen", false);
                if (_initialProgram is not null)
                    SetRequiredProperty(GetRequiredProperty(ocx, "SecuredSettings2"), "StartProgram", _initialProgram);

                var advanced = GetRequiredProperty(
                    ocx,
                    "AdvancedSettings9");
                SetRequiredProperty(advanced, "EnableCredSspSupport", true);
                SetRequiredProperty(advanced, "SmartSizing", false);
                _host.Size = new System.Drawing.Size(width, height);
                _viewport.AutoScrollMinSize = _host.Size;
                SetRequiredProperty(advanced, "RedirectClipboard", false);
                SetRequiredProperty(advanced, "RedirectDrives", false);
                SetRequiredProperty(advanced, "RedirectPrinters", false);
                SetRequiredProperty(advanced, "RedirectPOSDevices", false);

                var extended = (IMsRdpExtendedSettings)ocx;
                object childSession = true;
                extended.SetProperty("ConnectToChildSession", ref childSession);
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Connect()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        InvokeOnView(() =>
            InvokeRequiredMethod(GetOcx(), "Connect"));
    }

    public Task SetResolutionAsync(int width, int height, CancellationToken cancellationToken)
    {
        if (width is < 800 or > 3840 || height is < 600 or > 2160)
            throw new ArgumentOutOfRangeException(nameof(width));
        cancellationToken.ThrowIfCancellationRequested();
        InvokeOnView(() =>
        {
            InvokeRequiredMethod(GetOcx(), "UpdateSessionDisplaySettings",
                (uint)width, (uint)height, (uint)(width * 254 / 960), (uint)(height * 254 / 960),
                0u, 100u, 100u);
            _host.Size = new System.Drawing.Size(width, height);
            _viewport.AutoScrollMinSize = _host.Size;
        });
        return Task.CompletedTask;
    }

    public void Disconnect()
    {
        if (_disposed || _ocx is null)
            return;

        InvokeOnView(() =>
        {
            if (Convert.ToInt32(GetRequiredProperty(_ocx, "Connected"),
                CultureInfo.InvariantCulture) == 0)
                return;
            try
            {
                InvokeRequiredMethod(_ocx, "Disconnect");
            }
            catch (COMException)
            {
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        try
        {
            Disconnect();
        }
        finally
        {
            _disposed = true;
            if (_host.IsHandleCreated && _host.InvokeRequired)
            {
                _host.BeginInvoke(new Action(_viewport.Dispose));
            }
            else
            {
                _viewport.Dispose();
            }
        }
    }

    private object GetOcx() =>
        _ocx ?? throw new InvalidOperationException(
            "The RDP control has not been configured.");

    private void InvokeOnView(Action action)
    {
        if (_host.InvokeRequired)
        {
            _host.Invoke(action);
            return;
        }

        action();
    }

    private static object GetRequiredProperty(
        object target,
        string propertyName) =>
        ReadProperty(target, propertyName) ??
        throw new InvalidOperationException(
            $"The RDP control does not expose {propertyName}.");

    private static object? ReadProperty(
        object target,
        string propertyName)
    {
        try
        {
            return target.GetType().InvokeMember(
                propertyName,
                BindingFlags.GetProperty,
                binder: null,
                target,
                args: null,
                CultureInfo.InvariantCulture);
        }
        catch (TargetInvocationException exception)
        {
            throw new InvalidOperationException(
                $"Unable to read RDP property {propertyName}.",
                exception.InnerException ?? exception);
        }
    }

    private static void SetRequiredProperty(
        object target,
        string propertyName,
        object value)
    {
        try
        {
            target.GetType().InvokeMember(
                propertyName,
                BindingFlags.SetProperty,
                binder: null,
                target,
                args: [value],
                CultureInfo.InvariantCulture);
        }
        catch (TargetInvocationException exception)
        {
            throw new InvalidOperationException(
                $"Unable to set RDP property {propertyName}.",
                exception.InnerException ?? exception);
        }
        catch (MissingMethodException exception)
        {
            throw new InvalidOperationException(
                $"The RDP control does not support {propertyName}.",
                exception);
        }
    }

    private static void InvokeRequiredMethod(
        object target,
        string methodName,
        params object[] arguments)
    {
        try
        {
            target.GetType().InvokeMember(
                methodName,
                BindingFlags.InvokeMethod,
                binder: null,
                target,
                args: arguments,
                CultureInfo.InvariantCulture);
        }
        catch (TargetInvocationException exception)
        {
            throw new InvalidOperationException(
                $"RDP method {methodName} failed.",
                exception.InnerException ?? exception);
        }
        catch (MissingMethodException exception)
        {
            throw new InvalidOperationException(
                $"The RDP control does not support {methodName}.",
                exception);
        }
    }

    [ComImport]
    [Guid("302D8188-0052-4807-806A-362B628F9AC5")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMsRdpExtendedSettings
    {
        void SetProperty(
            [MarshalAs(UnmanagedType.BStr)] string name,
            [In, MarshalAs(UnmanagedType.Struct)] ref object value);

        [return: MarshalAs(UnmanagedType.Struct)]
        object GetProperty([MarshalAs(UnmanagedType.BStr)] string name);
    }

    private sealed class RdpAxHost() : AxHost(ChildSessionClsid)
    {
        public object GetActiveXObject() =>
            GetOcx() ?? throw new InvalidOperationException(
                "The RDP ActiveX control was not created.");
    }
}
