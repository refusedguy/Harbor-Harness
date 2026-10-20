using CSharpFunctionalExtensions;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using Harbor.Registries.Providers;
using Harbor.Registries.Tools;

// #1769: the doubles are generated (a concrete class per interface), so the
// hand-written member-by-member fakes are gone.
using FakeLlmClientBase = Harbor.TestKit.Generated.FakeLlmClient;
using FakeToolBase = Harbor.TestKit.Generated.FakeTool;

namespace Harbor.Core.Tests;
public class RegistryTests
{
    [Test]
    public async Task ToolRegistry_Register_And_GetTool()
    {
        var registry = new ToolRegistry();
        var tool = new TestTool("test", "Test tool");

        var result = registry.Register(tool);
        await Assert.That(result.IsSuccess).IsTrue();

        var getResult = registry.GetTool(ToolName.Create("test"));
        await Assert.That(getResult.IsSuccess).IsTrue();
        await Assert.That(getResult.Value.Name.Value).IsEqualTo("test");
    }

    [Test]
    public async Task ToolRegistry_DuplicateRegister_Fails()
    {
        var registry = new ToolRegistry();
        var tool = new TestTool("test", "Test");
        registry.Register(tool);

        var dup = registry.Register(tool);
        await Assert.That(dup.IsSuccess).IsFalse();
    }

    [Test]
    public async Task ToolRegistry_Unregister_RemovesTool()
    {
        var registry = new ToolRegistry();
        registry.Register(new TestTool("test", "Test"));

        var result = registry.Unregister(ToolName.Create("test"));
        await Assert.That(result.IsSuccess).IsTrue();

        var getResult = registry.GetTool(ToolName.Create("test"));
        await Assert.That(getResult.IsSuccess).IsFalse();
    }

    [Test]
    public async Task ProviderRegistry_Register_And_GetClient()
    {
        var registry = new ProviderRegistry();
        registry.Register(ProviderId.Create("test"), () => new TestLlmClient("test"));

        var result = registry.GetClient(ProviderId.Create("test"));
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.ProviderId.Value).IsEqualTo("test");
    }

    [Test]
    public async Task ProviderRegistry_Unregister_RemovesProvider()
    {
        var registry = new ProviderRegistry();
        registry.Register(ProviderId.Create("test"), () => new TestLlmClient("test"));

        var result = registry.Unregister(ProviderId.Create("test"));
        await Assert.That(result.IsSuccess).IsTrue();

        var getResult = registry.GetClient(ProviderId.Create("test"));
        await Assert.That(getResult.IsSuccess).IsFalse();
    }
}

internal sealed class TestTool : FakeToolBase
{
    public TestTool(string name, string description)
    {
        Name = ToolName.Create(name);
        DisplayName = name;
        Description = description;
        ExecuteAsyncValue = ToolResult.Success("test output");
    }
}

internal sealed class TestLlmClient : FakeLlmClientBase
{
    public TestLlmClient(string id)
    {
        ProviderId = ProviderId.Create(id);
        GetModelsAsyncResult = Result.Success<IReadOnlyList<ModelInfo>>([]);
    }
}
