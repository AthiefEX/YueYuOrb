using System.Text.Json;
using System.Text.Json.Serialization;
using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;

namespace FloatDesk.Infrastructure.Settings;

public sealed class JsonSettingsStore : ISettingsStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _directory;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private string? _readOnlyReason;

    public JsonSettingsStore(string directory)
    {
        _directory = Path.GetFullPath(directory);
    }

    private string PrimaryPath => Path.Combine(_directory, "settings.json");
    private string BackupPath => Path.Combine(_directory, "settings.json.bak");

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        foreach (var path in new[] { PrimaryPath, BackupPath })
        {
            if (!File.Exists(path))
                continue;
            try
            {
                var json = await File.ReadAllTextAsync(path, cancellationToken);
                return SettingsMigrator.Deserialize(json, JsonOptions);
            }
            catch (NewerSettingsVersionException)
            {
                _readOnlyReason =
                    "设置由更新版本的 FloatDesk 创建，当前版本只会只读运行，不会覆盖该文件。";
                break;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or
                JsonException or InvalidDataException or InvalidOperationException)
            {
                // Try the previous valid snapshot next.
            }
        }

        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        return AppSettings.CreateDefault(pictures);
    }

    public async Task SaveAsync(AppSettings value, CancellationToken cancellationToken)
    {
        if (_readOnlyReason is not null)
            throw new InvalidOperationException(_readOnlyReason);
        var errors = SettingsValidator.Validate(value);
        if (errors.Count > 0)
            throw new InvalidDataException(string.Join(" ", errors));

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_directory);
            var temporary = PrimaryPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var json = JsonSerializer.Serialize(value, JsonOptions);
                await File.WriteAllTextAsync(temporary, json, cancellationToken);
                if (File.Exists(PrimaryPath))
                    File.Replace(temporary, PrimaryPath, BackupPath, ignoreMetadataErrors: true);
                else
                    File.Move(temporary, PrimaryPath);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Dispose() => _writeGate.Dispose();
}
