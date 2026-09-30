using System.Collections.Concurrent;
using Harbor.Plugins.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Plugins.Runtime.Tests.Hosting;

/// <summary>
///     Pins the burst-resolution ORDER <see cref="DebouncedPluginWatcher" /> actually
///     implements, so the class summary cannot drift back into promising a severity
///     merge. Separate file from <c>DebouncedPluginWatcherTests</c> on purpose: that
///     class owns the shared debounce tuning, this one only asserts settled state and
///     must stay independent of it.
/// </summary>
/// <remarks>
///     <para>
///         Every pre-existing watcher test is order-BLIND, which is why the class doc
///         could promise a severity merge for so long without a red run:
///         <c>QuickSaveBurst</c> writes only (Modified×5), <c>Created_File</c> asserts
///         merely "not Removed", <c>Rename_*</c> spans two paths so no per-path merge
///         happens, and <c>Delete_OutranksEarlierModifications</c> is rescued by the
///         fire-time filesystem check — which forces <c>Removed</c> whether Rank is
///         overwritten or max-accumulated.
///     </para>
///     <para>
///         The only descending-rank sequence that survives that check is
///         <c>Modified → Added</c> on a path that EXISTS again (<c>Added</c> short-circuits
///         the <c>kind != Added</c> guard). These tests cover it, and both assert the
///         SETTLED last report per path rather than "the Nth callback" — so a debounce
///         window that splits a burst across several fires still lands on the same
///         final kind, and the assertion does not depend on debounce length.
///     </para>
/// </remarks>
public sealed class DebouncedPluginWatcherOrderingTests : IDisposable
{
    private readonly string _dir;
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(120);

    public DebouncedPluginWatcherOrderingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "harbor-watch-order-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException)
        { /* best-effort cleanup */
        }
    }

    private static DebouncedPluginWatcher Watch(string dir, ConcurrentQueue<PluginSourceChangeEventArgs> received)
    {
        var watcher = new DebouncedPluginWatcher(
            [dir], Debounce, NullLogger<DebouncedPluginWatcher>.Instance);
        watcher.ChangesReady += (_, c) => received.Enqueue(c);
        return watcher;
    }

    /// <summary>
    ///     Wait for real progress past <paramref name="baseline" />, THEN for a quiet
    ///     stretch long enough that a late inotify echo has landed and armed its own
    ///     window. The two phases cannot be swapped: a count that is merely STABLE is not
    ///     settled, because the queue is trivially stable before the first event arrives —
    ///     a stability-first loop declares "settled" on an empty queue and fails. The 10s
    ///     progress budget and the <c>Debounce * 3</c> settle are the same numbers
    ///     <c>DebouncedPluginWatcherTests</c> already uses.
    /// </summary>
    private static async Task SettledAsync(ConcurrentQueue<PluginSourceChangeEventArgs> received, int baseline)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (received.Count <= baseline && DateTime.UtcNow < deadline)
            await Task.Delay(40);
        await Assert.That(received.Count).IsGreaterThan(baseline);

        while (true)
        {
            int current = received.Count;
            await Task.Delay(Debounce * 3);
            if (received.Count == current)
                return;
        }
    }

    /// <summary>
    ///     Delete + immediate re-create of the same path is the one sequence where
    ///     "last event wins" and "most severe wins" disagree while the file still exists.
    ///     The file is present when the burst fires, so the fire-time filesystem check
    ///     cannot rescue a max-accumulated <c>Modified</c>: this assertion fails for a
    ///     severity merge and passes for the shipped overwrite.
    /// </summary>
    [Test]
    public async Task RecreatedFile_InOneBurst_SettlesToAdded_NotMergedModified()
    {
        string path = Path.Combine(_dir, "recycled.cs");
        var received = new ConcurrentQueue<PluginSourceChangeEventArgs>();
        using DebouncedPluginWatcher watcher = Watch(_dir, received);

        File.WriteAllText(path, "// v1");
        await SettledAsync(received, baseline: 0);

        File.AppendAllText(path, "// v2");   // Modified
        File.Delete(path);                   // Removed
        File.WriteAllText(path, "// v3");    // Added again — last raw event, file exists

        await SettledAsync(received, baseline: 1);
        var last = received.Last(c => c.Path == path);
        await Assert.That(last.Kind).IsEqualTo(PluginSourceChangeKind.Added);
    }

    /// <summary>
    ///     The mirror case, and the flake the component fixed in
    ///     <c>DebouncedPluginWatcher</c>: a file that is modified then DELETED must settle
    ///     on <c>Removed</c> even when a trailing inotify <c>Changed</c> echo arrives
    ///     AFTER the <c>Deleted</c>. Asserting the concrete kind — not "not Added" —
    ///     keeps the fire-time filesystem check load-bearing rather than incidental.
    /// </summary>
    [Test]
    public async Task ModifiedThenDeleted_SettlesToRemoved_NotLastEchoModified()
    {
        string path = Path.Combine(_dir, "vanishing.cs");
        var received = new ConcurrentQueue<PluginSourceChangeEventArgs>();
        using DebouncedPluginWatcher watcher = Watch(_dir, received);

        File.WriteAllText(path, "// v1");
        await SettledAsync(received, baseline: 0);

        File.AppendAllText(path, "// v2");
        File.Delete(path);

        await SettledAsync(received, baseline: 1);
        var last = received.Last(c => c.Path == path);
        await Assert.That(last.Kind).IsEqualTo(PluginSourceChangeKind.Removed);
    }
}
