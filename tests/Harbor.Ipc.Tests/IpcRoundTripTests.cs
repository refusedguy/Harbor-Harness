using Harbor.Abstractions.Agents;
using Harbor.Ipc.Client;
using Harbor.Ipc.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.Ipc.Tests;

/// <summary>
///     End-to-end IPC round-trip tests. Each test starts a real
///     <see cref="HarborIpcServer" /> on a unique pipe name, connects a
///     real <see cref="IpcHarborClient" />, calls methods, and asserts
///     the response. These tests exercise the full
///     <c>MessagePack → frame → pipe → frame → MessagePack → dispatch →
///     service → MessagePack → frame → pipe → frame → MessagePack</c>
///     pipeline.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this matters:</b> even with perfect unit tests for each
///         layer, the full pipeline can break in subtle ways (wrong key
///         ordering on a derived request type, missing <c>[Union]</c> tag,
///         per-stream write-lock deadlock, etc.). These integration tests
///         catch all of those in one shot.
///     </para>
///     <para>
///         <b>Test isolation:</b> each test uses a unique pipe name via
///         <see cref="TestHost.UniquePipeName" /> so they can run in
///         parallel without colliding on the same socket file.
///     </para>
/// </remarks>
[NotInParallel]
    public class IpcRoundTripTests
{
    /// <summary>
    ///     CreateSession should round-trip through the server and return
    ///     a Session with a non-empty id.
    /// </summary>
    [Test]
    public async Task Ipc_CreateSession_RoundTrips()
    {
        var sp = TestHost.Build();
        string pipe = TestHost.UniquePipeName("harbor-ipc-test-create");
        await using var server = new HarborIpcServer(sp, pipe, sp.GetService<ILoggerFactory>());
        await server.StartAsync();
        try
        {
            await using var client = new IpcHarborClient(pipe,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<IpcHarborClient>());
            await client.ConnectAsync();

            var result = await client.CreateSessionAsync("/tmp/test", "code", "ollama", "qwen2.5-coder:7b");
            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value.Id.Length).IsGreaterThan(0);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    /// <summary>
    ///     ListSessions → GetSession round-trip.
    /// </summary>
    [Test]
    public async Task Ipc_ListSessions_Returns_Created_Session()
    {
        var sp = TestHost.Build();
        string pipe = TestHost.UniquePipeName("harbor-ipc-test-list");
        await using var server = new HarborIpcServer(sp, pipe, sp.GetService<ILoggerFactory>());
        await server.StartAsync();
        try
        {
            await using var client = new IpcHarborClient(pipe,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<IpcHarborClient>());
            await client.ConnectAsync();

            var create = await client.CreateSessionAsync("/tmp/test", "code", "ollama", "qwen2.5-coder:7b");
            var list = await client.ListSessionsAsync();
            await Assert.That(list.IsSuccess).IsTrue();
            await Assert.That(list.Value.Any(s => s.Id == create.Value.Id)).IsTrue();
        }
        finally
        {
            await server.StopAsync();
        }
    }

    /// <summary>
    ///     ListProviders returns the empty list (no providers registered
    ///     in the test host). Verifies the OkResponse.Payload path with a
    ///     typed collection.
    /// </summary>
    [Test]
    public async Task Ipc_ListProviders_Returns_List()
    {
        var sp = TestHost.Build();
        string pipe = TestHost.UniquePipeName("harbor-ipc-test-providers");
        await using var server = new HarborIpcServer(sp, pipe, sp.GetService<ILoggerFactory>());
        await server.StartAsync();
        try
        {
            await using var client = new IpcHarborClient(pipe,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<IpcHarborClient>());
            await client.ConnectAsync();

            var result = await client.ListProvidersAsync();
            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value).IsNotNull();
        }
        finally
        {
            await server.StopAsync();
        }
    }

    /// <summary>
    ///     ListTools returns the empty list (no tools registered in the
    ///     test host).
    /// </summary>
    [Test]
    public async Task Ipc_ListTools_Returns_List()
    {
        var sp = TestHost.Build();
        string pipe = TestHost.UniquePipeName("harbor-ipc-test-tools");
        await using var server = new HarborIpcServer(sp, pipe, sp.GetService<ILoggerFactory>());
        await server.StartAsync();
        try
        {
            await using var client = new IpcHarborClient(pipe,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<IpcHarborClient>());
            await client.ConnectAsync();

            var result = await client.ListToolsAsync();
            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Value).IsNotNull();
        }
        finally
        {
            await server.StopAsync();
        }
    }

    /// <summary>
    ///     DeleteSession should succeed for a freshly-created session.
    /// </summary>
    [Test]
    public async Task Ipc_DeleteSession_Succeeds()
    {
        var sp = TestHost.Build();
        string pipe = TestHost.UniquePipeName("harbor-ipc-test-delete");
        await using var server = new HarborIpcServer(sp, pipe, sp.GetService<ILoggerFactory>());
        await server.StartAsync();
        try
        {
            await using var client = new IpcHarborClient(pipe,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<IpcHarborClient>());
            await client.ConnectAsync();

            var create = await client.CreateSessionAsync("/tmp/test", "code", "ollama", "qwen2.5-coder:7b");
            var delete = await client.DeleteSessionAsync(create.Value.Id);
            await Assert.That(delete.IsSuccess).IsTrue();
        }
        finally
        {
            await server.StopAsync();
        }
    }

    /// <summary>
    ///     GetSession on a non-existent id should return failure (not throw).
    /// </summary>
    [Test]
    public async Task Ipc_GetSession_Unknown_Id_Returns_Failure()
    {
        var sp = TestHost.Build();
        string pipe = TestHost.UniquePipeName("harbor-ipc-test-unknown");
        await using var server = new HarborIpcServer(sp, pipe, sp.GetService<ILoggerFactory>());
        await server.StartAsync();
        try
        {
            await using var client = new IpcHarborClient(pipe,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<IpcHarborClient>());
            await client.ConnectAsync();

            var result = await client.GetSessionAsync("nonexistent-id");
            await Assert.That(result.IsFailure).IsTrue();
        }
        finally
        {
            await server.StopAsync();
        }
    }
}
