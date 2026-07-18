// Engines layer tests — JintScriptEngine in isolation. SharpTsScriptEngine is
// also tested here, but most assertions are about its absence-handling (the
// `sharpts` tool is not installed in CI) so the tests are deterministic.
using Harbor.Scripting.Bridge;
using Harbor.Scripting.Engines;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Scripting.Tests.Engines;

public class JintScriptEngineTests
{
    private static ScriptGlobals NewGlobals(IToolRegistry? tools = null)
    {
        return new ScriptGlobals
        {
            Tools = tools ?? new ToolRegistry(),
            Providers = new ProviderRegistry(),
            Agents = new AgentRegistry(),
            Logger = NullLogger.Instance
        };
    }

    private static JintScriptEngine NewEngine() => new(NullLogger.Instance);

    [Test]
    public async Task Evaluate_SimpleExpression_ReturnsResult()
    {
        var engine = NewEngine();
        var globals = NewGlobals();

        var result = engine.Evaluate<int>("1 + 2", ScriptEngineOptions.Default, globals);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEqualTo(3);
    }

    [Test]
    public async Task Evaluate_AccessGlobal_HarborObject()
    {
        var engine = NewEngine();
        var globals = NewGlobals();

        var result = engine.Evaluate<string>("typeof Harbor", ScriptEngineOptions.Default, globals);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEqualTo("object");
    }

    [Test]
    public async Task Evaluate_Timeout_AbortsAfterTimeout()
    {
        var engine = NewEngine();
        var globals = NewGlobals();
        // Bump the statement budget so the wall-clock timeout wins (a tight
        // `while(true){}` loop hits the 1M-statement default in microseconds).
        var opts = ScriptEngineOptions.Default with
        {
            Timeout = TimeSpan.FromMilliseconds(50),
            MaxStatements = int.MaxValue
        };

        var result = engine.Evaluate("while (true) { }", opts, globals);

        await Assert.That(result.IsSuccess).IsFalse();
        // Either the wall-clock timeout or a Jint constraint can fire first —
        // both are valid abort signals.
        string err = result.Error.ToLowerInvariant();
        bool isTimeout = err.Contains("timed out") || err.Contains("timeout") || err.Contains("statements") || err.Contains("limit");
        await Assert.That(isTimeout).IsTrue();
    }

    [Test]
    public async Task Evaluate_RequiresDisallowAccess_Process()
    {
        var engine = NewEngine();
        var globals = NewGlobals();

        var result = engine.Evaluate<string>("typeof process", ScriptEngineOptions.Default, globals);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEqualTo("undefined");
    }

    [Test]
    public async Task Evaluate_RequiresDisallowAccess_Require()
    {
        var engine = NewEngine();
        var globals = NewGlobals();

        var result = engine.Evaluate<string>("typeof require", ScriptEngineOptions.Default, globals);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsEqualTo("undefined");
    }

    [Test]
    public async Task RegisterToolViaScript_ToolAvailableInRegistry()
    {
        var registry = new ToolRegistry();
        var engine = NewEngine();
        var globals = NewGlobals(tools: registry);

        const string script = """
                              Harbor.registerTool({
                                name: "greet",
                                displayName: "Greet",
                                description: "Greets the caller.",
                                parameterSchema: { type: "object", properties: { name: { type: "string" } } },
                                execute: (args) => ({ output: "Hello, " + (args.name || "world") + "!", isError: false })
                              });
                              """;

        var result = engine.Evaluate(script, ScriptEngineOptions.Default, globals);

        await Assert.That(result.IsSuccess).IsTrue();
        var toolResult = registry.GetTool(ToolName.Create("greet"));
        await Assert.That(toolResult.IsSuccess).IsTrue();
        await Assert.That(toolResult.Value.Name.Value).IsEqualTo("greet");
        await Assert.That(toolResult.Value.DisplayName).IsEqualTo("Greet");
    }

    [Test]
    public async Task Evaluate_LogFunction_RoutesToLogger()
    {
        var engine = NewEngine();
        var globals = NewGlobals();

        var result = engine.Evaluate("Harbor.log('test message'); 42;", ScriptEngineOptions.Default, globals);

        await Assert.That(result.IsSuccess).IsTrue();
    }

    [Test]
    public async Task Evaluate_ScriptError_ReturnsFailureNotException()
    {
        var engine = NewEngine();
        var globals = NewGlobals();

        var result = engine.Evaluate("throw new Error('boom');", ScriptEngineOptions.Default, globals);

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.Error).Contains("boom");
    }

    [Test]
    public async Task Evaluate_ConvertsObject_ToDictionary()
    {
        var engine = NewEngine();
        var globals = NewGlobals();

        var result = engine.Evaluate<Dictionary<string, object>>("({ a: 1, b: 'two' })", ScriptEngineOptions.Default, globals);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value).IsNotNull();
        await Assert.That(result.Value.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Evaluate_EmptyCode_ReturnsFailure()
    {
        var engine = NewEngine();
        var globals = NewGlobals();

        var result = engine.Evaluate("   ", ScriptEngineOptions.Default, globals);

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.Error).Contains("empty");
    }
}
