using Harbor.Ui.Framework.State;
using TUnit.Assertions;

namespace Harbor.Ui.Framework.Tests;

/// <summary>
///     #484 — the <see cref="AsyncStatus" /> → surface mapping is written
///     exactly once, in <see cref="AsyncDataBinder" />. These tests pin every
///     arm so the provider picker and the provider browser cannot drift apart
///     again (they had already: the browser surfaced failures, the picker
///     swallowed them).
/// </summary>
public sealed class AsyncDataBinderTests
{
    private sealed class RecordingSink : IAsyncDataSink<string>
    {
        public int LoadingCalls { get; private set; }
        public List<IReadOnlyList<string>> Loaded { get; } = [];
        public List<string> Errors { get; } = [];

        public void OnLoading() => LoadingCalls++;

        public void OnLoaded(IReadOnlyList<string> items) => Loaded.Add(items);

        public void OnError(string message) => Errors.Add(message);
    }

    private static AsyncData<IReadOnlyList<string>> Loading => AsyncData<IReadOnlyList<string>>.Idle.ToLoading();

    private static AsyncData<IReadOnlyList<string>> Refreshing =>
        AsyncData<IReadOnlyList<string>>.Success(["a"]).ToLoading();

    [Test]
    public async Task Apply_Loading_RoutesToOnLoading()
    {
        var sink = new RecordingSink();

        AsyncDataBinder.Apply(Loading, sink);

        await Assert.That(sink.LoadingCalls).IsEqualTo(1);
        await Assert.That(sink.Loaded).IsEmpty();
        await Assert.That(sink.Errors).IsEmpty();
    }

    [Test]
    public async Task Apply_Refreshing_RoutesToOnLoading_SoStaleRowsCannotLinger()
    {
        var sink = new RecordingSink();

        AsyncDataBinder.Apply(Refreshing, sink);

        await Assert.That(sink.LoadingCalls).IsEqualTo(1);
        await Assert.That(sink.Loaded).IsEmpty();
    }

    [Test]
    public async Task Apply_Success_RoutesRowsToOnLoaded()
    {
        var sink = new RecordingSink();
        string[] rows = ["gpt-5", "claude-opus-4"];

        AsyncDataBinder.Apply(AsyncData<IReadOnlyList<string>>.Success(rows), sink);

        await Assert.That(sink.LoadingCalls).IsEqualTo(0);
        await Assert.That(sink.Loaded.Count).IsEqualTo(1);
        await Assert.That(sink.Loaded[0].Count).IsEqualTo(2);
        await Assert.That(sink.Errors).IsEmpty();
    }

    [Test]
    public async Task Apply_SuccessWithoutValue_RoutesEmptyRows_NotAFailure()
    {
        var sink = new RecordingSink();

        AsyncDataBinder.Apply(new AsyncData<IReadOnlyList<string>>(AsyncStatus.Success), sink);

        await Assert.That(sink.Loaded.Count).IsEqualTo(1);
        await Assert.That(sink.Loaded[0]).IsEmpty();
        await Assert.That(sink.Errors).IsEmpty();
    }

    [Test]
    public async Task Apply_Error_RoutesMessageToOnError()
    {
        var sink = new RecordingSink();

        AsyncDataBinder.Apply(AsyncData<IReadOnlyList<string>>.Failed("connection refused"), sink);

        await Assert.That(sink.Errors.Count).IsEqualTo(1);
        await Assert.That(sink.Errors[0]).IsEqualTo("connection refused");
        await Assert.That(sink.Loaded).IsEmpty();
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Apply_ErrorWithoutMessage_FallsBackToUnknownError(string? error)
    {
        var sink = new RecordingSink();

        AsyncDataBinder.Apply(AsyncData<IReadOnlyList<string>>.Failed(error!), sink);

        await Assert.That(sink.Errors.Count).IsEqualTo(1);
        await Assert.That(sink.Errors[0]).IsEqualTo(AsyncDataBinder.UnknownError);
    }

    [Test]
    [Arguments(AsyncStatus.Idle)]
    [Arguments(AsyncStatus.None)]
    public async Task Apply_NonTerminalStatus_TouchesNothing(AsyncStatus status)
    {
        var sink = new RecordingSink();

        AsyncDataBinder.Apply(new AsyncData<IReadOnlyList<string>>(status), sink);

        await Assert.That(sink.LoadingCalls).IsEqualTo(0);
        await Assert.That(sink.Loaded).IsEmpty();
        await Assert.That(sink.Errors).IsEmpty();
    }
}
