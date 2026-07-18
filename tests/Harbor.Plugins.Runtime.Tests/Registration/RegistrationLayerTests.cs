using CSharpFunctionalExtensions;
using Harbor.Abstractions.Plugins;
using Harbor.Abstractions.Tools;
using Harbor.Plugins.Runtime.Instantiation;
using Harbor.Plugins.Runtime.Registration;
using Harbor.Plugins.Runtime.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
namespace Harbor.Plugins.Runtime.Tests.Registration;

/// <summary>
///     Tests for the Registration layer: <see cref="PluginRegistrar" /> and
/// <see cref="SafePluginRegistrar" />.
/// </summary>
public sealed class RegistrationLayerTests
{
    /// <summary>
    ///     Test 1 — <see cref="PluginRegistrar" /> calls Initialize on the plugin and
    ///     dispatches RegisterTools into the host when the plugin is an IToolPlugin.
    /// </summary>
    [Test]
    public async Task Registrar_ToolPlugin_CallsInitializeAndRegistersTools()
    {
        var host = new FakePluginLoadHost();
        var registrar = new PluginRegistrar(
            "/home/me/.harbor/plugins",
            NullLogger<PluginRegistrar>.Instance);
        var plugin = new FakeToolPlugin("reg-tool-1", "tool_reg_1");
        var loaded = new LoadedPlugin(
            Instance: plugin,
            Name: plugin.Name,
            Version: plugin.Version,
            PluginType: plugin.GetType(),
            SourcePath: "/home/me/.harbor/plugins/reg1.cs",
            SourceHash: "abc",
            LoadedFromCache: false);

        var result = registrar.Register(loaded, host);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(plugin.WasInitialized).IsTrue();
        await Assert.That(host.RegisteredTools.Count).IsEqualTo(1);
    }

    /// <summary>
    ///     Test 2 — <see cref="SafePluginRegistrar" /> swallows exceptions thrown by the
    ///     inner registrar and returns a failure result instead of propagating.
    /// </summary>
    [Test]
    public async Task SafeRegistrar_SwallowsExceptions_ReturnsFailure()
    {
        var host = new FakePluginLoadHost();
        var throwing = new ThrowingRegistrar();
        var safe = new SafePluginRegistrar(throwing, NullLogger.Instance);
        var loaded = new LoadedPlugin(
            Instance: new FakeToolPlugin("reg-throw", "tool_throw"),
            Name: "reg-throw",
            Version: new Version(1, 0, 0),
            PluginType: typeof(object),
            SourcePath: "/x.cs",
            SourceHash: "x",
            LoadedFromCache: false);

        var result = safe.Register(loaded, host);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("threw");
    }

    /// <summary>
    ///     Test 3 — <see cref="PluginRegistrar" /> reports a failure (not throw) when
    ///     Initialize throws. The error message should mention "Initialize".
    /// </summary>
    [Test]
    public async Task Registrar_InitializeThrows_ReturnsFailure()
    {
        var host = new FakePluginLoadHost();
        var registrar = new PluginRegistrar(
            "/home/me/.harbor/plugins",
            NullLogger<PluginRegistrar>.Instance);
        var plugin = new ThrowingInitializePlugin();
        var loaded = new LoadedPlugin(
            Instance: plugin,
            Name: plugin.Name,
            Version: plugin.Version,
            PluginType: plugin.GetType(),
            SourcePath: "/x.cs",
            SourceHash: "x",
            LoadedFromCache: false);

        var result = registrar.Register(loaded, host);

        await Assert.That(result.IsFailure).IsTrue();
        await Assert.That(result.Error).Contains("Initialize");
    }

    /// <summary>
    ///     Test 4 — <see cref="PluginRegistrar" /> builds a PluginContext whose
    ///     DataDirectory is derived from the configured plugin root and the plugin's
    ///     Name. We verify by capturing the context inside a fake plugin.
    /// </summary>
    [Test]
    public async Task Registrar_BuildsContextWithDataDirectoryUnderPluginRoot()
    {
        var host = new FakePluginLoadHost();
        var registrar = new PluginRegistrar(
            "/home/me/.harbor/plugins",
            NullLogger<PluginRegistrar>.Instance);
        var plugin = new ContextCapturingPlugin("capture-1");
        var loaded = new LoadedPlugin(
            Instance: plugin,
            Name: plugin.Name,
            Version: plugin.Version,
            PluginType: plugin.GetType(),
            SourcePath: "/home/me/.harbor/plugins/cap1.cs",
            SourceHash: "x",
            LoadedFromCache: false);

        registrar.Register(loaded, host);

        await Assert.That(plugin.CapturedContext).IsNotNull();
        await Assert.That(plugin.CapturedContext!.DataDirectory)
            .IsEqualTo("/home/me/.harbor/plugins/data/capture-1");
    }

    private sealed class FakeToolPlugin : IToolPlugin
    {
        private readonly string _toolName;
        public FakeToolPlugin(string name, string toolName)
        {
            Name = name;
            _toolName = toolName;
        }
        public string Name { get; }
        public Version Version => new(1, 0, 0);
        public Version RequiredHarborVersion => new(0, 4, 0);
        public string Description => "fake";
        public bool WasInitialized { get; private set; }
        public void Initialize(PluginContext context) { WasInitialized = true; }
        public void RegisterTools(IToolRegistryBuilder builder) => builder.AddTool(new FakeTool(_toolName));
        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeTool : Harbor.Abstractions.Tools.ITool
    {
        public FakeTool(string name) { Name = Harbor.Abstractions.Models.Identifiers.ToolName.Create(name); }
        public Harbor.Abstractions.Models.Identifiers.ToolName Name { get; }
        public string DisplayName => "fake";
        public string Description => "fake";
        public System.Text.Json.JsonDocument ParameterSchema =>
            System.Text.Json.JsonDocument.Parse("{\"type\":\"object\"}");
        public Harbor.Abstractions.Tools.ExecutionMode ExecutionMode => Harbor.Abstractions.Tools.ExecutionMode.Parallel;
        public string? PromptSnippet => null;
        public IReadOnlyList<string> PromptGuidelines => Array.Empty<string>();
        public Task<Harbor.Abstractions.Models.ToolResult> ExecuteAsync(
            System.Text.Json.JsonElement args,
            Harbor.Abstractions.Tools.ToolContext context,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Harbor.Abstractions.Models.ToolResult.Success("ok"));
    }

    private sealed class ThrowingInitializePlugin : IPlugin
    {
        public string Name => "throw-init";
        public Version Version => new(1, 0, 0);
        public Version RequiredHarborVersion => new(0, 4, 0);
        public string Description => "throws on init";
        public void Initialize(PluginContext context) => throw new InvalidOperationException("boom");
        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ContextCapturingPlugin : IPlugin
    {
        public ContextCapturingPlugin(string name) { Name = name; }
        public string Name { get; }
        public Version Version => new(1, 0, 0);
        public Version RequiredHarborVersion => new(0, 4, 0);
        public string Description => "captures context";
        public PluginContext? CapturedContext { get; private set; }
        public void Initialize(PluginContext context) { CapturedContext = context; }
        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ThrowingRegistrar : IPluginRegistrar
    {
        public Result Register(LoadedPlugin plugin, IPluginLoadHost host)
            => throw new InvalidOperationException("registrar exploded");
    }
}
