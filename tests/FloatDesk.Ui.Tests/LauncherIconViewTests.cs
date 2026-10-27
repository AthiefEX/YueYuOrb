using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FloatDesk.App.Launcher;
using FloatDesk.App.Views;
using FloatDesk.Core.Models;

namespace FloatDesk.Ui.Tests;

[Collection("WPF")]
public sealed class LauncherIconViewTests
{
    [Fact(Timeout = 10_000)]
    public Task ShowsFallbackUntilImageArrivesThenDisplaysImage() =>
        RunOnStaAsync(async () =>
        {
            var completion = new TaskCompletionSource<ImageSource?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var provider = new FakeIconProvider((_, _) => completion.Task);
            var view = new LauncherIconView
            {
                Launcher = NewLauncher("first.exe"),
                IconProvider = provider,
                IconSize = 42
            };

            Assert.True(view.IsFallbackVisible);
            Assert.Equal("\uE71D", view.FallbackGlyphText);
            await provider.WaitForRequestsAsync(1);

            var image = CreateImage(Colors.CornflowerBlue);
            completion.SetResult(image);
            await WaitUntilAsync(() => ReferenceEquals(
                view.DisplayedImage,
                image));

            Assert.False(view.IsFallbackVisible);
            Assert.NotNull(view.DisplayedImage);
        });

    [Fact(Timeout = 10_000)]
    public Task ProviderFailureKeepsFallbackVisible() =>
        RunOnStaAsync(async () =>
        {
            var provider = new FakeIconProvider(
                (_, _) => Task.FromResult<ImageSource?>(null));
            var view = new LauncherIconView
            {
                Launcher = NewLauncher("missing.exe"),
                IconProvider = provider,
                IconSize = 24
            };

            await provider.WaitForRequestsAsync(1);
            await WaitUntilAsync(() => provider.CompletedRequests >= 1);

            Assert.True(view.IsFallbackVisible);
            Assert.Null(view.DisplayedImage);
        });

    [Fact(Timeout = 10_000)]
    public Task StaleRequestCannotReplaceNewLauncherImage() =>
        RunOnStaAsync(async () =>
        {
            var requests = new List<(
                LauncherItem Item,
                TaskCompletionSource<ImageSource?> Completion)>();
            var provider = new FakeIconProvider((item, _) =>
            {
                var completion = new TaskCompletionSource<ImageSource?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                requests.Add((item, completion));
                return completion.Task;
            });
            var first = NewLauncher("first.exe");
            var second = NewLauncher("second.exe");
            var view = new LauncherIconView
            {
                Launcher = first,
                IconProvider = provider,
                IconSize = 42
            };
            await WaitUntilAsync(() => requests.Count == 1);

            view.Launcher = second;
            await WaitUntilAsync(() => requests.Count == 2);
            var firstImage = CreateImage(Colors.Red);
            var secondImage = CreateImage(Colors.Green);
            requests[0].Completion.SetResult(firstImage);
            await PumpDispatcherAsync();

            Assert.Null(view.DisplayedImage);
            Assert.True(view.IsFallbackVisible);

            requests[1].Completion.SetResult(secondImage);
            await WaitUntilAsync(() => ReferenceEquals(
                view.DisplayedImage,
                secondImage));

            Assert.False(view.IsFallbackVisible);
        });

    [Fact(Timeout = 10_000)]
    public Task LoadedEventKeepsWarmedImageVisible() =>
        RunOnStaAsync(async () =>
        {
            var image = CreateImage(Colors.Gold);
            var provider = new FakeIconProvider(
                (_, _) => Task.FromResult<ImageSource?>(image));
            var view = new LauncherIconView
            {
                Launcher = NewLauncher("warm.exe"),
                IconProvider = provider,
                IconSize = 42
            };
            await WaitUntilAsync(() => ReferenceEquals(
                view.DisplayedImage,
                image));

            view.RaiseEvent(new System.Windows.RoutedEventArgs(
                System.Windows.FrameworkElement.LoadedEvent));

            Assert.False(view.IsFallbackVisible);
            Assert.Same(image, view.DisplayedImage);
            Assert.Equal(1, provider.Requests);
        });

    private static async Task RunOnStaAsync(Func<Task> test)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await test();
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(
                        DispatcherPriority.Background);
                }
            });
            Dispatcher.Run();
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        await completion.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.True(
            thread.Join(TimeSpan.FromSeconds(2)),
            "STA test thread timed out.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Timed out waiting for icon state.");
            await PumpDispatcherAsync();
        }
    }

    private static async Task PumpDispatcherAsync()
    {
        await Dispatcher.Yield(DispatcherPriority.Background);
        await Task.Delay(5);
        await Dispatcher.Yield(DispatcherPriority.Background);
    }

    private static LauncherItem NewLauncher(string name) =>
        new(
            Guid.NewGuid(),
            Path.GetFileNameWithoutExtension(name),
            LauncherKind.Exe,
            Path.Combine(@"C:\Apps", name),
            [],
            @"C:\Apps",
            0);

    private static BitmapSource CreateImage(Color color)
    {
        var bitmap = BitmapSource.Create(
            2,
            2,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            new byte[]
            {
                0, 0, 0, 0,
                0, 0, 0, 0,
                0, 0, 0, 0,
                color.B, color.G, color.R, color.A
            },
            8);
        bitmap.Freeze();
        return bitmap;
    }

    private sealed class FakeIconProvider(
        Func<LauncherItem, CancellationToken, Task<ImageSource?>> getIcon) :
        ILauncherIconProvider
    {
        private readonly object _gate = new();
        private int _requests;
        private int _completedRequests;

        public int CompletedRequests
        {
            get
            {
                lock (_gate)
                    return _completedRequests;
            }
        }

        public int Requests
        {
            get
            {
                lock (_gate)
                    return _requests;
            }
        }

        public async Task<ImageSource?> GetAsync(
            LauncherItem item,
            CancellationToken cancellationToken)
        {
            lock (_gate)
                _requests++;
            try
            {
                return await getIcon(item, cancellationToken);
            }
            finally
            {
                lock (_gate)
                    _completedRequests++;
            }
        }

        public async Task WaitForRequestsAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (true)
            {
                lock (_gate)
                {
                    if (_requests >= count)
                        return;
                }
                if (DateTime.UtcNow >= deadline)
                    throw new TimeoutException("Timed out waiting for icon request.");
                await PumpDispatcherAsync();
            }
        }
    }
}
