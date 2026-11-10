using FloatDesk.Core.Models;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;

namespace FloatDesk.SessionAgent;

public static class SessionDesktopCommands
{
    public static async Task<CommandResult> CloseAsync(SessionCommand command, CancellationToken token)
    {
        if (command.Handle.WindowsSessionId != (uint)Process.GetCurrentProcess().SessionId)
            return Fail(command, "WrongWindowsSession");
        if (!command.Parameters.TryGetValue("path", out var path) || !Path.IsPathFullyQualified(path)) return Fail(command, "ApplicationPathRequired");
        var force = command.Parameters.TryGetValue("force", out var flag) && flag.Equals("true", StringComparison.OrdinalIgnoreCase);
        var closed = 0;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                var matched = false;
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (process.Id == Environment.ProcessId || process.SessionId != (int)command.Handle.WindowsSessionId ||
                        !string.Equals(process.MainModule?.FileName, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) continue;
                    matched = true;
                    if (force) process.Kill();
                    else if (!process.CloseMainWindow()) return Fail(command, "ApplicationHasNoCloseableWindow");
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(token); wait.CancelAfter(TimeSpan.FromSeconds(3));
                    try { await process.WaitForExitAsync(wait.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Fail(command, "ApplicationNeedsAttention"); }
                    closed++;
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
                { if (matched) return Fail(command, "ApplicationCloseFailed"); }
            }
        }
        return new(command.CommandId, true, false, null) { DataJson = JsonSerializer.Serialize(new { closed }) };
    }

    public static Task<CommandResult> ObserveAsync(SessionCommand command, CancellationToken token)
    {
        if (command.Handle.WindowsSessionId != (uint)Process.GetCurrentProcess().SessionId || !DesktopInputDiagnostics.IsReady())
            return Task.FromResult(Fail(command, "DesktopNotReady"));
        try
        {
            token.ThrowIfCancellationRequested();
            if (command.Parameters.TryGetValue("mode", out var mode) && mode == "screenshot")
                return Task.FromResult(Capture(command));
            var windows = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
            var nodes = new List<object>(); var remaining = 200;
            object? Read(AutomationElement element, int depth)
            {
                token.ThrowIfCancellationRequested();
                if (remaining-- <= 0) return null;
                try
                {
                    var info = element.Current;
                    if (info.IsPassword) return null;
                    using var process = Process.GetProcessById(info.ProcessId);
                    if (process.SessionId != (int)command.Handle.WindowsSessionId) return null;
                    var rect = info.BoundingRectangle;
                    var children = new List<object>();
                    if (depth < 4 && mode == "uiTree")
                    {
                        var elements = element.FindAll(TreeScope.Children, Condition.TrueCondition);
                        for (var i = 0; i < elements.Count && remaining > 0; i++) if (Read(elements[i], depth + 1) is { } child) children.Add(child);
                    }
                    string Limit(string text) => text.Length > 64 ? text[..64] : text;
                    return new { processId = info.ProcessId, name = Limit(info.Name), automationId = Limit(info.AutomationId),
                        className = Limit(info.ClassName), controlType = info.ControlType.ProgrammaticName,
                        left = rect.IsEmpty ? 0 : rect.Left, top = rect.IsEmpty ? 0 : rect.Top,
                        width = rect.IsEmpty ? 0 : rect.Width, height = rect.IsEmpty ? 0 : rect.Height, children };
                }
                catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception) { return null; }
            }
            for (var i = 0; i < windows.Count && remaining > 0; i++) if (Read(windows[i], 0) is { } node) nodes.Add(node);
            return Task.FromResult(new CommandResult(command.CommandId, true, false, null) { DataJson = JsonSerializer.Serialize(new { windows = nodes }) });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return Task.FromResult(Fail(command, "DesktopObservationFailed")); }
    }

    private static CommandResult Capture(SessionCommand command)
    {
        var width = GetSystemMetrics(0); var height = GetSystemMetrics(1);
        if (width < 1 || height < 1 || (long)width * height > 16777216) return Fail(command, "DesktopCaptureSizeInvalid");
        using var full = new Bitmap(width, height);
        using (var graphics = Graphics.FromImage(full)) graphics.CopyFromScreen(0, 0, 0, 0, full.Size);
        var scale = Math.Min(1.0, 1280.0 / width);
        using var resized = new Bitmap(full, new Size(Math.Max(1, (int)(width * scale)), Math.Max(1, (int)(height * scale))));
        using var output = new MemoryStream();
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, 55L);
        resized.Save(output, ImageCodecInfo.GetImageEncoders().Single(codec => codec.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid), parameters);
        if (output.Length > 393216) return Fail(command, "DesktopCaptureTooLarge");
        return new(command.CommandId, true, false, null) { DataJson = JsonSerializer.Serialize(new { mimeType = "image/jpeg",
            imageBase64 = Convert.ToBase64String(output.ToArray()), width, height, previewWidth = resized.Width, previewHeight = resized.Height }) };
    }

    private static CommandResult Fail(SessionCommand command, string code) => new(command.CommandId, false, false, code);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
}
