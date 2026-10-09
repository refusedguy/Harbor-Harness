using System.Collections.Concurrent;
using System.Diagnostics;
using Harbor.Plugins.Hosting;
using Harbor.Plugins.Storage;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Plugins.Runtime.Tests.Hosting;

/// <summary>
///     Tests for <see cref="DebouncedPluginWatcher" /> — real filesystem watches at
///     the product's own debounce (<see cref="Debounce" />). Sequences are split
///     across distinct files/delays so per-path severity merges stay deterministic
///     under Linux FSW event ordering.
/// </summary>
public sealed class DebouncedPluginWatcherTests : IDisposable
{
    private readonly string _dir;

    /// <summary>
    ///     The debounce under test — 500ms, matching <c>PluginAutoReloader.DebounceMs</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         #757: this was 120ms, which made the test STRICTER than the product it
    ///         is testing. Every settle in this file has to outlast a debounce of this
    ///         size, so at 120ms the margin the test needed and the margin it could
    ///         obtain were the same number — there was no headroom to spend on a slow
    ///         runner. Matching the product's own window removes the inversion.
    ///     </para>
    ///     <para>
    ///         It also widens the gap a split delivery has to cross. A burst is split
    ///         across two windows only if an event of it arrives more than one debounce
    ///         after the first, so the pre-emption needed to mislead this file grows
    ///         with the debounce: ~119ms of it at 120ms, ~499ms at 500ms.
    ///     </para>
    /// </remarks>
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);

    /// <summary>
    ///     Bound on every quiescence wait in this file. A drain that cannot end is
    ///     the other half of the same defect #757 fixed: the old <c>while (true)</c>
    ///     loop had no exit a hostile host could not deny it.
    /// </summary>
    private const int DrainAttempts = 8;

    public DebouncedPluginWatcherTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "harbor-watch-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException)
        { /* best-effort cleanup */
        }
    }

    private static async Task<PluginSourceChangeEventArgs> NextAsync(
        DebouncedPluginWatcher watcher,
        ConcurrentQueue<PluginSourceChangeEventArgs> received,
        int expectedCount,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (received.Count >= expectedCount)
                return received.ElementAt(received.Count - 1);
            await Task.Delay(40);
        }

        throw new TimeoutException($"Expected {expectedCount} change(s), got {received.Count}");
    }

    /// <summary>
    ///     Waits for the callback queue to stop growing, and returns the count it settled at.
    /// </summary>
    /// <param name="received">The queue the watcher's callbacks land in.</param>
    /// <param name="quiet">
    ///     How long to wait between samples. Must be at least one debounce window — see
    ///     the remarks; this is a requirement of the method, not a tuning knob.
    /// </param>
    /// <param name="maxAttempts">Samples to take before giving up and reporting the count.</param>
    /// <remarks>
    ///     <para>
    ///         This replaces both halves of #757's settle. The fixed <c>Debounce * 2</c>
    ///         could not distinguish "nothing else fired" from "a second burst is still
    ///         inside its own window", because the two are the same order of magnitude.
    ///         The unbounded <c>while (true)</c> drain that preceded it could not be
    ///         denied an exit. This one OBSERVES quiescence — two consecutive samples
    ///         that agree — and gives up after <paramref name="maxAttempts" />.
    ///     </para>
    ///     <para>
    ///         <paramref name="quiet" /> being at least one debounce window is what makes
    ///         the observation sound. There is no hook on raw events on the public
    ///         surface, so "no callback is pending" can only be concluded by waiting past
    ///         the point one would have fired: a raw event arriving at <c>t</c> produces a
    ///         callback at <c>t + debounce</c>. Sampling faster than that reports quiescence
    ///         while a burst is still in flight — the original defect wearing a new helper.
    ///     </para>
    /// </remarks>
    private static async Task<int> SettledCountAsync(
        ConcurrentQueue<PluginSourceChangeEventArgs> received,
        TimeSpan quiet,
        int maxAttempts)
    {
        var settled = received.Count;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            await Task.Delay(quiet);
            int current = received.Count;
            if (current == settled)
            {
                return current;
            }

            settled = current;
        }

        return received.Count;
    }

    [Test]
    public async Task Created_File_RaisesSingleAddedAfterQuietPeriod()
    {
        var received = new ConcurrentQueue<PluginSourceChangeEventArgs>();
        using var watcher = new DebouncedPluginWatcher(
            [_dir], Debounce, NullLogger<DebouncedPluginWatcher>.Instance);
        watcher.ChangesReady += (_, c) => received.Enqueue(c);

        string path = Path.Combine(_dir, "new-plugin.cs");
        File.WriteAllText(path, "// v1");

        var change = await NextAsync(watcher, received, 1, TimeSpan.FromSeconds(10));
        await Assert.That(change.Path).IsEqualTo(path);
        // Create+write may land as Created followed by one or more Changed events —
        // the LAST raw event wins, so both Added and Modified are valid here; only
        // Removed would be wrong for an existing file.
        await Assert.That(change.Kind).IsNotEqualTo(PluginSourceChangeKind.Removed);
        await Assert.That(received.Count).IsEqualTo(1); // nothing extra fired during the same quiet period
    }

    /// <summary>
    ///     A <c>.cs</c> file created in a subdirectory raises a change: the watcher
    ///     is recursive to match <see cref="FileSystemPluginSource" /> discovery
    ///     (issue #1046). Only a missing change would be wrong here, so
    ///     the kind assertion mirrors the top-level test (anything but Removed).
    /// </summary>
    /// <remarks>
    ///     The subdirectory pre-exists the watcher: on Linux a FileSystemWatcher
    ///     does not pick up subdirectories created after watching starts, so
    ///     creating it first tests the supported shape (pre-existing tree).
    /// </remarks>
    [Test]
    public async Task Created_FileInSubdirectory_RaisesChange()
    {
        string sub = Path.Combine(_dir, "nested");
        Directory.CreateDirectory(sub);

        var received = new ConcurrentQueue<PluginSourceChangeEventArgs>();
        using var watcher = new DebouncedPluginWatcher(
            [_dir], Debounce, NullLogger<DebouncedPluginWatcher>.Instance);
        watcher.ChangesReady += (_, c) => received.Enqueue(c);

        string path = Path.Combine(sub, "nested-plugin.cs");
        File.WriteAllText(path, "// v1");

        var change = await NextAsync(watcher, received, 1, TimeSpan.FromSeconds(10));
        await Assert.That(change.Path).IsEqualTo(path);
        await Assert.That(change.Kind).IsNotEqualTo(PluginSourceChangeKind.Removed);
    }

    [Test]
    public async Task QuickSaveBurst_CollapsesToSingleModified()
    {
        string path = Path.Combine(_dir, "burst.cs");
        var received = new ConcurrentQueue<PluginSourceChangeEventArgs>();
        using var watcher = new DebouncedPluginWatcher(
            [_dir], Debounce, NullLogger<DebouncedPluginWatcher>.Instance);
        watcher.ChangesReady += (_, c) => received.Enqueue(c);

        File.WriteAllText(path, "// v1");
        // Consume the creation burst so the save burst starts from a clean slate.
        await NextAsync(watcher, received, 1, TimeSpan.FromSeconds(10));

        // Drain late echoes of the creation write: inotify can deliver additional
        // Changed events well after the debounced callback fired, and each echo arms
        // its own debounce window. An echo counted as part of the save burst would
        // fake a second callback below.
        int baseline = await SettledCountAsync(received, Debounce, DrainAttempts);

        // Tight burst — five writes without sleeps land as one inotify batch inside
        // a single debounce window; spreading them lets a loaded host split delivery
        // across two windows.
        var burst = Stopwatch.StartNew();
        foreach (int i in Enumerable.Range(0, 5))
            File.WriteAllText(path, $"// v{i + 2}");
        burst.Stop();

        // #996: the burst duration is REPORTED, not gated. Five 8-byte writes read
        // 178 ms on shared CI against the old 125 ms (Debounce/4) budget — 35 ms per
        // write is host pre-emption, and six green runs of one unchanged commit spread
        // 1.43x, so no threshold separates code from runner. The count below stays the
        // gate: splitting it now needs ~one full debounce of pre-emption (#757 raised
        // the window 120 ms → 500 ms), not a quarter of it.
        Console.WriteLine(
            $"[burst] five writes took {burst.ElapsedMilliseconds} ms inside a {(int)Debounce.TotalMilliseconds} ms debounce window.");

        var change = await NextAsync(watcher, received, baseline + 1, TimeSpan.FromSeconds(10));
        await Assert.That(change.Kind).IsEqualTo(PluginSourceChangeKind.Modified);

        // The burst produced ONE callback. Wait for the queue to go quiet rather than
        // for a fixed multiple of the debounce: the old `Debounce * 2` settle was the
        // same order as the window it waited out, so a legal second burst was
        // indistinguishable from "nothing else fired" (#757).
        await SettledCountAsync(received, Debounce, DrainAttempts);

        await Assert.That(received.Count)
            .IsEqualTo(baseline + 1)
            .Because(
                "five tight writes inside one debounce window must collapse to a single callback, and the "
                + "settle above waited for quiescence rather than for a fixed multiple of the debounce — so "
                + "this count is read after the queue actually stopped growing. A higher count means a second "
                + "burst arrived in a genuinely separate window.");
    }

    [Test]
    public async Task Delete_OutranksEarlierModifications()
    {
        string path = Path.Combine(_dir, "gone.cs");
        var received = new ConcurrentQueue<PluginSourceChangeEventArgs>();
        using var watcher = new DebouncedPluginWatcher(
            [_dir], Debounce, NullLogger<DebouncedPluginWatcher>.Instance);
        watcher.ChangesReady += (_, c) => received.Enqueue(c);

        File.WriteAllText(path, "// temp");
        await NextAsync(watcher, received, 1, TimeSpan.FromSeconds(10)); // consume Add

        // Drain late echoes of the creation write before the append+delete burst
        // (#757). Without a baseline, append and delete can land in two windows and
        // the first callback reports Modified — the assertion below then goes red
        // for a delivery split, which is a different reason than the one it names.
        int baseline = await SettledCountAsync(received, Debounce, DrainAttempts);

        File.AppendAllText(path, "// more");
        File.Delete(path);

        // Wait for the Removed signal SPECIFICALLY, not for a bare event count: if
        // delivery splits across two windows the first callback is Modified, and a
        // count cannot tell that from a ranking failure. Same predicate shape as
        // Rename_SignalsRemovedForOldAndAddedForNew below (#757).
        bool SawRemoved() => received.Skip(baseline)
            .Any(c => c.Path == path && c.Kind == PluginSourceChangeKind.Removed);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!SawRemoved() && DateTime.UtcNow < deadline)
            await Task.Delay(40);

        await Assert.That(SawRemoved())
            .IsTrue()
            .Because(
                "a file that no longer exists must be reported Removed, and this waits for that specific "
                + "signal rather than for a count: the watcher's own File.Exists backstop is what makes a "
                + "vanished file Removed, so a Modified here would mean the backstop did not hold. Callbacks "
                + "observed after the drained baseline: " + string.Join(", ",
                    received.Skip(baseline).Select(c => c.Kind.ToString())));
    }

    [Test]
    public async Task NonCsFiles_AreIgnored()
    {
        var received = new ConcurrentQueue<PluginSourceChangeEventArgs>();
        using var watcher = new DebouncedPluginWatcher(
            [_dir], Debounce, NullLogger<DebouncedPluginWatcher>.Instance);
        watcher.ChangesReady += (_, c) => received.Enqueue(c);

        File.WriteAllText(Path.Combine(_dir, "notes.md"), "# readme");
        File.WriteAllText(Path.Combine(_dir, "trust.json"), "{}");

        // #757: was a hardcoded 900ms — 7.5x the old 120ms debounce, a number that
        // says nothing about the window it is waiting out and rots the moment Debounce
        // changes. Derived now, so it tracks the debounce instead. It stays a fixed
        // wait on purpose, unlike the settles above: this asserts a NEGATIVE, so
        // "wait longer" IS the assertion rather than a proxy for "nothing else fired".
        await Task.Delay(Debounce * 2);
        await Assert.That(received.Count)
            .IsEqualTo(0)
            .Because(
                "only *.cs files are watched, so a .md and a .json in the same directory must produce no "
                + "callback at all. The wait above is longer than the debounce, so a callback here would be "
                + "a filter failure rather than a slow delivery.");
    }

    [Test]
    public async Task MissingDirectories_AreSkipped_NotWatched()
    {
        string missing = Path.Combine(_dir, "no-such-dir");
        using var watcher = new DebouncedPluginWatcher(
            [_dir, missing], Debounce, NullLogger<DebouncedPluginWatcher>.Instance);

        await Assert.That(watcher.WatchedDirectories).Contains(_dir);
        await Assert.That(watcher.WatchedDirectories.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Rename_SignalsRemovedForOldAndAddedForNew()
    {
        string oldPath = Path.Combine(_dir, "before.cs");
        string newPath = Path.Combine(_dir, "after.cs");
        var received = new ConcurrentQueue<PluginSourceChangeEventArgs>();
        using var watcher = new DebouncedPluginWatcher(
            [_dir], Debounce, NullLogger<DebouncedPluginWatcher>.Instance);
        watcher.ChangesReady += (_, c) => received.Enqueue(c);

        File.WriteAllText(oldPath, "// x");
        await NextAsync(watcher, received, 1, TimeSpan.FromSeconds(10)); // consume Add(before)

        File.Move(oldPath, newPath);

        // Wait for BOTH rename signals specifically, not a bare event count: the
        // Removed(old) and Added(new) debounced timers fire near-simultaneously, and
        // under thread-pool load the polling loop can observe count>=2 from
        // [Added(before), Removed(old)] before Added(new) has been enqueued.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        bool RemovedOld() => received.Any(c => c.Path == oldPath && c.Kind == PluginSourceChangeKind.Removed);
        bool AddedNew() => received.Any(c => c.Path == newPath && c.Kind == PluginSourceChangeKind.Added);
        while (!(RemovedOld() && AddedNew()) && DateTime.UtcNow < deadline)
            await Task.Delay(40);
        await Assert.That(RemovedOld()).IsTrue();
        await Assert.That(AddedNew()).IsTrue();
    }
}
