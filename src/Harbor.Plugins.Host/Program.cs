using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Harbor.Abstractions.Env;
using Harbor.Plugins.Abstractions;
using Harbor.Plugins.Compilation;
using Harbor.Plugins.Hosting;
using Harbor.Plugins.Instantiation;
using Harbor.Plugins.Registration;
using Harbor.Plugins.Storage;
using Microsoft.Extensions.Logging;

namespace Harbor.Plugins.Host;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // MCP speaks JSON-RPC on stdout — route all logging to stderr so it never corrupts
        // the protocol stream. The server writes to Console.OpenStandardOutput() directly,
        // which this redirect does not affect.
        Console.SetOut(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var globalDir = Path.Combine(home, ".harbor", "plugins");
        var projectDir = Path.Combine(Directory.GetCurrentDirectory(), ".harbor", "plugins");

        var parsed = ParseArgs(args, globalDir, projectDir);
        var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        }));

        var logger = loggerFactory.CreateLogger("harbor-plugins-host");
        ProgramLog.Starting(logger, string.Join(", ", parsed.Dirs));

        var source = new FileSystemPluginSource(parsed.Dirs, loggerFactory.CreateLogger<FileSystemPluginSource>());
        var references = new PluginAssemblyReferences(loggerFactory.CreateLogger<PluginAssemblyReferences>());
        var compiler = new CachingCompiler(
            new RoslynPluginCompiler(references),
            parsed.CacheDir,
            loggerFactory.CreateLogger<CachingCompiler>());
        var instantiator = new ReflectionPluginInstantiator();

        // #1734: the plugin's own configuration section is read from
        // ~/.harbor/config.json, ONCE, before the plugins load. The plugin
        // pipeline never touches the file: it asks this source for a section,
        // and the section is checked against the schema the plugin declared
        // before the plugin is initialized.
        IPluginConfigSource configSource = await ConfigFilePluginConfigSource
            .LoadAsync(logger)
            .ConfigureAwait(false);

        var registrar = new SafePluginRegistrar(
            new PluginRegistrar(
                parsed.PluginRoot,
                loggerFactory.CreateLogger<PluginRegistrar>(),
                loggerFactory,
                config: configSource),
            loggerFactory.CreateLogger<SafePluginRegistrar>());

        var loadHost = new McpPluginLoadHost(loggerFactory, new NullEventBus());

        // #1733: the host publishes ONE capability — "mcp-session", the MCP session with
        // the CLI. It is not true when the plugins load (the client has not connected
        // yet), so a plugin that declares it waits, visibly, and comes up the moment the
        // handshake lands.
        var dependencies = new PluginDependencyResolver(
            new Dictionary<string, Func<bool>>(StringComparer.Ordinal)
            {
                ["mcp-session"] = () => loadHost.IsSessionEstablished
            },
            loggerFactory.CreateLogger<PluginDependencyResolver>());

        var host = new PluginHostBuilder()
            .WithSource(source)
            .WithCompiler(compiler)
            .WithInstantiator(instantiator)
            .WithRegistrar(registrar)
            .WithOptions(o =>
            {
                o.PluginRoot = parsed.PluginRoot;
                // #1739: the running version, for the host's early manifest check
                // (a plugin whose manifest asks for a newer Harbor is refused
                // before it is compiled).
                o.HarborVersion = PluginLifecycle.CurrentHarborVersion;
                o.Dependencies = dependencies;
            })
            .Build(loggerFactory.CreateLogger<PluginHost>());

        var loadResult = await host.LoadAllAsync(loadHost).ConfigureAwait(false);

        if (loadResult.IsFailure)
            ProgramLog.PluginLoadFailed(logger, loadResult.Error);
        ProgramLog.ExposedTools(logger, loadHost.Tools.Count);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var server = new McpStdioServer(
            loadHost,
            loggerFactory.CreateLogger<McpStdioServer>(),
            () => host.ActivatePending(loadHost));
        try
        {
            await server.RunAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // clean shutdown
        }

        return 0;
    }

    private static (IReadOnlyList<string> Dirs, string PluginRoot, string CacheDir) ParseArgs(
        string[] args, string defaultGlobal, string defaultProject)
    {
        var global = defaultGlobal;
        var project = HarborEnv.Get(HarborEnv.ProjectPlugins) ?? defaultProject;

        int i = 0;
        while (i < args.Length)
        {
            if (args[i] == "--plugins-dir" && i + 1 < args.Length)
            {
                global = args[i + 1];
                i += 2;
            }
            else if (args[i] == "--project-plugins-dir" && i + 1 < args.Length)
            {
                project = args[i + 1];
                i += 2;
            }
            else
            {
                i++;
            }
        }

        var dirs = new List<string> { global };
        if (!string.Equals(global, project, StringComparison.OrdinalIgnoreCase))
            dirs.Add(project);

        var cacheDir = Path.Combine(global, "cache");
        return (dirs, global, cacheDir);
    }
}

/// <summary>
///     SG10: BCL <c>[LoggerMessage]</c> delegates for <see cref="Program" />.
///     Templates, levels and operands are 1-to-1 with the former <c>LogX</c> calls.
/// </summary>
internal static partial class ProgramLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Starting harbor-plugins-host. plugin dirs: [{Dirs}]")]
    public static partial void Starting(ILogger logger, string dirs);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Plugin load failed: {Error}")]
    public static partial void PluginLoadFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Exposed {Count} plugin tool(s) over MCP")]
    public static partial void ExposedTools(ILogger logger, int count);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Failed to read the plugin configuration: {Error}. The plugins start with their own defaults.")]
    public static partial void ConfigLoadFailed(ILogger logger, string error);
}
