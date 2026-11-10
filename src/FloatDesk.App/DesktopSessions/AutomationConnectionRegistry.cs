using FloatDesk.Core.Contracts;
using FloatDesk.Core.Models;
using FloatDesk.Windows.DesktopSessions;

namespace FloatDesk.App.DesktopSessions;

public sealed class AutomationConnectionRegistry(ISettingsCoordinator settings,
    Func<AutomationCaller,string,AutomationClientTrust>? identityResolver=null)
{
    private readonly object _sync=new();
    private readonly Dictionary<Guid,(AutomationCaller Caller,string ClientId)> _connections=[];
    private readonly HashSet<string> _prompted=[];
    public event Action<AutomationClientTrust>? ConfirmationRequested;

    public async Task<DesktopSessionControlResponse> RegisterAsync(DesktopSessionControlRequest request,CancellationToken token)
    {
        var caller=request.VerifiedCaller??throw new InvalidOperationException("VerifiedCallerRequired");
        var name=request.ClientName?.Trim();
        if(string.IsNullOrEmpty(name)||name.Length>100||name.Any(char.IsControl))throw new InvalidOperationException("ClientNameInvalid");
        var identity=(identityResolver??AutomationClientIdentity.Resolve)(caller,name);
        Guid id;
        lock(_sync)
        {
            id=_connections.FirstOrDefault(item=>item.Value.Caller==caller&&item.Value.ClientId==identity.Id).Key;
            if(id==Guid.Empty)
            {
                if(_connections.Count>=128)throw new InvalidOperationException("ConnectionLimitReached");
                id=Guid.NewGuid();_connections.Add(id,(caller,identity.Id));
            }
        }
        var result=await settings.UpdateAsync(current=>current with {DesktopSessions=current.DesktopSessions with
        {AutomationClients=current.DesktopSessions.AutomationClients.Where(client=>client.Id!=identity.Id).Append(identity with
        {Enabled=current.DesktopSessions.AutomationClients.Any(client=>client.Id==identity.Id&&client.Enabled)}).TakeLast(64).ToArray()}},token).ConfigureAwait(false);
        if(!result.Success) return DesktopSessionControlResponse.Fail("ConnectionSaveFailed","连接记录未保存。");
        var trusted=settings.Current.DesktopSessions.AutomationClients.Any(client=>client.Id==identity.Id&&client.Enabled);
        var prompt=false;lock(_sync)if(!trusted)prompt=_prompted.Add(identity.Id);
        if(prompt)ConfirmationRequested?.Invoke(identity);
        return new(true,null,null,null,null,null,null,null,null){ClientConnectionId=id,ConnectionState=trusted?"Trusted":"Pending"};
    }
    public AutomationClientTrust? AuthorizedClient(DesktopSessionControlRequest request)
    {
        if(request.ClientConnectionId is not {} id || request.VerifiedCaller is not {} caller)return null;
        string clientId;
        lock(_sync)
        {
            if(!_connections.TryGetValue(id,out var entry)||entry.Caller!=caller)return null;
            clientId=entry.ClientId;
        }
        return settings.Current.DesktopSessions.AutomationClients.FirstOrDefault(client=>client.Id==clientId&&client.Enabled);
    }
    public DesktopSessionControlResponse Status(DesktopSessionControlRequest request)
    {
        lock(_sync)
        {
            if(request.ClientConnectionId is not {} id||!_connections.TryGetValue(id,out var entry)||request.VerifiedCaller!=entry.Caller)
                return DesktopSessionControlResponse.Fail("ConnectionNotFound","请重新连接 MCP。");
        }
        return new(true,null,null,null,null,null,null,null,null){ClientConnectionId=request.ClientConnectionId,
            ConnectionState=AuthorizedClient(request) is null?"Pending":"Trusted"};
    }
    public Task<OperationResult> SetEnabledAsync(string id,bool enabled,CancellationToken token)=>settings.UpdateAsync(current=>current with
    {DesktopSessions=current.DesktopSessions with {AutomationClients=current.DesktopSessions.AutomationClients.Select(client=>client.Id==id?client with {Enabled=enabled}:client).ToArray()}},token);
}
