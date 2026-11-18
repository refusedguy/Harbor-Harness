using Harbor.App.Cli.Repl;
using Harbor.Tui.CellForge.Input;

namespace Harbor.App.Cli.Tests;

/// <summary>
///     #2495: editor selection and the suspend/restore contract, without a
///     real process or tty. The environment accessor and the child launcher
///     are seams on <see cref="ExternalEditorScope"/>, so every assertion here
///     is on pure decisions and callback order.
/// </summary>
public class ExternalEditorScopeTests
{
    private static Func<string?, string?> Env(params (string Name, string? Value)[] vars) =>
        name => Array.Find(vars, v => v.Name == name).Value;

    [Test]
    public async Task ResolveEditor_VisualWinsOverEditor()
    {
        string selected = ExternalEditorScope.ResolveEditor(Env(
            ("VISUAL", "nvim -f"),
            ("EDITOR", "emacs -nw")));

        await Assert.That(selected).IsEqualTo("nvim -f")
            .Because("$VISUAL is the preferred editor, even when $EDITOR is set.");
    }

    [Test]
    public async Task ResolveEditor_EditorUsedWhenVisualBlank()
    {
        foreach (string? blank in new string?[] { null, "", "   " })
        {
            string selected = ExternalEditorScope.ResolveEditor(Env(
                ("VISUAL", blank),
                ("EDITOR", "emacs -nw")));

            await Assert.That(selected).IsEqualTo("emacs -nw")
                .Because("blank $VISUAL must fall through to $EDITOR.");
        }
    }

    [Test]
    public async Task ResolveEditor_FallbackWhenBothUnset()
    {
        string selected = ExternalEditorScope.ResolveEditor(Env(
            ("VISUAL", null),
            ("EDITOR", "  ")));

        await Assert.That(selected).IsEqualTo(ExternalEditorScope.FallbackEditor)
            .Because("a fresh install with no editor variables still has a usable default.");
    }

    [Test]
    public async Task RunAsync_SuspendsBeforeLaunch_AndRestoresWithRepaintAfter()
    {
        var order = new List<string>();
        var mode = new FakeMode(order);
        var scope = new ExternalEditorScope(
            mode,
            leaveScreen: _ => Record(order, "leave"),
            enterScreenAndRepaint: _ => Record(order, "repaint"),
            getEnv: Env(("VISUAL", "nvim -f"), ("EDITOR", "vi")),
            stdinRedirected: () => false,
            launch: editor =>
            {
                order.Add("launch:" + editor);
                return 7;
            });

        int? exitCode = await scope.RunAsync();

        await Assert.That(exitCode.HasValue).IsTrue();
        await Assert.That(exitCode!.Value).IsEqualTo(7);
        await Assert.That(order).IsEquivalentTo(new[] { "leave", "mode:restore", "launch:nvim -f", "mode:enter", "repaint" });
    }

    [Test]
    public async Task RunAsync_RestoresWithRepaint_EvenWhenLaunchThrows()
    {
        var order = new List<string>();
        var mode = new FakeMode(order);
        var scope = new ExternalEditorScope(
            mode,
            leaveScreen: _ => Record(order, "leave"),
            enterScreenAndRepaint: _ => Record(order, "repaint"),
            getEnv: Env(("VISUAL", "nvim"), ("EDITOR", "vi")),
            stdinRedirected: () => false,
            launch: _ => throw new InvalidOperationException("child failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await scope.RunAsync());

        await Assert.That(order).IsEquivalentTo(new[] { "leave", "mode:restore", "mode:enter", "repaint" })
            .Because("a child failure must not leave the terminal suspended or the screen stale.");
    }

    [Test]
    public async Task RunAsync_RedirectedStdin_NeitherSuspendsNorLaunches()
    {
        var order = new List<string>();
        var mode = new FakeMode(order);
        var scope = new ExternalEditorScope(
            mode,
            leaveScreen: _ => Record(order, "leave"),
            enterScreenAndRepaint: _ => Record(order, "repaint"),
            getEnv: Env(("VISUAL", "nvim"), ("EDITOR", "vi")),
            stdinRedirected: () => true,
            launch: editor =>
            {
                order.Add("launch:" + editor);
                return 0;
            });

        int? exitCode = await scope.RunAsync();

        await Assert.That(exitCode).IsNull();
        await Assert.That(order).IsEmpty()
            .Because("a hand-over with redirected stdin is rejected before touching the terminal.");
    }

    private static ValueTask Record(List<string> order, string entry)
    {
        order.Add(entry);
        return ValueTask.CompletedTask;
    }

    private sealed class FakeMode(List<string> order) : ITerminalModeController
    {
        public bool IsRaw { get; private set; }

        public void Enter()
        {
            IsRaw = true;
            order.Add("mode:enter");
        }

        public void Restore()
        {
            IsRaw = false;
            order.Add("mode:restore");
        }
    }
}
