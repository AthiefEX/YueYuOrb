using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using FloatDesk.Core.Models;
using Microsoft.Data.Sqlite;

namespace FloatDesk.Infrastructure.Settings;

public static class UserDataArchive
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private static readonly string[] Assets = ["assets/orb.png", "assets/background.png", "assistant/background.png"];
    private static readonly string[] ManagedFiles = ["settings.json", "settings.json.bak", "data/notes.db", "data/notes.db-wal", "data/notes.db-shm", "assets/orb.png", "assets/background.png",
        "assistant/assistant.db", "assistant/assistant.db-wal", "assistant/assistant.db-shm", "assistant/background.png"];
    private static readonly HashSet<string> Entries = new(["manifest.json", "settings.json", "notes.db", "assistant/assistant.db", .. Assets], StringComparer.Ordinal);
    private sealed record Pending(string Action);
    private sealed record Manifest(int Version, bool IncludesCredentials = false, bool IncludesVirtualMachines = false);

    public static async Task ExportAsync(string root, AppSettings settings, string destination, CancellationToken token)
    {
        var temp = Path.Combine(Path.GetTempPath(), "YueYu-Export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var output = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var zip = ZipFile.Open(output, ZipArchiveMode.Create))
            {
                await WriteTextAsync(zip, "manifest.json", JsonSerializer.Serialize(new Manifest(1), Json), token).ConfigureAwait(false);
                await WriteTextAsync(zip, "settings.json", JsonSerializer.Serialize(UserDataPolicy.Exportable(settings), Json), token).ConfigureAwait(false);
                foreach (var relative in new[] { "data/notes.db", "assistant/assistant.db" })
                {
                    var db = SafePath(root, relative); if (!File.Exists(db)) continue;
                    var snapshot = Path.Combine(temp, relative.Replace('/', '_'));
                    using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                    using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = snapshot, Pooling = false }.ToString());
                    await source.OpenAsync(token).ConfigureAwait(false); await target.OpenAsync(token).ConfigureAwait(false);
                    source.BackupDatabase(target);
                    target.Close();
                    zip.CreateEntryFromFile(snapshot, relative == "data/notes.db" ? "notes.db" : relative, CompressionLevel.Optimal);
                }
                foreach (var asset in Assets)
                    if (File.Exists(SafePath(root, asset))) zip.CreateEntryFromFile(SafePath(root, asset), asset, CompressionLevel.Optimal);
            }
            File.Move(output, destination, true);
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
            foreach (var file in Directory.EnumerateFiles(temp)) File.Delete(file);
            Directory.Delete(temp);
        }
    }

    public static async Task QueueImportAsync(string root, string archive, CancellationToken token)
    {
        var files = await ReadArchiveAsync(archive, token).ConfigureAwait(false);
        var directory = SafePath(root, "user-maintenance"); Directory.CreateDirectory(directory);
        var destination = SafePath(root, "user-maintenance/import.zip");
        var staged = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var zip = ZipFile.Open(staged, ZipArchiveMode.Create))
                foreach (var entry in files)
                {
                    using var stream = zip.CreateEntry(entry.Key).Open();
                    await stream.WriteAsync(entry.Value, token).ConfigureAwait(false);
                }
            File.Move(staged, destination, true);
        }
        finally { if (File.Exists(staged)) File.Delete(staged); }
        await WritePendingAsync(root, "import", token).ConfigureAwait(false);
    }

    public static Task QueueResetAsync(string root, CancellationToken token) => WritePendingAsync(root, "reset", token);

    private static async Task WritePendingAsync(string root, string action, CancellationToken token)
    {
        Directory.CreateDirectory(SafePath(root, "user-maintenance"));
        var path = SafePath(root, "user-maintenance/pending.json");
        await ReplaceBytesAsync(path, System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Pending(action), Json)), token).ConfigureAwait(false);
    }

    public static async Task ApplyPendingAsync(string root, CancellationToken token)
    {
        try { await ApplyPendingCoreAsync(root, token).ConfigureAwait(false); }
        catch
        {
            // Never repeat a failed destructive operation on subsequent starts.
            var marker = SafePath(root, "user-maintenance/pending.json");
            if (File.Exists(marker)) File.Move(marker, SafePath(root, "user-maintenance/failed.json"), true);
            throw;
        }
    }

    private static async Task ApplyPendingCoreAsync(string root, CancellationToken token)
    {
        var marker = SafePath(root, "user-maintenance/pending.json");
        if (!File.Exists(marker)) return;
        var action = JsonSerializer.Deserialize<Pending>(await File.ReadAllTextAsync(marker, token).ConfigureAwait(false), Json)?.Action;
        if (action is not ("import" or "reset")) throw new InvalidDataException("数据操作标记无效。");
        using var store = new JsonSettingsStore(root);
        var current = await store.LoadAsync(token).ConfigureAwait(false);
        Dictionary<string, byte[]>? files = action == "import" ? await ReadArchiveAsync(SafePath(root, "user-maintenance/import.zip"), token).ConfigureAwait(false) : null;
        var incoming = files is null ? AppSettings.CreateDefault() : ParseSettings(files["settings.json"]);
        var merged = UserDataPolicy.MergeOrdinary(incoming, current);
        var errors = SettingsValidator.Validate(merged);
        if (errors.Count != 0) throw new InvalidDataException(string.Join(" ", errors));
        var rollback = SafePath(root, "user-maintenance/rollback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rollback);
        var existed = new HashSet<string>();
        var cleanupRollback = true;
        try
        {
            foreach (var relative in ManagedFiles)
            {
                var path = SafePath(root, relative);
                if (!File.Exists(path)) continue;
                File.Copy(path, Path.Combine(rollback, relative.Replace('/', '_')), true); existed.Add(relative);
            }
            try
            {
                await store.SaveAsync(merged, token).ConfigureAwait(false);
                foreach (var relative in ManagedFiles.Where(relative => !relative.StartsWith("settings.", StringComparison.Ordinal)))
                {
                    var path = SafePath(root, relative);
                    var entry = relative == "data/notes.db" ? "notes.db" : relative;
                    if (files is not null && files.TryGetValue(entry, out var content))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                        await ReplaceBytesAsync(path, content, token).ConfigureAwait(false);
                    }
                    else if (File.Exists(path)) File.Delete(path);
                }
                File.Delete(marker);
                var failure = SafePath(root, "user-maintenance/failed.json"); if (File.Exists(failure)) File.Delete(failure);
                var input = SafePath(root, "user-maintenance/import.zip"); if (File.Exists(input)) File.Delete(input);
            }
            catch
            {
                cleanupRollback = false;
                foreach (var relative in ManagedFiles)
                {
                    var path = SafePath(root, relative);
                    if (existed.Contains(relative)) File.Copy(Path.Combine(rollback, relative.Replace('/', '_')), path, true);
                    else if (File.Exists(path)) File.Delete(path);
                }
                cleanupRollback = true;
                throw;
            }
        }
        finally
        {
            if (cleanupRollback)
            {
                foreach (var file in Directory.EnumerateFiles(rollback)) File.Delete(file);
                Directory.Delete(rollback);
            }
        }
    }

    private static async Task<Dictionary<string, byte[]>> ReadArchiveAsync(string path, CancellationToken token)
    {
        if (new FileInfo(path).Length > 512L * 1024 * 1024) throw new InvalidDataException("备份包超过512MiB。");
        using var zip = ZipFile.OpenRead(path);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            if (!Entries.Contains(entry.FullName) || files.ContainsKey(entry.FullName) || entry.Length > 256L * 1024 * 1024 || (total += entry.Length) > 512L * 1024 * 1024)
                throw new InvalidDataException("备份含未知路径、重复条目或过大内容。");
            using var source = entry.Open(); using var target = new MemoryStream();
            var buffer = new byte[65536]; int count;
            while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                if (target.Length + count > entry.Length) throw new InvalidDataException("备份内容长度无效。");
                await target.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            }
            files.Add(entry.FullName, target.ToArray());
        }
        if (!files.TryGetValue("manifest.json", out var manifestBytes) || !files.TryGetValue("settings.json", out var settingsBytes)) throw new InvalidDataException("备份缺少清单或设置。");
        var manifest = JsonSerializer.Deserialize<Manifest>(manifestBytes, Json);
        if (manifest is not { Version: 1, IncludesCredentials: false, IncludesVirtualMachines: false }) throw new InvalidDataException("不支持该备份格式或包含受保护数据。");
        _ = ParseSettings(settingsBytes);
        if (files.TryGetValue("assistant/assistant.db", out var assistant))
            await ValidateAssistantSnapshotAsync(assistant, token).ConfigureAwait(false);
        if (files.TryGetValue("notes.db", out var notes))
        {
            var temporary = Path.GetTempFileName();
            try
            {
                await File.WriteAllBytesAsync(temporary, notes, token).ConfigureAwait(false);
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temporary, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                await connection.OpenAsync(token).ConfigureAwait(false);
                using var command = connection.CreateCommand(); command.CommandText = "PRAGMA quick_check;";
                if (!string.Equals(await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string, "ok", StringComparison.Ordinal)) throw new InvalidDataException("笔记数据库校验失败。");
                command.CommandText = "PRAGMA table_info(notes);";
                using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                var columns = new HashSet<string>(StringComparer.Ordinal);
                while (await reader.ReadAsync(token).ConfigureAwait(false)) columns.Add(reader.GetString(1));
                if (!columns.SetEquals(["id", "title", "body", "revision", "created_utc", "updated_utc", "deleted_utc"]))
                    throw new InvalidDataException("笔记数据库结构不兼容。");
            }
            finally { File.Delete(temporary); }
        }
        return files;
    }

    private static async Task ValidateAssistantSnapshotAsync(byte[] bytes, CancellationToken token)
    {
        var temporary = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, token).ConfigureAwait(false);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temporary, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await connection.OpenAsync(token).ConfigureAwait(false);
            using var command = connection.CreateCommand(); command.CommandText = "PRAGMA quick_check;";
            if (!string.Equals(await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string, "ok", StringComparison.Ordinal)) throw new InvalidDataException("助手数据库校验失败。");
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name IN ('sessions','messages','memories','memory_revisions','memory_suppressions','assets');";
            using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false); var count = 0;
            while (await reader.ReadAsync(token).ConfigureAwait(false)) count++;
            if (count != 6) throw new InvalidDataException("助手数据库结构不兼容。");
        }
        finally { File.Delete(temporary); }
    }

    private static async Task WriteTextAsync(ZipArchive zip, string name, string text, CancellationToken token)
    {
        using var stream = zip.CreateEntry(name).Open(); using var writer = new StreamWriter(stream);
        await writer.WriteAsync(text.AsMemory(), token).ConfigureAwait(false);
    }

    private static AppSettings ParseSettings(byte[] bytes)
    {
        try { var parsed = SettingsMigrator.Deserialize(System.Text.Encoding.UTF8.GetString(bytes), Json); if (FloatDesk.Core.Assistant.AssistantSettingsValidator.Validate(parsed.Assistant).Count != 0) throw new InvalidDataException("备份助手设置无效。"); return parsed; }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or NullReferenceException or FormatException or OverflowException)
        { throw new InvalidDataException("备份设置缺失、损坏或格式不兼容。"); }
    }

    private static async Task ReplaceBytesAsync(string path, byte[] bytes, CancellationToken token)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string SafePath(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("数据路径越界。");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("数据文件不能是链接。");
        for (var current = Path.GetDirectoryName(path); current is not null && current.Length >= fullRoot.Length; current = Path.GetDirectoryName(current))
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("数据目录不能是链接。");
        return path;
    }
}
