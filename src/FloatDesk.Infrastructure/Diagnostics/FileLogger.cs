using System.Text.Json;

namespace FloatDesk.Infrastructure.Diagnostics;

public sealed class FileLogger : IDisposable
{
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileLogger(string directory) => _directory = directory;

    public async Task WriteAsync(
        string eventName,
        IReadOnlyDictionary<string, object?>? properties = null)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(
                _directory,
                $"app-{DateTime.UtcNow:yyyyMMdd}.log");
            var line = JsonSerializer.Serialize(new
            {
                timestampUtc = DateTime.UtcNow,
                eventName,
                properties
            });
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await File.AppendAllTextAsync(path, line + Environment.NewLine)
                    .ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch
        {
            // Diagnostics must not take down the app.
        }
    }

    public void Dispose() => _gate.Dispose();
}
