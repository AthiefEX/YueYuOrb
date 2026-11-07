namespace FloatDesk.Core.Tests;

public sealed class McpTaskSchemaTests
{
    [Fact]
    public void McpProjectUsesStdioOnlyEntryPointWithoutCredentialTooling()
    {
        var directory = FindRepoRoot();
        var program = File.ReadAllText(Path.Combine(directory, "src", "FloatDesk.McpServer", "Program.cs"));
        Assert.Contains("Console.In.ReadLineAsync", program, StringComparison.Ordinal);
        Assert.DoesNotContain("credential", program, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Process.Start", program, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "FloatDesk.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException();
    }
}
