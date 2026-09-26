using System.Diagnostics;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Sessions;
using Harbor.App.Cli.Demo;
using Harbor.Application.Configuration;
using Harbor.Terminal.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     <c>harbor demo</c> (alias <c>harbor --demo</c>) — scripted, deterministic
///     showcase of the agent pipeline without any real LLM or API keys.
/// </summary>
/// <remarks>
///     <para>
///         The command boots an in-process OpenAI-compatible mock server
///         (<see cref="DemoLlmServer" />), points a throw-away provider config at
///         it (temp <c>HOME</c>, so the user's real <c>~/.harbor</c> is never
///         touched), and plays the scripted scenes through the normal agent loop
///         and renderer. Output is what GIF recorders capture: VHS tapes
///         (<c>demo/*.tape</c>) and the E2E <c>TuiDemoRecorder</c> both drive
///         this command.
///     </para>
///     <para>
///         Supported flags: <c>--scene hero|markdown|approval|all</c>,
///         <c>--tui ansi|plain</c>, <c>--chunk-delay &lt;ms&gt;</c>.
///     </para>
/// </remarks>
public sealed class DemoCommand : ICommand
{
    private static readonly HashSet<string> SupportedTuis = new(StringComparer.OrdinalIgnoreCase) { "ansi", "plain" };

    private readonly TextWriter _output;
    private readonly TextWriter _error;

    public DemoCommand(TextWriter output, TextWriter error)
    {
        _output = output;
        _error = error;
    }

    /// <inheritdoc />
    public string Name => "demo";

    /// <inheritdoc />
    public async Task<int> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        DemoOptions? options = DemoOptions.Parse(args, _error);
        if (options is null)
        {
            PrintUsage();
            return 2;
        }

        DemoLlmServer server = new(options.ChunkDelayMs);
        await server.StartAsync(ct).ConfigureAwait(false);
        string demoHome = CreateDemoHome(server.BaseUri);
        await _output.WriteLineAsync("harbor demo — scripted showcase, mock LLM, no API keys").ConfigureAwait(false);
        await _output.WriteLineAsync($"  scene: {options.Scene}  tui: {options.Tui}  home: {demoHome}").ConfigureAwait(false);

        try
        {
            WireEnvironment(demoHome, options);
            server.Enqueue(DemoScenes.Replies(options.Scene));
            return await RunScenesAsync(options, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await _error.WriteLineAsync("harbor demo: cancelled").ConfigureAwait(false);
            return 130;
        }
        finally
        {
            await server.DisposeAsync().ConfigureAwait(false);
            try { Directory.Delete(demoHome, recursive: true); }
            catch { /* best-effort cleanup of the throw-away HOME */ }
        }
    }

    /// <summary>Point every config discovery path at the throw-away demo HOME.</summary>
    private static void WireEnvironment(string demoHome, DemoOptions options)
    {
        Environment.SetEnvironmentVariable("HOME", demoHome);
        Environment.SetEnvironmentVariable("USERPROFILE", demoHome);
        Environment.SetEnvironmentVariable("HARBOR_TUI", options.Tui);
        Environment.SetEnvironmentVariable("HARBOR_MODEL", "demo/" + DemoLlmServer.ModelId);
        Environment.SetEnvironmentVariable("DEMO_API_KEY", "demo-key");
        Environment.SetEnvironmentVariable("HARBOR_SKIP_ONBOARDING", "1");
        Environment.SetEnvironmentVariable("HARBOR_DEMO", "1");
    }

    /// <summary>Create a temp HOME containing the mock provider config + onboarding-complete marker.</summary>
    private static string CreateDemoHome(Uri mockBaseUri)
    {
        string home = Path.Combine(Path.GetTempPath(), "harbor-demo-" + Guid.NewGuid().ToString("N"));
        string harborDir = Path.Combine(home, ".harbor");
        Directory.CreateDirectory(Path.Combine(harborDir, "providers"));

        string providerConfig = $$"""
            {
              "id": "demo",
              "displayName": "Harbor Demo (mock)",
              "description": "In-process mock LLM for harbor demo — no API keys.",
              "baseUrl": "{{mockBaseUri}}",
              "apiType": "openai-compatible",
              "authType": "bearer",
              "authEnvVar": "DEMO_API_KEY",
              "models": [
                {
                  "id": "{{DemoLlmServer.ModelId}}",
                  "providerId": "demo",
                  "displayName": "Harbor Demo Model",
                  "contextWindow": 128000,
                  "maxOutputTokens": 4096,
                  "supportsReasoning": false,
                  "supportsVision": false,
                  "supportsToolUse": true,
                  "pricing": { "inputPerMillion": 0, "outputPerMillion": 0 },
                  "promptTemplate": "openai"
                }
              ]
            }
            """;
        File.WriteAllText(Path.Combine(harborDir, "providers", "demo.json"), providerConfig);

        File.WriteAllText(Path.Combine(harborDir, "config.json"), """
            {
              "provider": "demo",
              "model": "demo/harbor-1",
              "agent": "code",
              "onboarded": true
            }
            """);
        return home;
    }

    /// <summary>
    ///     Play the selected scenes through the real agent pipeline — one
    ///     session, one prompt per scene, streamed through the renderer exactly
    ///     like <c>harbor ask</c>.
    /// </summary>
    private async Task<int> RunScenesAsync(DemoOptions options, CancellationToken ct)
    {
        using IHost host = Hosting.HostBuilder.Build();
        IServiceProvider sp = host.Services;

        var renderer = sp.GetRequiredService<ITuiRenderer>();
        var eventBus = sp.GetRequiredService<IEventBus>();
        var agent = sp.GetRequiredService<IAgent>();
        var sessionStore = sp.GetRequiredService<ISessionStore>();
        var agentRegistry = sp.GetRequiredService<IAgentRegistry>();
        var configStore = sp.GetRequiredService<IConfigStore>();

        await renderer.InitializeAsync().ConfigureAwait(false);
        eventBus.Subscribe(async (evt, c) => await renderer.RenderAsync(evt, c).ConfigureAwait(false));

        HarborConfig config = (await configStore.LoadAsync().ConfigureAwait(false)).Value;
        var defaultAgent = agentRegistry.GetAllAgents().FirstOrDefault(a => a.Name.Value == config.Agent)
                           ?? agentRegistry.GetAllAgents()[0];
        string[] modelParts = config.EffectiveModel.Split('/', 2);
        var sessionResult = await sessionStore.CreateAsync(
            Environment.CurrentDirectory, defaultAgent.Name.Value, modelParts[0],
            modelParts.Length > 1 ? modelParts[1] : config.EffectiveModel).ConfigureAwait(false);
        if (sessionResult.IsFailure)
        {
            await _error.WriteLineAsync("harbor demo: session creation failed: " + sessionResult.Error).ConfigureAwait(false);
            return 1;
        }

        agent.Initialize(sessionResult.Value, defaultAgent);

        foreach (DemoScene scene in DemoScenes.Select(options.Scene))
        {
            await renderer.WriteLineAsync($"\n━━━ harbor demo · scene: {scene.Id} ━━━ provider: demo (mock) · tui: {options.Tui}").ConfigureAwait(false);
            await renderer.WriteLineAsync("> " + scene.Prompt).ConfigureAwait(false);

            var stopwatch = Stopwatch.StartNew();
            Result promptResult = await agent.PromptAsync(scene.Prompt).ConfigureAwait(false);
            stopwatch.Stop();
            if (promptResult.IsFailure)
            {
                await renderer.WriteLineAsync("✗ scene failed: " + promptResult.Error).ConfigureAwait(false);
                return 1;
            }

            await renderer.WriteLineAsync(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"✔ scene complete in {stopwatch.Elapsed.TotalSeconds:0.0}s")).ConfigureAwait(false);
        }

        await renderer.WriteLineAsync("\nharbor demo finished — no API keys were used. Record GIFs: vhs demo/hero.tape").ConfigureAwait(false);
        return 0;
    }

    private void PrintUsage()
    {
        _error.WriteLine("""
                         Usage: harbor demo [--scene hero|markdown|approval|all] [--tui ansi|plain] [--chunk-delay <ms>]

                           Plays a scripted demo (mock LLM, no API keys) through the real
                           agent pipeline. Output is meant to be recorded into GIFs:
                             vhs demo/hero.tape
                             dotnet run --project apps/Harbor.App.Cli -- --demo --scene hero
                         """);
    }

    private sealed record DemoOptions(string Scene, string Tui, int ChunkDelayMs)
    {
        public static DemoOptions? Parse(string[] args, TextWriter error)
        {
            string scene = "all";
            string tui = "ansi";
            int chunkDelayMs = 30;

            int i = 0;
            while (i < args.Length)
            {
                string arg = args[i];
                string? inline = arg.Contains('=', StringComparison.Ordinal) ? arg[(arg.IndexOf('=') + 1)..] : null;
                switch (arg)
                {
                    case "--scene" or "-s":
                    case var _ when arg.StartsWith("--scene=", StringComparison.OrdinalIgnoreCase):
                        scene = (inline ?? (i + 1 < args.Length ? args[i + 1] : null) ?? string.Empty).ToLowerInvariant();
                        i += inline is null ? 2 : 1;
                        break;

                    case "--tui" or "-t":
                    case var _ when arg.StartsWith("--tui=", StringComparison.OrdinalIgnoreCase):
                        tui = (inline ?? (i + 1 < args.Length ? args[i + 1] : null) ?? string.Empty).ToLowerInvariant();
                        i += inline is null ? 2 : 1;
                        break;

                    case "--chunk-delay":
                    case var _ when arg.StartsWith("--chunk-delay=", StringComparison.OrdinalIgnoreCase):
                        {
                            string raw = inline ?? (i + 1 < args.Length ? args[i + 1] : string.Empty);
                            if (!int.TryParse(raw, out chunkDelayMs) || chunkDelayMs < 0)
                            {
                                error.WriteLine("harbor demo: --chunk-delay must be a non-negative integer (ms)");
                                return null;
                            }

                            i += inline is null ? 2 : 1;
                            break;
                        }

                    default:
                        error.WriteLine("harbor demo: unknown argument '" + arg + "'");
                        return null;
                }
            }

            if (scene is not ("hero" or "markdown" or "approval" or "all"))
            {
                error.WriteLine("harbor demo: unknown scene '" + scene + "' (expected hero|markdown|approval|all)");
                return null;
            }

            if (!SupportedTuis.Contains(tui))
            {
                error.WriteLine("harbor demo: --tui must be 'ansi' or 'plain' (interactive shells are not scriptable)");
                return null;
            }

            return new DemoOptions(scene, tui, chunkDelayMs);
        }
    }

    private sealed record DemoScene(string Id, string Prompt);

    /// <summary>Canned scene scripts: prompts for the loop, replies for the mock FIFO.</summary>
    private static class DemoScenes
    {
        private static readonly DemoScene Hero = new(
            "hero",
            "Introduce Harbor in one paragraph for the README");

        private static readonly DemoScene Markdown = new(
            "markdown",
            "Show me the Harbor agent loop as a diagram");

        private static readonly DemoScene Approval = new(
            "approval",
            "Run `echo hello from Harbor` in the shell");

        /// <summary>Scenes for the given selector, in play order.</summary>
        public static IEnumerable<DemoScene> Select(string scene) =>
            scene switch
            {
                "hero" => new[] { Hero },
                "markdown" => new[] { Markdown },
                "approval" => new[] { Approval },
                _ => new[] { Hero, Markdown, Approval }
            };

        /// <summary>Mock replies for the given selector, in the exact order the agent will request them.</summary>
        public static DemoReply[] Replies(string scene) =>
            scene switch
            {
                "hero" => HeroReplies,
                "markdown" => MarkdownReplies,
                "approval" => ApprovalReplies,
                _ => [.. HeroReplies, .. MarkdownReplies, .. ApprovalReplies]
            };

        private static readonly DemoReply[] HeroReplies =
        [
            DemoReply.FromText(
                "Harbor is a modular .NET 10 AI coding agent harness. Every concern — providers, storage, TUI " +
                "rendering, tool execution, permissions — lives behind an interface and swaps through DI. It ships " +
                "4 native LLM clients plus 13 JSON-config providers, 18 builtin tools, JSONL-first session storage, " +
                "and a plugin host that compiles C# sources at startup — all performance-first and NativeAOT-ready."),
        ];

        private static readonly DemoReply[] MarkdownReplies =
        [
            DemoReply.FromText(
                "**The agent loop** — every turn is one LLM call plus any tool calls:\n\n" +
                "```text\n" +
                "UserMessage → SystemPromptBuilder → LlmClient.StreamAsync\n" +
                "                                        ↓\n" +
                "                                  ToolCallEvent\n" +
                "                                        ↓\n" +
                "                          ToolRegistry.GetTool → Execute\n" +
                "                                        ↓\n" +
                "                                  ToolResult → next turn\n" +
                "```\n\n" +
                "1. **Stream** tokens as they arrive — renderers subscribe to typed events only\n" +
                "2. **Check** permissions (allow / ask / deny per tool + glob)\n" +
                "3. **Repeat** until the model stops calling tools, then publish AgentEnd"),
        ];

        private static readonly DemoReply[] ApprovalReplies =
        [
            // turn 1 — the tool call (bash falls through to Ask in PermissionRuleset.Default)
            DemoReply.FromToolCall("bash", """{"command":"echo hello from Harbor"}"""),
            // turn 2 — summarize after the tool result
            DemoReply.FromText(
                "Approved and executed. The approval gate asked once, the demo policy allowed it, and the shell " +
                "printed: hello from Harbor"),
        ];
    }
}
