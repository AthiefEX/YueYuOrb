using FloatDesk.Core.Models;
using FloatDesk.SessionAgent;

namespace FloatDesk.Windows.Tests;

public sealed class StartupApplicationRunnerTests
{
    [Fact]
    public async Task MissingPathFailsClearly()
    {
        var command = CreateCommand(
            CreateEntry(@"C:\missing-startup.exe"));

        var result = await StartupApplicationRunner.RunAsync(
            command,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("StartupPathMissing", result.ErrorCode);
    }

    [Fact]
    public async Task RelativePathRejected()
    {
        var result = await StartupApplicationRunner.RunAsync(
            CreateCommand(CreateEntry(@"relative\app.exe")),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("StartupPathInvalid", result.ErrorCode);
    }

    [Fact]
    public async Task ScriptExtensionsRejected()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"startup-{Guid.NewGuid():N}.cmd");
        await File.WriteAllTextAsync(path, "whoami");
        try
        {
            var result = await StartupApplicationRunner.RunAsync(
                CreateCommand(CreateEntry(path)),
                CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal(
                "StartupExecutableTypeUnsupported",
                result.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ShortcutOverridesRejected()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"startup-{Guid.NewGuid():N}.lnk");
        await File.WriteAllBytesAsync(path, []);
        try
        {
            var result = await StartupApplicationRunner.RunAsync(
                CreateCommand(CreateEntry(path)),
                CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal(
                "ShortcutOverridesNotSupported",
                result.ErrorCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static SessionStartupEntry CreateEntry(string path) => new(
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        "测试应用",
        path,
        ["--unexpected"],
        WorkingDirectory: null,
        Order: 0,
        DelaySeconds: 0,
        Enabled: true,
        WaitForExit: false,
        Revision: 1);

    private static SessionCommand CreateCommand(
        SessionStartupEntry entry) =>
        new(
            Guid.NewGuid(),
            new SessionHandle(
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                WindowsSessionId: 2,
                Guid.Parse("33333333-3333-3333-3333-333333333333"),
                PreparationCompleted: true),
            SessionCommandKind.LaunchApplication,
            SessionStartupCommandCodec.ToParameters(entry),
            DateTimeOffset.UtcNow.AddMinutes(1));
}
