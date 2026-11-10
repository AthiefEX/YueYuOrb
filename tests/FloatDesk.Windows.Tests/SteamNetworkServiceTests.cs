using System.Net;
using System.Net.Sockets;
using System.Reflection;
using FloatDesk.Core.Models;
using FloatDesk.Windows.Network;

namespace FloatDesk.Windows.Tests;

public sealed class SteamNetworkServiceTests
{
    [Fact]
    public void PacOuterRequestBudgetAllowsThirtySecondUpstreamBudgetToFinish()
    {
        Assert.Equal(TimeSpan.FromSeconds(35), SteamNetworkService.OuterRequestTimeout);
    }

    [Fact]
    public async Task ParentCancellationDisposesCandidateSocketBeforePropagating()
    {
        var method = typeof(SteamNetworkService).GetMethod("TryConnectCandidateAsync", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.True(method is not null, "The TCP attempt must expose its ownership boundary for a controlled cancellation regression.");

        using var parent = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var enteredConnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new TcpClient();
        var socketHandle = client.Client.SafeHandle;
        Func<AddressFamily, TcpClient> createClient = _ => client;
        Func<TcpClient, IPAddress, CancellationToken, Task> connect = async (_, _, token) =>
        {
            enteredConnect.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        Action<SteamNetworkDiagnostic> report = _ => { };
        var candidate = new SteamResolvedAddress(IPAddress.Parse("8.8.8.8"), SteamDnsSource.HttpsDns);
        var pending = (Task<TcpClient?>)method!.Invoke(null,
        [
            "steamcommunity.com",
            candidate,
            createClient,
            connect,
            report,
            TimeSpan.FromSeconds(2),
            parent.Token
        ])!;

        await enteredConnect.Task.WaitAsync(parent.Token);
        parent.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);

        Assert.True(socketHandle.IsClosed);
    }

    [Fact]
    public async Task ServiceStopDisposesCandidateSocketDuringPendingConnect()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var resolver = new SteamNetworkResolver(
            (_, _, _) => Task.FromResult(Array.Empty<IPAddress>()),
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("8.8.8.8") }));
        var connectEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TcpClient? candidateClient = null;
        System.Net.Sockets.SafeSocketHandle? socketHandle = null;
        using var service = new SteamNetworkService(
            resolver,
            family =>
            {
                candidateClient = new TcpClient(family);
                socketHandle = candidateClient.Client.SafeHandle;
                return candidateClient;
            },
            async (_, _, token) =>
            {
                connectEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            });
        service.Start(new SteamNetworkSettings { UseHttpsDns = false });
        using var downstream = new TcpClient();
        await downstream.ConnectAsync(IPAddress.Loopback, service.Port, timeout.Token);
        await downstream.GetStream().WriteAsync("CONNECT steamcommunity.com:443 HTTP/1.1\r\n\r\n"u8.ToArray(), timeout.Token);
        await connectEntered.Task.WaitAsync(timeout.Token);

        service.Stop();
        await WaitUntilAsync(() => socketHandle?.IsClosed == true, timeout.Token);

        Assert.NotNull(candidateClient);
        Assert.True(socketHandle!.IsClosed);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken token)
    {
        while (!predicate()) await Task.Delay(10, token);
    }
}
