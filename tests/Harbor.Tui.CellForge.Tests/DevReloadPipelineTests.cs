using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Harbor.Tui.CellForge.CodeGen.DevReload;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     Issue #2434: the watcher layer debounces writes, compiles a complete
///     candidate before publishing, refuses a swap during render/layout and
///     keeps the last valid frame on failure or on a stale generation.
/// </summary>
public class DevReloadPipelineTests
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

    private const string V3 = """
        panel "main":
            .text "third"
            text "done"
        """;

    private static readonly DateTimeOffset T0 = new(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);

    private sealed class Source
    {
        private string _text;

        internal Source(string text) => _text = text;

        internal string Read() => _text;

        internal void Set(string text) => _text = text;
    }

    private sealed class CountingCompiler : IDevReloadCompiler
    {
        private readonly Func<string, ReloadCandidate> _compile;

        internal CountingCompiler(Func<string, ReloadCandidate>? compile = null) =>
            _compile = compile ?? (text => DevReloadInterpreter.TryCreate(text, out var interpreter, out var error)
                ? ReloadCandidate.Compiled(interpreter!)
                : ReloadCandidate.Failed(error));

        internal int Calls;

        public ReloadCandidate Compile(string markup)
        {
            Interlocked.Increment(ref Calls);
            return _compile(markup);
        }
    }

    private sealed class SwitchableCompiler : IDevReloadCompiler
    {
        private readonly IDevReloadCompiler _inner = new DevReloadInterpreterCompiler();

        internal bool Fail;

        public ReloadCandidate Compile(string markup) => Fail
            ? ReloadCandidate.Failed("source does not parse")
            : _inner.Compile(markup);
    }

    private sealed class BlockingCompiler : IDevReloadCompiler
    {
        private readonly IDevReloadCompiler _inner = new DevReloadInterpreterCompiler();
        private readonly SemaphoreSlim _started;
        private readonly SemaphoreSlim _release;
        private int _blockNext;

        internal BlockingCompiler(SemaphoreSlim started, SemaphoreSlim release)
        {
            _started = started;
            _release = release;
        }

        internal void BlockNext() => Volatile.Write(ref _blockNext, 1);

        public ReloadCandidate Compile(string markup)
        {
            if (Interlocked.Exchange(ref _blockNext, 0) == 1)
            {
                _started.Release();
                if (!_release.Wait(TimeSpan.FromSeconds(10)))
                    return ReloadCandidate.Failed("blocked compile timed out");
            }

            return _inner.Compile(markup);
        }
    }

    private static DevReloadPipeline Pipeline(
        Source source,
        IDevReloadCompiler? compiler = null,
        IDevReloadFrameGate? gate = null) =>
        new(source.Read, compiler, gate, DevReloadTiming.Debounce);

    [Test]
    public async Task Initialize_PublishesFirstTreeWithGenerationOne()
    {
        var pipeline = Pipeline(new Source(V1));

        var result = pipeline.Initialize(new FileStamp(1, 10));

        await Assert.That(result.Outcome).IsEqualTo(ReloadCommitOutcome.Published);
        await Assert.That(pipeline.Generation).IsEqualTo(1);
        await Assert.That(pipeline.Current!.Interpreter.TextOf(1)).IsEqualTo("hello");
        await Assert.That(pipeline.Current.SourceChecksum).IsNotEmpty();
    }

    [Test]
    public async Task Timing_DefaultsMatchTheSpec()
    {
        await Assert.That(DevReloadTiming.Debounce).IsEqualTo(TimeSpan.FromMilliseconds(200));
        await Assert.That(DevReloadTiming.PollInterval).IsEqualTo(TimeSpan.FromMilliseconds(250));
    }

    [Test]
    public async Task Poll_UnchangedStamp_DoesNotCompileAgain()
    {
        var compiler = new CountingCompiler();
        var pipeline = Pipeline(new Source(V1), compiler);
        pipeline.Initialize(new FileStamp(1, 10));
        int calls = compiler.Calls;

        var result = pipeline.Poll(new FileStamp(1, 10), T0.AddSeconds(5));

        await Assert.That(result.Outcome).IsEqualTo(ReloadCommitOutcome.Unchanged);
        await Assert.That(compiler.Calls).IsEqualTo(calls);
        await Assert.That(pipeline.Generation).IsEqualTo(1);
    }

    [Test]
    public async Task Poll_BeforeDebounce_KeepsThePublishedTree()
    {
        var source = new Source(V1);
        var pipeline = Pipeline(source);
        pipeline.Initialize(new FileStamp(1, 10));
        var published = pipeline.Current;
        source.Set(V2);

        var early = pipeline.Poll(new FileStamp(2, 11), T0);
        var almost = pipeline.Poll(new FileStamp(2, 11), T0.AddMilliseconds(199));

        await Assert.That(early.Outcome).IsEqualTo(ReloadCommitOutcome.Debounced);
        await Assert.That(almost.Outcome).IsEqualTo(ReloadCommitOutcome.Debounced);
        await Assert.That(ReferenceEquals(published, pipeline.Current)).IsTrue();
        await Assert.That(pipeline.Generation).IsEqualTo(1);
    }

    [Test]
    public async Task Poll_AfterDebounce_PublishesTheLastOfABurst()
    {
        var source = new Source(V1);
        var pipeline = Pipeline(source);
        pipeline.Initialize(new FileStamp(1, 10));
        var published = pipeline.Current;

        source.Set(V2);
        pipeline.Poll(new FileStamp(2, 11), T0);
        source.Set(V3);
        pipeline.Poll(new FileStamp(3, 12), T0.AddMilliseconds(80));
        var result = pipeline.Poll(new FileStamp(3, 12), T0.AddMilliseconds(280));

        await Assert.That(result.Outcome).IsEqualTo(ReloadCommitOutcome.Published);
        await Assert.That(result.DidPublish).IsTrue();
        await Assert.That(pipeline.Generation).IsEqualTo(2);
        await Assert.That(ReferenceEquals(published, pipeline.Current)).IsFalse();
        await Assert.That(pipeline.Current!.Interpreter.TextOf(1)).IsEqualTo("third");
    }

    [Test]
    public async Task Poll_DuringRenderOrLayout_IsRejectedAndRetriedAfterTheFrame()
    {
        var gate = new DevReloadFrameGate();
        var source = new Source(V1);
        var pipeline = Pipeline(source, gate: gate);
        pipeline.Initialize(new FileStamp(1, 10));
        var published = pipeline.Current;
        source.Set(V2);
        pipeline.Poll(new FileStamp(2, 11), T0);

        using (gate.EnterFrame())
        {
            var rejected = pipeline.Poll(new FileStamp(2, 11), T0.AddMilliseconds(300));

            await Assert.That(rejected.Outcome).IsEqualTo(ReloadCommitOutcome.FramePhaseRejected);
            await Assert.That(ReferenceEquals(published, pipeline.Current)).IsTrue();
            await Assert.That(pipeline.Generation).IsEqualTo(1);
            await Assert.That(pipeline.Current!.Interpreter.TextOf(1)).IsEqualTo("hello");
        }

        var retried = pipeline.Poll(new FileStamp(2, 11), T0.AddMilliseconds(600));

        await Assert.That(retried.Outcome).IsEqualTo(ReloadCommitOutcome.Published);
        await Assert.That(pipeline.Current!.Interpreter.TextOf(1)).IsEqualTo("goodbye");
    }

    [Test]
    public async Task Poll_CompileFailure_KeepsTheLastValidFrame()
    {
        var compiler = new SwitchableCompiler();
        var source = new Source(V1);
        var pipeline = Pipeline(source, compiler);
        pipeline.Initialize(new FileStamp(1, 10));
        var valid = pipeline.Current;
        compiler.Fail = true;
        source.Set(V2);
        pipeline.Poll(new FileStamp(2, 11), T0);

        var rejected = pipeline.Poll(new FileStamp(2, 11), T0.AddMilliseconds(300));

        await Assert.That(rejected.Outcome).IsEqualTo(ReloadCommitOutcome.CompileRejected);
        await Assert.That(ReferenceEquals(valid, pipeline.Current)).IsTrue();
        await Assert.That(pipeline.Current!.Interpreter.TextOf(1)).IsEqualTo("hello");

        compiler.Fail = false;
        pipeline.Poll(new FileStamp(3, 12), T0.AddMilliseconds(600));
        var recovered = pipeline.Poll(new FileStamp(3, 12), T0.AddMilliseconds(900));

        await Assert.That(recovered.Outcome).IsEqualTo(ReloadCommitOutcome.Published);
        await Assert.That(pipeline.Current!.Interpreter.TextOf(1)).IsEqualTo("goodbye");
    }

    [Test]
    public async Task Poll_StaleGenerationCandidate_IsDiscarded()
    {
        using var started = new SemaphoreSlim(0, 1);
        using var release = new SemaphoreSlim(0, 1);
        var compiler = new BlockingCompiler(started, release);
        var source = new Source(V1);
        var pipeline = Pipeline(source, compiler);
        var t0 = T0.AddMilliseconds(300);
        pipeline.Initialize(new FileStamp(1, 10));
        compiler.BlockNext();

        source.Set(V2);
        pipeline.Poll(new FileStamp(2, 11), t0);
        var slow = Task.Run(() => pipeline.Poll(new FileStamp(2, 11), t0.AddMilliseconds(300)));
        await started.WaitAsync(TimeSpan.FromSeconds(10));

        source.Set(V3);
        pipeline.Poll(new FileStamp(3, 12), t0.AddMilliseconds(600));
        var fast = pipeline.Poll(new FileStamp(3, 12), t0.AddMilliseconds(900));

        await Assert.That(fast.Outcome).IsEqualTo(ReloadCommitOutcome.Published);
        await Assert.That(pipeline.Generation).IsEqualTo(2);
        await Assert.That(pipeline.Current!.Interpreter.TextOf(1)).IsEqualTo("third");

        release.Release();
        var stale = await slow.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(stale.Outcome).IsEqualTo(ReloadCommitOutcome.StaleGeneration);
        await Assert.That(pipeline.Generation).IsEqualTo(2);
        await Assert.That(pipeline.Current!.Interpreter.TextOf(1)).IsEqualTo("third");
    }

    [Test]
    public async Task Watcher_PublishesInitialTreeAndStopsOnDispose()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, V1);
            var pipeline = Pipeline(new Source(V1));
            using var watcher = new DevFileWatcher(path, pipeline, pollInterval: DevReloadTiming.PollInterval);

            await Assert.That(watcher.InitialResult.Outcome).IsEqualTo(ReloadCommitOutcome.Published);
            await Assert.That(watcher.FilePath).IsEqualTo(path);
            await Assert.That(ReferenceEquals(watcher.Pipeline, pipeline)).IsTrue();
            await Assert.That(pipeline.Current!.Interpreter.TextOf(1)).IsEqualTo("hello");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public async Task FrameGate_NestedScopes_CloseOnlyWithTheOutermostLease()
    {
        var gate = new DevReloadFrameGate();

        await Assert.That(gate.IsFrameInProgress).IsFalse();
        var outer = gate.EnterFrame();
        var inner = gate.EnterFrame();
        await Assert.That(gate.IsFrameInProgress).IsTrue();
        inner.Dispose();
        await Assert.That(gate.IsFrameInProgress).IsTrue();
        outer.Dispose();
        await Assert.That(gate.IsFrameInProgress).IsFalse();
        outer.Dispose();
        await Assert.That(gate.IsFrameInProgress).IsFalse();
    }
}
