#if HARBOR_WITH_DSL_DEV
using System.IO;
using ConsoleAppFramework;
using Harbor.Tui.CellForge.CodeGen;
using Harbor.Tui.CellForge.CodeGen.Automation;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     #2438: headless automation protocol over SSE <c>data:</c> lines. The
///     live tree is the parsed <c>.harbor-ui</c> tree itself: the same arena
///     the generated BuildRoot consumes, so there is no test double behind
///     the getTree, clock and testId methods.
/// </summary>
/// <remarks>
///     Dev-only. The whole file is behind <c>HARBOR_WITH_DSL_DEV</c>, which
///     an AOT publish switches off together with the reference to the parser
///     assembly. None of this reaches the AOT binary.
/// </remarks>
internal static class DslAutomationCommands
{
    /// <summary>
    ///     Runs the versioned automation protocol for one markup file. Without
    ///     --request, JSON commands are read from stdin as <c>data:</c> lines
    ///     and answers are written to stdout as <c>data:</c> lines; other
    ///     lines are ignored so logs cannot leak into the stream. With
    ///     --request, one command is answered and the process exits.
    /// </summary>
    /// <param name="file">Path to a .harbor-ui file.</param>
    /// <param name="request">Optional single JSON request.</param>
    public static async Task<int> Automation([Argument] string file, string? request = null)
    {
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
        {
            Console.Error.WriteLine("dsl automation: a .harbor-ui file is required.");
            return 1;
        }

        var parsed = MarkupParser.Parse(File.ReadAllText(file));
        if (!parsed.IsValid)
        {
            foreach (var diagnostic in parsed.Diagnostics)
                Console.Error.WriteLine($"{file}({diagnostic.Line},{diagnostic.Column}): {diagnostic.Message}");
            return 1;
        }

        var session = new AutomationSession(HeadlessTree.Build(parsed.Arena));
        if (request is not null)
        {
            string response = session.ExecuteJson(request);
            Console.Out.Write("data: ");
            Console.Out.Write(response);
            Console.Out.Write('\n');
            return response.Contains("\"error\"", StringComparison.Ordinal) ? 1 : 0;
        }

        var transport = new AutomationTransport(session, Console.In, Console.Out);
        await transport.RunAsync().ConfigureAwait(false);
        return 0;
    }
}
#endif
