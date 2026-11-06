using FloatDesk.Windows.DesktopSessions;
using System.Security.Cryptography;

namespace FloatDesk.Windows.Tests;

public sealed class SessionBrokerProtocolTests
{
    [Fact]
    public async Task ClientReceivesBoundBootstrapAndNeverUsesCommandLineSecret()
    {
        var pipeName = "YueYuOrb.SessionBroker.Tests." +
            Guid.NewGuid().ToString("N");
        var environmentId = Guid.NewGuid();
        var generation = Guid.NewGuid();
        var loginInstanceId = Guid.NewGuid();
        var processStarted = DateTimeOffset.UtcNow.AddMinutes(-1);
        var secret = RandomNumberGenerator.GetBytes(32);
        var expectedSecret = secret.ToArray();
        var launcher = new RecordingLauncher(new SessionBrokerResponse(
            SessionBrokerProtocol.CurrentVersion,
            true,
            loginInstanceId,
            "S-1-5-21-1",
            1234,
            processStarted,
            secret,
            null));
        var server = new SessionBrokerServer(launcher, pipeName);
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromSeconds(10));
        var serverTask = server.RunAsync(cancellation.Token);
        try
        {
            var client = new SessionBrokerClient(pipeName);
            var bootstrap = await client.LaunchAsync(
                environmentId,
                generation,
                7,
                cancellation.Token);

            Assert.Equal(loginInstanceId, bootstrap.LoginInstanceId);
            Assert.Equal("S-1-5-21-1", bootstrap.UserSid);
            Assert.Equal(1234u, bootstrap.ProcessId);
            Assert.Equal(expectedSecret, bootstrap.BootstrapSecret);
            Assert.NotNull(launcher.LastRequest);
            Assert.Equal(environmentId, launcher.LastRequest.EnvironmentId);
            Assert.Equal(generation, launcher.LastRequest.Generation);
            Assert.Equal(7u, launcher.LastRequest.WindowsSessionId);
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await serverTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [Fact]
    public async Task AgentManifestMismatchIsRejectedBeforePrivilegedLaunch()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "YueYuOrb.Broker.Tests." + Guid.NewGuid().ToString("N"));
        var brokerDirectory = Path.Combine(root, "broker");
        var agentDirectory = Path.Combine(root, "agent");
        Directory.CreateDirectory(brokerDirectory);
        Directory.CreateDirectory(agentDirectory);
        var agentPath = Path.Combine(
            agentDirectory,
            "YueYuOrb.SessionAgent.exe");
        try
        {
            await File.WriteAllTextAsync(agentPath, "test-agent");
            await File.WriteAllTextAsync(
                agentPath + ".sha256",
                new string('0', 64));
            var launcher = new WindowsChildSessionAgentLauncher(
                brokerDirectory,
                agentPath);
            var request = new SessionBrokerRequest(
                SessionBrokerProtocol.CurrentVersion,
                Guid.NewGuid(),
                Guid.NewGuid(),
                2);
            var caller = new SessionPeerIdentity(
                100,
                1,
                "S-1-5-21-1");

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => launcher.LaunchAsync(
                    request,
                    caller,
                    CancellationToken.None));

            Assert.Equal(
                "SessionAgentManifestMismatch",
                exception.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RecordingLauncher(
        SessionBrokerResponse response) : ISessionBrokerAgentLauncher
    {
        public SessionBrokerRequest? LastRequest { get; private set; }

        public Task<SessionBrokerResponse> LaunchAsync(
            SessionBrokerRequest request,
            SessionPeerIdentity caller,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(response);
        }
    }
}
