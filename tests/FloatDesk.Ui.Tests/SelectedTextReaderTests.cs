using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FloatDesk.App.Translation;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

public sealed class SelectedTextReaderTests
{
    private static readonly string[] OldFileDrop = { "old.txt" };
    private static readonly string[] RestoredFileDrop =
        { @"C:\old.txt" };
    private static readonly SendInputEvent[] ExpectedCopyEvents =
    {
        new(0x11, 0),
        new(0x43, 0),
        new(0x43, 0x0002),
        new(0x11, 0x0002)
    };
    private static readonly SendInputEvent ControlKeyUp = new(0x11, 0x0002);
    private static readonly SendInputEvent CKeyUp = new(0x43, 0x0002);

    [Fact]
    public async Task ReaderWaitsForHotkeyModifiersBeforeSendingCopy()
    {
        var order = new List<string>();
        var clipboardCaptured = false;
        var activator = new FakeActivator
        {
            OnCapture = () => order.Add("identity"),
            OnCheckForeground = () => order.Add("foreground")
        };
        var modifiers = new FakeModifiers
        {
            ReleaseAfterPolls = 2,
            OnPoll = () =>
            {
                if (!clipboardCaptured)
                    order.Add("poll");
            }
        };
        var clipboard = new FakeClipboardSession
        {
            TextResult = ClipboardTextReadResult.WithText(
                "selected text",
                11),
            OnWait = () => order.Add("wait"),
            OnRestore = () => order.Add("restore")
        };
        clipboard.OnAfterCapture = () =>
        {
            clipboardCaptured = true;
            order.Add("clipboard");
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
            TextResult = ClipboardTextReadResult.WithText(
                "new selection",
                42)
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
            TextResult = ClipboardTextReadResult.WithoutText(
                observedSequenceNumber: null)
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
            TextResult = ClipboardTextReadResult.WithoutText(11)
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
            TextResult = ClipboardTextReadResult.WithText("selection", 11)
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
    public async Task ReaderAbortsWhenFocusedControlChangesAfterSnapshot()
    {
        var activator = new FakeActivator();
        var clipboard = new FakeClipboardSession
        {
            OnAfterCapture = () =>
                activator.FocusedControlHwnd = new nint(0x9999),
            TextResult = ClipboardTextReadResult.WithText(
                "must not be read",
                11)
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
        Assert.Equal(0, clipboard.WaitCount);
        Assert.Equal(1, clipboard.RestoreCount);
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
    public async Task ReaderAbortsWhenModifierIsPressedDuringClipboardCapture()
    {
        var modifiers = new FakeModifiers();
        var clipboard = new FakeClipboardSession
        {
            OnAfterCapture = () => modifiers.AlwaysHeld = true
        };
        var sender = new FakeCopySender();
        var reader = CreateReader(
            clipboard,
            sender,
            modifiers,
            new FakeActivator());

        var result = await reader.ReadAsync(Target(), CancellationToken.None);

        Assert.Equal("ModifiersHeld", result.ErrorCode);
        Assert.Equal(0, sender.Count);
        Assert.Equal(0, clipboard.WaitCount);
        Assert.Equal(1, clipboard.RestoreCount);
    }

    [Fact]
    public async Task ReaderRechecksModifierAfterFinalForegroundActivation()
    {
        var modifiers = new FakeModifiers();
        var activator = new FakeActivator();
        var clipboardCaptured = false;
        var foregroundCleared = false;
        modifiers.OnPoll = () =>
        {
            if (!clipboardCaptured || foregroundCleared)
                return;
            foregroundCleared = true;
            activator.IsForeground = false;
        };
        activator.OnActivateAsync = async _ =>
        {
            modifiers.AlwaysHeld = true;
            await Task.Yield();
        };
        var clipboard = new FakeClipboardSession
        {
            TextResult = ClipboardTextReadResult.WithText(
                "must not be read",
                11)
        };
        clipboard.OnAfterCapture = () => clipboardCaptured = true;
        var sender = new FakeCopySender();
        var reader = CreateReader(
            clipboard,
            sender,
            modifiers,
            activator);

        var result = await reader.ReadAsync(Target(), CancellationToken.None);

        Assert.Equal("ModifiersHeld", result.ErrorCode);
        Assert.Equal(1, activator.ActivationCount);
        Assert.Equal(0, sender.Count);
        Assert.Equal(0, clipboard.WaitCount);
        Assert.Equal(1, clipboard.RestoreCount);
    }

    [Fact]
    public async Task ExternalChangeBeforeInjectionAbortsWithoutRestoringNewerClipboard()
    {
        var clipboard = new FakeClipboardSession();
        clipboard.OnAfterCapture =
            () => clipboard.CurrentSequenceNumber = 11;
        var sender = new FakeCopySender();
        var reader = CreateReader(
            clipboard,
            sender,
            new FakeModifiers(),
            new FakeActivator());

        var result = await reader.ReadAsync(Target(), CancellationToken.None);

        Assert.Equal("ClipboardDenied", result.ErrorCode);
        Assert.Equal(0, sender.Count);
        Assert.Equal(0, clipboard.RestoreAttemptCount);
        Assert.Equal(11u, clipboard.CurrentSequenceNumber);
    }

    [Fact]
    public async Task ExternalChangeAfterObservedCopyIsNeverOverwritten()
    {
        var clipboard = new FakeClipboardSession
        {
            TextResult = ClipboardTextReadResult.WithText(
                "stale selection",
                11)
        };
        clipboard.OnAfterWait =
            () => clipboard.CurrentSequenceNumber = 12;
        var sender = new FakeCopySender();
        var reader = CreateReader(
            clipboard,
            sender,
            new FakeModifiers(),
            new FakeActivator());

        var result = await reader.ReadAsync(Target(), CancellationToken.None);

        Assert.Equal("ClipboardDenied", result.ErrorCode);
        Assert.Null(result.Text);
        Assert.Equal(0, clipboard.RestoreAttemptCount);
        Assert.Equal(12u, clipboard.CurrentSequenceNumber);
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

    [Fact(Timeout = 10_000)]
    public async Task DisposeAsyncCancelsActiveAndQueuedReadsBeforeDisposingGate()
    {
        var waitStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var clipboard = new FakeClipboardSession
        {
            WaitFactory = async (_, _, cancellationToken) =>
            {
                waitStarted.TrySetResult();
                await Task.Delay(
                        Timeout.InfiniteTimeSpan,
                        cancellationToken)
                    .ConfigureAwait(false);
                throw new InvalidOperationException(
                    "The wait should have been cancelled.");
            }
        };
        var reader = CreateReader(
            clipboard,
            new FakeCopySender(),
            new FakeModifiers(),
            new FakeActivator());

        var active = reader.ReadAsync(Target(), CancellationToken.None);
        await waitStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var queued = reader.ReadAsync(Target(), CancellationToken.None);
        Assert.Equal(2, reader.ActiveReadCount);

        var disposal = reader.DisposeAsync().AsTask();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => active.WaitAsync(TimeSpan.FromSeconds(2)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => queued.WaitAsync(TimeSpan.FromSeconds(2)));
        await disposal.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, reader.ActiveReadCount);
        Assert.Equal(1, clipboard.RestoreCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => reader.ReadAsync(Target(), CancellationToken.None));
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
            Result = new CopyShortcutSendResult(
                RequestedCount: 4,
                InsertedCount: 2,
                CompensationRequestedCount: 2,
                CompensationInsertedCount: 2)
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
    public async Task ReaderFailsSafeWhenCompensationRemainsIncomplete()
    {
        var clipboard = new FakeClipboardSession();
        var sender = new FakeCopySender
        {
            Result = new CopyShortcutSendResult(
                RequestedCount: 4,
                InsertedCount: 2,
                CompensationRequestedCount: 2,
                CompensationInsertedCount: 1)
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

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void CopySenderUsesStableEventOrderAndCompensatesPartials(
        int insertedCount)
    {
        var requiredKeyUps = insertedCount switch
        {
            1 or 3 => new[] { ControlKeyUp },
            2 => new[] { ControlKeyUp, CKeyUp },
            _ => Array.Empty<SendInputEvent>()
        };
        var backend = new FakeSendInputBackend(
            insertedCount,
            requiredKeyUps.Select(_ => 1).ToArray());
        var sender = new Win32CopyShortcutSender(backend);

        var result = sender.SendCopy();

        Assert.Equal(4, result.RequestedCount);
        Assert.Equal(insertedCount, result.InsertedCount);
        Assert.Equal(
            ExpectedCopyEvents,
            backend.Batches[0]);

        if (insertedCount is > 0 and < 4)
        {
            Assert.Equal(
                requiredKeyUps.Length + 1,
                backend.Batches.Count);
            for (var index = 0; index < requiredKeyUps.Length; index++)
            {
                Assert.Equal(
                    new[] { requiredKeyUps[index] },
                    backend.Batches[index + 1]);
            }
            Assert.Equal(
                requiredKeyUps.Length,
                result.CompensationRequestedCount);
            Assert.Equal(
                requiredKeyUps.Length,
                result.CompensationInsertedCount);
            Assert.True(result.CompensationComplete);
            Assert.False(result.Succeeded);
        }
        else
        {
            Assert.Single(backend.Batches);
            Assert.Equal(0, result.CompensationRequestedCount);
            Assert.Equal(0, result.CompensationInsertedCount);
            Assert.True(result.CompensationComplete);
            Assert.Equal(insertedCount == 4, result.Succeeded);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void CopySenderReportsAcceptedCompensationKeyUps(
        int acceptedCount)
    {
        var responses = acceptedCount switch
        {
            0 => new[] { 0, 0, 0, 0, 0, 0 },
            1 => new[] { 1, 0, 0, 0 },
            _ => new[] { 1, 1 }
        };
        var backend = new FakeSendInputBackend(
            insertedCount: 2,
            responses);
        var sender = new Win32CopyShortcutSender(backend);

        var result = sender.SendCopy();

        Assert.Equal(
            new CopyShortcutSendResult(
                RequestedCount: 4,
                InsertedCount: 2,
                CompensationRequestedCount: 2,
                CompensationInsertedCount: acceptedCount),
            result);
        Assert.Equal(acceptedCount == 2, result.CompensationComplete);
        Assert.False(result.Succeeded);
        var expectedCompensationAttempts = acceptedCount switch
        {
            0 => 6,
            1 => 4,
            _ => 2
        };
        Assert.Equal(
            expectedCompensationAttempts + 1,
            backend.Batches.Count);
        Assert.All(
            backend.Batches.Skip(1),
            batch => Assert.Single(batch));
        Assert.Contains(
            backend.Batches.Skip(1),
            batch =>
                batch.Count == 1 &&
                batch[0] == ControlKeyUp);
        Assert.Contains(
            backend.Batches.Skip(1),
            batch =>
                batch.Count == 1 &&
                batch[0] == CKeyUp);
    }

    [Fact]
    public void CopySenderRetriesTransientCompensationFailures()
    {
        var responses = new[] { 0, 1, 0, 1 };
        var backend = new FakeSendInputBackend(
            insertedCount: 2,
            compensationResponses: responses);
        var sender = new Win32CopyShortcutSender(backend);

        var result = sender.SendCopy();

        Assert.Equal(
            new CopyShortcutSendResult(
                RequestedCount: 4,
                InsertedCount: 2,
                CompensationRequestedCount: 2,
                CompensationInsertedCount: 2),
            result);
        Assert.True(result.CompensationComplete);
        Assert.False(result.Succeeded);
        Assert.Equal(5, backend.Batches.Count);
        Assert.Equal(new[] { ControlKeyUp }, backend.Batches[1]);
        Assert.Equal(new[] { ControlKeyUp }, backend.Batches[2]);
        Assert.Equal(new[] { CKeyUp }, backend.Batches[3]);
        Assert.Equal(new[] { CKeyUp }, backend.Batches[4]);
    }

    [Fact]
    public void ClipboardSnapshotMaterializerClonesMutablePayloads()
    {
        RunOnSta(() =>
        {
            var bytes = new byte[] { 1, 2, 3 };
            var (byteClone, byteOwnsData) =
                ClipboardSnapshotMaterializer.CloneValue(bytes);
            var clonedBytes = Assert.IsType<byte[]>(byteClone);
            Assert.NotSame(bytes, clonedBytes);
            Assert.Equal(bytes, clonedBytes);
            Assert.False(byteOwnsData);

            var stream = new MemoryStream(bytes)
            {
                Position = 2
            };
            var (streamClone, streamOwnsData) =
                ClipboardSnapshotMaterializer.CloneValue(stream);
            var clonedStream = Assert.IsType<MemoryStream>(streamClone);
            Assert.True(streamOwnsData);
            Assert.Equal(2, clonedStream.Position);
            Assert.Equal(2, stream.Position);
            clonedStream.Position = 0;
            using var copied = new MemoryStream();
            clonedStream.CopyTo(copied);
            Assert.Equal(bytes, copied.ToArray());
            clonedStream.Dispose();

            var bitmap = BitmapSource.Create(
                1,
                1,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                new byte[] { 1, 2, 3, 4 },
                4);
            var (bitmapClone, bitmapOwnsData) =
                ClipboardSnapshotMaterializer.CloneValue(bitmap);
            var clonedBitmap =
                Assert.IsAssignableFrom<BitmapSource>(bitmapClone);
            Assert.NotSame(bitmap, clonedBitmap);
            Assert.False(bitmapOwnsData);
        });
    }

    [Fact]
    public void ClipboardSnapshotMaterializerBuildsRestorableDataObject()
    {
        RunOnSta(() =>
        {
            var snapshot = new ClipboardSessionSnapshot(
                5,
                new[]
                {
                    new ClipboardFormatSnapshot(
                        DataFormats.UnicodeText,
                        "old text"),
                    new ClipboardFormatSnapshot(
                        "Bytes",
                        new byte[] { 4, 5, 6 }),
                    new ClipboardFormatSnapshot(
                        "Stream",
                        new MemoryStream(new byte[] { 7, 8 })),
                    new ClipboardFormatSnapshot(
                        DataFormats.FileDrop,
                        RestoredFileDrop)
                });
            try
            {
                var dataObject =
                    ClipboardSnapshotMaterializer.CreateDataObject(snapshot);

                Assert.Equal(
                    "old text",
                    dataObject.GetData(DataFormats.UnicodeText));
                Assert.Equal(
                    new byte[] { 4, 5, 6 },
                    Assert.IsType<byte[]>(
                        dataObject.GetData("Bytes")));
                var stream = Assert.IsAssignableFrom<Stream>(
                    dataObject.GetData("Stream"));
                stream.Position = 0;
                using var copied = new MemoryStream();
                stream.CopyTo(copied);
                Assert.Equal(new byte[] { 7, 8 }, copied.ToArray());
                Assert.Equal(
                    RestoredFileDrop,
                    Assert.IsType<string[]>(
                        dataObject.GetData(DataFormats.FileDrop)));
            }
            finally
            {
                snapshot.Dispose();
            }
        });
    }

    [Fact]
    public async Task RestoreFailureDoesNotReplaceSuccessfulText()
    {
        var clipboard = new FakeClipboardSession
        {
            TextResult = ClipboardTextReadResult.WithText("selection", 11),
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
            ClipboardTextReadResult.WithText("first", 11));
        var first = await reader.ReadAsync(Target(), CancellationToken.None);
        clipboard.TextResults.Enqueue(
            ClipboardTextReadResult.WithText("second", 11));
        var second = await reader.ReadAsync(Target(), CancellationToken.None);
        clipboard.TextResults.Enqueue(
            ClipboardTextReadResult.WithoutText(11));
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

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(
            thread.Join(TimeSpan.FromSeconds(5)),
            "STA test thread timed out.");
        if (failure is not null)
            throw failure;
    }

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
            ClipboardTextReadResult.WithText("fallback", 11);
        public Queue<ClipboardTextReadResult> TextResults { get; } = new();
        public Func<
            uint,
            TimeSpan,
            CancellationToken,
            Task<ClipboardTextReadResult>>? WaitFactory
        {
            get;
            set;
        }
        public Exception? WaitException { get; set; }
        public Exception? RestoreException { get; set; }
        public Action? OnCapture { get; set; }
        public Action? OnAfterCapture { get; set; }
        public Action? OnWait { get; set; }
        public Action? OnAfterWait { get; set; }
        public Action? OnRestore { get; set; }
        public int CaptureCount { get; private set; }
        public int WaitCount { get; private set; }
        public int SequenceCheckCount { get; private set; }
        public int RestoreAttemptCount { get; private set; }
        public int RestoreCount { get; private set; }
        public uint LastSequenceNumber { get; private set; }
        public uint? LastRestoreExpectedSequence { get; private set; }
        public TimeSpan? LastWaitTimeout { get; private set; }
        public uint CurrentSequenceNumber { get; set; } = 10;
        public List<ClipboardSessionSnapshot> RestoredSnapshots { get; } =
            new();

        public async Task<ClipboardSessionSnapshot?> CaptureAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureCount++;
            OnCapture?.Invoke();
            if (CaptureException is not null)
                throw CaptureException;
            var snapshot = await CaptureFactory(cancellationToken)
                .ConfigureAwait(false);
            if (snapshot is not null)
                CurrentSequenceNumber = snapshot.SequenceNumber;
            OnAfterCapture?.Invoke();
            return snapshot;
        }

        public Task<uint> GetSequenceNumberAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SequenceCheckCount++;
            return Task.FromResult(CurrentSequenceNumber);
        }

        public async Task<ClipboardTextReadResult> WaitForTextAsync(
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
            var result = WaitFactory is not null
                ? await WaitFactory(
                        baselineSequenceNumber,
                        timeout,
                        cancellationToken)
                    .ConfigureAwait(false)
                : TextResults.Count > 0
                    ? TextResults.Dequeue()
                    : TextResult;
            if (result.ObservedSequenceNumber is uint observedSequence)
                CurrentSequenceNumber = observedSequence;
            OnAfterWait?.Invoke();
            return result;
        }

        public Task<bool> RestoreIfSequenceAsync(
            ClipboardSessionSnapshot snapshot,
            uint expectedSequenceNumber)
        {
            RestoreAttemptCount++;
            LastRestoreExpectedSequence = expectedSequenceNumber;
            if (CurrentSequenceNumber != expectedSequenceNumber)
                return Task.FromResult(false);
            RestoreCount++;
            RestoredSnapshots.Add(snapshot);
            OnRestore?.Invoke();
            if (RestoreException is not null)
                throw RestoreException;
            return Task.FromResult(true);
        }
    }

    private sealed class FakeCopySender : ICopyShortcutSender
    {
        public CopyShortcutSendResult Result { get; set; } =
            CopyShortcutSendResult.Complete(4);
        public int Count { get; private set; }
        public Action? OnSend { get; set; }

        public CopyShortcutSendResult SendCopy()
        {
            Count++;
            OnSend?.Invoke();
            return Result;
        }
    }

    private sealed class FakeSendInputBackend : ISendInputBackend
    {
        private readonly int _mainInsertedCount;
        private readonly Queue<int> _compensationResponses;

        public FakeSendInputBackend(
            int insertedCount,
            params int[] compensationResponses)
        {
            _mainInsertedCount = insertedCount;
            _compensationResponses = new Queue<int>(
                compensationResponses);
        }

        public List<IReadOnlyList<SendInputEvent>> Batches { get; } = new();

        public int SendInput(IReadOnlyList<SendInputEvent> events)
        {
            Batches.Add(events.ToArray());
            return Batches.Count == 1
                ? _mainInsertedCount
                : _compensationResponses.Count > 0
                    ? _compensationResponses.Dequeue()
                    : 0;
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
        public nint ForegroundTopLevelHwnd { get; set; } =
            new(0x1234);
        public nint FocusedControlHwnd { get; set; } = new(0x1235);
        public bool ActivationSucceeds { get; set; } = true;
        public bool IdentityIsValid { get; set; } = true;
        public int CaptureCount { get; private set; }
        public int ActivationCount { get; private set; }
        public Action? OnCapture { get; set; }
        public Action? OnCheckForeground { get; set; }
        public Func<CancellationToken, Task>? OnActivateAsync
        {
            get;
            set;
        }

        public ForegroundTargetIdentity? CaptureIdentity(
            ForegroundSnapshot target)
        {
            CaptureCount++;
            OnCapture?.Invoke();
            if (!IdentityIsValid)
                return null;
            return new ForegroundTargetIdentity(
                target.TopLevelHwnd,
                target.FocusHwnd,
                target.ProcessId,
                target.ThreadId,
                target.ProcessStartUtc,
                target.ExePath,
                "Target window");
        }

        public bool IsTargetForeground(ForegroundTargetIdentity identity)
        {
            OnCheckForeground?.Invoke();
            return IdentityIsValid &&
                IsForeground &&
                identity.TopLevelHwnd == ForegroundTopLevelHwnd &&
                identity.FocusHwnd == FocusedControlHwnd;
        }

        public async Task<bool> TryActivateAsync(
            ForegroundTargetIdentity identity,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ActivationCount++;
            if (!ActivationSucceeds ||
                identity.FocusHwnd != FocusedControlHwnd)
                return false;
            if (OnActivateAsync is not null)
                await OnActivateAsync(cancellationToken);
            IsForeground = true;
            ForegroundTopLevelHwnd = identity.TopLevelHwnd;
            return true;
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
