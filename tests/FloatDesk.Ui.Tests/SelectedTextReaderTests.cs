using System.Reflection;
using FloatDesk.App.Translation;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

public sealed class SelectedTextReaderTests
{
    private static readonly string[] OldFileDrop = { "old.txt" };

    [Fact]
    public async Task ReaderWaitsForHotkeyModifiersBeforeSendingCopy()
    {
        var order = new List<string>();
        var activator = new FakeActivator
        {
            OnCapture = () => order.Add("identity"),
            OnCheckForeground = () => order.Add("foreground")
        };
        var modifiers = new FakeModifiers
        {
            ReleaseAfterPolls = 2,
            OnPoll = () => order.Add("poll")
        };
        var clipboard = new FakeClipboardSession
        {
            OnCapture = () => order.Add("clipboard"),
            TextResult = ClipboardTextReadResult.WithText("selected text"),
            OnWait = () => order.Add("wait"),
            OnRestore = () => order.Add("restore")
        };
        var sender = new FakeCopySender
        {
            OnSend = () => order.Add("copy")
        };
        var reader = CreateReader(
            clipboard,
            sender,
            modifiers,
            activator);

        var result = await reader.ReadAsync(Target(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("selected text", result.Text);
        Assert.True(order.IndexOf("copy") > order.LastIndexOf("poll"));
        Assert.True(order.IndexOf("clipboard") > order.LastIndexOf("poll"));
        Assert.True(order.IndexOf("copy") > order.IndexOf("clipboard"));
        Assert.True(order.IndexOf("restore") > order.IndexOf("wait"));
    }

    [Fact]
    public async Task ReaderRestoresEveryCapturedClipboardFormat()
    {
        var snapshot = new ClipboardSessionSnapshot(
            41,
            Array.Empty<ClipboardFormatSnapshot>()
                .Concat(new[]
            {
                new ClipboardFormatSnapshot("UnicodeText", "old text"),
                new ClipboardFormatSnapshot("Bitmap", "old image"),
                new ClipboardFormatSnapshot("FileDrop", OldFileDrop)
            })
                .ToArray());
        var clipboard = new FakeClipboardSession
        {
            CaptureResult = snapshot,
            TextResult = ClipboardTextReadResult.WithText("new selection")
        };
        var reader = CreateReader(
            clipboard,
            new FakeCopySender(),
            new FakeModifiers(),
            new FakeActivator());

        var result = await reader.ReadAsync(Target(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("new selection", result.Text);
        var restored = Assert.Single(clipboard.RestoredSnapshots);
        Assert.Same(snapshot, restored);
        Assert.Equal(snapshot.Formats, restored.Formats);
        Assert.Equal(41u, restored.SequenceNumber);
    }

    [Fact]
    public async Task ReaderIgnoresClipboardTextWhenSequenceDidNotChange()
    {
        var clipboard = new FakeClipboardSession
        {
            TextResult = new ClipboardTextReadResult(
                Denied: false,
                SequenceChanged: false,
                Text: "old clipboard text")
        };
        var reader = CreateReader(
            clipboard,
            new FakeCopySender(),
            new FakeModifiers(),
            new FakeActivator());

        var result = await reader.ReadAsync(Target(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("NoSelectedText", result.ErrorCode);
        Assert.Equal(1, clipboard.RestoreCount);
    }

    [Fact]
    public async Task ReaderTreatsChangedNonTextClipboardAsNoSelectedText()
    {
        var clipboard = new FakeClipboardSession
        {
            TextResult = new ClipboardTextReadResult(
                Denied: false,
                SequenceChanged: true,
                Text: null)
        };
        var reader = CreateReader(
            clipboard,
            new FakeCopySender(),
            new FakeModifiers(),
            new FakeActivator());

        var result = await reader.ReadAsync(Target(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("NoSelectedText", result.ErrorCode);
        Assert.Equal(1, clipboard.RestoreCount);
    }

    [Fact]
    public async Task ReaderAbortsBeforeClipboardWhenTargetCannotBecomeForeground()
    {
        var clipboard = new FakeClipboardSession();
        var sender = new FakeCopySender();
        var activator = new FakeActivator
        {
            IsForeground = false,
            ActivationSucceeds = false
        };
        var modifiers = new FakeModifiers();
        var reader = CreateReader(
            clipboard,
            sender,
            modifiers,
            activator);

        var result = await reader.ReadAsync(Target(), CancellationToken.None);

        Assert.Equal("TargetNotForeground", result.ErrorCode);
        Assert.Equal(1, activator.ActivationCount);
        Assert.Equal(0, modifiers.PollCount);
        Assert.Equal(0, clipboard.CaptureCount);
        Assert.Equal(0, sender.Count);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public async Task ReaderActivatesTargetOnlyWhenItIsNotForeground(
        bool initiallyForeground,
        int expectedActivations)
    {
        var activator = new FakeActivator
        {
            IsForeground = initiallyForeground
        };
        var clipboard = new FakeClipboardSession
        {
            TextResult = ClipboardTextReadResult.WithText("selection")
        };
        var reader = CreateReader(
            clipboard,
            new FakeCopySender(),
            new FakeModifiers(),
            activator);

        var result = await reader.ReadAsync(Target(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(expectedActivations, activator.ActivationCount);
    }

    [Fact]
    public async Task ReaderRechecksTargetAfterModifierRelease()
    {
        var clipboard = new FakeClipboardSession();
        var sender = new FakeCopySender();
        var activator = new FakeActivator
        {
            ActivationSucceeds = false
        };
        var modifiers = new FakeModifiers
        {
            ReleaseAfterPolls = 1,
            OnPoll = () => activator.IsForeground = false
        };
        var reader = CreateReader(
            clipboard,
            sender,
            modifiers,
            activator);

        var result = await reader.ReadAsync(Target(), CancellationToken.None);

        Assert.Equal("TargetNotForeground", result.ErrorCode);
        Assert.Equal(0, clipboard.CaptureCount);
        Assert.Equal(0, sender.Count);
    }

    [Fact]
    public async Task ReaderReturnsModifiersHeldWithoutTouchingClipboard()
    {
        var clipboard = new FakeClipboardSession();
        var sender = new FakeCopySender();
        var polling = new FakePolling();
        var reader = CreateReader(
            clipboard,
            sender,
            new FakeModifiers { AlwaysHeld = true },
            new FakeActivator(),
            polling: polling);

        var result = await reader.ReadAsync(Target(), CancellationToken.None);

        Assert.Equal("ModifiersHeld", result.ErrorCode);
        Assert.InRange(
            polling.Elapsed,
            TimeSpan.FromMilliseconds(1000),
            TimeSpan.FromMilliseconds(1025));
        Assert.Equal(0, clipboard.CaptureCount);
        Assert.Equal(0, sender.Count);
    }

    [Fact]
    public async Task CancellationAfterSnapshotRestoresClipboardBeforeCopy()
    {
        using var cancellation = new CancellationTokenSource();
        var clipboard = new FakeClipboardSession
        {
            OnCapture = cancellation.Cancel
        };
        var sender = new FakeCopySender();
        var reader = CreateReader(
            clipboard,
            sender,
            new FakeModifiers(),
            new FakeActivator());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reader.ReadAsync(Target(), cancellation.Token));

        Assert.Equal(0, sender.Count);
        Assert.Equal(1, clipboard.RestoreCount);
    }

    [Fact]
    public async Task CancellationDuringCopyWaitRestoresClipboard()
    {
        using var cancellation = new CancellationTokenSource();
        var clipboard = new FakeClipboardSession
        {
            OnWait = cancellation.Cancel,
            WaitException = new OperationCanceledException(
                cancellation.Token)
        };
        var sender = new FakeCopySender();
        var reader = CreateReader(
            clipboard,
            sender,
            new FakeModifiers(),
            new FakeActivator());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reader.ReadAsync(Target(), cancellation.Token));

        Assert.Equal(1, sender.Count);
        Assert.Equal(1, clipboard.RestoreCount);
    }

    [Fact]
    public async Task CancellationBeforeSnapshotDoesNotChangeClipboard()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var clipboard = new FakeClipboardSession();
        var activator = new FakeActivator
        {
            ActivationSucceeds = false
        };
        var reader = CreateReader(
            clipboard,
            new FakeCopySender(),
            new FakeModifiers(),
            activator);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reader.ReadAsync(Target(), cancellation.Token));

        Assert.Equal(0, activator.CaptureCount);
        Assert.Equal(0, clipboard.CaptureCount);
        Assert.Equal(0, clipboard.RestoreCount);
    }

    [Fact]
    public async Task ClipboardCaptureDeniedReturnsTypedFailureWithoutCopy()
    {
        var clipboard = new FakeClipboardSession
        {
            CaptureResult = null
        };
        var sender = new FakeCopySender();
        var reader = CreateReader(
            clipboard,
            sender,
            new FakeModifiers(),
            new FakeActivator());

        var result = await reader.ReadAsync(Target(), CancellationToken.None);

        Assert.Equal("ClipboardDenied", result.ErrorCode);
        Assert.Equal(0, sender.Count);
        Assert.Equal(0, clipboard.RestoreCount);
    }

    [Fact]
    public async Task ClipboardReadDeniedRestoresSnapshot()
    {
        var clipboard = new FakeClipboardSession
        {
            TextResult = ClipboardTextReadResult.AccessDenied()
        };
        var reader = CreateReader(
            clipboard,
            new FakeCopySender(),
            new FakeModifiers(),
            new FakeActivator());

        var result = await reader.ReadAsync(Target(), CancellationToken.None);

        Assert.Equal("ClipboardDenied", result.ErrorCode);
        Assert.Equal(1, clipboard.RestoreCount);
    }

    [Fact]
    public async Task TargetFailureAfterSnapshotRestoresWithoutSendingCopy()
    {
        var activator = new FakeActivator
        {
            ActivationSucceeds = false
        };
        var clipboard = new FakeClipboardSession
        {
            OnCapture = () => activator.IsForeground = false
        };
        var sender = new FakeCopySender();
        var reader = CreateReader(
            clipboard,
            sender,
            new FakeModifiers(),
            activator);

        var result = await reader.ReadAsync(Target(), CancellationToken.None);

        Assert.Equal("TargetNotForeground", result.ErrorCode);
        Assert.Equal(0, sender.Count);
        Assert.Equal(1, clipboard.RestoreCount);
    }

    [Fact]
    public async Task PartialSendInputFailureRestoresWithoutWaitingForText()
    {
        var clipboard = new FakeClipboardSession();
        var sender = new FakeCopySender
        {
            Succeeds = false
        };
        var reader = CreateReader(
            clipboard,
            sender,
            new FakeModifiers(),
            new FakeActivator());

        var result = await reader.ReadAsync(Target(), CancellationToken.None);

        Assert.Equal("NoSelectedText", result.ErrorCode);
        Assert.Equal(0, clipboard.WaitCount);
        Assert.Equal(1, clipboard.RestoreCount);
    }

    [Fact]
    public async Task RestoreFailureDoesNotReplaceSuccessfulText()
    {
        var clipboard = new FakeClipboardSession
        {
            TextResult = ClipboardTextReadResult.WithText("selection"),
            RestoreException = new InvalidOperationException(
                "restore-secret")
        };
        var reader = CreateReader(
            clipboard,
            new FakeCopySender(),
            new FakeModifiers(),
            new FakeActivator());

        var result = await reader.ReadAsync(Target(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("selection", result.Text);
        Assert.Null(result.ErrorCode);
        Assert.Null(result.Message);
        Assert.Equal(1, clipboard.RestoreCount);
    }

    [Fact]
    public async Task ClipboardFailureDoesNotExposeExceptionDetail()
    {
        var clipboard = new FakeClipboardSession
        {
            CaptureException = new InvalidOperationException(
                "clipboard-secret")
        };
        var reader = CreateReader(
            clipboard,
            new FakeCopySender(),
            new FakeModifiers(),
            new FakeActivator());

        var result = await reader.ReadAsync(Target(), CancellationToken.None);

        Assert.Equal("ClipboardDenied", result.ErrorCode);
        Assert.DoesNotContain(
            "clipboard-secret",
            result.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "InvalidOperationException",
            result.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReaderDoesNotRetainClipboardTextBetweenReads()
    {
        var clipboard = new FakeClipboardSession();
        var reader = CreateReader(
            clipboard,
            new FakeCopySender(),
            new FakeModifiers(),
            new FakeActivator());

        clipboard.TextResults.Enqueue(
            ClipboardTextReadResult.WithText("first"));
        var first = await reader.ReadAsync(Target(), CancellationToken.None);
        clipboard.TextResults.Enqueue(
            ClipboardTextReadResult.WithText("second"));
        var second = await reader.ReadAsync(Target(), CancellationToken.None);
        clipboard.TextResults.Enqueue(
            new ClipboardTextReadResult(
                Denied: false,
                SequenceChanged: true,
                Text: null));
        var third = await reader.ReadAsync(Target(), CancellationToken.None);

        Assert.Equal("first", first.Text);
        Assert.Equal("second", second.Text);
        Assert.Equal("NoSelectedText", third.ErrorCode);
        Assert.Equal(3, clipboard.CaptureCount);
        Assert.Equal(3, clipboard.RestoreCount);
        Assert.Equal(3, clipboard.RestoredSnapshots.Distinct().Count());
        Assert.DoesNotContain(
            typeof(SelectedTextReader)
                .GetFields(
                    BindingFlags.Static |
                    BindingFlags.Public |
                    BindingFlags.NonPublic)
                .ToArray(),
            field => !field.IsLiteral && !field.IsInitOnly);
    }

    private static SelectedTextReader CreateReader(
        IClipboardSession clipboard,
        ICopyShortcutSender sender,
        IHotkeyModifierState modifiers,
        IForegroundActivator activator,
        FakePolling? polling = null) =>
        new(
            clipboard,
            sender,
            modifiers,
            activator,
            TimeSpan.FromMilliseconds(800),
            modifierReleaseTimeout: TimeSpan.FromSeconds(1),
            pollInterval: TimeSpan.FromMilliseconds(25),
            polling: polling ?? new FakePolling());

    private static ForegroundSnapshot Target() =>
        new(
            Epoch: 7,
            TopLevelHwnd: new nint(0x1234),
            FocusHwnd: new nint(0x1235),
            ProcessId: 42,
            ThreadId: 9,
            ProcessStartUtc: DateTimeOffset.UnixEpoch,
            ExePath: @"C:\Apps\target.exe",
            IsOwnInteractive: false);

    private sealed class FakeClipboardSession : IClipboardSession
    {
        public Func<CancellationToken, Task<ClipboardSessionSnapshot?>>
            CaptureFactory { get; set; } =
                _ => Task.FromResult<ClipboardSessionSnapshot?>(
                    new ClipboardSessionSnapshot(
                        10,
                        Array.Empty<ClipboardFormatSnapshot>()));

        public ClipboardSessionSnapshot? CaptureResult
        {
            set
            {
                CaptureException = null;
                CaptureFactory = _ => Task.FromResult(value);
            }
        }

        public Exception? CaptureException { get; set; }
        public ClipboardTextReadResult TextResult { get; set; } =
            ClipboardTextReadResult.WithText("fallback");
        public Queue<ClipboardTextReadResult> TextResults { get; } = new();
        public Exception? WaitException { get; set; }
        public Exception? RestoreException { get; set; }
        public Action? OnCapture { get; set; }
        public Action? OnWait { get; set; }
        public Action? OnRestore { get; set; }
        public int CaptureCount { get; private set; }
        public int WaitCount { get; private set; }
        public int RestoreCount { get; private set; }
        public uint LastSequenceNumber { get; private set; }
        public TimeSpan? LastWaitTimeout { get; private set; }
        public List<ClipboardSessionSnapshot> RestoredSnapshots { get; } =
            new();

        public Task<ClipboardSessionSnapshot?> CaptureAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureCount++;
            OnCapture?.Invoke();
            if (CaptureException is not null)
                throw CaptureException;
            return CaptureFactory(cancellationToken);
        }

        public Task<ClipboardTextReadResult> WaitForTextAsync(
            uint baselineSequenceNumber,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WaitCount++;
            LastSequenceNumber = baselineSequenceNumber;
            LastWaitTimeout = timeout;
            OnWait?.Invoke();
            if (WaitException is not null)
                throw WaitException;
            return Task.FromResult(
                TextResults.Count > 0
                    ? TextResults.Dequeue()
                    : TextResult);
        }

        public Task RestoreAsync(ClipboardSessionSnapshot snapshot)
        {
            RestoreCount++;
            RestoredSnapshots.Add(snapshot);
            OnRestore?.Invoke();
            if (RestoreException is not null)
                throw RestoreException;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCopySender : ICopyShortcutSender
    {
        public bool Succeeds { get; set; } = true;
        public int Count { get; private set; }
        public Action? OnSend { get; set; }

        public bool TrySendCopy()
        {
            Count++;
            OnSend?.Invoke();
            return Succeeds;
        }
    }

    private sealed class FakeModifiers : IHotkeyModifierState
    {
        public bool AlwaysHeld { get; set; }
        public int ReleaseAfterPolls { get; set; }
        public int PollCount { get; private set; }
        public Action? OnPoll { get; set; }

        public bool AreAnyModifiersHeld()
        {
            PollCount++;
            OnPoll?.Invoke();
            if (AlwaysHeld)
                return true;
            if (ReleaseAfterPolls <= 0)
                return false;
            ReleaseAfterPolls--;
            return true;
        }
    }

    private sealed class FakeActivator : IForegroundActivator
    {
        public bool IsForeground { get; set; } = true;
        public bool ActivationSucceeds { get; set; } = true;
        public bool IdentityIsValid { get; set; } = true;
        public int CaptureCount { get; private set; }
        public int ActivationCount { get; private set; }
        public Action? OnCapture { get; set; }
        public Action? OnCheckForeground { get; set; }

        public ForegroundTargetIdentity? CaptureIdentity(
            ForegroundSnapshot target)
        {
            CaptureCount++;
            OnCapture?.Invoke();
            if (!IdentityIsValid)
                return null;
            return new ForegroundTargetIdentity(
                target.TopLevelHwnd,
                target.ProcessId,
                target.ThreadId,
                target.ProcessStartUtc,
                target.ExePath,
                "Target window");
        }

        public bool IsTargetForeground(ForegroundTargetIdentity identity)
        {
            OnCheckForeground?.Invoke();
            return IdentityIsValid && IsForeground;
        }

        public Task<bool> TryActivateAsync(
            ForegroundTargetIdentity identity,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ActivationCount++;
            if (!ActivationSucceeds)
                return Task.FromResult(false);
            IsForeground = true;
            return Task.FromResult(true);
        }
    }

    private sealed class FakePolling : ISelectedTextPolling
    {
        public TimeSpan Elapsed { get; private set; }
        public int DelayCount { get; private set; }

        public TimeSpan GetTimestamp() => Elapsed;

        public Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DelayCount++;
            Elapsed += delay;
            return Task.CompletedTask;
        }
    }
}
