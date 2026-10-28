using FloatDesk.App.Translation;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

public sealed class Win32ForegroundActivatorTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(0x1236)]
    public void CaptureRetainsValidSnapshotFocusWhenBackgroundThreadFocusChanges(int currentFocus)
    {
        var native = new FakeNative { Focus = currentFocus };
        var activator = new Win32ForegroundActivator(native, new FakePolling());

        var identity = activator.CaptureIdentity(Target());

        Assert.NotNull(identity);
        Assert.Equal(new nint(0x1235), identity.FocusHwnd);
        Assert.False(activator.IsTargetForeground(identity));
        Assert.Equal(0, native.ActivationCount);
        Assert.Empty(native.Attachments);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x1236)]
    public async Task ActivationRestoresSnapshotChildFocusAfterForegroundSwitch(int activatedFocus)
    {
        var native = new FakeNative();
        var activator = new Win32ForegroundActivator(native, new FakePolling());
        var identity = Assert.IsType<ForegroundTargetIdentity>(activator.CaptureIdentity(Target()));
        native.OnActivate = () => native.Focus = activatedFocus;

        var activated = await activator.TryActivateAsync(identity, CancellationToken.None);

        Assert.True(activated);
        Assert.True(activator.IsTargetForeground(identity));
        Assert.Equal(new nint(0x1235), native.Focus);
        Assert.Equal(1, native.ActivationCount);
        Assert.Equal(1, native.FocusSetCount);
        Assert.Equal(new[] { (3u, 9u, true), (3u, 9u, false) }, native.Attachments);
    }

    [Fact]
    public async Task SuccessfulActivationDoesNotReclaimForegroundAfterUserSwitchesAway()
    {
        var native = new FakeNative();
        var activator = new Win32ForegroundActivator(native, new FakePolling());
        var identity = Assert.IsType<ForegroundTargetIdentity>(activator.CaptureIdentity(Target()));
        Assert.True(await activator.TryActivateAsync(identity, CancellationToken.None));
        native.Foreground = 0x7777;

        Assert.False(await activator.TryActivateAsync(identity, CancellationToken.None));
        Assert.Equal(new nint(0x7777), native.Foreground);
        Assert.Equal(1, native.ActivationCount);
    }

    [Fact]
    public async Task InitiallyForegroundTargetDoesNotReclaimForegroundAfterUserSwitchesAway()
    {
        var native = new FakeNative { Foreground = 0x1234 };
        var activator = new Win32ForegroundActivator(native, new FakePolling());
        var identity = Assert.IsType<ForegroundTargetIdentity>(activator.CaptureIdentity(Target()));
        native.Foreground = 0x7777;

        Assert.False(await activator.TryActivateAsync(identity, CancellationToken.None));
        Assert.Equal(0, native.ActivationCount);
        Assert.Equal(0, native.FocusSetCount);
    }

    [Fact]
    public async Task ChangedForegroundSinceCaptureIsNotOverridden()
    {
        var native = new FakeNative();
        var activator = new Win32ForegroundActivator(native, new FakePolling());
        var identity = Assert.IsType<ForegroundTargetIdentity>(activator.CaptureIdentity(Target()));
        native.Foreground = 0x7777;

        Assert.False(await activator.TryActivateAsync(identity, CancellationToken.None));
        Assert.Equal(0, native.ActivationCount);
    }

    [Fact]
    public async Task FailedForegroundRequestIsNotRepeatedForSameIdentity()
    {
        var native = new FakeNative { ActivationSucceeds = false };
        var activator = new Win32ForegroundActivator(native, new FakePolling());
        var identity = Assert.IsType<ForegroundTargetIdentity>(activator.CaptureIdentity(Target()));

        Assert.False(await activator.TryActivateAsync(identity, CancellationToken.None));
        Assert.False(await activator.TryActivateAsync(identity, CancellationToken.None));
        Assert.Equal(1, native.ActivationCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x1236)]
    public void CaptureRejectsChangedFocusWhenTargetIsAlreadyForeground(int currentFocus)
    {
        var native = new FakeNative { Foreground = 0x1234, Focus = currentFocus };
        var activator = new Win32ForegroundActivator(native, new FakePolling());

        Assert.Null(activator.CaptureIdentity(Target()));
        Assert.Equal(0, native.ActivationCount);
        Assert.Equal(0, native.FocusSetCount);
    }

    [Theory]
    [InlineData("top-destroyed")]
    [InlineData("focus-destroyed")]
    [InlineData("focus-reparented")]
    [InlineData("focus-process")]
    [InlineData("process")]
    [InlineData("thread")]
    [InlineData("process-start")]
    [InlineData("exe-path")]
    [InlineData("start-unavailable")]
    [InlineData("path-unavailable")]
    public void CaptureRejectsInvalidSnapshotIdentityEvenWithoutCurrentFocus(string change)
    {
        var native = new FakeNative { Focus = 0 };
        Invalidate(native, change);
        var activator = new Win32ForegroundActivator(native, new FakePolling());

        Assert.Null(activator.CaptureIdentity(Target()));
        Assert.Equal(0, native.ActivationCount);
        Assert.Empty(native.Attachments);
    }

    [Theory]
    [InlineData("top-destroyed")]
    [InlineData("focus-destroyed")]
    [InlineData("focus-reparented")]
    [InlineData("focus-process")]
    [InlineData("process")]
    [InlineData("thread")]
    [InlineData("process-start")]
    [InlineData("exe-path")]
    [InlineData("title")]
    [InlineData("start-unavailable")]
    [InlineData("path-unavailable")]
    public async Task ActivationRejectsChangedIdentityBeforeForegroundRequest(string change)
    {
        var native = new FakeNative();
        var activator = new Win32ForegroundActivator(native, new FakePolling());
        var identity = Assert.IsType<ForegroundTargetIdentity>(activator.CaptureIdentity(Target()));
        Invalidate(native, change);

        Assert.False(await activator.TryActivateAsync(identity, CancellationToken.None));
        Assert.Equal(0, native.ActivationCount);
        Assert.Equal(0, native.FocusSetCount);
        Assert.Empty(native.Attachments);
    }

    [Theory]
    [InlineData("top-destroyed")]
    [InlineData("focus-destroyed")]
    [InlineData("focus-reparented")]
    [InlineData("focus-process")]
    [InlineData("process")]
    [InlineData("thread")]
    [InlineData("process-start")]
    [InlineData("exe-path")]
    [InlineData("title")]
    [InlineData("foreground")]
    public async Task ActivationRechecksIdentityAfterForegroundRequestBeforeRestoringFocus(string change)
    {
        var native = new FakeNative();
        var activator = new Win32ForegroundActivator(native, new FakePolling());
        var identity = Assert.IsType<ForegroundTargetIdentity>(activator.CaptureIdentity(Target()));
        native.OnActivate = () =>
        {
            native.Focus = 0;
            Invalidate(native, change);
        };

        Assert.False(await activator.TryActivateAsync(identity, CancellationToken.None));
        Assert.Equal(1, native.ActivationCount);
        Assert.Equal(0, native.FocusSetCount);
        Assert.Empty(native.Attachments);
    }

    [Theory]
    [InlineData("focus-destroyed")]
    [InlineData("focus-reparented")]
    [InlineData("focus-process")]
    [InlineData("process")]
    [InlineData("thread")]
    [InlineData("process-start")]
    [InlineData("exe-path")]
    [InlineData("title")]
    [InlineData("foreground")]
    public async Task FocusRestorationRevalidatesAfterAttachingAndAlwaysDetaches(string change)
    {
        var native = new FakeNative();
        var activator = new Win32ForegroundActivator(native, new FakePolling());
        var identity = Assert.IsType<ForegroundTargetIdentity>(activator.CaptureIdentity(Target()));
        native.OnActivate = () => native.Focus = 0;
        native.OnAttach = () => Invalidate(native, change);

        Assert.False(await activator.TryActivateAsync(identity, CancellationToken.None));
        Assert.Equal(0, native.FocusSetCount);
        Assert.Equal(new[] { (3u, 9u, true), (3u, 9u, false) }, native.Attachments);
    }

    [Fact]
    public async Task ExistingForegroundWithDifferentChildFocusIsNeverOverridden()
    {
        var native = new FakeNative();
        var activator = new Win32ForegroundActivator(native, new FakePolling());
        var identity = Assert.IsType<ForegroundTargetIdentity>(activator.CaptureIdentity(Target()));
        native.Foreground = 0x1234;
        native.Focus = 0x1236;

        Assert.False(await activator.TryActivateAsync(identity, CancellationToken.None));
        Assert.Equal(0, native.ActivationCount);
        Assert.Equal(0, native.FocusSetCount);
        Assert.Empty(native.Attachments);
    }

    [Fact]
    public async Task AlreadyCorrectFocusRequiresNoInputAttachment()
    {
        var native = new FakeNative();
        var activator = new Win32ForegroundActivator(native, new FakePolling());
        var identity = Assert.IsType<ForegroundTargetIdentity>(activator.CaptureIdentity(Target()));

        Assert.True(await activator.TryActivateAsync(identity, CancellationToken.None));
        Assert.Equal(0, native.FocusSetCount);
        Assert.Empty(native.Attachments);
    }

    [Fact]
    public async Task SameThreadFocusRestorationDoesNotAttachToItself()
    {
        var native = new FakeNative { CurrentThreadId = 9 };
        var activator = new Win32ForegroundActivator(native, new FakePolling());
        var identity = Assert.IsType<ForegroundTargetIdentity>(activator.CaptureIdentity(Target()));
        native.OnActivate = () => native.Focus = 0;

        Assert.True(await activator.TryActivateAsync(identity, CancellationToken.None));
        Assert.Equal(1, native.FocusSetCount);
        Assert.Empty(native.Attachments);
    }

    [Fact]
    public async Task FailedAttachmentDoesNotAttemptFocusRestoration()
    {
        var native = new FakeNative { AttachmentSucceeds = false };
        var activator = new Win32ForegroundActivator(native, new FakePolling());
        var identity = Assert.IsType<ForegroundTargetIdentity>(activator.CaptureIdentity(Target()));
        native.OnActivate = () => native.Focus = 0;

        Assert.False(await activator.TryActivateAsync(identity, CancellationToken.None));
        Assert.Equal(0, native.FocusSetCount);
        Assert.Equal(new[] { (3u, 9u, true) }, native.Attachments);
    }

    [Fact]
    public async Task FocusRestorationExceptionDetachesInputAndDoesNotRetry()
    {
        var native = new FakeNative { OnSetFocus = () => throw new InvalidOperationException() };
        var activator = new Win32ForegroundActivator(native, new FakePolling());
        var identity = Assert.IsType<ForegroundTargetIdentity>(activator.CaptureIdentity(Target()));
        native.OnActivate = () => native.Focus = 0;

        Assert.False(await activator.TryActivateAsync(identity, CancellationToken.None));
        Assert.False(await activator.TryActivateAsync(identity, CancellationToken.None));
        Assert.Equal(1, native.ActivationCount);
        Assert.Equal(1, native.FocusSetCount);
        Assert.Equal(new[] { (3u, 9u, true), (3u, 9u, false) }, native.Attachments);
    }

    [Fact]
    public async Task CancellationAfterAttachmentDetachesBeforePropagatingCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var native = new FakeNative { OnAttach = cancellation.Cancel };
        var activator = new Win32ForegroundActivator(native, new FakePolling());
        var identity = Assert.IsType<ForegroundTargetIdentity>(activator.CaptureIdentity(Target()));
        native.OnActivate = () => native.Focus = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => activator.TryActivateAsync(identity, cancellation.Token));
        Assert.Equal(0, native.FocusSetCount);
        Assert.Equal(new[] { (3u, 9u, true), (3u, 9u, false) }, native.Attachments);
    }

    [Fact]
    public async Task PendingActivationStopsAtTimeoutWithoutRepeatedForegroundRequests()
    {
        var native = new FakeNative();
        var polling = new FakePolling();
        var activator = new Win32ForegroundActivator(native, polling);
        var identity = Assert.IsType<ForegroundTargetIdentity>(activator.CaptureIdentity(Target()));
        native.OnActivate = () => native.Foreground = 0x5555;

        Assert.False(await activator.TryActivateAsync(identity, CancellationToken.None));
        Assert.False(await activator.TryActivateAsync(identity, CancellationToken.None));
        Assert.Equal(TimeSpan.FromMilliseconds(400), polling.Elapsed);
        Assert.Equal(1, native.ActivationCount);
        Assert.Equal(0, native.FocusSetCount);
        Assert.Empty(native.Attachments);
    }

    [Fact]
    public async Task UserForegroundSwitchDuringPendingActivationAbortsImmediately()
    {
        var native = new FakeNative();
        var polling = new FakePolling { OnDelay = () => native.Foreground = 0x7777 };
        var activator = new Win32ForegroundActivator(native, polling);
        var identity = Assert.IsType<ForegroundTargetIdentity>(activator.CaptureIdentity(Target()));
        native.OnActivate = () => native.Foreground = 0x5555;

        Assert.False(await activator.TryActivateAsync(identity, CancellationToken.None));
        Assert.Equal(TimeSpan.FromMilliseconds(10), polling.Elapsed);
        Assert.Equal(new nint(0x7777), native.Foreground);
        Assert.Equal(1, native.ActivationCount);
        Assert.Equal(0, native.FocusSetCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReaderTouchesClipboardOnlyAfterNativeActivatorVerifiesRestoredFocus(bool canAttach)
    {
        var native = new FakeNative { Focus = 0, AttachmentSucceeds = canAttach };
        native.OnActivate = () => native.Focus = 0;
        var polling = new FakePolling();
        var clipboard = new TestClipboard();
        var sender = new TestCopySender();
        await using var reader = new SelectedTextReader(
            clipboard,
            sender,
            new ReleasedModifiers(),
            new Win32ForegroundActivator(native, polling),
            TimeSpan.FromMilliseconds(800),
            polling: polling);

        var result = await reader.ReadAsync(Target(), CancellationToken.None);

        Assert.Equal(canAttach, result.Success);
        Assert.Equal(canAttach ? "selection" : null, result.Text);
        Assert.Equal(canAttach ? null : "TargetNotForeground", result.ErrorCode);
        Assert.Equal(canAttach ? 1 : 0, clipboard.CaptureCount);
        Assert.Equal(canAttach ? 1 : 0, sender.Count);
        Assert.Equal(canAttach ? 11u : null, clipboard.RestoreSequence);
        Assert.Equal(1, native.ActivationCount);
    }

    private sealed class TestClipboard : IClipboardSession
    {
        private uint _sequence = 10;
        public int CaptureCount { get; private set; }
        public uint? RestoreSequence { get; private set; }

        public Task<ClipboardSessionSnapshot?> CaptureAsync(CancellationToken cancellationToken)
        {
            CaptureCount++;
            return Task.FromResult<ClipboardSessionSnapshot?>(
                new ClipboardSessionSnapshot(10, Array.Empty<ClipboardFormatSnapshot>()));
        }

        public Task<uint> GetSequenceNumberAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_sequence);

        public Task<ClipboardTextReadResult> WaitForTextAsync(
            uint baselineSequenceNumber,
            uint targetProcessId,
            TimeSpan timeout,
            Action<uint>? sequenceObserved,
            CancellationToken cancellationToken)
        {
            _sequence = 11;
            sequenceObserved?.Invoke(_sequence);
            return Task.FromResult(ClipboardTextReadResult.WithText("selection", _sequence));
        }

        public Task<bool> RestoreIfSequenceAsync(ClipboardSessionSnapshot snapshot, uint expectedSequenceNumber)
        {
            RestoreSequence = expectedSequenceNumber;
            return Task.FromResult(_sequence == expectedSequenceNumber);
        }
    }

    private sealed class TestCopySender : ICopyShortcutSender
    {
        public int Count { get; private set; }
        public CopyShortcutSendResult SendCopy()
        {
            Count++;
            return CopyShortcutSendResult.Complete(4);
        }
    }

    private sealed class ReleasedModifiers : IHotkeyModifierState
    {
        public bool AreAnyModifiersHeld() => false;
    }

    private static void Invalidate(FakeNative native, string change)
    {
        switch (change)
        {
            case "top-destroyed": native.TopLevelValid = false; break;
            case "focus-destroyed": native.FocusValid = false; break;
            case "focus-reparented": native.FocusRoot = 0x8888; break;
            case "focus-process": native.FocusProcessId = 43; break;
            case "process": native.ProcessId = 43; break;
            case "thread": native.ThreadId = 10; break;
            case "process-start": native.ProcessStartUtc = DateTimeOffset.UnixEpoch.AddMinutes(1); break;
            case "exe-path": native.ExePath = @"C:\Apps\replacement.exe"; break;
            case "title": native.Title = "Different window"; break;
            case "start-unavailable": native.StartUnavailable = true; break;
            case "path-unavailable": native.PathUnavailable = true; break;
            case "foreground": native.Foreground = 0x7777; break;
            default: throw new ArgumentOutOfRangeException(nameof(change));
        }
    }

    private static ForegroundSnapshot Target() => new(
        Epoch: 7,
        TopLevelHwnd: 0x1234,
        FocusHwnd: 0x1235,
        ProcessId: 42,
        ThreadId: 9,
        ProcessStartUtc: DateTimeOffset.UnixEpoch,
        ExePath: @"C:\Apps\target.exe",
        IsOwnInteractive: false);

    private sealed class FakeNative : Win32ForegroundActivator.INativeBackend
    {
        public nint Foreground { get; set; } = 0x5555;
        public nint Focus { get; set; } = 0x1235;
        public bool ActivationSucceeds { get; set; } = true;
        public int ActivationCount { get; private set; }
        public int FocusSetCount { get; private set; }
        public Action? OnActivate { get; set; }
        public Action? OnAttach { get; set; }
        public Action? OnSetFocus { get; set; }
        public bool TopLevelValid { get; set; } = true;
        public bool FocusValid { get; set; } = true;
        public nint FocusRoot { get; set; } = 0x1234;
        public uint ProcessId { get; set; } = 42;
        public uint FocusProcessId { get; set; } = 42;
        public uint ThreadId { get; set; } = 9;
        public uint CurrentThreadId { get; set; } = 3;
        public DateTimeOffset ProcessStartUtc { get; set; } = DateTimeOffset.UnixEpoch;
        public string ExePath { get; set; } = @"C:\Apps\target.exe";
        public string Title { get; set; } = "Target window";
        public bool StartUnavailable { get; set; }
        public bool PathUnavailable { get; set; }
        public bool AttachmentSucceeds { get; set; } = true;
        public List<(uint Current, uint Target, bool Attach)> Attachments { get; } = new();

        public bool IsWindow(nint hwnd) => hwnd switch
        {
            0x1234 => TopLevelValid,
            0x1235 => FocusValid,
            0x1236 => true,
            _ => false
        };
        public nint GetForegroundWindow() => Foreground;
        public bool SetForegroundWindow(nint hwnd)
        {
            ActivationCount++;
            if (!ActivationSucceeds)
                return false;
            Foreground = hwnd;
            OnActivate?.Invoke();
            return true;
        }
        public uint GetWindowThreadProcessId(nint hwnd, out uint processId)
        {
            processId = !IsWindow(hwnd) ? 0 : hwnd == 0x1235 ? FocusProcessId : ProcessId;
            return processId == 0 ? 0 : ThreadId;
        }
        public nint GetRoot(nint hwnd) => !IsWindow(hwnd) ? 0 : hwnd == 0x1235 ? FocusRoot : 0x1234;
        public nint ReadFocusHwnd(uint threadId) => Focus;
        public string ReadWindowTitle(nint hwnd) => Title;
        public DateTimeOffset ReadProcessStartUtc(uint processId) =>
            StartUnavailable ? throw new InvalidOperationException() : ProcessStartUtc;
        public string? ReadExePath(uint processId) =>
            PathUnavailable ? throw new InvalidOperationException() : ExePath;
        public uint GetCurrentThreadId() => CurrentThreadId;
        public bool AttachThreadInput(uint currentThread, uint targetThread, bool attach)
        {
            Attachments.Add((currentThread, targetThread, attach));
            if (attach)
                OnAttach?.Invoke();
            return !attach || AttachmentSucceeds;
        }
        public void SetFocus(nint hwnd)
        {
            FocusSetCount++;
            OnSetFocus?.Invoke();
            Focus = hwnd;
        }
    }

    private sealed class FakePolling : ISelectedTextPolling
    {
        public TimeSpan Elapsed { get; private set; }
        public Action? OnDelay { get; set; }
        public TimeSpan GetTimestamp() => Elapsed;
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Elapsed += delay;
            OnDelay?.Invoke();
            return Task.CompletedTask;
        }
    }
}
