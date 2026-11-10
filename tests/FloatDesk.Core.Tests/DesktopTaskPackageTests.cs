using FloatDesk.Core.Models;
using DesktopAutomation = FloatDesk.Core.Models.DesktopAutomation;

namespace FloatDesk.Core.Tests;

public sealed class DesktopTaskPackageTests
{
    [Fact]
    public void GlobalTaskDoesNotRequireCreatingOrEnablingAChildEnvironment()
    {
        var task=Sample() with {EnvironmentId=GlobalTaskTarget.Id};
        DesktopSessionSettingsValidator.Validate(new(false,[],[task]));
    }
    [Theory]
    [InlineData("local-notepad.json", DesktopSessionBackendKind.LocalChild)]
    [InlineData("guest-user-script.json", DesktopSessionBackendKind.HyperV)]
    [InlineData("guest-admin-script.json", DesktopSessionBackendKind.HyperV)]
    [InlineData("global-brightness.json", DesktopSessionBackendKind.LocalChild)]
    public void ShippedExamplesContainValidDefinitionsAndVerifiedAssets(string name, DesktopSessionBackendKind backend)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "FloatDesk.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var task = DesktopTaskPackagePolicy.Parse(File.ReadAllText(Path.Combine(root.FullName, "docs", "examples", name)));
        DesktopTaskPackagePolicy.Validate(task, backend);
        Assert.False(task.Enabled);
        Assert.False(DesktopTaskPackagePolicy.IsApproved(task));
        if (backend == DesktopSessionBackendKind.LocalChild&&task.EnvironmentId!=GlobalTaskTarget.Id)
            Assert.True(SessionStartupCommandCodec.TryDecode(task.Steps[0].Parameters, out _));
    }
    [Fact]
    public void NullCollectionEntriesAreRejectedCleanly()
    {
        Assert.Throws<InvalidOperationException>(() => DesktopTaskPackagePolicy.Validate(Sample() with { Steps = [null!] }, DesktopSessionBackendKind.LocalChild));
        Assert.Throws<InvalidOperationException>(() => DesktopTaskPackagePolicy.Validate(Sample() with { Scripts = [null!] }, DesktopSessionBackendKind.HyperV));
    }

    [Fact]
    public void LegacyImportMapsOnlyExplicitlySupportedOperationsAndNeverExecutes()
    {
        var legacy = DesktopAutomation.TaskDocumentV2.Create(Guid.NewGuid(), [DesktopAutomation.GuestOperation.StartApplication("C:\\Windows\\notepad.exe")]);
        var json = System.Text.Json.JsonSerializer.Serialize(legacy, DesktopTaskPackagePolicy.JsonOptions);
        var task = DesktopTaskPackagePolicy.Parse(json);
        Assert.Equal(SessionCommandKind.LaunchApplication, Assert.Single(task.Steps).Kind);
        Assert.True(SessionStartupCommandCodec.TryDecode(task.Steps[0].Parameters, out var entry));
        Assert.Equal("C:\\Windows\\notepad.exe", entry!.ExecutablePath);
        Assert.False(task.Enabled);
        Assert.Null(task.McpApprovalHash);
        legacy = legacy with { Operations = [DesktopAutomation.GuestOperation.SetEnvironmentVariable("A", "B")] };
        Assert.Throws<InvalidOperationException>(() => DesktopTaskPackagePolicy.Parse(System.Text.Json.JsonSerializer.Serialize(legacy, DesktopTaskPackagePolicy.JsonOptions)));
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("null")]
    [InlineData("[null]")]
    public void ScriptArgumentsMustBeAStringArray(string arguments)
    {
        var task = Sample() with { Steps = [new(Guid.NewGuid(), SessionCommandKind.RunScript, new Dictionary<string,string> { ["arguments"] = arguments }, 30, 0)] };
        Assert.Throws<InvalidOperationException>(() => DesktopTaskPackagePolicy.Validate(task, DesktopSessionBackendKind.LocalChild));
    }
    [Fact]
    public void ExternalImportCannotKeepExecutionOrApproval()
    {
        var task = Sample() with { Enabled = true, McpApprovalHash = "forged", Rules = "saved agent rules" };
        var draft = DesktopTaskPackagePolicy.AsExternalDraft(task, "mcp");
        Assert.False(draft.Enabled); Assert.Null(draft.McpApprovalHash);
        Assert.Equal(TaskTriggerKind.Manual, draft.Trigger.Kind); Assert.Equal(task.Rules, draft.Rules);
    }

    [Fact]
    public void ScriptOrEnvironmentChangeInvalidatesApproval()
    {
        var task = Sample() with { Scripts = [DesktopTaskScript.Create("check.ps1", "Get-Date | Out-Null")] };
        task = task with { McpApprovalHash = DesktopTaskPackagePolicy.DefinitionHash(task) };
        Assert.True(DesktopTaskPackagePolicy.IsApproved(task));
        Assert.False(DesktopTaskPackagePolicy.IsApproved(task with { EnvironmentId = Guid.NewGuid() }));
        Assert.False(DesktopTaskPackagePolicy.IsApproved(task with { Scripts = [DesktopTaskScript.Create("check.ps1", "Write-Output changed")] }));
    }

    [Fact]
    public void LocalTaskCannotRequestGuestAdministratorExecution()
    {
        var task = Sample() with { Scripts = [DesktopTaskScript.Create("admin.ps1", "Get-Date")],
            Steps = [new(Guid.NewGuid(), SessionCommandKind.RunSystemScript, new Dictionary<string,string> { ["scriptName"] = "admin.ps1" }, 30, 0)] };
        Assert.Throws<InvalidOperationException>(() => DesktopTaskPackagePolicy.Validate(task, DesktopSessionBackendKind.LocalChild));
        DesktopTaskPackagePolicy.Validate(task, DesktopSessionBackendKind.HyperV);
    }

    [Fact]
    public void ModifiedScriptContentCannotKeepOldDigest()
    {
        var script = DesktopTaskScript.Create("check.ps1", "Get-Date");
        var task = Sample() with { Scripts = [script with { Content = "changed" }] };
        Assert.Throws<InvalidOperationException>(() => DesktopTaskPackagePolicy.Validate(task, DesktopSessionBackendKind.HyperV));
    }

    private static DesktopSessionTask Sample() => new(1, Guid.NewGuid(), "task", Guid.NewGuid(), [], new(TaskTriggerKind.Manual,null,null), 1, true);
}
