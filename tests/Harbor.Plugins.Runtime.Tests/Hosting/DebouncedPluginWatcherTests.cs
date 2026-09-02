using System.Collections.Concurrent;
using Harbor.Plugins.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Plugins.Runtime.Tests.Hosting;

/// <summary>
///     Tests for <see cref="DebouncedPluginWatcher" /> — real filesystem watches with a
///     short debounce. Sequences are split across distinct files/delays so per-path
///     severity merges stay deterministic under Linux FSW event ordering.
/// </summary>
public sealed class DebouncedPluginWatcherTests : IDisposable
{
    private readonly string _dir;
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(120);

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
        int baseline;
        while (true)
        {
            baseline = received.Count;
            await Task.Delay(Debounce * 3);
            if (received.Count == baseline)
                break;
        }

        // Tight burst — five writes without sleeps land as one inotify batch inside
        // a single debounce window; spreading them over 40ms lets a loaded host
        // split delivery across two windows.
        foreach (int i in Enumerable.Range(0, 5))
            File.WriteAllText(path, $"// v{i + 2}");

        var change = await NextAsync(watcher, received, baseline + 1, TimeSpan.FromSeconds(10));
        await Assert.That(change.Kind).IsEqualTo(PluginSourceChangeKind.Modified);
        // The burst produced ONE callback: nothing else fires within a settle window.
        await Task.Delay(Debounce * 2);
        await Assert.That(received.Count).IsEqualTo(baseline + 1);
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

        File.AppendAllText(path, "// more");
        File.Delete(path);

        var change = await NextAsync(watcher, received, 2, TimeSpan.FromSeconds(10));
        await Assert.That(change.Kind).IsEqualTo(PluginSourceChangeKind.Removed);
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

        await Task.Delay(900);
        await Assert.That(received.Count).IsEqualTo(0); // nothing matched within the settle window
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
