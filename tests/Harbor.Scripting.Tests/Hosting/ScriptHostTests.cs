// Hosting layer tests — ScriptHost orchestrates engine + store + compiler.
using Harbor.Scripting.Abstractions;
using Harbor.Scripting.Bridge;
using Harbor.Scripting.Compilation;
using Harbor.Scripting.Engines;
using Harbor.Scripting.Hosting;
using Harbor.Scripting.Storage;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Scripting.Tests.Hosting;

/// <summary>
///     A fake engine that records Evaluate calls and returns canned results.
///     Used to test ScriptHost in isolation from real engines.
/// </summary>
internal sealed class FakeEngine : IScriptEngine
{
    public int EvaluateCalls { get; private set; }
    public Func<string, Result> OnEvaluate { get; set; } = _ => Result.Success();
    public Func<string, Result<T>> OnEvaluateT<T>() => _ => Result.Failure<T>("not implemented");

    public Result Evaluate(string code, ScriptEngineOptions options, ScriptGlobals globals)
    {
        EvaluateCalls++;
        return OnEvaluate(code);
    }

    public Result<T> Evaluate<T>(string code, ScriptEngineOptions options, ScriptGlobals globals)
    {
        EvaluateCalls++;
        return OnEvaluateT<T>()(code);
    }
}

public class ScriptHostTests
{
    private static ScriptGlobals NewGlobals() => new()
    {
        Tools = new ToolRegistry(),
        Providers = new ProviderRegistry(),
        Agents = new AgentRegistry(),
        Logger = NullLogger.Instance
    };

    [Test]
    public async Task LoadAllAsync_EmptyStore_ReturnsSuccessWithNoInstances()
    {
        var host = new ScriptHost(
            new FakeEngine(),
            new InMemoryScriptStore(),
            new PassThroughCompiler(),
            NullLogger<ScriptHost>.Instance);

        var result = await host.LoadAllAsync(NewGlobals());

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Instances).IsEmpty();
        await Assert.That(result.Value.Errors).IsEmpty();
    }

    [Test]
    public async Task LoadAllAsync_TwoScripts_EvaluatesBothInOrder()
    {
        var engine = new FakeEngine();
        var store = new InMemoryScriptStore(new Dictionary<string, string>
        {
            ["a"] = "Harbor.log('a')",
            ["b"] = "Harbor.log('b')"
        });
        var host = new ScriptHost(engine, store, new PassThroughCompiler(), NullLogger<ScriptHost>.Instance);

        var result = await host.LoadAllAsync(NewGlobals());

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(engine.EvaluateCalls).IsEqualTo(2);
        await Assert.That(result.Value.Instances.Count).IsEqualTo(2);
    }

    [Test]
    public async Task LoadAllAsync_WhenEngineFails_ContinuesAndRecordsError()
    {
        var engine = new FakeEngine
        {
            OnEvaluate = _ => Result.Failure("boom")
        };
        var store = new InMemoryScriptStore(new Dictionary<string, string>
        {
            ["a"] = "x",
            ["b"] = "y"
        });
        var host = new ScriptHost(engine, store, new PassThroughCompiler(), NullLogger<ScriptHost>.Instance);

        var result = await host.LoadAllAsync(NewGlobals());

        // Both scripts evaluated, both failed, errors recorded — but the host
        // succeeded overall (ContinueOnFailure defaults to true).
        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Errors.Count).IsEqualTo(2);
        await Assert.That(result.Value.Instances.Count).IsEqualTo(2);
        await Assert.That(result.Value.Instances[0].Succeeded).IsFalse();
    }

    [Test]
    public async Task LoadAllAsync_WhenCompilerFails_ScriptMarkedFailed()
    {
        var engine = new FakeEngine();
        var store = new InMemoryScriptStore(new Dictionary<string, string>
        {
            ["a"] = "let x: number = 1;"
        });
        // Use TscCompiler when tsc is unavailable — Compile will fail for .ts.
        var compiler = new TscCompiler(NullLogger<TscCompiler>.Instance);
        var host = new ScriptHost(engine, store, compiler, NullLogger<ScriptHost>.Instance);

        var result = await host.LoadAllAsync(NewGlobals());

        await Assert.That(result.IsSuccess).IsTrue(); // ContinueOnFailure
        await Assert.That(engine.EvaluateCalls).IsEqualTo(0); // Engine never called — compile failed first
        await Assert.That(result.Value.Instances[0].Succeeded).IsFalse();
        await Assert.That(result.Value.Instances[0].Error).Contains("tsc");
    }

    [Test]
    public async Task EvaluateAsync_OneShotScript_ReturnsInstance()
    {
        var engine = new JintScriptEngine(NullLogger.Instance);
        var host = new ScriptHost(
            engine,
            new InMemoryScriptStore(),
            new PassThroughCompiler(),
            NullLogger<ScriptHost>.Instance);

        var result = await host.EvaluateAsync("inline.js", "1 + 2", NewGlobals());

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Succeeded).IsTrue();
        await Assert.That(result.Value.Source.Name).IsEqualTo("inline");
    }

    [Test]
    public async Task LoadByNameAsync_ExistingScript_EvaluatesIt()
    {
        var engine = new JintScriptEngine(NullLogger.Instance);
        var store = new InMemoryScriptStore(new Dictionary<string, string>
        {
            ["greet"] = "Harbor.log('hi')"
        });
        var host = new ScriptHost(engine, store, new PassThroughCompiler(), NullLogger<ScriptHost>.Instance);

        var result = await host.LoadByNameAsync("greet", NewGlobals());

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Value.Succeeded).IsTrue();
    }

    [Test]
    public async Task LoadByNameAsync_MissingScript_ReturnsFailure()
    {
        var host = new ScriptHost(
            new FakeEngine(),
            new InMemoryScriptStore(),
            new PassThroughCompiler(),
            NullLogger<ScriptHost>.Instance);

        var result = await host.LoadByNameAsync("nonexistent", NewGlobals());

        await Assert.That(result.IsSuccess).IsFalse();
        await Assert.That(result.Error).Contains("not found");
    }
}
