using Harbor.Abstractions.Tools;
using Harbor.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Harbor.Hosting.Tests;

/// <summary>
///     Slice-1 absence matrix for issue #1055 (graceful absence): the CLI must
///     start fast and work fully with no plugin dirs, no host binary and no
///     network; a broken <c>.cs</c> warns with its file name while the rest
///     still load; and a plugin-less composition must not pull
///     <c>Microsoft.CodeAnalysis.*</c> into the process (the Roslyn side of
///     <c>PluginRuntimeComposer.Compose</c> is lazy).
/// </summary>
[NotInParallel("hosting")]
public class PluginGracefulAbsenceTests
{
    private static string TempHarborDir() =>
        Path.Combine(Path.GetTempPath(), "harbor-absence-tests", Guid.NewGuid().ToString("N"));

    private static ServiceProvider Compose(string harborDir, CaptureLoggerFactory capture)
    {
        var services = new ServiceCollection();
        services.AddHarbor(new HarborComposeOptions
        {
            HarborDir = harborDir,
            DefaultStorageBackend = "memory",
            BootstrapLoggerFactory = () => capture,
        });
        return services.BuildServiceProvider();
    }

    private static IDisposable WithTempCwd()
    {
        string previous = Directory.GetCurrentDirectory();
        string temp = Path.Combine(Path.GetTempPath(), "harbor-absence-cwd", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        Directory.SetCurrentDirectory(temp);
        return new RestoreCwd(previous, temp);
    }

    private sealed class RestoreCwd : IDisposable
    {
        private readonly string _previous;
        private readonly string _temp;
        public RestoreCwd(string previous, string temp)
        {
            _previous = previous;
            _temp = temp;
        }

        public void Dispose()
        {
            Directory.SetCurrentDirectory(_previous);
            try
            {
                Directory.Delete(_temp, true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CaptureLogger : ILogger
    {
        private readonly List<LogEntry> _entries;
        public CaptureLogger(List<LogEntry> entries)
        {
            _entries = entries;
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add(new LogEntry(logLevel, formatter(state, exception)));
            }
        }
    }

    private sealed class CaptureLoggerFactory : ILoggerFactory
    {
        private readonly List<LogEntry> _entries = new();
        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_entries)
                {
                    return _entries.ToArray();
                }
            }
        }

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => new CaptureLogger(_entries);

        public void Dispose()
        {
        }
    }

    /// <summary>
    ///     #1055 (a): no plugin dirs, no host binary — composition succeeds,
    ///     builtins are intact, and exactly one honest line reports the
    ///     plugin stack as off.
    /// </summary>
    [Test]
    public async Task NoPluginDirs_ComposeSucceeds_LogsSinglePluginOffLine()
    {
        string harborDir = TempHarborDir(); // deliberately NOT created
        var capture = new CaptureLoggerFactory();
        using var _cwd = WithTempCwd(); // empty project scope
        using ServiceProvider sp = Compose(harborDir, capture);

        var toolNames = sp.GetRequiredService<IToolRegistry>()
            .GetAllTools()
            .Select(t => t.Name.Value)
            .ToArray();
        await Assert.That(toolNames.Contains("read")).IsTrue()
            .Because("absence of plugins must not degrade the builtin tool set");

        var offLines = capture.Entries
            .Where(e => e.Message.Contains("plugins: off", StringComparison.Ordinal))
            .ToArray();
        await Assert.That(offLines.Length).IsEqualTo(1)
            .Because("graceful absence reports itself in exactly one line, not zero and not per-module");
        await Assert.That(offLines[0].Message).Contains("plugins: off (no host)");
    }

    /// <summary>
    ///     #1055 (d): composing with no plugin scripts must not load
    ///     <c>Microsoft.CodeAnalysis.*</c> — the Roslyn compiler behind
    ///     <c>PluginRuntimeComposer.Compose</c> builds lazily on first compile.
    ///     Diff-based (not absolute): other tests in this process may have
    ///     compiled plugins already; only what THIS composition loads counts.
    /// </summary>
    [Test]
    public async Task NoPluginDirs_ComposeLoadsNoCodeAnalysisAssemblies()
    {
        var before = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetName().Name ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);

        string harborDir = TempHarborDir(); // deliberately NOT created
        var capture = new CaptureLoggerFactory();
        using var _cwd = WithTempCwd(); // empty project scope
        using ServiceProvider sp = Compose(harborDir, capture);
        _ = sp.GetRequiredService<IToolRegistry>();

        var addedRoslyn = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetName().Name ?? string.Empty)
            .Where(n => n.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal))
            .Where(n => !before.Contains(n))
            .ToArray();
        await Assert.That(addedRoslyn.Length).IsEqualTo(0)
            .Because("a plugin-less composition must never pay for Roslyn: "
                + string.Join(",", addedRoslyn));
    }

    /// <summary>
    ///     #1055 (b) slice 3: composition (the CLI startup equivalent) succeeds
    ///     with scripts on disk — exit 0, not a failed load — and registers
    ///     nothing in-process. A broken <c>.cs</c> no longer warns per file
    ///     (there is no in-process compile left to fail); instead one honest
    ///     line says where the scripts go. Per-plugin isolation now lives in
    ///     the host process (<c>SafePluginRegistrar</c>), covered by
    ///     <c>Harbor.Plugins.Host.Tests</c>.
    /// </summary>
    [Test]
    public async Task BrokenPlugin_WarnsWithFileName_OthersLoad()
    {
        string harborDir = TempHarborDir();
        string pluginsDir = Path.Combine(harborDir, "plugins");
        Directory.CreateDirectory(pluginsDir);
        await File.WriteAllTextAsync(
            Path.Combine(pluginsDir, "broken.cs"),
            "// Intentionally broken: missing expression.\n"
            + "public sealed class Broken {\n"
            + "    public void M() {\n"
            + "        int x = ;\n"
            + "    }\n"
            + "}\n").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(pluginsDir, "good.cs"), GoodSource)
            .ConfigureAwait(false);

        var capture = new CaptureLoggerFactory();
        using var _cwd = WithTempCwd(); // empty project scope
        using ServiceProvider sp = Compose(harborDir, capture);

        // Slice 3 has no background in-process load; Completion is already
        // empty. Awaiting it keeps the observation point, not a race.
        var loaded = await sp.GetRequiredService<StartupPluginLoad>().Completion
            .WaitAsync(TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        await Assert.That(loaded.Count).IsEqualTo(0)
            .Because("slice 3: the CLI never compiles CS plugins in-process");

        var toolNames = sp.GetRequiredService<IToolRegistry>()
            .GetAllTools()
            .Select(t => t.Name.Value)
            .ToArray();
        await Assert.That(toolNames.Contains("absence_s1_hello")).IsEqualTo(false)
            .Because("slice 3: plugin tools arrive over MCP from harbor-plugins-host, "
                + "never through the in-process IToolRegistry");
        await Assert.That(toolNames.Contains("read")).IsTrue()
            .Because("scripts on disk must not degrade the builtin tool set");

        var pluginLines = capture.Entries
            .Where(e => e.Message.Contains("plugins:", StringComparison.Ordinal))
            .ToArray();
        await Assert.That(pluginLines.Length).IsEqualTo(1)
            .Because("the plugin route reports itself in exactly one line, not zero and not per-file");
        await Assert.That(pluginLines[0].Message).Contains("harbor-plugins-host");
    }

    private const string GoodSource = """
        using System;
        using System.Collections.Generic;
        using System.Text.Json;
        using System.Threading;
        using System.Threading.Tasks;
        using CSharpFunctionalExtensions;
        using Harbor.Abstractions.Models;
        using Harbor.Abstractions.Models.Identifiers;
        using Harbor.Abstractions.Permissions;
        using Harbor.Abstractions.Plugins;
        using Harbor.Abstractions.Tools;
        using Microsoft.Extensions.Logging;

        public sealed class AbsenceS1HelloPlugin : IToolPlugin
        {
            public string Name => "absence-s1-hello";
            public Version Version => new(1, 0, 0);
            public Version RequiredHarborVersion => new(0, 4, 0);
            public string Description => "Slice-1 absence-matrix healthy plugin";

            public void Initialize(PluginContext context)
            {
            }

            public void RegisterTools(IToolRegistryBuilder builder) => builder.AddTool<AbsenceS1HelloTool>();

            public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        }

        public sealed class AbsenceS1HelloTool : ITool
        {
            public ToolName Name => ToolName.Create("absence_s1_hello");
            public string DisplayName => "Absence S1 Hello";
            public string Description => "Returns a greeting";
            public JsonDocument ParameterSchema => JsonDocument.Parse("{\"type\":\"object\"}");
            public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
            public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

            public string? PromptSnippet => null;
            public IReadOnlyList<string> PromptGuidelines => Array.Empty<string>();

            public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext context, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(ToolResult.Success("Hello from absence-s1!"));
            }
        }
        """;
}
