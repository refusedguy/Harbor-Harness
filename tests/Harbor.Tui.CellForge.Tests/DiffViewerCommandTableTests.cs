using System.Text;
using Harbor.Tui.CellForge.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// #1544: the diff viewer's three key dispatches (the legacy named-key
/// switch, its rune switch and the kitty pair) are ONE command table now.
/// The table is internal — <c>Harbor.Tui.CellForge</c> grants this project
/// InternalsVisibleTo for exactly this: the rows ARE the spec, and a
/// behavioural test alone cannot see a row that no key ever reaches.
/// </summary>
/// <remarks>
/// What this file holds is the property the old switches could not express:
/// the legacy console key and the kitty key resolve the SAME table row, so
/// there is nothing left to drift. Every gesture is therefore probed twice —
/// once per encoding — and the two must agree on the handled flag and on
/// every observable of the viewer, in every layout mode.
/// </remarks>
public sealed class DiffViewerCommandTableTests
{
    /// <summary>
    /// Five hunks, so 'next'/'previous'/'first'/'last' and the page keys all
    /// have somewhere to move. Painted into a 20x6 viewport, which gives the
    /// body two rows — small enough that a page scroll is observable.
    /// </summary>
    private const string Sample = """
        @@ -1,3 +1,3 @@
         context-a
        -oldA
        +newB
        @@ -10,3 +10,3 @@
         context-b
        -oldC
        +newD
        @@ -20,3 +20,3 @@
         context-e
        -oldF
        +newG
        @@ -30,3 +30,3 @@
         context-h
        -oldI
        +newJ
        @@ -40,3 +40,3 @@
         context-k
        -oldL
        +newM
        """;

    private const int LastHunk = 4;

    /// <summary>What a gesture did to the viewer — every observable of it.</summary>
    private readonly record struct Snapshot(
        bool Visible,
        int SelectedHunk,
        DiffViewerMode Mode,
        bool Wrap,
        int Scroll);

    /// <summary>
    /// One gesture in BOTH encodings, with the state it must produce. The
    /// preparation moves the viewer AWAY from the expected state, so a key
    /// that did nothing cannot pass.
    /// </summary>
    private sealed record GestureCase(
        string Name,
        ConsoleKeyInfo Legacy,
        KeyEvent Kitty,
        Action<DiffViewerOverlay>? Prepare,
        Snapshot Expected);

    private static DiffViewerOverlay Shown(DiffViewerMode mode = DiffViewerMode.Unified, bool wrap = false)
    {
        var overlay = new DiffViewerOverlay();
        overlay.Show("src/app.cs", Sample);
        if (mode == DiffViewerMode.Split)
        {
            overlay.ToggleMode();
        }

        if (wrap)
        {
            overlay.ToggleWrap();
        }

        Paint(overlay);
        return overlay;
    }

    /// <summary>
    /// Paints once so the body height and the row count are known: the page
    /// keys scroll by the body height, and the scroll is clamped to the rows
    /// that exist.
    /// </summary>
    private static void Paint(DiffViewerOverlay overlay)
    {
        var buffer = new ScreenBuffer(20, 6);
        overlay.Paint(buffer, new Rect(0, 0, 20, 6));
    }

    private static Snapshot Snap(DiffViewerOverlay overlay) => new(
        overlay.Visible,
        overlay.SelectedHunk,
        overlay.Mode,
        overlay.Wrap,
        overlay.ScrollOffset);

    /// <summary>
    /// Every gesture the table binds, in both encodings. The legacy encoding
    /// is what <c>Console.ReadKey</c> produces; the kitty one is what a
    /// kitty-capable host produces, with the capital arriving as the capital
    /// rune PLUS Shift.
    /// </summary>
    private static GestureCase[] Cases() =>
    [
        new(
            "escape",
            new ConsoleKeyInfo('\x1b', ConsoleKey.Escape, false, false, false),
            KeyEvent.Simple(KeyCode.Escape),
            null,
            new Snapshot(false, 0, DiffViewerMode.Unified, false, 0)),
        new(
            "up",
            new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, false, false, false),
            KeyEvent.Simple(KeyCode.Up),
            o => o.NextHunk(),
            new Snapshot(true, 0, DiffViewerMode.Unified, false, 0)),
        new(
            "down",
            new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, false),
            KeyEvent.Simple(KeyCode.Down),
            null,
            new Snapshot(true, 1, DiffViewerMode.Unified, false, 0)),
        new(
            "page_up",
            new ConsoleKeyInfo('\0', ConsoleKey.PageUp, false, false, false),
            KeyEvent.Simple(KeyCode.PageUp),
            o => o.ScrollBy(2),
            new Snapshot(true, 0, DiffViewerMode.Unified, false, 0)),
        new(
            "page_down",
            new ConsoleKeyInfo('\0', ConsoleKey.PageDown, false, false, false),
            KeyEvent.Simple(KeyCode.PageDown),
            null,
            new Snapshot(true, 0, DiffViewerMode.Unified, false, 2)),
        new(
            "home",
            new ConsoleKeyInfo('\0', ConsoleKey.Home, false, false, false),
            KeyEvent.Simple(KeyCode.Home),
            o => o.NextHunk(),
            new Snapshot(true, 0, DiffViewerMode.Unified, false, 0)),
        new(
            "end",
            new ConsoleKeyInfo('\0', ConsoleKey.End, false, false, false),
            KeyEvent.Simple(KeyCode.End),
            null,
            new Snapshot(true, LastHunk, DiffViewerMode.Unified, false, 0)),
        new(
            "q",
            new ConsoleKeyInfo('q', ConsoleKey.Q, false, false, false),
            KeyEvent.Char(new Rune('q')),
            null,
            new Snapshot(false, 0, DiffViewerMode.Unified, false, 0)),
        new(
            "n",
            new ConsoleKeyInfo('n', ConsoleKey.N, false, false, false),
            KeyEvent.Char(new Rune('n')),
            null,
            new Snapshot(true, 1, DiffViewerMode.Unified, false, 0)),
        new(
            "j",
            new ConsoleKeyInfo('j', ConsoleKey.J, false, false, false),
            KeyEvent.Char(new Rune('j')),
            null,
            new Snapshot(true, 1, DiffViewerMode.Unified, false, 0)),
        new(
            "p",
            new ConsoleKeyInfo('p', ConsoleKey.P, false, false, false),
            KeyEvent.Char(new Rune('p')),
            o => o.NextHunk(),
            new Snapshot(true, 0, DiffViewerMode.Unified, false, 0)),
        new(
            "k",
            new ConsoleKeyInfo('k', ConsoleKey.K, false, false, false),
            KeyEvent.Char(new Rune('k')),
            o => o.NextHunk(),
            new Snapshot(true, 0, DiffViewerMode.Unified, false, 0)),
        new(
            "s",
            new ConsoleKeyInfo('s', ConsoleKey.S, false, false, false),
            KeyEvent.Char(new Rune('s')),
            null,
            new Snapshot(true, 0, DiffViewerMode.Split, false, 0)),
        new(
            "t",
            new ConsoleKeyInfo('t', ConsoleKey.T, false, false, false),
            KeyEvent.Char(new Rune('t')),
            null,
            new Snapshot(true, 0, DiffViewerMode.Split, false, 0)),
        new(
            "w",
            new ConsoleKeyInfo('w', ConsoleKey.W, false, false, false),
            KeyEvent.Char(new Rune('w')),
            null,
            new Snapshot(true, 0, DiffViewerMode.Unified, true, 0)),
        new(
            "g",
            new ConsoleKeyInfo('g', ConsoleKey.G, false, false, false),
            KeyEvent.Char(new Rune('g')),
            o => o.NextHunk(),
            new Snapshot(true, 0, DiffViewerMode.Unified, false, 0)),
        new(
            "G",
            new ConsoleKeyInfo('G', ConsoleKey.G, true, false, false),
            KeyEvent.Char(new Rune('G'), KeyModifiers.Shift),
            null,
            new Snapshot(true, LastHunk, DiffViewerMode.Unified, false, 0)),
    ];

    // ------------------------------------------------------------- behaviour --

    [Test]
    public async Task EveryGesture_DoesWhatTheTableSays_OnBothEncodings()
    {
        List<string> breaks = [];

        foreach (GestureCase gesture in Cases())
        {
            foreach (bool kitty in new[] { false, true })
            {
                var overlay = Shown();
                gesture.Prepare?.Invoke(overlay);

                bool handled = kitty
                    ? overlay.HandleKey(gesture.Kitty)
                    : overlay.HandleKey(gesture.Legacy);

                Snapshot actual = Snap(overlay);
                if (!handled || !actual.Equals(gesture.Expected))
                {
                    breaks.Add($"{gesture.Name}/{(kitty ? "kitty" : "legacy")}: handled={handled} {actual}");
                }
            }
        }

        await Assert.That(string.Join(" | ", breaks)).IsEqualTo(string.Empty)
            .Because("the table is the one place that says what a key does, and both encodings must obey it");
    }

    [Test]
    public async Task TheTwoEncodings_Agree_OnEveryGesture_InEveryMode()
    {
        List<string> breaks = [];

        foreach (GestureCase gesture in Cases())
        {
            foreach (DiffViewerMode mode in new[] { DiffViewerMode.Unified, DiffViewerMode.Split })
            {
                foreach (bool wrap in new[] { false, true })
                {
                    var legacy = Shown(mode, wrap);
                    var kitty = Shown(mode, wrap);
                    gesture.Prepare?.Invoke(legacy);
                    gesture.Prepare?.Invoke(kitty);

                    bool legacyHandled = legacy.HandleKey(gesture.Legacy);
                    bool kittyHandled = kitty.HandleKey(gesture.Kitty);

                    string broken = Break(
                        gesture.Name,
                        $"{mode}/{(wrap ? "wrap" : "nowrap")}",
                        Snap(legacy),
                        legacyHandled,
                        Snap(kitty),
                        kittyHandled);
                    if (broken.Length > 0)
                    {
                        breaks.Add(broken);
                    }
                }
            }
        }

        await Assert.That(string.Join(" | ", breaks)).IsEqualTo(string.Empty)
            .Because("the legacy console key and the kitty key resolve the SAME table row, so there is "
                + "nothing left for them to drift apart on");
    }

    /// <summary>
    /// The one property the old hand-written switches could not express: the
    /// runes are the SAME command behind a second gesture, not a second
    /// implementation of it.
    /// </summary>
    [Test]
    public async Task TheRows_ShareTheirCommandObjects()
    {
        await Assert.That(ReferenceEquals(CommandFor(KeyCode.Char, 'n'), CommandFor(KeyCode.Down))).IsTrue()
            .Because("'n' is the Down move behind a second gesture");
        await Assert.That(ReferenceEquals(CommandFor(KeyCode.Char, 'j'), CommandFor(KeyCode.Down))).IsTrue();
        await Assert.That(ReferenceEquals(CommandFor(KeyCode.Char, 'p'), CommandFor(KeyCode.Up))).IsTrue();
        await Assert.That(ReferenceEquals(CommandFor(KeyCode.Char, 'k'), CommandFor(KeyCode.Up))).IsTrue();
        await Assert.That(ReferenceEquals(CommandFor(KeyCode.Char, 'g'), CommandFor(KeyCode.Home))).IsTrue();
        await Assert.That(ReferenceEquals(CommandFor(KeyCode.Char, 'G'), CommandFor(KeyCode.End))).IsTrue();
        await Assert.That(ReferenceEquals(CommandFor(KeyCode.Char, 'q'), CommandFor(KeyCode.Escape))).IsTrue();
        await Assert.That(ReferenceEquals(CommandFor(KeyCode.Char, 's'), CommandFor(KeyCode.Char, 't'))).IsTrue();
    }

    [Test]
    public async Task EveryRowOfTheTable_ChangesTheViewer()
    {
        List<string> dead = [];

        foreach ((DiffViewerGesture gesture, IDiffViewerCommand _) in DiffViewerCommandTable.Commands)
        {
            var overlay = Shown();
            overlay.NextHunk();
            overlay.ScrollBy(2);
            Snapshot before = Snap(overlay);

            bool handled = overlay.HandleKey(KeyFor(gesture));

            if (!handled || before.Equals(Snap(overlay)))
            {
                dead.Add(Describe(gesture));
            }
        }

        await Assert.That(string.Join(" | ", dead)).IsEqualTo(string.Empty)
            .Because("a row no key reaches is a row the old switch never had either");
    }

    // --------------------------------------------------------- the gate --

    [Test]
    public async Task CommandModifiers_AreRefused_AndShiftIsAdmitted()
    {
        // Ctrl/Alt/Meta + a rune: the chord belongs to the host.
        foreach (KeyModifiers mods in new[] { KeyModifiers.Ctrl, KeyModifiers.Alt, KeyModifiers.Meta })
        {
            var overlay = Shown();
            bool handled = overlay.HandleKey(KeyEvent.Char(new Rune('n'), mods));

            await Assert.That(handled).IsFalse()
                .Because($"{mods} is a command modifier, not a case-shaper");
            await Assert.That(overlay.SelectedHunk).IsEqualTo(0);
        }

        // Shift + a capital that IS a bound command: admitted. 'G' is the
        // viewer's own "last hunk", and a real encoder sends the capital rune
        // AND Shift.
        var shifted = Shown();
        await Assert.That(shifted.HandleKey(KeyEvent.Char(new Rune('G'), KeyModifiers.Shift))).IsTrue()
            .Because("'G' is a bound command here, so refusing Shift would make it unreachable");
        await Assert.That(shifted.SelectedHunk).IsEqualTo(LastHunk);

        // The legacy vocabulary has no Meta slot: Ctrl and Alt are its
        // command modifiers.
        foreach (ConsoleKeyInfo chord in new[]
        {
            new ConsoleKeyInfo('n', ConsoleKey.N, false, false, true),
            new ConsoleKeyInfo('n', ConsoleKey.N, false, true, false),
        })
        {
            var legacy = Shown();
            await Assert.That(legacy.HandleKey(chord)).IsFalse();
            await Assert.That(legacy.SelectedHunk).IsEqualTo(0);
        }
    }

    [Test]
    public async Task A_NamedKey_IgnoresModifiers()
    {
        // The named keys never looked at modifiers, and they still do not:
        // Ctrl+Down moves the selection.
        var overlay = Shown();

        await Assert.That(overlay.HandleKey(KeyEvent.Simple(KeyCode.Down, KeyModifiers.Ctrl))).IsTrue()
            .Because("a named key is not a chord the viewer has to refuse");
        await Assert.That(overlay.SelectedHunk).IsEqualTo(1);

        var legacy = Shown();
        await Assert.That(legacy.HandleKey(new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, true))).IsTrue();
        await Assert.That(legacy.SelectedHunk).IsEqualTo(1);
    }

    [Test]
    public async Task Gestures_TheTableDoesNotClaim_AreNotHandled()
    {
        var overlay = Shown();

        await Assert.That(overlay.HandleKey(KeyEvent.Char(new Rune('x')))).IsFalse();
        await Assert.That(overlay.HandleKey(KeyEvent.Simple(KeyCode.Enter))).IsFalse();
        await Assert.That(overlay.HandleKey(KeyEvent.Simple(KeyCode.Tab))).IsFalse();
        await Assert.That(overlay.HandleKey(new ConsoleKeyInfo('x', ConsoleKey.X, false, false, false))).IsFalse();
        await Assert.That(overlay.HandleKey(new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false))).IsFalse();

        await Assert.That(overlay.SelectedHunk).IsEqualTo(0);
        await Assert.That(overlay.Visible).IsTrue();
        await Assert.That(overlay.Mode).IsEqualTo(DiffViewerMode.Unified);
        await Assert.That(overlay.Wrap).IsFalse();
    }

    [Test]
    public async Task ARuneOutsideTheBmp_IsNotClaimed()
    {
        var overlay = Shown();

        // U+1F600 is TWO UTF-16 chars, so the string switch the refactor
        // replaced matched nothing for it either.
        await Assert.That(overlay.HandleKey(KeyEvent.Char(new Rune(0x1F600)))).IsFalse();
        await Assert.That(overlay.SelectedHunk).IsEqualTo(0);
    }

    [Test]
    public async Task AHiddenViewer_ClaimsNothing()
    {
        var overlay = new DiffViewerOverlay();

        await Assert.That(overlay.HandleKey(KeyEvent.Char(new Rune('n')))).IsFalse();
        await Assert.That(overlay.HandleKey(KeyEvent.Simple(KeyCode.Escape))).IsFalse();
        await Assert.That(overlay.HandleKey(new ConsoleKeyInfo('n', ConsoleKey.N, false, false, false))).IsFalse();
        await Assert.That(overlay.HandleKey(new ConsoleKeyInfo('\x1b', ConsoleKey.Escape, false, false, false))).IsFalse();
        await Assert.That(overlay.Visible).IsFalse();
    }

    // ---------------------------------------------------------------- helpers --

    /// <summary>
    /// Describes every observable the two encodings must agree on, or empty
    /// when they agree.
    /// </summary>
    private static string Break(
        string gesture,
        string mode,
        Snapshot legacy,
        bool legacyHandled,
        Snapshot kitty,
        bool kittyHandled)
    {
        var parts = new List<string>();
        if (legacy.Visible != kitty.Visible)
        {
            parts.Add($"visible {legacy.Visible}/{kitty.Visible}");
        }

        if (legacy.SelectedHunk != kitty.SelectedHunk)
        {
            parts.Add($"hunk {legacy.SelectedHunk}/{kitty.SelectedHunk}");
        }

        if (legacy.Mode != kitty.Mode)
        {
            parts.Add($"mode {legacy.Mode}/{kitty.Mode}");
        }

        if (legacy.Wrap != kitty.Wrap)
        {
            parts.Add($"wrap {legacy.Wrap}/{kitty.Wrap}");
        }

        if (legacy.Scroll != kitty.Scroll)
        {
            parts.Add($"scroll {legacy.Scroll}/{kitty.Scroll}");
        }

        if (legacyHandled != kittyHandled)
        {
            parts.Add($"handled {legacyHandled}/{kittyHandled}");
        }

        return parts.Count == 0 ? string.Empty : $"{gesture}/{mode}: {string.Join(", ", parts)}";
    }

    private static IDiffViewerCommand CommandFor(KeyCode key, char? rune = null) =>
        DiffViewerCommandTable.Commands[new DiffViewerGesture(key, rune)];

    private static KeyEvent KeyFor(DiffViewerGesture gesture) =>
        gesture.Rune is { } rune ? KeyEvent.Char(new Rune(rune)) : KeyEvent.Simple(gesture.Key);

    private static string Describe(DiffViewerGesture gesture) =>
        gesture.Rune is { } rune ? $"{gesture.Key} '{rune}'" : gesture.Key.ToString();
}
