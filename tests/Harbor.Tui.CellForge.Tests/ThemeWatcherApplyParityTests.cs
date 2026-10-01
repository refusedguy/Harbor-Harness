using Harbor.DesignSystem;
using Harbor.Hosting.Themes;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     #479-A6 — the apply-policy half of the "two theme watchers" duplication,
///     measured.
/// </summary>
/// <remarks>
///     <para>
///         #720 removed the duplicated READ: both watchers stat and load through
///         <see cref="IThemeStore" /> now, so on one input they parse to the same
///         theme. The APPLY was left out of that fix, and the apply is the half a
///         user can see. These two watchers differ in exactly one observable way:
///         <c>ThemeDirectoryWatcher</c> applies through
///         <see cref="TerminalColorPalette.Apply" /> unconditionally, while
///         <c>ThemeFileWatcher</c> applies <em>only when no
///         <c>onApplied</c> callback was supplied</em> — and the one product site
///         that arms it, <c>ReplLifecycle.ArmThemeFile</c>, always supplies one.
///         So the single-file arm (the documented
///         <c>HARBOR_THEME_FILE</c> / <c>~/.harbor/theme.json</c> path) polls,
///         announces "theme: live-reload → …", marks the whole screen dirty and
///         never changes a color, while the directory arm repaints. Same input,
///         different colors.
///     </para>
///     <para>
///         Why the duplication survived: <c>ThemeDirectoryWatcherTests</c> asserts
///         the ambient palette <em>with</em> a callback in place, and
///         <c>ThemeFileWatcherTests</c> documents at two call sites why it
///         deliberately does not ("Global ambient intentionally not asserted").
///         Each file is honest about its own slice; nothing compared the two.
///     </para>
///     <para>
///         Bare <c>[NotInParallel]</c> = one at a time globally, per #648/#703:
///         both watchers write the process-global palette that every painter in
///         this assembly reads.
///     </para>
/// </remarks>
[NotInParallel]
public class ThemeWatcherApplyParityTests
{
    private string _dir = null!;
    private string _path = null!;

    [Before(Test)]
    public Task NewTempDir()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"harbor-theme-parity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "parity.json");
        return Task.CompletedTask;
    }

    [After(Test)]
    public void Cleanup()
    {
        TerminalColorPalette.Apply(HarborTheme.HarborDark);
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private const string V1 = """{ "name": "parity-v1", "accent": "#0a0a0a" }""";

    /// <summary>
    ///     The measurement. One theme document, two watchers, two arms of the
    ///     product's own <c>ArmThemeWatcher</c>, each constructed the way
    ///     <c>ReplLifecycle</c> constructs it — with a non-null
    ///     <c>onApplied</c>. The ambient palette is sampled after each poll and
    ///     the two samples are compared, so this fails on a DIVERGENCE rather
    ///     than on an absolute value that either side could drift from.
    /// </summary>
    [Test]
    public async Task One_Theme_File_Leaves_The_Same_Ambient_Palette_Under_Both_Watchers()
    {
        var store = new ThemeStore(_dir);

        HarborTheme? directorySaw = null;
        HarborTheme? fileSaw = null;

        // Both constructed BEFORE the file exists, so each watcher's initial
        // stamp predates the write and the first poll is a real change.
        using var directoryWatcher = new ThemeDirectoryWatcher(
            _dir, store, onApplied: t => directorySaw = t, autoStart: false);
        using var fileWatcher = new ThemeFileWatcher(
            _path, store, onApplied: t => fileSaw = t);

        // --- arm 1: the directory watcher, sampled on a clean baseline -------
        TerminalColorPalette.Apply(HarborTheme.HarborDark);
        await File.WriteAllTextAsync(_path, V1);
        directoryWatcher.Poll();
        var directoryArm = TerminalColorPalette.Current;

        // --- arm 2: the file watcher, same document, same baseline ----------
        TerminalColorPalette.Apply(HarborTheme.HarborDark);
        fileWatcher.Poll();
        var fileArm = TerminalColorPalette.Current;

        // Both arms parsed it — that half #720 fixed and the parity holds.
        await Assert.That(directorySaw?.Name).IsEqualTo("parity-v1");
        await Assert.That(fileSaw?.Name).IsEqualTo("parity-v1");

        // And the half it did not: what the user sees.
        await Assert.That(fileArm).IsEqualTo(directoryArm);
        await Assert.That(fileArm.Name).IsEqualTo("parity-v1");
        await Assert.That(fileArm.Accent).IsEqualTo(new RgbColor(0x0a, 0x0a, 0x0a));
    }

    /// <summary>
    ///     Non-vacuity control for the test above. Its only difference from the
    ///     file arm of the parity test is the ABSENCE of <c>onApplied</c>, so
    ///     together the two tests make the callback the named discriminator and
    ///     prove the palette read is live: if
    ///     <see cref="TerminalColorPalette.Apply" /> had gone inert, or the
    ///     baseline were not taking, this fails and the parity comparison above
    ///     cannot be reporting a false zero. (The #931 shape — a rule whose form
    ///     of the question silently loses real findings.)
    /// </summary>
    [Test]
    public async Task A_Watcher_With_No_Callback_Does_Repaint_The_Ambient_Palette()
    {
        TerminalColorPalette.Apply(HarborTheme.HarborDark);
        using var watcher = new ThemeFileWatcher(_path, new ThemeStore(_dir));

        await File.WriteAllTextAsync(_path, V1);
        watcher.Poll();

        await Assert.That(TerminalColorPalette.Current.Name).IsEqualTo("parity-v1");
        await Assert.That(TerminalColorPalette.Current.Accent).IsEqualTo(new RgbColor(0x0a, 0x0a, 0x0a));
    }
}