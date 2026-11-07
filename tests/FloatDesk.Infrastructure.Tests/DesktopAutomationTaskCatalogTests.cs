using FloatDesk.Core.Models.DesktopAutomation;
using FloatDesk.Infrastructure.DesktopSessions;

namespace FloatDesk.Infrastructure.Tests;

public sealed class DesktopAutomationTaskCatalogTests
{
    [Fact]
    public async Task UpdateRequiresExpectedRevision()
    {
        var directory = Path.Combine(Path.GetTempPath(), "yueyu-task-catalog-" + Guid.NewGuid().ToString("N"));
        try
        {
            var catalog = new DesktopAutomationTaskCatalog(directory);
            var task = TaskDocumentV2.Create(Guid.NewGuid(), [GuestOperation.SetEnvironmentVariable("A", "B")]);
            await catalog.CreateAsync(task, default);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.UpdateAsync(task.Id, 2, task, default));
            Assert.Equal("TaskRevisionConflict", error.Message);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
