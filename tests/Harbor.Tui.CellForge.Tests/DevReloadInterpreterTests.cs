using System;
using System.IO;
using System.Linq;
using Harbor.Tui.CellForge.CodeGen.DevReload;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     Issue #2433: the dev interpreter re-parses markup, decides patch versus
///     rebuild from the emptied template, and keeps per-id session state across
///     a patch. A logic change rebuilds and requests a host restart.
/// </summary>
public class DevReloadInterpreterTests
{
    private const string BaseMarkup = """
        panel "main":
            .text "hello"
            .height 10
            text "done"
        """;

    private static DevReloadInterpreter Interpreter(string markup = BaseMarkup)
    {
        if (!DevReloadInterpreter.TryCreate(markup, out var interpreter, out var error))
            throw new InvalidOperationException("Fixture markup must parse: " + error);
        return interpreter!;
    }

    // ── 1. Dev parse uses the one shared parser and builds the live tree ──

    [Test]
    public async Task TryCreate_ParsesMarkupIntoALiveElementTree()
    {
        var interpreter = Interpreter();

        await Assert.That(interpreter.LiveRootId).IsEqualTo(1);
        await Assert.That(interpreter.Revision).IsEqualTo(1);
        await Assert.That(interpreter.Contains(1)).IsTrue();
        await Assert.That(interpreter.Contains(2)).IsTrue();
        await Assert.That(interpreter.TextOf(1)).IsEqualTo("hello");
        await Assert.That(interpreter.TextOf(2)).IsEqualTo("done");
        await Assert.That(interpreter.ChildrenOf(1).SequenceEqual([2])).IsTrue();
    }

    [Test]
    public async Task TryCreate_InvalidMarkup_ReturnsErrorWithoutAnInterpreter()
    {
        var created = DevReloadInterpreter.TryCreate("not_a_widget \"x\"", out var interpreter, out var error);

        await Assert.That(created).IsFalse();
        await Assert.That(interpreter).IsNull();
        await Assert.That(error).Contains("Unknown node kind");
    }

    // ── 2. Template diff empties bodies and ignores comments ─────────────

    [Test]
    public async Task TemplateSignature_IsIdenticalWhenOnlyTextValuesChange()
    {
        var first = Interpreter();
        var second = Interpreter("""
            panel "main":
                .text "goodbye"
                .height 99
                text "changed"
            """);

        await Assert.That(second.TemplateSignature).IsEqualTo(first.TemplateSignature);
    }

    [Test]
    public async Task TemplateSignature_DiffersWhenTheShapeChanges()
    {
        var first = Interpreter();
        var second = Interpreter("""
            panel "main":
                .text "hello"
                .height 10
                text "done"
                text "extra"
            """);

        await Assert.That(second.TemplateSignature).IsNotEqualTo(first.TemplateSignature);
    }

    [Test]
    public async Task Reload_CommentOnlyEdit_IsUnchangedAndKeepsRevision()
    {
        var interpreter = Interpreter();
        int revision = interpreter.Revision;

        var result = interpreter.Reload("# a brand new comment\n" + BaseMarkup);

        await Assert.That(result.Outcome).IsEqualTo(ReloadOutcome.Unchanged);
        await Assert.That(result.Revision).IsEqualTo(revision);
        await Assert.That(result.PreservedElementIds.Count).IsEqualTo(0);
    }

    // ── 3. Patch: markup change updates the tree, keeps state/focus/scroll ──

    [Test]
    public async Task Reload_TextValueChange_PatchesInPlaceAndPreservesSessionState()
    {
        var interpreter = Interpreter();
        interpreter.SetSession(1, new ElementSessionState("counter", Focused: true, ScrollOffset: 7));
        interpreter.SetSession(2, new ElementSessionState(null, Focused: false, ScrollOffset: 3));
        int revision = interpreter.Revision;

        var result = interpreter.Reload("""
            panel "main":
                .text "hello world"
                .height 10
                text "done"
            """);

        await Assert.That(result.Outcome).IsEqualTo(ReloadOutcome.Patched);
        await Assert.That(result.HotApplied).IsTrue();
        await Assert.That(result.NeedsRestart).IsFalse();
        await Assert.That(result.Revision).IsEqualTo(revision + 1);

        // The tree moved to the new value on the same ids.
        await Assert.That(interpreter.TextOf(1)).IsEqualTo("hello world");
        await Assert.That(interpreter.TextOf(2)).IsEqualTo("done");
        await Assert.That(interpreter.ChildrenOf(1).SequenceEqual([2])).IsTrue();

        // VM state, focus and scroll on unchanged positions survived the patch.
        await Assert.That(result.PreservedElementIds.SequenceEqual([1, 2])).IsTrue();
        await Assert.That(interpreter.SessionOf(1)).IsEqualTo(
            new ElementSessionState("counter", Focused: true, ScrollOffset: 7));
        await Assert.That(interpreter.SessionOf(2)).IsEqualTo(
            new ElementSessionState(null, Focused: false, ScrollOffset: 3));
    }

    [Test]
    public async Task Reload_StyleValueChange_PatchesTheStyleOnTheSameId()
    {
        var interpreter = Interpreter();
        int? before = interpreter.StyleIdOf(1);
        interpreter.SetSession(1, new ElementSessionState("keep", Focused: true, ScrollOffset: 5));

        var result = interpreter.Reload("""
            panel "main":
                .text "hello"
                .height 20
                text "done"
            """);

        await Assert.That(result.Outcome).IsEqualTo(ReloadOutcome.Patched);
        await Assert.That(interpreter.StyleIdOf(1)).IsNotEqualTo(before);
        await Assert.That(interpreter.SessionOf(1)?.ViewModelState).IsEqualTo("keep");
        await Assert.That(interpreter.SessionOf(1)?.Focused).IsTrue();
        await Assert.That(interpreter.SessionOf(1)?.ScrollOffset).IsEqualTo(5);
    }

    // ── 4. Rebuild and restart fallbacks ─────────────────────────────────

    [Test]
    public async Task Reload_TemplateShapeChange_RebuildsAndDropsSessionState()
    {
        var interpreter = Interpreter();
        interpreter.SetSession(1, new ElementSessionState("counter", Focused: true, ScrollOffset: 7));

        var result = interpreter.Reload("""
            panel "main":
                .text "hello"
                .height 10
                text "done"
                text "extra"
            """);

        await Assert.That(result.Outcome).IsEqualTo(ReloadOutcome.Rebuilt);
        await Assert.That(result.HotApplied).IsFalse();
        await Assert.That(result.NeedsRestart).IsFalse();
        await Assert.That(result.PreservedElementIds.Count).IsEqualTo(0);
        await Assert.That(interpreter.SessionOf(1)).IsNull();
        await Assert.That(interpreter.Contains(3)).IsTrue();
    }

    [Test]
    public async Task Reload_StateDirectiveChange_RebuildsAndRequestsRestart()
    {
        var interpreter = Interpreter("""
            state: Sample.CounterState
            panel "main":
                .text "hello"
            """);
        interpreter.SetSession(1, new ElementSessionState("counter", Focused: true, ScrollOffset: 7));

        var result = interpreter.Reload("""
            state: Sample.OtherState
            panel "main":
                .text "hello"
            """);

        await Assert.That(result.Outcome).IsEqualTo(ReloadOutcome.RestartRequired);
        await Assert.That(result.NeedsRestart).IsTrue();
        await Assert.That(result.PreservedElementIds.Count).IsEqualTo(0);
        await Assert.That(interpreter.SessionOf(1)).IsNull();
    }

    [Test]
    public async Task Reload_HandlerValueChange_RequestsRestartWithoutARebuildShapeChange()
    {
        var interpreter = Interpreter("""
            panel "main":
                .on_click "Save"
                text "ok"
            """);

        var result = interpreter.Reload("""
            panel "main":
                .on_click "Load"
                text "ok"
            """);

        await Assert.That(result.Outcome).IsEqualTo(ReloadOutcome.RestartRequired);
        // A rebuild installs a fresh applier, whose revision starts over at 1.
        await Assert.That(result.Revision).IsEqualTo(1);
        await Assert.That(result.NeedsRestart).IsTrue();
    }

    [Test]
    public async Task Reload_InvalidMarkup_RejectsAndLeavesTheLiveTreeUntouched()
    {
        var interpreter = Interpreter();
        interpreter.SetSession(1, new ElementSessionState("keep", Focused: true, ScrollOffset: 9));
        int revision = interpreter.Revision;

        var result = interpreter.Reload("not_a_widget \"x\"");

        await Assert.That(result.Outcome).IsEqualTo(ReloadOutcome.Rejected);
        await Assert.That(result.Revision).IsEqualTo(revision);
        await Assert.That(interpreter.TextOf(1)).IsEqualTo("hello");
        await Assert.That(interpreter.SessionOf(1)?.ScrollOffset).IsEqualTo(9);
    }

    // ── 5. Dev log channel: diagnostics never go through the renderer ────

    [Test]
    public async Task Reload_WritesOneDecisionToTheDevLogChannelAndItsSink()
    {
        var lines = new List<string>();
        var channel = new DevLogChannel(lines.Add);
        var interpreter = Interpreter();
        interpreter.AttachLog(channel);

        var result = interpreter.Reload("""
            panel "main":
                .text "hello world"
                .height 10
                text "done"
            """);

        var records = channel.Read();
        await Assert.That(records.Count).IsEqualTo(1);
        await Assert.That(records[0].Source).IsEqualTo("interpreter");
        await Assert.That(records[0].Message).Contains(result.Outcome.ToString());

        var parsed = DevLogChannel.ParseLines(lines, 10);
        await Assert.That(parsed.Count).IsEqualTo(1);
        await Assert.That(parsed[0].Message).IsEqualTo(records[0].Message);
        await Assert.That(parsed[0].At.ToUnixTimeMilliseconds()).IsEqualTo(records[0].At.ToUnixTimeMilliseconds());
    }

    [Test]
    public async Task DevLogChannel_BoundedTailReturnsTheNewestRecords()
    {
        var channel = new DevLogChannel(capacity: 2);

        channel.Write("watcher", "first");
        channel.Write("interpreter", "second");
        channel.Write("watcher", "third");

        var records = channel.Read();
        await Assert.That(records.Count).IsEqualTo(2);
        await Assert.That(records[0].Message).IsEqualTo("second");
        await Assert.That(records[1].Message).IsEqualTo("third");
        await Assert.That(DevLogChannel.ParseLines(Array.Empty<string>(), 5).Count).IsEqualTo(0);
    }

    [Test]
    public async Task DevFileWatcher_LogsDetectionAndAppliedReloadToTheChannel()
    {
        string directory = Path.Combine(Path.GetTempPath(), "harbor-devwatch-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "sample.harbor-ui");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, BaseMarkup);
            DateTime stamp = File.GetLastWriteTimeUtc(path);

            var channel = new DevLogChannel();
            var interpreter = Interpreter();
            interpreter.AttachLog(channel);
            var source = new DevMarkupFileSource(() => stamp, () => File.ReadAllText(path));
            using var watcher = new DslDevFileWatcher(
                source, interpreter, channel, pollInterval: TimeSpan.Zero, debounce: TimeSpan.Zero);

            watcher.Poll();
            await Assert.That(channel.Read().Count).IsEqualTo(0);

            File.WriteAllText(path, BaseMarkup.Replace("hello", "changed", StringComparison.Ordinal));
            stamp = stamp.AddSeconds(2);
            watcher.Poll(); // detects the edit, starts the debounce window
            watcher.Poll(); // applies one reload

            var messages = channel.Read(10).Select(entry => $"[{entry.Source}] {entry.Message}").ToArray();
            await Assert.That(messages.Any(message => message.Contains("[watcher] change detected"))).IsTrue();
            await Assert.That(messages.Any(message => message.Contains("[interpreter] Patched"))).IsTrue();
            await Assert.That(messages.Any(message => message.Contains("[watcher] applied Patched"))).IsTrue();
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
