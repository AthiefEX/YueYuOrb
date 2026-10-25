using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FloatDesk.Core.Geometry;
using FloatDesk.App;
using FloatDesk.App.Views;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

[Collection("WPF")]
public sealed class NotesWindowSelectionTests
{
    [Fact(Timeout = 10_000)]
    public async Task FailedSaveRestoresSelectionAndBlocksDelete()
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var app = new FloatDesk.App.App();
                app.InitializeComponent();
                var first = Note(1, "First", "first body");
                var second = Note(2, "Second", "second body");
                var repository = new FailingSaveRepository(first, second);
                var window = new NotesWindow(repository);
                window.Show();
                _ = RunAssertionAsync(app, window, first, second)
                    .ContinueWith(
                        task =>
                        {
                            if (task.IsCompletedSuccessfully)
                                completion.SetResult();
                            else
                                completion.SetException(task.Exception!);
                            app.Shutdown();
                        },
                        TaskScheduler.Default);
                Dispatcher.Run();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        thread.Join(TimeSpan.FromSeconds(2));
    }

    private static async Task RunAssertionAsync(
        FloatDesk.App.App app,
        NotesWindow window,
        NoteDocument first,
        NoteDocument second)
    {
        var list = GetField<ListBox>(window, "NotesList");
        var body = GetField<TextBox>(window, "BodyBox");
        await WaitUntilAsync(() =>
            list.SelectedItem is NoteSummary summary &&
            summary.Id == first.Id &&
            body.Text == first.Body);

        body.Text = "unsaved body";
        list.SelectedItem = list.Items
            .Cast<NoteSummary>()
            .Single(summary => summary.Id == second.Id);
        await WaitUntilAsync(() => body.Text == "unsaved body" && list.SelectedItem is NoteSummary);

        Assert.Equal(first.Id, Assert.IsType<NoteSummary>(list.SelectedItem).Id);
        var deleteMethod = window.GetType().GetMethod(
            "MoveCurrentToRecycleAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(deleteMethod);
        var deleted = await Assert.IsType<Task<bool>>(
            deleteMethod.Invoke(window, null));
        Assert.False(deleted);
        Assert.Equal(0, ((FailingSaveRepository)GetField<object>(
            window,
            "_repository")).SetDeletedCalls);
        Assert.Equal("unsaved body", body.Text);

        var invalidDirectory = Path.Combine(
            Path.GetTempPath(),
            $"FloatDeskCapture-{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(invalidDirectory, "not a directory");
        try
        {
            var captureSettings = AppSettings.CreateDefault().Capture with
            {
                AutoSave = true,
                SaveDirectory = invalidDirectory
            };
            await window.Dispatcher.InvokeAsync(async () =>
            {
                using var frame = new CaptureFrame(
                    new PixelRect(0, 0, 16, 16),
                    16 * 4,
                    new byte[16 * 16 * 4]);
                var editor = new CaptureEditorWindow(frame, captureSettings);
                var exportMethod = editor.GetType().GetMethod(
                    "ExportAndCompleteAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(exportMethod);
                var exportTask = Assert.IsAssignableFrom<Task>(
                    exportMethod!.Invoke(editor, [false]));

                await exportTask;

                Assert.False(editor.Completed.IsCompleted);
                Assert.Contains(
                    "保存",
                    GetField<TextBlock>(editor, "StatusText").Text,
                    StringComparison.Ordinal);
                editor.Close();
            }).Task.Unwrap();
        }
        finally
        {
            File.Delete(invalidDirectory);
        }

        await window.Dispatcher.InvokeAsync(async () =>
        {
            using var captionNotes = new NotesWindow(
                new FailingSaveRepository(first, second));
            await AssertCaptionButtonsAsync(
                captionNotes,
                "NotesMinimizeButton",
                "NotesMaximizeRestoreButton",
                "NotesCloseButton",
                "NotesMaximizeGlyph",
                "最大化记事本",
                "还原记事本");
            using var captionBrowser = new AiBrowserWindow(
                new AppDataPaths(),
                _ => Task.CompletedTask);
            await AssertCaptionButtonsAsync(
                captionBrowser,
                "AiMinimizeButton",
                "AiMaximizeRestoreButton",
                "AiCloseButton",
                "AiMaximizeGlyph",
                "最大化 AI 网页",
                "还原 AI 网页");
        }).Task.Unwrap();

        await window.Dispatcher.InvokeAsync(window.Hide);
        await Task.CompletedTask;
    }

    private static async Task AssertCaptionButtonsAsync(
        Window window,
        string minimizeName,
        string maximizeName,
        string closeName,
        string glyphName,
        string maximizeAutomationName,
        string restoreAutomationName)
    {
        window.Show();
        var minimize = Assert.IsType<Button>(window.FindName(minimizeName));
        var maximize = Assert.IsType<Button>(window.FindName(maximizeName));
        var close = Assert.IsType<Button>(window.FindName(closeName));
        var glyph = Assert.IsType<TextBlock>(window.FindName(glyphName));
        var closeRequested = false;
        window.Closing += (_, _) => closeRequested = true;

        Assert.Equal(WindowState.Normal, window.WindowState);
        Assert.Equal("\uE922", glyph.Text);
        Assert.Equal(
            maximizeAutomationName,
            System.Windows.Automation.AutomationProperties.GetName(maximize));

        InvokeButton(maximize);
        await WaitUntilAsync(() => window.WindowState == WindowState.Maximized);

        Assert.Equal("\uE923", glyph.Text);
        Assert.Equal(
            restoreAutomationName,
            System.Windows.Automation.AutomationProperties.GetName(maximize));

        InvokeButton(maximize);
        await WaitUntilAsync(() => window.WindowState == WindowState.Normal);

        Assert.Equal("\uE922", glyph.Text);
        Assert.Equal(
            maximizeAutomationName,
            System.Windows.Automation.AutomationProperties.GetName(maximize));

        InvokeButton(minimize);
        await WaitUntilAsync(() => window.WindowState == WindowState.Minimized);
        window.WindowState = WindowState.Normal;
        InvokeButton(close);

        await WaitUntilAsync(() => closeRequested);
        window.Hide();
    }

    private static void InvokeButton(Button button)
    {
        var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(button);
        var provider = Assert.IsAssignableFrom<
            System.Windows.Automation.Provider.IInvokeProvider>(
            peer.GetPattern(
                System.Windows.Automation.Peers.PatternInterface.Invoke));
        provider.Invoke();
    }

    private static T GetField<T>(object target, string name) =>
        (T)(target.GetType().GetField(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target)
            ?? throw new InvalidOperationException($"Field {name} was not found."));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException("The notes window did not reach the expected state.");
            await Dispatcher.Yield(DispatcherPriority.Background);
        }
    }

    private static NoteDocument Note(int idSeed, string title, string body)
    {
        var now = DateTimeOffset.UtcNow;
        return new NoteDocument(
            new Guid(idSeed, 0, 0, new byte[8]),
            title,
            body,
            1,
            now,
            now,
            null);
    }

    private sealed class FailingSaveRepository(
        NoteDocument first,
        NoteDocument second) : INoteRepository
    {
        private readonly IReadOnlyList<NoteDocument> _notes = [first, second];

        public int SetDeletedCalls { get; private set; }

        public Task<IReadOnlyList<NoteSummary>> SearchAsync(
            string query,
            bool recycleBin,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<NoteSummary>>(
                _notes
                    .Select(note => new NoteSummary(
                        note.Id,
                        note.Title,
                        note.Revision,
                        note.UpdatedUtc,
                        false))
                    .ToArray());

        public Task<NoteDocument?> GetAsync(
            Guid id,
            CancellationToken cancellationToken) =>
            Task.FromResult(_notes.FirstOrDefault(note => note.Id == id));

        public Task<OperationResult> SaveAsync(
            NoteDocument note,
            CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Fail("DiskFull", "disk full"));

        public Task<OperationResult> SetDeletedAsync(
            Guid id,
            bool deleted,
            CancellationToken cancellationToken)
        {
            SetDeletedCalls++;
            return Task.FromResult(OperationResult.Ok());
        }

        public Task<OperationResult> PurgeDeletedAsync(
            DateTimeOffset cutoffUtc,
            CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Ok());
    }
}

[CollectionDefinition("WPF", DisableParallelization = true)]
public sealed class WpfTestGroup;
