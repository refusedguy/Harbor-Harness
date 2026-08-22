using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
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
        logger.LogInformation(
            "Starting harbor-plugins-host. plugin dirs: [{Dirs}]",
            string.Join(", ", parsed.Dirs));

        var source = new FileSystemPluginSource(parsed.Dirs, loggerFactory.CreateLogger<FileSystemPluginSource>());
        var references = new PluginAssemblyReferences(loggerFactory.CreateLogger<PluginAssemblyReferences>());
        var compiler = new CachingCompiler(
            new RoslynPluginCompiler(references),
            parsed.CacheDir,
            loggerFactory.CreateLogger<CachingCompiler>());
        var instantiator = new ReflectionPluginInstantiator();
        var registrar = new SafePluginRegistrar(
            new PluginRegistrar(parsed.PluginRoot, loggerFactory.CreateLogger<PluginRegistrar>(), loggerFactory),
            loggerFactory.CreateLogger<SafePluginRegistrar>());

        var host = new PluginHostBuilder()
            .WithSource(source)
            .WithCompiler(compiler)
            .WithInstantiator(instantiator)
            .WithRegistrar(registrar)
            .WithOptions(o => o.PluginRoot = parsed.PluginRoot)
            .Build(loggerFactory.CreateLogger<PluginHost>());

        var loadHost = new McpPluginLoadHost(loggerFactory, new NullEventBus());
        var loadResult = await host.LoadAllAsync(loadHost).ConfigureAwait(false);

        if (loadResult.IsFailure)
            logger.LogError("Plugin load failed: {Error}", loadResult.Error);
        logger.LogInformation("Exposed {Count} plugin tool(s) over MCP", loadHost.Tools.Count);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var server = new McpStdioServer(loadHost, loggerFactory.CreateLogger<McpStdioServer>());
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
        var project = Environment.GetEnvironmentVariable("HARBOR_PROJECT_PLUGINS") ?? defaultProject;

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
