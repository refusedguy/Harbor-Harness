using Harbor.DesignSystem;
using Harbor.Hosting.Themes;
using Harbor.Tui.CellForge.Widgets;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Theme live-reload: the watcher applies a rewritten theme file on the next
/// poll, keeps the previous theme on parse failures, and stays quiet when the
/// file is untouched. Uses the public Poll() — no wall-clock flakiness.
/// </summary>
/// <remarks>
///     The watcher reads through an injected <see cref="IThemeStore" /> since
///     #668, so these tests drive it through a fake that answers from memory.
///     That is the point of the port and it is asserted here rather than only in
///     the architecture suite: if the watcher went back to touching the disk
///     itself, a fake-driven test could not be written at all, because there
///     would be nothing to inject.
/// </remarks>
// #648/#703: bare [NotInParallel] = one at a time GLOBALLY. The watcher applies
// themes to the process-global palette, which every painter in this assembly
// reads; the old ("pty") constraint key only excluded other "pty" tests, so
// unkeyed readers (PostFxTests, PanelFxTests, MascotPanelTests) still ran
// through the swap — that is the race behind #703's Timeline_PublishesGateGlowRegions
// flake, observed on four PRs. #720 put the key back while porting the watcher
// to IThemeStore and said nothing about the trade; this restores #703's form.
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

    /// <summary>
    ///     A store over the real file, so these tests keep exercising the actual
    ///     mtime/save path they did before the port existed.
    /// </summary>
    private IThemeStore RealStore() => new ThemeStore(Path.GetDirectoryName(_path)!);

    [Test]
    public async Task Poll_AppliesRewrittenTheme()
    {
        await File.WriteAllTextAsync(_path, """{ "name": "v1", "accent": "#111111" }""");
        using var watcher = new ThemeFileWatcher(_path, RealStore());

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
        using var watcher = new ThemeFileWatcher(_path, RealStore(), onApplied: t => applied.Add(t.Name));

        await File.WriteAllTextAsync(_path, """{ "name": "cb2", "accent": "#444444" }""");
        watcher.Poll();

        await Assert.That(applied).IsEquivalentTo(["cb2"]);
    }

    [Test]
    public async Task Poll_BrokenJson_KeepsLastTheme_ReportsError()
    {
        await File.WriteAllTextAsync(_path, """{ "name": "good", "accent": "#555555" }""");
        string? error = null;
        using var watcher = new ThemeFileWatcher(_path, RealStore(), onError: e => error = e);

        await File.WriteAllTextAsync(_path, """{ "name": "good", "accent": "#666666" }""");
        watcher.Poll();
        // Deterministic core: this watcher's own application record. The
        // global Current is NOT asserted here — it was reachable only because
        // this class is [NotInParallel] (one at a time globally, #648), so
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
        using var watcher = new ThemeFileWatcher(_path, RealStore()); // never created

        watcher.Poll();
        await Assert.That(watcher.LastApplied.HasNoValue).IsTrue();
    }

    // ---------------------------------------------------------------------
    // The port, driven from memory. No temp file, no clock, no wall time.
    // ---------------------------------------------------------------------

    [Test]
    public async Task Poll_Reads_Through_The_Injected_Store_And_Not_The_Path()
    {
        var store = new FakeThemeStore
        {
            // The path the watcher is given does not exist; the store is the
            // only thing that can answer. If the watcher still read the disk it
            // would report nothing, which is what this asserts against.
            Stamps = { ["/does/not/exist.json"] = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
        };

        var applied = new List<string>();
        using var watcher = new ThemeFileWatcher("/does/not/exist.json", store, onApplied: t => applied.Add(t.Name));

        store.Stamps["/does/not/exist.json"] = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        store.Seen = ThemeJson.Parse("""{ "name": "from-port" }""", HarborTheme.HarborDark);
        watcher.Poll();

        await Assert.That(store.LoadCalls).IsEqualTo(1);
        await Assert.That(applied).IsEquivalentTo(["from-port"]);
        await Assert.That(watcher.LastApplied.Value.Name).IsEqualTo("from-port");
    }

    [Test]
    public async Task Poll_StoreFailure_Reaches_OnError_And_AppliesNothing()
    {
        var store = new FakeThemeStore
        {
            Stamps = { ["/x.json"] = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            Seen = ThemeParseResult.Fail(["synthetic failure"], []),
        };

        string? error = null;
        using var watcher = new ThemeFileWatcher("/x.json", store, onError: e => error = e);

        store.Stamps["/x.json"] = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        watcher.Poll();

        await Assert.That(error).IsEqualTo("synthetic failure");
        await Assert.That(watcher.LastApplied.HasNoValue).IsTrue();
    }

    [Test]
    public async Task Ctor_Rejects_A_Null_Store()
    {
        // A defaulted store would let the widget quietly build its own reader,
        // which is the defect this port was introduced to remove.
        await Assert.That(
            () => new ThemeFileWatcher("/x.json", store: null!)).Throws<ArgumentNullException>();
    }

    /// <summary>
    ///     An in-memory <see cref="IThemeStore" />. Lives in a test assembly on
    ///     purpose: ThemeStoreSeamRules counts production implementers only, and
    ///     a fake is how the port is shown to be injectable without reading
    ///     anybody's files.
    /// </summary>
    private sealed class FakeThemeStore : IThemeStore
    {
        public Dictionary<string, DateTime> Stamps { get; } = new(StringComparer.Ordinal);

        public ThemeParseResult Seen { get; set; } = ThemeParseResult.Fail(["never set"], []);

        public int LoadCalls { get; private set; }

        public ThemeParseResult LoadFile(string path)
        {
            LoadCalls++;
            return Seen;
        }

        public bool TryGetLastWriteUtc(string path, out DateTime lastWriteUtc)
            => Stamps.TryGetValue(path, out lastWriteUtc);
    }
}
