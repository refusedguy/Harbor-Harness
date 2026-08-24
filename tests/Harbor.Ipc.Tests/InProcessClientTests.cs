using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Ipc.InProcess;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TUnit.Core;

namespace Harbor.Ipc.Tests;

/// <summary>
///     Smoke tests for <see cref="InProcessHarborClient" />. Verifies that
///     the in-process client correctly delegates to the application-layer
///     services (ISessionStore, IProviderRegistry, IToolRegistry, IAgent)
///     without serialization.
/// </summary>
[NotInParallel]
    public class InProcessClientTests
{
    /// <summary>
    ///     CreateSession should round-trip through the session store and
    ///     return a Session with a non-empty id.
    /// </summary>
    [Test]
    public async Task CreateSession_Returns_Session_With_NonEmpty_Id()
    {
        var sp = TestHost.Build();
        await using var client = new InProcessHarborClient(
            sp.GetRequiredService<IAgent>(),
            sp.GetRequiredService<IAgentRegistry>(),
            sp.GetRequiredService<Harbor.Abstractions.Sessions.ISessionStore>(),
            sp.GetRequiredService<Harbor.Abstractions.Providers.IProviderRegistry>(),
            sp.GetRequiredService<Harbor.Abstractions.Tools.IToolRegistry>(),
            sp.GetRequiredService<Harbor.Abstractions.Events.IEventBus>(),
            sp.GetRequiredService<ILogger<InProcessHarborClient>>());

        var result = await client.CreateSessionAsync(
            "/tmp/test",
            "code",
            "ollama",
            "qwen2.5-coder:7b");
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Id).IsNotNull();
        await Assert.That(result.Value.Id.Length).IsGreaterThan(0);
    }

    /// <summary>
    ///     ListSessions should return at least the sessions created during
    ///     the test (in this case, the one from CreateSession).
    /// </summary>
    [Test]
    public async Task ListSessions_Returns_Created_Session()
    {
        var sp = TestHost.Build();
        await using var client = new InProcessHarborClient(
            sp.GetRequiredService<IAgent>(),
            sp.GetRequiredService<IAgentRegistry>(),
            sp.GetRequiredService<Harbor.Abstractions.Sessions.ISessionStore>(),
            sp.GetRequiredService<Harbor.Abstractions.Providers.IProviderRegistry>(),
            sp.GetRequiredService<Harbor.Abstractions.Tools.IToolRegistry>(),
            sp.GetRequiredService<Harbor.Abstractions.Events.IEventBus>(),
            sp.GetRequiredService<ILogger<InProcessHarborClient>>());

        var create = await client.CreateSessionAsync("/tmp/test", "code", "ollama", "qwen2.5-coder:7b");
        var list = await client.ListSessionsAsync();
        await Assert.That(list.IsSuccess).IsTrue();
        await Assert.That(list.Value.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(list.Value.Any(s => s.Id == create.Value.Id)).IsTrue();
    }

    /// <summary>
    ///     GetSession should round-trip the session id.
    /// </summary>
    [Test]
    public async Task GetSession_Returns_The_Created_Session()
    {
        var sp = TestHost.Build();
        await using var client = new InProcessHarborClient(
            sp.GetRequiredService<IAgent>(),
            sp.GetRequiredService<IAgentRegistry>(),
            sp.GetRequiredService<Harbor.Abstractions.Sessions.ISessionStore>(),
            sp.GetRequiredService<Harbor.Abstractions.Providers.IProviderRegistry>(),
            sp.GetRequiredService<Harbor.Abstractions.Tools.IToolRegistry>(),
            sp.GetRequiredService<Harbor.Abstractions.Events.IEventBus>(),
            sp.GetRequiredService<ILogger<InProcessHarborClient>>());

        var create = await client.CreateSessionAsync("/tmp/test", "code", "ollama", "qwen2.5-coder:7b");
        var get = await client.GetSessionAsync(create.Value.Id);
        await Assert.That(get.IsSuccess).IsTrue();
        await Assert.That(get.Value.Id).IsEqualTo(create.Value.Id);
    }

    /// <summary>
    ///     ListProviders should return at least an empty list (no providers
    ///     registered in the test host).
    /// </summary>
    [Test]
    public async Task ListProviders_Returns_Empty_List_When_No_Providers_Registered()
    {
        var sp = TestHost.Build();
        await using var client = new InProcessHarborClient(
            sp.GetRequiredService<IAgent>(),
            sp.GetRequiredService<IAgentRegistry>(),
            sp.GetRequiredService<Harbor.Abstractions.Sessions.ISessionStore>(),
            sp.GetRequiredService<Harbor.Abstractions.Providers.IProviderRegistry>(),
            sp.GetRequiredService<Harbor.Abstractions.Tools.IToolRegistry>(),
            sp.GetRequiredService<Harbor.Abstractions.Events.IEventBus>(),
            sp.GetRequiredService<ILogger<InProcessHarborClient>>());

        var result = await client.ListProvidersAsync();
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsNotNull();
    }

    /// <summary>
    ///     ListTools should return at least an empty list (no tools
    ///     registered in the test host).
    /// </summary>
    [Test]
    public async Task ListTools_Returns_Empty_List_When_No_Tools_Registered()
    {
        var sp = TestHost.Build();
        await using var client = new InProcessHarborClient(
            sp.GetRequiredService<IAgent>(),
            sp.GetRequiredService<IAgentRegistry>(),
            sp.GetRequiredService<Harbor.Abstractions.Sessions.ISessionStore>(),
            sp.GetRequiredService<Harbor.Abstractions.Providers.IProviderRegistry>(),
            sp.GetRequiredService<Harbor.Abstractions.Tools.IToolRegistry>(),
            sp.GetRequiredService<Harbor.Abstractions.Events.IEventBus>(),
            sp.GetRequiredService<ILogger<InProcessHarborClient>>());

        var result = await client.ListToolsAsync();
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsNotNull();
    }

    /// <summary>
    ///     IsConnected is always true for the in-process client.
    /// </summary>
    [Test]
    public async Task IsConnected_Is_True_After_Construction()
    {
        var sp = TestHost.Build();
        await using var client = new InProcessHarborClient(
            sp.GetRequiredService<IAgent>(),
            sp.GetRequiredService<IAgentRegistry>(),
            sp.GetRequiredService<Harbor.Abstractions.Sessions.ISessionStore>(),
            sp.GetRequiredService<Harbor.Abstractions.Providers.IProviderRegistry>(),
            sp.GetRequiredService<Harbor.Abstractions.Tools.IToolRegistry>(),
            sp.GetRequiredService<Harbor.Abstractions.Events.IEventBus>(),
            sp.GetRequiredService<ILogger<InProcessHarborClient>>());

        await Assert.That(client.IsConnected).IsTrue();
        await client.ConnectAsync(); // no-op
        await Assert.That(client.IsConnected).IsTrue();
        await client.DisconnectAsync(); // no-op
        await Assert.That(client.IsConnected).IsTrue();
    }
}
