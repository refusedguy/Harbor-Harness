using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using Harbor.Tui.CellForge.CodeGen.DevReload;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     Issue #3358: the pipeline creates a <see cref="DevFileWatcher"/> and
///     an edit to the watched file triggers a debounced rebuild on its own.
///     No test code calls <see cref="DevReloadPipeline.Poll"/> directly.
/// </summary>
public class DevFileWatcherTests
{
    private const string V1 = """
        panel "main":
            .text "hello"
            text "done"
        """;

    private const string V2 = """
        panel "main":
            .text "goodbye"
            text "done"
        """;

    private static string WriteMarkup(string text)
    {
        string path = Path.Combine(Path.GetTempPath(), $"harbor-watch-{Guid.NewGuid():N}.harbor-ui");
        File.WriteAllText(path, text);
        return path;
    }

    [Test]
    public async Task WatchFile_FileEdit_RebuildsWithoutManualPoll()
    {
        string path = WriteMarkup(V1);
        try
        {
            var pipeline = new DevReloadPipeline(
                () => File.ReadAllText(path),
                debounce: TimeSpan.FromMilliseconds(30));
            using var watcher = pipeline.WatchFile(path, pollInterval: TimeSpan.FromMilliseconds(20));

            await Assert.That(watcher.InitialResult.Outcome).IsEqualTo(ReloadCommitOutcome.Published);
            await Assert.That(ReferenceEquals(watcher.Pipeline, pipeline)).IsTrue();
            await Assert.That(pipeline.Current!.Interpreter.TextOf(1)).IsEqualTo("hello");
            int initialGeneration = pipeline.Generation;

            File.WriteAllText(path, V2);

            // The watcher's own timer observes the new stamp and publishes once
            // the quiet window closes. Nothing here drives the pipeline by hand.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (pipeline.Generation == initialGeneration && DateTime.UtcNow < deadline)
                await Task.Delay(20);

            await Assert.That(pipeline.Generation).IsEqualTo(initialGeneration + 1);
            await Assert.That(pipeline.Current!.Interpreter.TextOf(1)).IsEqualTo("goodbye");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task WatchFile_ObserverSeesInitialAndRebuildDecisions()
    {
        string path = WriteMarkup(V1);
        try
        {
            var seen = new ConcurrentQueue<ReloadCommitResult>();
            var pipeline = new DevReloadPipeline(
                () => File.ReadAllText(path),
                debounce: TimeSpan.FromMilliseconds(30));
            using var watcher = pipeline.WatchFile(
                path,
                pollInterval: TimeSpan.FromMilliseconds(20),
                onCommit: seen.Enqueue);

            await Assert.That(seen.TryDequeue(out var initial)).IsTrue();
            await Assert.That(initial!.Outcome).IsEqualTo(ReloadCommitOutcome.Published);
            await Assert.That(watcher.InitialResult.Outcome).IsEqualTo(ReloadCommitOutcome.Published);

            File.WriteAllText(path, V2);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (pipeline.Generation == 1 && DateTime.UtcNow < deadline)
                await Task.Delay(20);

            await Assert.That(pipeline.Generation).IsEqualTo(2);
            await Assert.That(pipeline.Current!.Interpreter.TextOf(1)).IsEqualTo("goodbye");

            // Debounced decisions precede the publish; find the one that
            // actually swapped the tree instead of assuming queue order.
            bool sawRebuild = false;
            while (seen.TryDequeue(out var observed))
            {
                if (observed.Outcome == ReloadCommitOutcome.Published && observed.DidPublish)
                {
                    sawRebuild = true;
                    break;
                }
            }

            await Assert.That(sawRebuild).IsTrue();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
