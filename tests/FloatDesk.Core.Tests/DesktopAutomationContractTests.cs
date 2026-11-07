using FloatDesk.Core.Models.DesktopAutomation;

namespace FloatDesk.Core.Tests;

public sealed class DesktopAutomationContractTests
{
    [Fact]
    public void ResourceProfileRejectsImpossibleHostReservation()
    {
        var profile = new VmResourceProfile(
            Guid.NewGuid(), 4, 4096, 4096, 4096, 80, "D:\\VMs", "Default Switch");

        var result = VmResourceProfileValidator.Validate(
            profile,
            new HostResourceCapacity(4, 4096, 100));

        Assert.False(result.IsValid);
        Assert.Contains("memory", result.Errors.Single(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TaskDocumentRequiresKnownOperationsAndScopedPaths()
    {
        var task = TaskDocumentV2.Create(
            Guid.NewGuid(),
            [GuestOperation.StartApplication("C:\\Tools\\demo.exe")]) with
        {
            AllowedPaths = ["C:\\Approved\\"]
        };

        var result = TaskDocumentV2Validator.Validate(task);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "PathOutsideGrant");
    }

    [Fact]
    public void TaskDocumentRoundTripDoesNotContainSecrets()
    {
        var task = TaskDocumentV2.Create(
            Guid.NewGuid(),
            [GuestOperation.SetEnvironmentVariable("DEMO", "value")]) with
        {
            CredentialRef = "cred:guest-admin"
        };

        var json = System.Text.Json.JsonSerializer.Serialize(task);

        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cred:guest-admin", json, StringComparison.Ordinal);
    }
}
