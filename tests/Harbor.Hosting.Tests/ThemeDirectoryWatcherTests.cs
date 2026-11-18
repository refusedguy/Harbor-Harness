using Harbor.DesignSystem;
using Harbor.Hosting.Themes;
using Harbor.Ui.Framework.Projection;

namespace Harbor.Hosting.Tests;

/// <summary>
/// Marketplace live-reload: polling picks up new/changed theme files and
/// applies them to TerminalColorPalette; invalid files report errors and keep
/// the last applied theme. Watchers run with autoStart=false for full
/// determinism. Run one-at-a-time — the palette is process-global static state.
///
/// #648: bare [NotInParallel] (one at a time GLOBALLY), not a shared key. Per
/// TUnit's contract a constraint key only excludes tests that SHARE the key, so
/// ("terminal-color-palette") kept these mutators away from each other and from
/// nothing else; unkeyed readers could still observe a mid-test theme swap.
/// </summary>
[NotInParallel]
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
            using var watcher = new ThemeDirectoryWatcher(dir, new ThemeStore(), applied.Add, autoStart: false);

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
            using var watcher = new ThemeDirectoryWatcher(dir, new ThemeStore(), autoStart: false);
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
            using var watcher = new ThemeDirectoryWatcher(dir, new ThemeStore(), onError: errors.Add, autoStart: false);
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
            Path.Combine(TempDir(), "nope"), new ThemeStore(), autoStart: false);

        watcher.Poll();

        await Assert.That(watcher.LastApplied).IsNull();
    }

    /// <summary>
    ///     #2618: a second tick while the first poll is inside a slow stat must
    ///     be dropped. Before the gate the two callbacks iterated and wrote the
    ///     stamp dictionary at the same time; the blocking store makes the
    ///     overlap observable without relying on scheduling luck.
    /// </summary>
    [Test]
    public async Task Poll_OverlappingTick_IsDropped()
    {
        string dir = TempDir();
        try
        {
            TerminalColorPalette.Apply(HarborTheme.HarborDark);
            await File.WriteAllTextAsync(Path.Combine(dir, "night.json"),
                """{ "name": "night", "accent": "#1122ff" }""");

            using var store = new BlockingFirstStatStore();
            using var watcher = new ThemeDirectoryWatcher(dir, store, autoStart: false);

            Task first = Task.Run(watcher.Poll);
            try
            {
                if (!store.Entered.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("The first poll never reached the store.");
                }

                // Would block in the store (and race on _stamps) without the gate.
                watcher.Poll();

                await Assert.That(store.StatCalls).IsEqualTo(1);
            }
            finally
            {
                store.Release();
                await first;
            }

            await Assert.That(watcher.LastApplied?.Name).IsEqualTo("night");
        }
        finally
        {
            TerminalColorPalette.Apply(HarborTheme.HarborDark);
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Blocks the first stat so the overlap window is deterministic.</summary>
    private sealed class BlockingFirstStatStore : IThemeStore, IDisposable
    {
        private readonly IThemeStore _inner = new ThemeStore();
        private readonly ManualResetEventSlim _entered = new();
        private readonly ManualResetEventSlim _release = new();
        private int _statCalls;

        public ManualResetEventSlim Entered => _entered;
        public int StatCalls => Volatile.Read(ref _statCalls);

        public void Release() => _release.Set();

        public bool TryGetLastWriteUtc(string path, out DateTime lastWriteUtc)
        {
            if (Interlocked.Increment(ref _statCalls) == 1)
            {
                _entered.Set();
                if (!_release.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("The overlapping poll was released too late.");
                }
            }

            return _inner.TryGetLastWriteUtc(path, out lastWriteUtc);
        }

        public ThemeParseResult LoadFile(string path) => _inner.LoadFile(path);

        public void Dispose()
        {
            _entered.Dispose();
            _release.Dispose();
        }
    }
}
