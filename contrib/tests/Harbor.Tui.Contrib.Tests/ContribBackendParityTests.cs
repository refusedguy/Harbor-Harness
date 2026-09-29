// ContribBackendParityTests.cs — issue #554: the interactive TUI exists three
// times (contrib/tui/Harbor.Tui.RazorConsole, .Termina, .TerminalGui) and the
// three copies had drifted in three user-visible ways:
//
//   A. The Ctrl+P palette escaped untrusted text in ONE backend and not in the
//      other two, so the same pasted text (a session title carrying an ANSI
//      escape, a panel id that closes a markup tag) was handled differently
//      depending on the backend the user had selected.
//   B. Termina drew the "thinking" header band above an assistant-coloured
//      body — the role was computed for the header and then discarded.
//   C. The three disagreed on whether streaming belongs in the transcript:
//      TerminalGui read the raw UiState and appended the live buffers itself,
//      the other two consumed the projected UiScreenModel (whose transcript
//      already carries the streaming tail). Two owners for the same tail.
//
// Every test here runs ONE scenario through ALL THREE backends and compares
// them against each other. A test that exercised a single backend in isolation
// would have stayed green through all three bugs — the drift was only ever
// visible in the comparison.
using System.Text;
using System.Text.RegularExpressions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Tui.RazorConsole;
using Harbor.Tui.Termina;
using Harbor.Tui.TerminalGui;
using Harbor.Ui.Framework.Projection;
using Harbor.Ui.Framework.State;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RazorChatView = Harbor.Tui.RazorConsole.Views.ChatView;
using RazorMarkdown = Harbor.Tui.RazorConsole.Rendering.RazorMarkdownRenderer;
using RazorPaletteView = Harbor.Tui.RazorConsole.Views.CommandPaletteView;
using TerminaChatView = Harbor.Tui.Termina.Views.ChatView;
using TerminaMarkdown = Harbor.Tui.Termina.Rendering.TerminaMarkdownRenderer;
using TerminaPaletteView = Harbor.Tui.Termina.Views.CommandPaletteView;
using TerminalGuiChatView = Harbor.Tui.TerminalGui.Views.ChatView;
using TerminalGuiMarkdown = Harbor.Tui.TerminalGui.Rendering.TerminalGuiMarkdownRenderer;
using TerminalGuiPaletteView = Harbor.Tui.TerminalGui.Views.CommandPaletteView;

namespace Harbor.Tui.Tests;

/// <summary>
///     Cross-backend parity gate for the three contrib TUI shells. See the file
///     header for what drifted and why these tests compare instead of isolating.
/// </summary>
public class ContribBackendParityTests
{
    private const char Esc = '\u001B';

    /// <summary>Wrap width handed to every backend's markdown renderer.</summary>
    private const int Width = 78;

    /// <summary>
    ///     A palette query / panel id / session title that tries four injections at
    ///     once: a markup tag closer (<c>]red[/</c>), a 256-colour CSI sequence, an
    ///     OSC title-set, and a bare CR to break out of the popup frame.
    /// </summary>
    private const string Hostile = "]red[/\u001B[31m\u001B]0;owned\u0007\r";

    // ── Drift A: the command palette ──────────────────────────────────────

    [Test]
    public async Task Palette_HostileRowText_Renders_Inert_In_All_Three_Backends()
    {
        // The filter query has to actually select the hostile rows, so it is a
        // benign substring of them.
        var panels = new[] { "evil" + Hostile };
        var sessions = new[] { "evil" + Hostile };
        const string query = "evil";

        await AssertInert("razorconsole", new RazorPaletteView().Build(query, panels, sessions));
        await AssertInert("termina", new TerminaPaletteView().Build(query, panels, sessions));
        await AssertInert("terminalgui", new TerminalGuiPaletteView().Build(query, panels, sessions));
    }

    [Test]
    public async Task Palette_HostileQuery_Renders_Inert_In_All_Three_Backends()
    {
        // The query line is a different code path from the row lines: it is
        // filtered on, then echoed back — so it gets its own pass.
        await AssertInert("razorconsole", new RazorPaletteView().Build(Hostile, [], []));
        await AssertInert("termina", new TerminaPaletteView().Build(Hostile, [], []));
        await AssertInert("terminalgui", new TerminalGuiPaletteView().Build(Hostile, [], []));

        // Non-vacuity: Termina really does emit ESC for its colours, so the
        // escape-stripping assertion above is doing work there.
        await Assert.That(new TerminaPaletteView().Build(Hostile, [], []).Contains(Esc))
            .IsTrue()
            .Because("Termina colourises the palette, so it must contain ESC — otherwise the inertness checks are vacuous");
    }

    [Test]
    public async Task Palette_RazorConsole_Escapes_Markup_And_The_Other_Two_Need_Not()
    {
        // RazorConsole's output IS Spectre markup, so its half of the fix is
        // bracket doubling. Termina / TerminalGui speak ANSI and plain text, so
        // once the escape sequences are gone there is nothing left to escape —
        // and the plain-text reading of all three must agree.
        string razor = new RazorPaletteView().Build("evil", ["evil" + Hostile], []);
        string termina = new TerminaPaletteView().Build("evil", ["evil" + Hostile], []);
        string terminalGui = new TerminalGuiPaletteView().Build("evil", ["evil" + Hostile], []);

        await Assert.That(razor.Contains("]]red[[/", StringComparison.Ordinal))
            .IsTrue()
            .Because("RazorConsole must double the brackets of a markup-injecting panel id");

        string expected = StripDecoration(terminalGui);
        await Assert.That(expected).Contains("]red[/")
            .Because("sanitizing must neutralize what the terminal would execute, not swallow the user's text");
        await Assert.That(StripDecoration(termina)).IsEqualTo(expected)
            .Because("the ANSI shell must show the same palette text as the plain-text shell");
        await Assert.That(StripDecoration(razor)).IsEqualTo(expected)
            .Because("the markup shell must show the same palette text as the plain-text shell");
    }

    [Test]
    public async Task Palette_BenignScenario_Renders_Identically_In_All_Three_Backends()
    {
        var panels = new[] { "help-panel", "logs" };
        var sessions = new[] { "session one", "session two" };

        string razor = StripDecoration(new RazorPaletteView().Build("s", panels, sessions));
        string termina = StripDecoration(new TerminaPaletteView().Build("s", panels, sessions));
        string terminalGui = StripDecoration(new TerminalGuiPaletteView().Build("s", panels, sessions));

        await Assert.That(termina).IsEqualTo(terminalGui)
            .Because("the Termina palette must show the same rows as the plain-text shell");
        await Assert.That(razor).IsEqualTo(terminalGui)
            .Because("the RazorConsole palette must show the same rows as the plain-text shell");
        await Assert.That(terminalGui).Contains("session: session one")
            .Because("the comparisons above are over non-empty text — an all-empty palette would pass vacuously");
    }

    // ── Drift B: the thinking header / body role pair ─────────────────────

    [Test]
    public async Task ChatView_ThinkingBlock_Uses_ThinkingRole_For_Body_In_All_Three_Backends()
    {
        const string thinking = "- private deliberation";
        const string answer = "- public answer";
        UiScreenModel screen = ProjectStreaming(thinking, answer);

        IReadOnlyList<string> razor = new RazorChatView().Build(screen, Width);
        IReadOnlyList<string> termina = new TerminaChatView().Build(screen, Width);
        IReadOnlyList<string> terminalGui = new TerminalGuiChatView().Build(screen, Width);

        // Layout every backend builds: header, body, spacer — twice (thinking
        // tail, then text tail). Asserted so an index shift fails loudly instead
        // of silently comparing the wrong line.
        await Assert.That(Plain(razor[0])).IsEqualTo(Plain(terminalGui[0])).Because("header 0 differs");
        await Assert.That(Plain(razor[2])).IsEqualTo(Plain(terminalGui[2])).Because("spacer 2 differs");
        await Assert.That(Plain(razor[0])).Contains("thinking")
            .Because("the streaming thinking tail must get a thinking header band");

        // The body under that band is rendered with THAT role, not with
        // ChatRole.Assistant. Markdown is the tell: SupportsMarkdown is true for
        // Assistant and false for Thinking, so a role mix-up turns "- text" into
        // "• text" — observable in all three dialects.
        await Assert.That(Body(razor, 1)).IsEqualTo(RazorMarkdown.RenderBody(ChatRole.Thinking, thinking, Width)[0])
            .Because("RazorConsole must render the thinking body with the thinking role");
        await Assert.That(Body(termina, 1)).IsEqualTo(TerminaMarkdown.RenderBody(ChatRole.Thinking, thinking, Width)[0])
            .Because("Termina rendered the thinking body with ChatRole.Assistant (issue #554 drift B)");
        await Assert.That(Body(terminalGui, 1)).IsEqualTo(TerminalGuiMarkdown.RenderBody(ChatRole.Thinking, thinking, Width)[0])
            .Because("TerminalGui must render the thinking body with the thinking role");

        // Cross-backend: same visible text for the same block in all three, and
        // no backend markdown-processed it.
        await Assert.That(Plain(Body(razor, 1))).IsEqualTo(Plain(Body(termina, 1)))
            .Because("the shells disagree on the thinking body text");
        await Assert.That(Plain(Body(razor, 1))).IsEqualTo(Plain(Body(terminalGui, 1)))
            .Because("the shells disagree on the thinking body text");
        await Assert.That(Plain(Body(razor, 1)).Contains('•'))
            .IsFalse()
            .Because("markdown must not be applied to a thinking body in any backend");

        // The assistant block still gets markdown everywhere, so the assertion
        // above is discriminating rather than vacuous.
        await Assert.That(Plain(Body(razor, 4))).Contains("• public answer");
        await Assert.That(Plain(Body(termina, 4))).Contains("• public answer");
        await Assert.That(Plain(Body(terminalGui, 4))).Contains("• public answer");
    }

    // ── Drift C: streaming in the transcript ─────────────────────────────

    [Test]
    public async Task ChatView_StreamingTail_Is_Rendered_Exactly_Once_By_All_Three_Backends()
    {
        const string thinking = "streaming thought";
        const string answer = "streaming answer";
        UiScreenModel screen = ProjectStreaming(thinking, answer);

        string razor = string.Join('\n', new RazorChatView().Build(screen, Width));
        string termina = string.Join('\n', new TerminaChatView().Build(screen, Width));
        string terminalGui = string.Join('\n', new TerminalGuiChatView().Build(screen, Width));

        await Assert.That(CountOccurrences(razor, thinking)).IsEqualTo(1)
            .Because("the projected transcript already carries the streaming tail; a view that appends it again duplicates it");
        await Assert.That(CountOccurrences(termina, thinking)).IsEqualTo(1)
            .Because("the projected transcript already carries the streaming tail; a view that appends it again duplicates it");
        await Assert.That(CountOccurrences(terminalGui, thinking)).IsEqualTo(1)
            .Because("TerminalGui used to read the raw UiState and append the live buffers a second time (issue #554 drift C)");

        await Assert.That(CountOccurrences(razor, answer)).IsEqualTo(1);
        await Assert.That(CountOccurrences(termina, answer)).IsEqualTo(1);
        await Assert.That(CountOccurrences(terminalGui, answer)).IsEqualTo(1);

        await Assert.That(new RazorChatView().Build(screen, Width).Count)
            .IsEqualTo(new TerminalGuiChatView().Build(screen, Width).Count)
            .Because("the shells disagree on the transcript line count for the same screen");
    }

    [Test]
    public async Task ChatView_Build_Has_One_Signature_Across_The_Trio()
    {
        Type[] trio = [typeof(RazorChatView), typeof(TerminaChatView), typeof(TerminalGuiChatView)];

        // Non-vacuity: a guard that compared one type against itself would pass
        // forever.
        await Assert.That(trio.Distinct().Count()).IsEqualTo(3);

        string[] shapes = [.. trio.Select(DescribeBuild)];
        foreach (string shape in shapes)
        {
            await Assert.That(shape).IsEqualTo(shapes[0])
                .Because("ChatView.Build must have one signature across the trio (issue #554 drift C)");
        }
    }

    // ── the shared console-key router (#554 "cheap half") ─────────────────

    [Test]
    public async Task KeyHandler_Routes_The_Same_Key_Identically_In_All_Three_Bridges()
    {
        (string Name, ConsoleKeyInfo Key)[] battery =
        [
            ("char h", new ConsoleKeyInfo('h', ConsoleKey.H, false, false, false)),
            ("ctrl+l", new ConsoleKeyInfo('l', ConsoleKey.L, false, false, true)),
            ("ctrl+c", new ConsoleKeyInfo('\u0003', ConsoleKey.C, false, false, true)),
            ("escape", new ConsoleKeyInfo('\u001B', ConsoleKey.Escape, false, false, false)),
            ("lf alias", new ConsoleKeyInfo('\n', ConsoleKey.J, false, false, true)),
            ("up", new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, false, false, false)),
            ("alt+up", new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, false, false, true)),
            ("f12", new ConsoleKeyInfo('\0', ConsoleKey.F12, false, false, false)),
        ];

        foreach ((string name, ConsoleKeyInfo key) in battery)
        {
            string razor = Route(new RazorConsoleTeaBridge(MockAgent(), null, NullLogger.Instance), key);
            string termina = Route(new TerminaTeaBridge(MockAgent(), null, NullLogger.Instance), key);
            string terminalGui = Route(new TerminalGuiTeaBridge(MockAgent(), null, NullLogger.Instance), key);

            await Assert.That(razor).IsEqualTo(termina)
                .Because($"the RazorConsole and Termina shells route '{name}' differently");
            await Assert.That(razor).IsEqualTo(terminalGui)
                .Because($"the RazorConsole and TerminalGui shells route '{name}' differently");
        }

        // Non-vacuity: the battery really does move state through the router.
        var live = new TerminaTeaBridge(MockAgent(), null, NullLogger.Instance);
        Route(live, new ConsoleKeyInfo('h', ConsoleKey.H, false, false, false));
        await Assert.That(live.Store.State.Ui.Input.Text).IsEqualTo("h")
            .Because("a printable key must reach the composer in all three shells");
    }

    // ── fixtures ──────────────────────────────────────────────────────────

    /// <summary>
    ///     Shared inertness contract: nothing from the payload that a terminal
    ///     would execute survives into the rendered palette, in any of the three
    ///     dialects, and the printable part of the payload is still visible.
    /// </summary>
    private static async Task AssertInert(string backend, string output)
    {
        await Assert.That(output.Contains($"{Esc}]0;owned", StringComparison.Ordinal))
            .IsFalse()
            .Because($"{backend} leaked the OSC title-set from untrusted palette text");
        await Assert.That(output.Contains($"{Esc}[31m", StringComparison.Ordinal))
            .IsFalse()
            .Because($"{backend} leaked the CSI colour sequence from untrusted palette text");
        await Assert.That(output.Contains('\r', StringComparison.Ordinal))
            .IsFalse()
            .Because($"{backend} leaked a CR from untrusted palette text");
        await Assert.That(StripDecoration(output).Contains("]red[/", StringComparison.Ordinal))
            .IsTrue()
            .Because($"{backend} swallowed the printable part of the payload instead of neutralizing it");
    }

    private static string Route(RazorConsoleTeaBridge bridge, ConsoleKeyInfo key) =>
        Describe(bridge.HandleKey(key), bridge.Store);

    private static string Route(TerminaTeaBridge bridge, ConsoleKeyInfo key) =>
        Describe(bridge.HandleKey(key), bridge.Store);

    private static string Route(TerminalGuiTeaBridge bridge, ConsoleKeyInfo key) =>
        Describe(bridge.HandleKey(key), bridge.Store);

    /// <summary>
    ///     Observable routing result of one keypress: the effect the shell
    ///     returned plus the store fields a key can move. Compared as one string
    ///     because <c>UiState</c> holds <c>ImmutableArray</c>s, whose record
    ///     equality is reference-based and would report a false difference.
    /// </summary>
    private static string Describe(TuiEffect effect, UiStore store) =>
        $"effect={effect.GetType().Name};input='{store.State.Ui.Input.Text}';"
        + $"scroll={store.State.Ui.ScrollOffset};quit={store.State.Ui.ShouldQuit}";

    private static string DescribeBuild(Type view)
    {
        System.Reflection.MethodInfo? build = view.GetMethod("Build");
        if (build is null)
        {
            return "<no Build>";
        }

        return string.Join(", ", build.GetParameters().Select(p => p.ParameterType.Name));
    }

    private static IAgent MockAgent()
    {
        var definition = AgentDefinition.CodeDefault("test-model", "anthropic");
        var state = AgentState.Idle("s1", definition);
        var mock = new Mock<IAgent>();
        mock.SetupGet(a => a.State).Returns(state);
        mock.SetupGet(a => a.AbortToken).Returns(new CancellationTokenSource().Token);
        return mock.Object;
    }

    /// <summary>
    ///     A live streaming turn: a thinking tail and a text tail, both with
    ///     markdown-flavoured content so the role each body is rendered with is
    ///     observable in every backend.
    /// </summary>
    private static UiScreenModel ProjectStreaming(string thinking, string answer)
    {
        var bridge = new TerminaTeaBridge(MockAgent(), null, NullLogger.Instance);
        var message = AssistantMessage.Empty("s1", "m");
        bridge.Push(new MessageStartEvent(message));
        bridge.Push(new MessageUpdateEvent(new ThinkingDeltaEvent("0", thinking), message));
        bridge.Push(new MessageUpdateEvent(new TextDeltaEvent("0", answer), message));
        return new DefaultUiProjector().Project(bridge.Store.State);
    }

    /// <summary>
    ///     One rendered transcript line, whitespace-normalised: Termina and
    ///     TerminalGui indent bodies by two spaces, RazorConsole does not, and
    ///     that indent is not one of the three documented divergences.
    /// </summary>
    private static string Body(IReadOnlyList<string> lines, int index) => lines[index].Trim();

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static readonly Regex AnsiPattern = new("\\u001B\\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MarkupPattern = new("\\[/?[a-z][a-z ]*\\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    ///     The three backends speak three output dialects — Spectre markup, raw
    ///     ANSI, and plain Unicode — so "do they agree" is asked about the
    ///     VISIBLE text: drop ANSI sequences and markup tags, un-double the
    ///     escaped brackets, normalise whitespace. Only used on payloads whose
    ///     printable text has no markup metacharacters of its own, where the
    ///     un-doubling is unambiguous.
    /// </summary>
    private static string StripDecoration(string output)
    {
        string stripped = AnsiPattern.Replace(output, string.Empty);
        stripped = MarkupPattern.Replace(stripped, string.Empty);
        stripped = stripped.Replace("[[", "[", StringComparison.Ordinal).Replace("]]", "]", StringComparison.Ordinal);

        var sb = new StringBuilder(stripped.Length);
        string[] lines = stripped.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                sb.Append('\n');
            }

            sb.Append(lines[i].Trim());
        }

        return sb.ToString();
    }

    /// <summary>Visible text of one rendered line, for cross-backend comparison.</summary>
    private static string Plain(string line) => StripDecoration(line);
}
