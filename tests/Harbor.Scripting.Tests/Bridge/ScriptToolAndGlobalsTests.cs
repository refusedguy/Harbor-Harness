// Bridge layer tests — ScriptTool + ScriptGlobals + ScriptGlobalsExtensions.
using Harbor.Scripting.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Scripting.Tests.Bridge;
public class ScriptGlobalsTests
{
    [Test]
    public async Task ScriptGlobals_RequiredFields_EnforcedByCompiler()
    {
        // The `required` modifier on Tools and Logger means a ScriptGlobals
        // without them fails to compile. We assert this indirectly by
        // constructing a complete one.
        var globals = new ScriptGlobals
        {
            Tools = new ToolRegistry(),
            Providers = new ProviderRegistry(),
            Agents = new AgentRegistry(),
            Logger = NullLogger.Instance
        };

        await Assert.That(globals.Tools).IsNotNull();
        await Assert.That(globals.Logger).IsNotNull();
    }

    [Test]
    public async Task ScriptGlobals_OptionalRegistries_DefaultToNull()
    {
        var globals = new ScriptGlobals
        {
            Tools = new ToolRegistry(),
            Logger = NullLogger.Instance
        };

        await Assert.That(globals.Providers).IsNull();
        await Assert.That(globals.Agents).IsNull();
    }

    [Test]
    public async Task ScriptGlobalsBuilder_FluentApi_BuildsValidGlobals()
    {
        var globals = ScriptGlobalsBuilder.Create()
            .WithTools(new ToolRegistry())
            .WithProviders(new ProviderRegistry())
            .WithAgents(new AgentRegistry())
            .WithLogger(NullLogger.Instance)
            .Build();

        await Assert.That(globals.Tools).IsNotNull();
        await Assert.That(globals.Providers).IsNotNull();
        await Assert.That(globals.Agents).IsNotNull();
    }
}

public class ScriptToolTests
{
    private static JsonDocument Schema => JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}");

    private static ToolContext NewContext(CancellationToken? abort = null) => new(
        "s1",
        "m1",
        "c1",
        "default",
        abort ?? CancellationToken.None,
        Array.Empty<AgentMessage>(),
        (_, _) => Task.CompletedTask,
        (_, _) => Task.FromResult(new PermissionResponse(PermissionAction.Allow, false)),
        null!);

    [Test]
    public async Task ExecuteAsync_DelegatesToSuppliedExecuteDelegate()
    {
        var tool = new ScriptTool(
            "echo",
            "Echo",
            "Echoes back the input",
            Schema,
            ExecutionMode.Parallel,
            (args, ct) => Task.FromResult(ToolResult.Success("echoed")));

        var result = await tool.ExecuteAsync(JsonDocument.Parse("{}").RootElement, NewContext());

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).IsEqualTo("echoed");
    }

    [Test]
    public async Task ExecuteAsync_PassesThroughError_FromExecuteDelegate()
    {
        var tool = new ScriptTool(
            "fail",
            "Fail",
            "Always fails",
            Schema,
            ExecutionMode.Sequential,
            (_, _) => Task.FromResult(ToolResult.Error("boom")));

        var result = await tool.ExecuteAsync(JsonDocument.Parse("{}").RootElement, NewContext());

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).Contains("boom");
    }

    [Test]
    public async Task ConvertToToolResult_ObjectWithOutput_ReturnsOutput()
    {
        var el = JsonDocument.Parse("{\"output\":\"hello\",\"isError\":false}").RootElement;

        var result = ScriptTool.ConvertToToolResult(el);

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).IsEqualTo("hello");
    }

    [Test]
    public async Task ConvertToToolResult_ObjectWithIsErrorTrue_ReturnsError()
    {
        var el = JsonDocument.Parse("{\"output\":\"bad\",\"isError\":true}").RootElement;

        var result = ScriptTool.ConvertToToolResult(el);

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Output).IsEqualTo("bad");
    }

    [Test]
    public async Task ConvertToToolResult_PlainString_ReturnsSuccessWithString()
    {
        var el = JsonDocument.Parse("\"plain text\"").RootElement;

        var result = ScriptTool.ConvertToToolResult(el);

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).IsEqualTo("plain text");
    }

    [Test]
    public async Task ConvertToToolResult_Null_ReturnsEmptySuccess()
    {
        var el = JsonDocument.Parse("null").RootElement;

        var result = ScriptTool.ConvertToToolResult(el);

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Output).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Constructor_NullExecute_Throws()
    {
        Exception? caught = null;
        try
        {
            _ = new ScriptTool(
                "x",
                "X",
                "d",
                Schema,
                ExecutionMode.Parallel,
                null!);
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        await Assert.That(caught).IsNotNull();
        await Assert.That(caught is ArgumentNullException).IsTrue();
    }
}
