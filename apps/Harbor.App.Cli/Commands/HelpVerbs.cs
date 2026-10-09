namespace Harbor.App.Cli.Commands;

/// <summary>
///     Help/version/storage informational verbs, extracted from
///     <c>Program</c> (#176). Output text is byte-for-byte identical to the
///     former <c>Program.Print*</c> methods.
/// </summary>
internal static class HelpVerbs
{
    internal static void PrintTuiOptions()
    {
        Console.WriteLine("""
                          TUI renderers (set HARBOR_TUI):
                            Terminal:  ansi (default), plain, spectre, fullscreen, spectre-tui,
                                       terminal-gui, termina, razor, sixel
                            Desktop:   wpf (Windows), avalonia (cross-platform), maui (WinUI/Android/iOS/Mac)
                            Web:       blazor (Blazor Server, http://localhost:5000)
                            Non-interactive: notifications (desktop OS notifications only)

                          See docs/ALTERNATIVE_UIS.md for the full comparison.
                          Note: wpf/avalonia/maui/blazor/sixel/notifications require adding the
                          corresponding Harbor.Tui.* project reference to Harbor.App.Cli.csproj
                          (and the matching workload — e.g. `dotnet workload install maui`).
                          """);
    }

    internal static int PrintStorageOptions()
    {
        Console.WriteLine("Storage: jsonl (default), memory, sqlite");
        return 0;
    }

    internal static int PrintHelp()
    {
        Console.WriteLine("""
                          Harbor — modular AI coding agent.
                          Usage: harbor [ask <prompt>|run (task agent=<name> <prompt>|list)|demo|setup|auth|config|providers|models|sessions|mcp|serve|tui|events|storage|logs|help|version] [--script <path>]

                          demo [--scene hero|markdown|approval|all] [--tui ansi|plain|cellforge]
                                            Scripted demo with an in-process mock LLM — no API keys.
                                            The GIF recorder (tests/Harbor.E2E.Framework/TuiDemoRecorder)
                                            and the VHS tapes (demo/*.tape) drive this command.

                          --script <path>   Run a .js or .ts script at startup (registers tools via Harbor.registerTool).
                                            See docs/SCRIPTING.md for the full comparison of CS / Jint / SharpTS / MCP.
                          --loglevel <lvl>  Console log level (Trace/Debug/Information/Warning/Error/Critical).
                                            Defaults to Debug under debugger, Information otherwise.
                                            File log always captures down to Debug.
                          """);
        return 0;
    }

    internal static int PrintVersion()
    {
        Console.WriteLine("Harbor v0.4.0-alpha");
        Console.WriteLine($".NET {Environment.Version}");
        return 0;
    }

    /// <summary>
    ///     <c>harbor logs</c> — view/manage the per-run log files under
    ///     <c>~/.harbor/logs/</c>. Subcommands: <c>--list</c> (default),
    ///     <c>--last</c> (print the latest file), <c>--follow</c> (tail -f),
    ///     <c>--clean</c> (delete all log files).
    /// </summary>
    /// <remarks>
    ///     Retained for parity with the former <c>Program.RunLogsCommand</c>:
    ///     live dispatch reaches <c>logs</c> through the <c>ICommand</c> array
    ///     in <c>Program.Main</c>, not through this helper.
    /// </remarks>
    internal static async Task<int> RunLogsCommand(string[] args)
    {
        var cmd = new LogsCommand(Console.Out, Console.Error);
        return await cmd.ExecuteAsync(args).ConfigureAwait(false);
    }
}
