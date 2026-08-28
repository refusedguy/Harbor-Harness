using Harbor.Ui.Framework.Projection;

namespace Harbor.DesignSystem.Tests;

/// <summary>
/// Marketplace live-reload: polling picks up new/changed theme files and
/// applies them to TerminalColorPalette; invalid files report errors and keep
/// the last applied theme. Watchers run with autoStart=false for full
/// determinism. Keyed with the other palette-mutating classes — the palette
/// is global static state.
/// </summary>
[NotInParallel("terminal-color-palette")]
public class ThemeDirectoryWatcherTests
{
    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"harbor-theme-watch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Test]
    public async Task Poll_NewValidFile_AppliesTheme()
    {
        string dir = TempDir();
        try
        {
            TerminalColorPalette.Apply(HarborTheme.HarborDark);
            var applied = new List<HarborTheme>();
            using var watcher = new ThemeDirectoryWatcher(dir, applied.Add, autoStart: false);

            await File.WriteAllTextAsync(dir + "/night.json", """{ "name": "night", "accent": "#1122ff" }""");
            watcher.Poll();

            await Assert.That(watcher.LastApplied).IsNotNull();
            await Assert.That(watcher.LastApplied!.Name).IsEqualTo("night");
            await Assert.That(TerminalColorPalette.Current.Name).IsEqualTo("night");
            await Assert.That(applied).Count().IsEqualTo(1);

            // unchanged file → no duplicate apply
            watcher.Poll();
            await Assert.That(applied).Count().IsEqualTo(1);
        }
        finally
        {
            TerminalColorPalette.Apply(HarborTheme.HarborDark);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Poll_ModifiedFile_ReloadsLive()
    {
        string dir = TempDir();
        string path = Path.Combine(dir, "live.json");
        try
        {
            TerminalColorPalette.Apply(HarborTheme.HarborDark);
            await File.WriteAllTextAsync(path, """{ "name": "v1", "accent": "#101010" }""");
            using var watcher = new ThemeDirectoryWatcher(dir, autoStart: false);
            watcher.Poll();

            await File.WriteAllTextAsync(path, """{ "name": "v2", "accent": "#202020" }""");
            // stamp granularity can swallow quick successive writes — force a fresh mtime
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
            watcher.Poll();

            await Assert.That(TerminalColorPalette.Current.Name).IsEqualTo("v2");
            await Assert.That(TerminalColorPalette.Current.Accent).IsEqualTo(new RgbColor(0x20, 0x20, 0x20));
        }
        finally
        {
            TerminalColorPalette.Apply(HarborTheme.HarborDark);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Poll_InvalidFile_ReportsError_KeepsLastTheme()
    {
        string dir = TempDir();
        try
        {
            TerminalColorPalette.Apply(HarborTheme.HarborCool);
            var errors = new List<string>();
            await File.WriteAllTextAsync(Path.Combine(dir, "bad.json"), "{ not json ");
            using var watcher = new ThemeDirectoryWatcher(dir, onError: errors.Add, autoStart: false);
            watcher.Poll();

            await Assert.That(errors).Count().IsEqualTo(1);
            await Assert.That(errors[0]).Contains("bad.json");
            await Assert.That(TerminalColorPalette.Current).IsEqualTo(HarborTheme.HarborCool);
            await Assert.That(watcher.LastApplied).IsNull();
        }
        finally
        {
            TerminalColorPalette.Apply(HarborTheme.HarborDark);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Poll_MissingDirectory_DoesNotCrash()
    {
        using var watcher = new ThemeDirectoryWatcher(
            Path.Combine(TempDir(), "nope"), autoStart: false);

        watcher.Poll();

        await Assert.That(watcher.LastApplied).IsNull();
    }
}
