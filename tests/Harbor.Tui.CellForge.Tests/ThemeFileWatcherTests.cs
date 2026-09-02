using Harbor.DesignSystem;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Theme live-reload: the watcher applies a rewritten theme file on the next
/// poll, keeps the previous theme on parse failures, and stays quiet when the
/// file is untouched. Uses the public Poll() — no wall-clock flakiness.
/// </summary>
[NotInParallel] // mutates global theme state
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
        await Assert.That(watcher.LastApplied).IsNull();

        await File.WriteAllTextAsync(_path, """{ "name": "v2", "accent": "#222222" }""");
        watcher.Poll();

        await Assert.That(watcher.LastApplied).IsNotNull();
        await Assert.That(watcher.LastApplied!.Name).IsEqualTo("v2");
        await Assert.That(TerminalColorPalette.Current.Name).IsEqualTo("v2");
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
        await Assert.That(TerminalColorPalette.Current.Name).IsEqualTo("good");

        await File.WriteAllTextAsync(_path, "totally not json");
        watcher.Poll();

        await Assert.That(TerminalColorPalette.Current.Name).IsEqualTo("good"); // unchanged
        await Assert.That(error).IsNotNull();
    }

    [Test]
    public async Task Poll_MissingFile_IsQuiet()
    {
        using var watcher = new ThemeFileWatcher(_path); // never created

        watcher.Poll();
        await Assert.That(watcher.LastApplied).IsNull();
    }
}
