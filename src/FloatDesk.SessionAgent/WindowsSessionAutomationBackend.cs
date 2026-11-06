using FloatDesk.Core.Geometry;
using FloatDesk.Core.Models;
using FloatDesk.Windows.Capture;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace FloatDesk.SessionAgent;

public sealed class WindowsSessionAutomationBackend :
    ISessionAutomationBackend
{
    private const uint InputKeyboard = 1;
    private const uint InputMouse = 0;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;
    private const ushort VkShift = 0x10;
    private const ushort VkControl = 0x11;
    private const ushort VkAlt = 0x12;
    private const ushort VkLeftWindows = 0x5B;
    private static readonly Dictionary<string, ControlType>
        KnownControlTypes = new Dictionary<string, ControlType>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Button"] = ControlType.Button,
            ["Calendar"] = ControlType.Calendar,
            ["CheckBox"] = ControlType.CheckBox,
            ["ComboBox"] = ControlType.ComboBox,
            ["Custom"] = ControlType.Custom,
            ["DataGrid"] = ControlType.DataGrid,
            ["DataItem"] = ControlType.DataItem,
            ["Document"] = ControlType.Document,
            ["Edit"] = ControlType.Edit,
            ["Group"] = ControlType.Group,
            ["Header"] = ControlType.Header,
            ["HeaderItem"] = ControlType.HeaderItem,
            ["Hyperlink"] = ControlType.Hyperlink,
            ["Image"] = ControlType.Image,
            ["List"] = ControlType.List,
            ["ListItem"] = ControlType.ListItem,
            ["Menu"] = ControlType.Menu,
            ["MenuBar"] = ControlType.MenuBar,
            ["MenuItem"] = ControlType.MenuItem,
            ["Pane"] = ControlType.Pane,
            ["ProgressBar"] = ControlType.ProgressBar,
            ["RadioButton"] = ControlType.RadioButton,
            ["ScrollBar"] = ControlType.ScrollBar,
            ["Separator"] = ControlType.Separator,
            ["Slider"] = ControlType.Slider,
            ["Spinner"] = ControlType.Spinner,
            ["SplitButton"] = ControlType.SplitButton,
            ["StatusBar"] = ControlType.StatusBar,
            ["Tab"] = ControlType.Tab,
            ["TabItem"] = ControlType.TabItem,
            ["Table"] = ControlType.Table,
            ["Text"] = ControlType.Text,
            ["Thumb"] = ControlType.Thumb,
            ["TitleBar"] = ControlType.TitleBar,
            ["ToolBar"] = ControlType.ToolBar,
            ["ToolTip"] = ControlType.ToolTip,
            ["Tree"] = ControlType.Tree,
            ["TreeItem"] = ControlType.TreeItem
        };

    private readonly IScreenCapture _capture;
    private readonly object _heldKeysSync = new();
    private readonly HashSet<ushort> _heldKeys = [];

    public WindowsSessionAutomationBackend(
        IScreenCapture? capture = null)
    {
        _capture = capture ?? new GdiScreenCapture();
    }

    public async Task<CommandResult> WaitForWindowAsync(
        SessionCommand command,
        CancellationToken cancellationToken)
    {
        if (!command.Parameters.TryGetValue("title", out var title) &&
            !command.Parameters.TryGetValue(
                "processName",
                out var processName))
        {
            return Failed(command, "InvalidWindowQuery");
        }

        command.Parameters.TryGetValue(
            "processName",
            out processName);
        while (DateTimeOffset.UtcNow < command.DeadlineUtc)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FindWindow(title, processName) != IntPtr.Zero)
                return Succeeded(command);
            await Task.Delay(
                TimeSpan.FromMilliseconds(200),
                cancellationToken).ConfigureAwait(false);
        }

        return Failed(command, "WindowNotFound");
    }

    public Task<CommandResult> ClickElementAsync(
        SessionCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsCoordinateStrategy(command.Parameters))
        {
            if (!TryReadInt(command.Parameters, "x", out var x) ||
                !TryReadInt(command.Parameters, "y", out var y))
            {
                return Task.FromResult(
                    Failed(command, "InvalidCoordinates"));
            }

            return Task.FromResult(
                ClickAt(x, y)
                    ? Succeeded(command)
                    : Failed(command, "ClickFailed"));
        }

        var element = FindElement(command.Parameters);
        if (element is null)
            return Task.FromResult(Failed(command, "ElementNotFound"));
        try
        {
            if (element.TryGetCurrentPattern(
                    InvokePattern.Pattern,
                    out var invokeObject) &&
                invokeObject is InvokePattern invoke)
            {
                invoke.Invoke();
                return Task.FromResult(Succeeded(command));
            }

            var bounds = element.Current.BoundingRectangle;
            if (bounds.IsEmpty)
                return Task.FromResult(Failed(command, "ElementNotClickable"));
            var clicked = ClickAt(
                checked((int)Math.Round(
                    bounds.Left + bounds.Width / 2)),
                checked((int)Math.Round(
                    bounds.Top + bounds.Height / 2)));
            return Task.FromResult(
                clicked
                    ? Succeeded(command)
                    : Failed(command, "ClickFailed"));
        }
        catch
        {
            return Task.FromResult(Failed(command, "ClickFailed"));
        }
    }

    public Task<CommandResult> TypeTextAsync(
        SessionCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!command.Parameters.TryGetValue("text", out var text))
            return Task.FromResult(Failed(command, "TextMissing"));

        var element = FindElement(command.Parameters);
        try
        {
            if (element is not null &&
                element.TryGetCurrentPattern(
                    ValuePattern.Pattern,
                    out var valueObject) &&
                valueObject is ValuePattern valuePattern &&
                !valuePattern.Current.IsReadOnly)
            {
                valuePattern.SetValue(text);
                return Task.FromResult(Succeeded(command));
            }

            element?.SetFocus();
        }
        catch
        {
            return Task.FromResult(Failed(command, "TextInputFailed"));
        }

        return Task.FromResult(
            SendUnicodeText(text)
                ? Succeeded(command)
                : Failed(command, "TextInputFailed"));
    }

    public Task<CommandResult> SendKeysAsync(
        SessionCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!command.Parameters.TryGetValue("keys", out var keys) ||
            !TrySendKeySequence(keys))
        {
            return Task.FromResult(Failed(command, "InvalidKeys"));
        }

        return Task.FromResult(Succeeded(command));
    }

    public async Task<CommandResult> CaptureScreenshotAsync(
        SessionCommand command,
        CancellationToken cancellationToken)
    {
        if (!TryReadInt(
                command.Parameters,
                "left",
                out var left) ||
            !TryReadInt(
                command.Parameters,
                "top",
                out var top) ||
            !TryReadPositiveInt(
                command.Parameters,
                "width",
                out var width) ||
            !TryReadPositiveInt(
                command.Parameters,
                "height",
                out var height) ||
            !command.Parameters.TryGetValue("path", out var path) ||
            string.IsNullOrWhiteSpace(path) ||
            !Path.IsPathFullyQualified(path))
        {
            return Failed(command, "InvalidScreenshotRequest");
        }

        try
        {
            using var frame = await _capture.CaptureAsync(
                new PixelRect(left, top, width, height),
                cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(
                Path.GetDirectoryName(path) ??
                Environment.CurrentDirectory);
            using var bitmap = new Bitmap(
                width,
                height,
                PixelFormat.Format32bppArgb);
            var data = bitmap.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                var pixels = frame.Bgra32;
                var sourceStride = frame.Stride;
                var targetStride = data.Stride;
                var copyLength = checked(width * 4);
                for (var row = 0; row < height; row++)
                {
                    Marshal.Copy(
                        pixels.Slice(
                            row * sourceStride,
                            copyLength).ToArray(),
                        0,
                        IntPtr.Add(data.Scan0, row * targetStride),
                        copyLength);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            bitmap.Save(
                path,
                System.Drawing.Imaging.ImageFormat.Png);
            return Succeeded(command);
        }
        catch
        {
            return Failed(command, "ScreenshotSaveFailed");
        }
    }

    public Task ReleaseHeldInputAsync(
        CancellationToken cancellationToken)
    {
        ushort[] keys;
        lock (_heldKeysSync)
        {
            keys = _heldKeys.ToArray();
            _heldKeys.Clear();
        }

        var inputs = keys.Select(key => KeyboardInput(
            key,
            char.MinValue,
            KeyEventKeyUp)).ToArray();
        if (inputs.Length > 0)
            _ = SendInput(
                checked((uint)inputs.Length),
                inputs,
                Marshal.SizeOf<InputNative>());
        return Task.CompletedTask;
    }

    private static bool ClickAt(int x, int y)
    {
        if (!SetCursorPos(x, y))
            return false;
        var inputs = new[]
        {
            MouseInput(MouseEventLeftDown),
            MouseInput(MouseEventLeftUp)
        };
        return SendInput(
            checked((uint)inputs.Length),
            inputs,
            Marshal.SizeOf<InputNative>()) == inputs.Length;
    }

    private static bool SendUnicodeText(string text)
    {
        foreach (var character in text)
        {
            var down = KeyboardInput(
                key: 0,
                character,
                KeyEventUnicode);
            var up = KeyboardInput(
                key: 0,
                character,
                KeyEventUnicode | KeyEventKeyUp);
            var inputs = new[] { down, up };
            if (SendInput(
                    checked((uint)inputs.Length),
                    inputs,
                    Marshal.SizeOf<InputNative>()) != inputs.Length)
            {
                return false;
            }
        }

        return true;
    }

    private bool TrySendKeySequence(string sequence)
    {
        var chords = sequence.Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);
        if (chords.Length == 0)
            return false;

        foreach (var chord in chords)
        {
            if (!TryParseChord(chord, out var modifiers, out var key))
                return false;
            foreach (var modifier in modifiers)
            {
                if (!PressKey(modifier, keyUp: false))
                    return false;
            }

            var pressed = PressKey(key, keyUp: false);
            pressed &= PressKey(key, keyUp: true);
            for (var index = modifiers.Count - 1; index >= 0; index--)
                pressed &= PressKey(modifiers[index], keyUp: true);
            if (!pressed)
                return false;
        }

        return true;
    }

    private bool PressKey(ushort key, bool keyUp)
    {
        var input = KeyboardInput(
            key,
            char.MinValue,
            keyUp ? KeyEventKeyUp : 0);
        if (SendInput(
                1,
                [input],
                Marshal.SizeOf<InputNative>()) != 1)
        {
            return false;
        }

        lock (_heldKeysSync)
        {
            if (keyUp)
                _heldKeys.Remove(key);
            else
                _heldKeys.Add(key);
        }

        return true;
    }

    private static bool TryParseChord(
        string chord,
        out List<ushort> modifiers,
        out ushort key)
    {
        modifiers = [];
        key = 0;
        var parts = chord.Split(
            '+',
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;
        foreach (var part in parts[..^1])
        {
            if (!TryMapModifier(part, out var modifier))
                return false;
            modifiers.Add(modifier);
        }

        return TryMapKey(parts[^1], out key);
    }

    private static bool TryMapModifier(string value, out ushort key)
    {
        switch (value.ToUpperInvariant())
        {
            case "CTRL":
            case "CONTROL":
                key = VkControl;
                return true;
            case "SHIFT":
                key = VkShift;
                return true;
            case "ALT":
                key = VkAlt;
                return true;
            case "WIN":
            case "WINDOWS":
                key = VkLeftWindows;
                return true;
            default:
                key = 0;
                return false;
        }
    }

    private static bool TryMapKey(string value, out ushort key)
    {
        var normalized = value.Trim().TrimStart('{').TrimEnd('}');
        if (normalized.Length == 1 &&
            char.IsLetterOrDigit(normalized[0]))
        {
            key = char.ToUpperInvariant(normalized[0]);
            return true;
        }

        var keys = new Dictionary<string, ushort>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["ENTER"] = 0x0D,
            ["RETURN"] = 0x0D,
            ["TAB"] = 0x09,
            ["ESC"] = 0x1B,
            ["ESCAPE"] = 0x1B,
            ["SPACE"] = 0x20,
            ["BACKSPACE"] = 0x08,
            ["DELETE"] = 0x2E,
            ["INSERT"] = 0x2D,
            ["HOME"] = 0x24,
            ["END"] = 0x23,
            ["PAGEUP"] = 0x21,
            ["PAGEDOWN"] = 0x22,
            ["LEFT"] = 0x25,
            ["UP"] = 0x26,
            ["RIGHT"] = 0x27,
            ["DOWN"] = 0x28,
            ["ADD"] = 0x6B,
            ["SUBTRACT"] = 0x6D,
            ["MULTIPLY"] = 0x6A,
            ["DIVIDE"] = 0x6F,
            ["DECIMAL"] = 0x6E
        };
        for (var index = 1; index <= 24; index++)
            keys[$"F{index}"] = checked((ushort)(0x6F + index));
        if (keys.TryGetValue(normalized, out key))
            return true;
        return ushort.TryParse(
            normalized,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out key);
    }

    private static AutomationElement? FindElement(
        IReadOnlyDictionary<string, string> parameters)
    {
        try
        {
            var conditions = new List<Condition>();
            if (parameters.TryGetValue(
                    "automationId",
                    out var automationId) &&
                !string.IsNullOrWhiteSpace(automationId))
            {
                conditions.Add(new PropertyCondition(
                    AutomationElement.AutomationIdProperty,
                    automationId));
            }

            if (parameters.TryGetValue("name", out var name) &&
                !string.IsNullOrWhiteSpace(name))
            {
                conditions.Add(new PropertyCondition(
                    AutomationElement.NameProperty,
                    name));
            }

            if (parameters.TryGetValue(
                    "className",
                    out var className) &&
                !string.IsNullOrWhiteSpace(className))
            {
                conditions.Add(new PropertyCondition(
                    AutomationElement.ClassNameProperty,
                    className));
            }

            if (parameters.TryGetValue(
                    "controlType",
                    out var controlTypeText) &&
                TryResolveControlType(
                    controlTypeText,
                    out var controlType))
            {
                conditions.Add(new PropertyCondition(
                    AutomationElement.ControlTypeProperty,
                    controlType));
            }

            if (conditions.Count == 0)
                return null;
            var condition = conditions.Count == 1
                ? conditions[0]
                : new AndCondition(conditions.ToArray());
            return AutomationElement.RootElement.FindFirst(
                TreeScope.Descendants,
                condition);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryResolveControlType(
        string value,
        out ControlType controlType)
    {
        controlType = null!;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var normalized = value.Trim();
        if (KnownControlTypes.TryGetValue(normalized, out var known))
        {
            controlType = known;
            return true;
        }
        if (!int.TryParse(
                normalized,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out var id) ||
            id <= 0)
        {
            return false;
        }

        try
        {
            var resolved = ControlType.LookupById(id);
            if (resolved is null)
                return false;
            controlType = resolved;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static IntPtr FindWindow(
        string? title,
        string? processName)
    {
        var result = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window))
                return true;
            if (!string.IsNullOrWhiteSpace(title))
            {
                var length = GetWindowTextLength(window);
                if (length <= 0)
                    return true;
                var text = new char[length + 1];
                _ = GetWindowText(window, text, text.Length);
                if (!new string(text).TrimEnd('\0').Contains(
                        title,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            if (!string.IsNullOrWhiteSpace(processName))
            {
                var threadId = GetWindowThreadProcessId(
                    window,
                    out var processId);
                if (threadId == 0)
                    return true;
                try
                {
                    using var process = Process.GetProcessById(
                        checked((int)processId));
                    var currentName = Path
                        .GetFileNameWithoutExtension(process.ProcessName);
                    var expected = Path.GetFileNameWithoutExtension(
                        processName);
                    if (!string.Equals(
                            currentName,
                            expected,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch
                {
                    return true;
                }
            }

            result = window;
            return false;
        }, IntPtr.Zero);
        return result;
    }

    private static bool IsCoordinateStrategy(
        IReadOnlyDictionary<string, string> parameters) =>
        parameters.TryGetValue("strategy", out var strategy) &&
            string.Equals(
                strategy,
                "coordinates",
                StringComparison.OrdinalIgnoreCase) ||
        parameters.ContainsKey("x") ||
        parameters.ContainsKey("y");

    private static bool TryReadPositiveInt(
        IReadOnlyDictionary<string, string> parameters,
        string name,
        out int value)
    {
        value = 0;
        return parameters.TryGetValue(name, out var text) &&
            int.TryParse(text, out value) &&
            value > 0;
    }

    private static bool TryReadInt(
        IReadOnlyDictionary<string, string> parameters,
        string name,
        out int value)
    {
        value = 0;
        return parameters.TryGetValue(name, out var text) &&
            int.TryParse(text, out value);
    }

    private static CommandResult Succeeded(SessionCommand command) =>
        new(command.CommandId, true, false, null);

    private static CommandResult Failed(
        SessionCommand command,
        string errorCode) =>
        new(command.CommandId, false, false, errorCode);

    private static InputNative KeyboardInput(
        ushort key,
        char character,
        uint flags) =>
        new()
        {
            Type = InputKeyboard,
            Union = new InputUnion
            {
                Keyboard = new KeyboardInputNative
                {
                    VirtualKey = key,
                    ScanCode = character,
                    Flags = flags
                }
            }
        };

    private static InputNative MouseInput(uint flags) =>
        new()
        {
            Type = InputMouse,
            Union = new InputUnion
            {
                Mouse = new MouseInputNative
                {
                    Flags = flags
                }
            }
        };

    private delegate bool EnumWindowsProc(
        IntPtr window,
        IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct InputNative
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInputNative Mouse;

        [FieldOffset(0)]
        public KeyboardInputNative Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputNative
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInputNative
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern uint SendInput(
        uint inputCount,
        [In] InputNative[] inputs,
        int inputSize);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(
        EnumWindowsProc callback,
        IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport(
        "user32.dll",
        EntryPoint = "GetWindowTextW",
        CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(
        IntPtr window,
        [Out] char[] text,
        int maximumCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(
        IntPtr window,
        out uint processId);
}
