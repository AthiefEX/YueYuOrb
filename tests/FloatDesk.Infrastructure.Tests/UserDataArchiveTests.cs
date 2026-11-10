using System.IO.Compression;
using FloatDesk.Core.Models;
using FloatDesk.Infrastructure.Settings;
using Microsoft.Data.Sqlite;

namespace FloatDesk.Infrastructure.Tests;

public sealed class UserDataArchiveTests
{
    [Fact]
    public async Task AssistantBackupIncludesOnlineDatabaseAndBackgroundButClearsPermissionsAndKeepsCredentials()
    {
        var temp = NewDirectory(); var source = Path.Combine(temp, "source"); var target = Path.Combine(temp, "target");
        Directory.CreateDirectory(source); Directory.CreateDirectory(target);
        try
        {
            using var assistant = new FloatDesk.Infrastructure.Assistant.AssistantStore(Path.Combine(source, "assistant", "assistant.db"));
            var session = await assistant.CreateSessionAsync("retained session", CancellationToken.None);
            var project = await assistant.CreateProjectAsync("retained project", CancellationToken.None); await assistant.SetSessionProjectAsync(session.Id, project.Id, CancellationToken.None);
            var message = new FloatDesk.Core.Assistant.AssistantMessage(Guid.NewGuid(), session.Id, "user", "retained message", DateTimeOffset.UtcNow) { ExtractionFingerprint = new string('f', 64) }; await assistant.AppendMessageAsync(message, CancellationToken.None);
            var document = new FloatDesk.Core.Assistant.AssistantDocument(Guid.NewGuid(), project.Id, "retained.txt", "local", new string('b', 64), true, "deny", DateTimeOffset.UtcNow);
            await assistant.SaveDocumentAsync(document, [new(Guid.NewGuid(), document.Id, 0, "段1", "主题资料保留")], CancellationToken.None);
            await File.WriteAllBytesAsync(Path.Combine(source, "assistant", "background.png"), [2, 4, 6]);
            await File.WriteAllTextAsync(Path.Combine(source, "assistant", "secrets.dat"), "source-secret");
            Directory.CreateDirectory(Path.Combine(target, "assistant"));
            await File.WriteAllTextAsync(Path.Combine(target, "assistant", "secrets.dat"), "target-secret");
            var settings = AppSettings.CreateDefault() with { Assistant = new() { Enabled = true, Endpoint = "https://example.test/v1", Model = "test", AllowComputerControl = true, AllowTaskManagement = true, Library = new() { UseKnowledge = true, ExtractCandidates = true, EmbeddingEnabled = true }, Voice = new() { Enabled = true, AutoSend = true } } };
            var archive = Path.Combine(temp, "backup.zip");
            await UserDataArchive.ExportAsync(source, settings, archive, CancellationToken.None);
            using (var zip = ZipFile.OpenRead(archive)) { Assert.NotNull(zip.GetEntry("assistant/assistant.db")); Assert.Null(zip.GetEntry("assistant/secrets.dat")); }
            await UserDataArchive.QueueImportAsync(target, archive, CancellationToken.None);
            await UserDataArchive.ApplyPendingAsync(target, CancellationToken.None);
            using var restored = new FloatDesk.Infrastructure.Assistant.AssistantStore(Path.Combine(target, "assistant", "assistant.db"));
            Assert.Equal("retained message", Assert.Single(await restored.ReadMessagesAsync(session.Id, CancellationToken.None)).Content);
            Assert.Single(await restored.ListDocumentsAsync(project.Id, CancellationToken.None)); Assert.Single(await restored.RecallAsync("主题", project.Id, false, true, 5000, CancellationToken.None));
            using var settingsStore = new JsonSettingsStore(target); var read = (await settingsStore.LoadAsync(CancellationToken.None)).Assistant;
            Assert.False(read.Enabled); Assert.False(read.AllowComputerControl); Assert.False(read.AllowTaskManagement); Assert.False(read.Voice.Enabled); Assert.False(read.Voice.AutoSend);
            Assert.False(read.Library.ExtractCandidates); Assert.False(read.Library.EmbeddingEnabled);
            Assert.Equal("target-secret", await File.ReadAllTextAsync(Path.Combine(target, "assistant", "secrets.dat")));
            await UserDataArchive.QueueResetAsync(target, CancellationToken.None); await UserDataArchive.ApplyPendingAsync(target, CancellationToken.None);
            Assert.False(File.Exists(Path.Combine(target, "assistant", "assistant.db"))); Assert.False(File.Exists(Path.Combine(target, "assistant", "background.png")));
            Assert.True(File.Exists(Path.Combine(target, "assistant", "secrets.dat")));
        }
        finally { Directory.Delete(temp, true); }
    }

    [Fact]
    public async Task RoundTripRestoresNotesAndPicturesButKeepsTargetVirtualMachineAndCredentials()
    {
        var temp = NewDirectory(); var source = Path.Combine(temp, "source"); var target = Path.Combine(temp, "target");
        Directory.CreateDirectory(source); Directory.CreateDirectory(target);
        try
        {
            var sourceVm = EnvironmentProfile.CreateDefault() with { Id = Guid.NewGuid(), Name = "source-vm-private", Backend = DesktopSessionBackendKind.HyperV };
            var targetVm = EnvironmentProfile.CreateDefault() with { Id = Guid.NewGuid(), Name = "target-vm", Backend = DesktopSessionBackendKind.HyperV };
            using var sourceStore = new JsonSettingsStore(source); using var targetStore = new JsonSettingsStore(target);
            var original = AppSettings.CreateDefault() with { Orb = AppSettings.CreateDefault().Orb with { ShowClock = true }, DesktopSessions = new(true, [sourceVm], []) };
            var current = AppSettings.CreateDefault() with { DesktopSessions = new(true, [targetVm], []), HyperVAutomation = new() { StorageDirectory = "V:\\keep-vms" } };
            await sourceStore.SaveAsync(original, CancellationToken.None); await targetStore.SaveAsync(current, CancellationToken.None);
            Directory.CreateDirectory(Path.Combine(source, "assets")); await File.WriteAllBytesAsync(Path.Combine(source, "assets", "orb.png"), [1, 2, 3]);
            Directory.CreateDirectory(Path.Combine(source, "data"));
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(source, "data", "notes.db"), Pooling = false }.ToString()))
            { await db.OpenAsync(); using var command = db.CreateCommand(); command.CommandText = "CREATE TABLE notes(id TEXT PRIMARY KEY, title TEXT, body TEXT, revision INTEGER, created_utc TEXT, updated_utc TEXT, deleted_utc TEXT); INSERT INTO notes VALUES ('1','note kept','body',1,'2026-11-09','2026-11-09',NULL);"; await command.ExecuteNonQueryAsync(); }
            await File.WriteAllTextAsync(Path.Combine(target, "secrets.bin"), "credential-sentinel");
            Directory.CreateDirectory(Path.Combine(target, "WebView2")); await File.WriteAllTextAsync(Path.Combine(target, "WebView2", "Cookies"), "login-sentinel");
            var backup = Path.Combine(temp, "backup.zip");
            await UserDataArchive.ExportAsync(source, original, backup, CancellationToken.None);
            using (var zip = ZipFile.OpenRead(backup))
            {
                Assert.DoesNotContain(zip.Entries, entry => entry.FullName.Contains("Cookies", StringComparison.Ordinal) || entry.FullName.Contains("secrets", StringComparison.Ordinal));
                using var reader = new StreamReader(zip.GetEntry("settings.json")!.Open());
                Assert.DoesNotContain("source-vm-private", await reader.ReadToEndAsync());
            }
            await UserDataArchive.QueueImportAsync(target, backup, CancellationToken.None);
            Assert.False((await targetStore.LoadAsync(CancellationToken.None)).Orb.ShowClock);
            await UserDataArchive.ApplyPendingAsync(target, CancellationToken.None);
            var restored = await targetStore.LoadAsync(CancellationToken.None);
            Assert.True(restored.Orb.ShowClock);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(targetVm), System.Text.Json.JsonSerializer.Serialize(Assert.Single(restored.DesktopSessions.Environments)));
            Assert.Equal(current.HyperVAutomation, restored.HyperVAutomation);
            Assert.Equal("credential-sentinel", await File.ReadAllTextAsync(Path.Combine(target, "secrets.bin")));
            Assert.Equal("login-sentinel", await File.ReadAllTextAsync(Path.Combine(target, "WebView2", "Cookies")));
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(target, "assets", "orb.png")));
            Assert.True(File.Exists(Path.Combine(target, "data", "notes.db")));
            await UserDataArchive.QueueResetAsync(target, CancellationToken.None);
            await UserDataArchive.ApplyPendingAsync(target, CancellationToken.None);
            var reset = await targetStore.LoadAsync(CancellationToken.None);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(targetVm), System.Text.Json.JsonSerializer.Serialize(Assert.Single(reset.DesktopSessions.Environments)));
            Assert.False(File.Exists(Path.Combine(target, "data", "notes.db")));
            Assert.False(File.Exists(Path.Combine(target, "assets", "orb.png")));
            Assert.True(File.Exists(Path.Combine(target, "secrets.bin")));
            Assert.True(File.Exists(Path.Combine(target, "WebView2", "Cookies")));
        }
        finally { Directory.Delete(temp, true); }
    }

    [Theory]
    [InlineData("../settings.json")]
    [InlineData("assets/../../disk.vhdx")]
    [InlineData("WebView2/Cookies")]
    public async Task UnknownOrTraversingArchiveEntriesAreRejected(string name)
    {
        var temp = NewDirectory();
        try
        {
            var archive = Path.Combine(temp, "bad.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create)) zip.CreateEntry(name);
            await Assert.ThrowsAsync<InvalidDataException>(() => UserDataArchive.QueueImportAsync(temp, archive, CancellationToken.None));
            Assert.False(File.Exists(Path.Combine(temp, "user-maintenance", "pending.json")));
        }
        finally { Directory.Delete(temp, true); }
    }

    [Fact]
    public async Task FailedImportIsNotRepeatedOnEveryStartup()
    {
        var temp = NewDirectory();
        try
        {
            var archive = Path.Combine(temp, "backup.zip");
            await UserDataArchive.ExportAsync(temp, AppSettings.CreateDefault(), archive, CancellationToken.None);
            await UserDataArchive.QueueImportAsync(temp, archive, CancellationToken.None);
            await File.WriteAllBytesAsync(Path.Combine(temp, "user-maintenance", "import.zip"), [1, 2, 3]);
            await Assert.ThrowsAsync<InvalidDataException>(() => UserDataArchive.ApplyPendingAsync(temp, CancellationToken.None));
            Assert.False(File.Exists(Path.Combine(temp, "user-maintenance", "pending.json")));
            Assert.True(File.Exists(Path.Combine(temp, "user-maintenance", "failed.json")));
            await UserDataArchive.ApplyPendingAsync(temp, CancellationToken.None);
        }
        finally { Directory.Delete(temp, true); }
    }

    [Fact]
    public async Task DuplicateArchiveEntryIsRejectedBeforeAnyDataChange()
    {
        var temp = NewDirectory();
        try
        {
            var archive = Path.Combine(temp, "duplicate.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create)) { zip.CreateEntry("settings.json"); zip.CreateEntry("settings.json"); }
            await Assert.ThrowsAsync<InvalidDataException>(() => UserDataArchive.QueueImportAsync(temp, archive, CancellationToken.None));
            Assert.False(File.Exists(Path.Combine(temp, "user-maintenance", "pending.json")));
        }
        finally { Directory.Delete(temp, true); }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"schemaVersion\":1,\"hotkeys\":null}")]
    [InlineData("{\"schemaVersion\":1,\"assistant\":{\"library\":null}}")]
    public async Task MalformedSettingsBecomeRecoverableImportErrors(string json)
    {
        var temp = NewDirectory();
        try
        {
            var archive = Path.Combine(temp, "malformed.zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open())) await writer.WriteAsync("{\"version\":1,\"includesCredentials\":false,\"includesVirtualMachines\":false}");
                using (var writer = new StreamWriter(zip.CreateEntry("settings.json").Open())) await writer.WriteAsync(json);
            }
            await Assert.ThrowsAsync<InvalidDataException>(() => UserDataArchive.QueueImportAsync(temp, archive, CancellationToken.None));
        }
        finally { Directory.Delete(temp, true); }
    }

    [Fact]
    public async Task NotesWithWrongSchemaAreRejectedBeforeImport()
    {
        var temp = NewDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(temp, "data"));
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(temp, "data", "notes.db"), Pooling = false }.ToString()))
            { await db.OpenAsync(); using var command = db.CreateCommand(); command.CommandText = "CREATE TABLE notes(wrong TEXT)"; await command.ExecuteNonQueryAsync(); }
            var archive = Path.Combine(temp, "bad-notes.zip");
            await UserDataArchive.ExportAsync(temp, AppSettings.CreateDefault(), archive, CancellationToken.None);
            await Assert.ThrowsAsync<InvalidDataException>(() => UserDataArchive.QueueImportAsync(temp, archive, CancellationToken.None));
        }
        finally { Directory.Delete(temp, true); }
    }

    private static string NewDirectory()
    { var path = Path.Combine(Path.GetTempPath(), "YueYu-Data-Test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
}
