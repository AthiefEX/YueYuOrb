namespace FloatDesk.Core.Models;

public sealed record AutomationClientTrust(string Id, string Name, string ExecutablePath, string Sha256, bool Enabled, DateTimeOffset LastConnectedUtc);
public sealed record AutomationCaller(uint ProcessId, uint SessionId, DateTimeOffset StartedAtUtc);

public static class GlobalTaskTarget
{
    public static Guid Id { get; } = new("e5b0f46b-4583-4ef3-9e66-5d3f887aba71");
    public static EnvironmentProfile Profile { get; } = EnvironmentProfile.CreateDefault() with
    { Id=Id, Name="主桌面（全局任务）", BackendProfileId="global-desktop" };
    public static EnvironmentProfile? Find(DesktopSessionSettings settings, Guid? id) => id==Id ? Profile : settings.Environments.FirstOrDefault(env=>env.Id==id);
    public static IEnumerable<EnvironmentProfile> All(DesktopSessionSettings settings) => settings.Environments.Append(Profile);
}
