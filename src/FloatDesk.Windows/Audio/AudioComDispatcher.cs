using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace FloatDesk.Windows.Audio;

internal sealed class AudioComDispatcher : IDisposable
{
    private readonly BlockingCollection<IWorkItem> queue = new();
    private readonly object gate = new();
    private readonly Thread thread;
    private bool disposed;

    internal AudioComDispatcher()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "FloatDesk Core Audio" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    internal Task<T> Invoke<T>(Func<T> action, CancellationToken token)
    {
        lock (gate)
        {
            if (disposed)
                return Task.FromException<T>(new ObjectDisposedException(nameof(WindowsAudioService)));
            if (token.IsCancellationRequested)
                return Task.FromCanceled<T>(token);
            var item = new WorkItem<T>(action, token);
            queue.Add(item, CancellationToken.None); // Unbounded queue; cancellation is checked by the worker.
            return item.Task;
        }
    }

    private void Run()
    {
        // Every successful CoInitializeEx, including S_FALSE, must have a matching CoUninitialize.
        var hr = AudioInterop.CoInitializeEx(IntPtr.Zero, 0); // COINIT_MULTITHREADED
        var initializationError = hr < 0 ? Marshal.GetExceptionForHR(hr) : null;
        try
        {
            foreach (var item in queue.GetConsumingEnumerable())
                item.Execute(initializationError);
        }
        finally
        {
            if (hr >= 0)
                AudioInterop.CoUninitialize();
            queue.Dispose();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (!disposed)
            {
                disposed = true;
                queue.CompleteAdding();
            }
        }
        thread.Join();
    }

    private interface IWorkItem { void Execute(Exception? initializationError); }

    private sealed class WorkItem<T>(Func<T> action, CancellationToken token) : IWorkItem
    {
        private readonly TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<T> Task => completion.Task;

        public void Execute(Exception? initializationError)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                if (initializationError is not null)
                    throw new InvalidOperationException("Could not initialize the Core Audio COM apartment.", initializationError);
                completion.SetResult(action());
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                completion.SetCanceled(token);
            }
            catch (Exception error)
            {
                // Errors are delivered to the caller; a failed endpoint must not kill the worker.
                completion.SetException(error);
            }
        }
    }
}

