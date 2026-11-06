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

public sealed class RdpControlFactory : IRdpControlFactory
{
    public IRdpControl Create() => new MsRdpControl();
}

internal sealed class MsRdpControl : IRdpControl, IRdpControlView
{
    private const string ChildSessionClsid =
        "8B918B82-7985-4C24-89DF-C33AD2BBFBCD";
    private readonly RdpAxHost _host = new();
    private object? _ocx;
    private bool _disposed;

    public Control View => _host;

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

                var advanced = GetRequiredProperty(
                    ocx,
                    "AdvancedSettings9");
                SetRequiredProperty(advanced, "EnableCredSspSupport", true);
                SetRequiredProperty(advanced, "RedirectClipboard", false);
                SetRequiredProperty(advanced, "RedirectDrives", false);
                SetRequiredProperty(advanced, "RedirectPrinters", false);
                SetRequiredProperty(advanced, "RedirectPOSDevices", false);

                var extended = GetRequiredProperty(ocx, "ExtendedSettings");
                InvokeRequiredMethod(
                    extended,
                    "set_Property",
                    "ConnectToChildSession",
                    true);
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

    public void Disconnect()
    {
        if (_disposed || _ocx is null)
            return;

        InvokeOnView(() =>
        {
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
                _host.BeginInvoke(new Action(_host.Dispose));
            }
            else
            {
                _host.Dispose();
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

    private sealed class RdpAxHost() : AxHost(ChildSessionClsid)
    {
        public object GetActiveXObject() =>
            GetOcx() ?? throw new InvalidOperationException(
                "The RDP ActiveX control was not created.");
    }
}
