using Harbor.Ipc.Client;
using Harbor.Ipc.Server;
using Harbor.Ipc.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.Ipc.Tests;

/// <summary>
///     Server transport security tests (sprint 6 A2):
///     a second listener must not steal a live endpoint, a stale socket
///     file must be reclaimed, the socket file must be owner-only (0600),
///     and repeated accept failures must back off instead of spinning hot.
/// </summary>
[NotInParallel]
public class ServerTransportSecurityTests
{
    [Test]
    public async Task Bind_WhenAnotherListenerAlive_RefusesInsteadOfStealing()
    {
        if (OperatingSystem.IsWindows())
            return;
        var sp = TestHost.Build();
        string pipe = TestHost.UniquePipeName("harbor-ipc-test-steal");
        await using var server = new HarborIpcServer(sp, pipe, sp.GetService<ILoggerFactory>());
        await server.StartAsync();
        try
        {
            var thief = new ServerPipeTransport(
                pipe,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<ServerPipeTransport>());

            await Assert.That(thief.IsBound).IsFalse();

            Exception? failure = null;
            try
            {
                await thief.BindAsync();
                await thief.UnbindAsync();
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            await Assert.That(failure).IsNotNull();
            await Assert.That(failure!.Message).Contains("another listener");

            // The original server must still serve clients — its endpoint
            // was never re-bound out from under it.
            await using var client = new IpcHarborClient(pipe,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<IpcHarborClient>());
            await client.ConnectAsync();
            var result = await client.ListProvidersAsync();
            await Assert.That(result.IsSuccess).IsTrue();
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Test]
    public async Task Bind_WithStaleSocketFile_ReclaimsIt()
    {
        var sp = TestHost.Build();
        string pipe = TestHost.UniquePipeName("harbor-ipc-test-stale");
        string socketPath = Path.Combine(Path.GetTempPath(), pipe + ".sock");

        // Plant a leftover file that nothing serves (a regular file is as
        // dead as an orphaned socket for probe purposes).
        File.WriteAllText(socketPath, "leftover from a crashed run");
        await Assert.That(File.Exists(socketPath)).IsTrue();

        await using var server = new HarborIpcServer(sp, pipe, sp.GetService<ILoggerFactory>());
        await server.StartAsync();
        try
        {
            // The stale file was replaced by a live socket bound by the server.
            await Assert.That(File.Exists(socketPath)).IsTrue();

            await using var client = new IpcHarborClient(pipe,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<IpcHarborClient>());
            await client.ConnectAsync();
            var result = await client.ListProvidersAsync();
            await Assert.That(result.IsSuccess).IsTrue();
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Test]
    public async Task Bind_SetsOwnerOnlyPermissions_OnSocketFile()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Unix file modes don't apply to Windows named pipes.
        }

        var sp = TestHost.Build();
        string pipe = TestHost.UniquePipeName("harbor-ipc-test-mode");
        await using var server = new HarborIpcServer(sp, pipe, sp.GetService<ILoggerFactory>());
        await server.StartAsync();
        try
        {
            string socketPath = Path.Combine(Path.GetTempPath(), pipe + ".sock");
            await Assert.That(File.Exists(socketPath)).IsTrue();

            var mode = File.GetUnixFileMode(socketPath);
            await Assert.That(mode.HasFlag(UnixFileMode.UserRead)).IsTrue();
            await Assert.That(mode.HasFlag(UnixFileMode.UserWrite)).IsTrue();
            await Assert.That(mode.HasFlag(UnixFileMode.GroupRead)).IsFalse();
            await Assert.That(mode.HasFlag(UnixFileMode.GroupWrite)).IsFalse();
            await Assert.That(mode.HasFlag(UnixFileMode.OtherRead)).IsFalse();
            await Assert.That(mode.HasFlag(UnixFileMode.OtherWrite)).IsFalse();
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Test]
    public async Task ComputeAcceptBackoff_DoublesAndCaps()
    {
        await Assert.That(ServerPipeTransport.ComputeAcceptBackoff(0)).IsEqualTo(TimeSpan.FromMilliseconds(100));
        await Assert.That(ServerPipeTransport.ComputeAcceptBackoff(1)).IsEqualTo(TimeSpan.FromMilliseconds(100));
        await Assert.That(ServerPipeTransport.ComputeAcceptBackoff(2)).IsEqualTo(TimeSpan.FromMilliseconds(200));
        await Assert.That(ServerPipeTransport.ComputeAcceptBackoff(3)).IsEqualTo(TimeSpan.FromMilliseconds(400));
        await Assert.That(ServerPipeTransport.ComputeAcceptBackoff(5)).IsEqualTo(TimeSpan.FromMilliseconds(1600));
        await Assert.That(ServerPipeTransport.ComputeAcceptBackoff(7)).IsEqualTo(TimeSpan.FromMilliseconds(5000));
        await Assert.That(ServerPipeTransport.ComputeAcceptBackoff(100)).IsEqualTo(TimeSpan.FromMilliseconds(5000));
    }

    [Test]
    public async Task Stop_RemovesSocketFile()
    {
        if (OperatingSystem.IsWindows())
            return;
        var sp = TestHost.Build();
        string pipe = TestHost.UniquePipeName("harbor-ipc-test-cleanup");
        string socketPath = Path.Combine(Path.GetTempPath(), pipe + ".sock");
        await using var server = new HarborIpcServer(sp, pipe, sp.GetService<ILoggerFactory>());
        await server.StartAsync();
        await Assert.That(File.Exists(socketPath)).IsTrue();
        await server.StopAsync();
        await Assert.That(File.Exists(socketPath)).IsFalse();
    }
}
