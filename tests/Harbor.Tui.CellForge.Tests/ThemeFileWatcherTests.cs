using Harbor.DesignSystem;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Theme live-reload: the watcher applies a rewritten theme file on the next
/// poll, keeps the previous theme on parse failures, and stays quiet when the
/// file is untouched. Uses the public Poll() — no wall-clock flakiness.
/// </summary>
// #648: bare [NotInParallel] = one at a time GLOBALLY. The watcher applies
// themes to the process-global palette, which every painter in this assembly
// reads; the old ("pty") constraint key only excluded other "pty" tests, so
// unkeyed readers still ran through the swap.
[NotInParallel]
public class ThemeFileWatcherTests
{
    private string _path = null!;

    [Before(Test)]
    public Task NewTempFile()
    {
        _path = Path.Combine(Path.GetTempPath(), $"harbor-theme-{Guid.NewGuid():N}.json");
        return Task.CompletedTask;
    }

    [After(Test)]
    public void Cleanup()
    {
        File.Delete(_path);
        TerminalColorPalette.Apply(HarborTheme.HarborDark);
    }

    [Test]
    public async Task Poll_AppliesRewrittenTheme()
    {
        await File.WriteAllTextAsync(_path, """{ "name": "v1", "accent": "#111111" }""");
        using var watcher = new ThemeFileWatcher(_path);

        watcher.Poll(); // first sight — same stamp as initial, no apply yet
        await Assert.That(watcher.LastApplied.HasNoValue).IsTrue();

        await File.WriteAllTextAsync(_path, """{ "name": "v2", "accent": "#222222" }""");
        watcher.Poll();

        await Assert.That(watcher.LastApplied.HasValue).IsTrue();
        await Assert.That(watcher.LastApplied.Value.Name).IsEqualTo("v2");
        // Global ambient intentionally not asserted (see below).
    }

    [Test]
    public async Task Poll_CallbackFiresOnSuccess()
    {
        await File.WriteAllTextAsync(_path, """{ "name": "cb", "accent": "#333333" }""");
        var applied = new List<string>();
        using var watcher = new ThemeFileWatcher(_path, onApplied: t => applied.Add(t.Name));

        await File.WriteAllTextAsync(_path, """{ "name": "cb2", "accent": "#444444" }""");
        watcher.Poll();

        await Assert.That(applied).IsEquivalentTo(["cb2"]);
    }

    [Test]
    public async Task Poll_BrokenJson_KeepsLastTheme_ReportsError()
    {
        await File.WriteAllTextAsync(_path, """{ "name": "good", "accent": "#555555" }""");
        string? error = null;
        using var watcher = new ThemeFileWatcher(_path, onError: e => error = e);

        await File.WriteAllTextAsync(_path, """{ "name": "good", "accent": "#666666" }""");
        watcher.Poll();
        // Deterministic core: this watcher's own application record. The
        // global Current is NOT asserted here — it was reachable only because
        // this class is now [NotInParallel] (one at a time globally, #648), so
        // no other theme test can hold the palette between our Poll and a read
        // of the shared static.
        await Assert.That(watcher.LastApplied.Value.Name).IsEqualTo("good");

        await File.WriteAllTextAsync(_path, "totally not json");
        watcher.Poll();

        await Assert.That(watcher.LastApplied.Value.Name).IsEqualTo("good"); // unchanged
        await Assert.That(error).IsNotNull();
    }

    [Test]
    public async Task Poll_MissingFile_IsQuiet()
    {
        using var watcher = new ThemeFileWatcher(_path); // never created

        watcher.Poll();
        await Assert.That(watcher.LastApplied.HasNoValue).IsTrue();
    }
}
