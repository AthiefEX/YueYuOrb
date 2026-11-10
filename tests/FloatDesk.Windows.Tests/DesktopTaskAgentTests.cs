using FloatDesk.Core.Models;
using FloatDesk.SessionAgent;
using System.Diagnostics;

namespace FloatDesk.Windows.Tests;

public sealed class DesktopTaskAgentTests
{
    [Fact]
    public async Task CloseTerminatesOnlyTheUniqueDisposableApplicationPath()
    {
        var folder = Path.Combine(Path.GetTempPath(), "yueyu-close-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var executable = Path.Combine(folder,"owned-ping.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory,"ping.exe"),executable);
        using var process = Process.Start(new ProcessStartInfo(executable,"-t 127.0.0.1")
        {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true})!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await Task.Delay(100,timeout.Token);
            Assert.False(process.HasExited);
            var result = await SessionDesktopCommands.CloseAsync(Command(SessionCommandKind.CloseApplication,
                new Dictionary<string,string>{["path"]=executable,["force"]="true"}),timeout.Token);
            Assert.True(result.Succeeded,result.ErrorCode);
            await process.WaitForExitAsync(timeout.Token);
            using var data = System.Text.Json.JsonDocument.Parse(result.DataJson!);
            Assert.Equal(1,data.RootElement.GetProperty("closed").GetInt32());
        }
        finally
        {
            if(!process.HasExited){process.Kill();await process.WaitForExitAsync(CancellationToken.None);}
            File.Delete(executable);Directory.Delete(folder);
        }
    }

    [Fact]
    public async Task DocumentedGeneratorProducesImportableTaskWithValidUnicodeHash()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while(root is not null && !File.Exists(Path.Combine(root.FullName,"FloatDesk.sln")))root=root.Parent;
        Assert.NotNull(root);
        var folder=Path.Combine(Path.GetTempPath(),"yueyu-example-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var source=Path.Combine(folder,"demo.ps1");var output=Path.Combine(folder,"task.json");
        await File.WriteAllTextAsync(source,"Write-Output '中文示例'");
        var start=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"WindowsPowerShell","v1.0","powershell.exe"))
        {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var argument in new[]{"-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",Path.Combine(root.FullName,"docs","examples","build-task-example.ps1"),"-ScriptPath",source,"-EnvironmentId",Guid.NewGuid().ToString(),"-OutputPath",output}) start.ArgumentList.Add(argument);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var process=Process.Start(start)!;
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0,process.ExitCode);
            var task=DesktopTaskPackagePolicy.Parse(await File.ReadAllTextAsync(output,timeout.Token));
            DesktopTaskPackagePolicy.Validate(task,DesktopSessionBackendKind.LocalChild);
            Assert.Equal("脚本任务",task.Name);
            Assert.Equal("Write-Output '中文示例'",Assert.Single(task.Scripts).Content);
        }
        finally
        {
            if(!process.HasExited){process.Kill(entireProcessTree:true);await process.WaitForExitAsync(CancellationToken.None);}
            File.Delete(source);if(File.Exists(output))File.Delete(output);Directory.Delete(folder);
        }
    }
    private static SessionCommand Command(SessionCommandKind kind, IReadOnlyDictionary<string,string> parameters, uint? session = null) => new(
        Guid.NewGuid(), new(Guid.NewGuid(), Guid.NewGuid(), session ?? (uint)Process.GetCurrentProcess().SessionId, Guid.NewGuid(), true),
        kind, parameters, DateTimeOffset.UtcNow.AddSeconds(30));

    [Fact]
    public async Task CloseRefusesAnotherSessionBeforeInspectingProcesses()
    {
        var result = await SessionDesktopCommands.CloseAsync(Command(SessionCommandKind.CloseApplication,
            new Dictionary<string,string> { ["path"] = Environment.ProcessPath! }, uint.MaxValue), CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Equal("WrongWindowsSession", result.ErrorCode);
    }

    [Fact]
    public async Task EmbeddedScriptWithWrongDigestNeverRuns()
    {
        using var runner = new JobObjectScriptRunner();
        var result = await runner.RunAsync(Command(SessionCommandKind.RunScript,
            new Dictionary<string,string> { ["scriptContent"] = "throw 'must not run'", ["scriptSha256"] = new string('0',64), ["arguments"] = "[]" }), CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Equal("ScriptAssetInvalid", result.ErrorCode);
    }

    [Fact]
    public async Task VerifiedEmbeddedScriptActuallyExecutesWithArgumentsInOwnedJob()
    {
        var marker = Path.Combine(Path.GetTempPath(), "yueyu-task-" + Guid.NewGuid().ToString("N") + ".txt");
        const string content = "param([string]$path,[string]$value)\n[IO.File]::WriteAllText($path,$value+' 中文脚本')";
        var asset = DesktopTaskScript.Create("probe.ps1", content);
        using var runner = new JobObjectScriptRunner();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var result = await runner.RunAsync(Command(SessionCommandKind.RunScript, new Dictionary<string,string>
            {
                ["scriptContent"] = content, ["scriptSha256"] = asset.Sha256,
                ["arguments"] = System.Text.Json.JsonSerializer.Serialize(new[] { marker, "probe success 空格" })
            }), timeout.Token);
            Assert.True(result.Succeeded, result.ErrorCode);
            Assert.Equal("probe success 空格 中文脚本", await File.ReadAllTextAsync(marker, timeout.Token));
        }
        finally { if (File.Exists(marker)) File.Delete(marker); }
    }
}
