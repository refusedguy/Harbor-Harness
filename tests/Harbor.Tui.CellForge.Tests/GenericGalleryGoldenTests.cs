using Harbor.TestKit;
using Harbor.Tui.CellForge.Generic;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// TR9 (#1228, epic #1195): gallery goldens over GenericGallery — the
// composability proof. Each entry paints at its fixed geometry and the
// rtrimmed grid art is pinned in tests/fixtures/generic-layout/<name>.golden.txt.
// Art is style-blind on purpose (styles stay pinned by the widget unit
// tests); what the goldens pin is COMPOSITION — splits, titles, focus
// markers, scripted state.
//
// Regeneration contract (mirrors Golden): run once with
// HARBOR_UPDATE_GOLDENS=1 to (re)write fixtures; CI never regenerates.
// Fixtures carry no trailing spaces, so an editorconfig trim cannot drift them.
//
// Rendering reads global TerminalColorPalette — serialized vs theme tests.
// The engine→shared key mapper below is test-local: VirtualInputSource
// speaks the engine leaf vocabulary, GenericScreen the shared one, and no
// product conversion axis exists between them by design.
[NotInParallel("pty")]
public class GenericGalleryGoldenTests
{
    /// <summary>Shared fixture directory (generic-layout goldens).</summary>
    private static string FixtureDir() =>
        Path.Combine(RepoPaths.Root, "tests", "fixtures", "generic-layout");

    private static string Normalize(string s) =>
        s.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);

    private static string[] ArtLines(ScreenBuffer buffer)
    {
        string[] split = TestRender.Art(buffer).Split('\n');
        var lines = new string[buffer.Rows];
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = (i < split.Length ? split[i] : string.Empty).TrimEnd();
        }

        return lines;
    }

    private static string[] PaintLines(GenericGalleryScreen entry, Action<EngineInput.VirtualInputSource>? script = null)
    {
        GenericScreen screen = entry.Build();
        if (script is not null)
        {
            var input = new EngineInput.VirtualInputSource();
            script(input);
            input.DrainTo(evt =>
            {
                if (evt.Kind == EngineInput.InputEventKind.Key)
                {
                    _ = screen.HandleKey(ToShared(evt.Key));
                }
            });
        }

        var buffer = TestRender.Buffer(entry.Cols, entry.Rows);
        screen.Solve(entry.Cols, entry.Rows);
        screen.PaintAll(buffer);
        return ArtLines(buffer);
    }

    private static void Verify(GenericGalleryScreen entry, Action<EngineInput.VirtualInputSource>? script = null)
    {
        string[] actual = PaintLines(entry, script);
        string header = $"# golden: {entry.Name} ({entry.Cols}x{entry.Rows})";
        string path = Path.Combine(FixtureDir(), entry.Name + ".golden.txt");
        if (Environment.GetEnvironmentVariable("HARBOR_UPDATE_GOLDENS") == "1")
        {
            Directory.CreateDirectory(FixtureDir());
            File.WriteAllText(path, header + "\n" + string.Join("\n", actual) + "\n");
            return;
        }

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"golden fixture missing: {path} (run once with HARBOR_UPDATE_GOLDENS=1 to seed it)");
        }

        string[] parts = Normalize(File.ReadAllText(path)).Split('\n');
        if (parts.Length > 0 && parts[^1] == string.Empty)
        {
            Array.Resize(ref parts, parts.Length - 1);
        }

        if (parts.Length == 0 || parts[0] != header)
        {
            throw new InvalidOperationException($"golden {entry.Name}: header mismatch (expected '{header}')");
        }

        string[] expected = parts[1..];
        if (expected.Length != entry.Rows)
        {
            throw new InvalidOperationException(
                $"golden {entry.Name}: expected {entry.Rows} art rows, fixture has {expected.Length}");
        }

        for (int i = 0; i < expected.Length; i++)
        {
            if (expected[i] != actual[i])
            {
                throw new InvalidOperationException(
                    $"golden {entry.Name} row {i}: expected '{expected[i]}', painted '{actual[i]}'");
            }
        }
    }

    /// <summary>Engine leaf key → shared key (test-local; no product axis).</summary>
    private static KeyEvent ToShared(EngineInput.KeyEvent key)
    {
        KeyModifiers mods = KeyModifiers.None;
        if ((key.Modifiers & EngineInput.KeyModifiers.Shift) != 0)
        {
            mods |= KeyModifiers.Shift;
        }

        if ((key.Modifiers & EngineInput.KeyModifiers.Ctrl) != 0)
        {
            mods |= KeyModifiers.Ctrl;
        }

        if ((key.Modifiers & EngineInput.KeyModifiers.Alt) != 0)
        {
            mods |= KeyModifiers.Alt;
        }

        if ((key.Modifiers & EngineInput.KeyModifiers.Meta) != 0)
        {
            mods |= KeyModifiers.Meta;
        }

        KeyEventType type = key.EventType switch
        {
            EngineInput.KeyEventType.Release => KeyEventType.Release,
            EngineInput.KeyEventType.Repeat => KeyEventType.Repeat,
            _ => KeyEventType.Press,
        };
        bool kitty = key.IsKittyEncoded;

        if (key.Key == EngineInput.KeyCode.Char)
        {
            return new KeyEvent(KeyCode.Char, key.Character, mods, type, kitty, key.Codepoint);
        }

        KeyCode code = key.Key switch
        {
            EngineInput.KeyCode.Enter => KeyCode.Enter,
            EngineInput.KeyCode.Tab => KeyCode.Tab,
            EngineInput.KeyCode.Backspace => KeyCode.Backspace,
            EngineInput.KeyCode.Escape => KeyCode.Escape,
            EngineInput.KeyCode.Up => KeyCode.Up,
            EngineInput.KeyCode.Down => KeyCode.Down,
            EngineInput.KeyCode.Left => KeyCode.Left,
            EngineInput.KeyCode.Right => KeyCode.Right,
            EngineInput.KeyCode.Home => KeyCode.Home,
            EngineInput.KeyCode.End => KeyCode.End,
            EngineInput.KeyCode.PageUp => KeyCode.PageUp,
            EngineInput.KeyCode.PageDown => KeyCode.PageDown,
            EngineInput.KeyCode.Insert => KeyCode.Insert,
            EngineInput.KeyCode.Delete => KeyCode.Delete,
            _ => KeyCode.Unknown,
        };
        return new KeyEvent(code, default, mods, type, kitty, key.Codepoint);
    }

    private static GenericGalleryScreen Entry(string name)
    {
        foreach (var entry in GenericGallery.All())
        {
            if (entry.Name == name)
            {
                return entry;
            }
        }

        throw new InvalidOperationException($"gallery entry missing: {name}");
    }

    [Test]
    public async Task Gallery_Has_Ten_Unique_Entries()
    {
        var all = GenericGallery.All();
        await Assert.That(all.Count).IsEqualTo(10);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in all)
        {
            await Assert.That(names.Add(entry.Name)).IsTrue();
            await Assert.That(entry.Cols).IsGreaterThan(0);
            await Assert.That(entry.Rows).IsGreaterThan(0);
        }
    }

    // #1770: the scripted-free gallery goldens used to be one copy-pasted
    // method each. The assert never changed — only the entry name did. Every
    // row keeps its original test name as the case display name. The scripted
    // entries keep their own methods: a script is not an attribute constant.
    [Test]
    [Arguments("single-static", DisplayName = "SingleStatic_Golden")]
    [Arguments("two-pane-stack", DisplayName = "TwoPaneStack_Golden")]
    [Arguments("two-pane-side", DisplayName = "TwoPaneSide_Golden")]
    [Arguments("field-empty", DisplayName = "FieldEmpty_Golden")]
    [Arguments("field-filled", DisplayName = "FieldFilled_Golden")]
    [Arguments("list-basic", DisplayName = "ListBasic_Golden")]
    [Arguments("three-pane", DisplayName = "ThreePane_Golden")]
    public async Task GalleryEntry_Golden(string entryName)
    {
        Verify(Entry(entryName));
        await Task.CompletedTask;
    }

    [Test]
    public async Task FieldTyped_Golden()
    {
        // Scripted proof: virtual typing reaches the hosted field.
        Verify(Entry("field-typed"), input => input.SendText("hi"));
        await Task.CompletedTask;
    }

    [Test]
    public async Task ListDriven_Golden()
    {
        // Scripted proof: virtual arrows move the list selection.
        Verify(Entry("list-driven"), input =>
        {
            input.SendSimple(EngineInput.KeyCode.Down);
            input.SendSimple(EngineInput.KeyCode.Down);
        });
        await Task.CompletedTask;
    }

    [Test]
    public async Task FocusCycle_Golden()
    {
        // Scripted proof: virtual Tab moves the focus marker across panes.
        Verify(Entry("focus-cycle"), input => input.SendSimple(EngineInput.KeyCode.Tab));
        await Task.CompletedTask;
    }
}
