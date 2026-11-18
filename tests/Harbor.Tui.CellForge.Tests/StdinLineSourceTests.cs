using System.Collections.Concurrent;
using CSharpFunctionalExtensions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// #2639: <see cref="StdinLineSource"/> must have exactly one owner of stdin
/// for a renderer's lifetime. Cancelling a read detaches that caller only; the
/// pump keeps its single blocking read, so the next line is handed to the next
/// caller instead of being stolen by an orphaned second reader.
/// </summary>
public class StdinLineSourceTests
{
    [Test]
    public async Task CancelledRead_LeavesOneReader_NextReadGetsTheLine()
    {
        var stdin = new ManualStdin();
        using var source = new StdinLineSource(stdin.ReadLine);
        using var cts = new CancellationTokenSource();

        Task<Maybe<string>> first = source.ReadLineAsync(cts.Token).AsTask();
        await stdin.WaitForReaderAsync();

        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await first);

        Task<Maybe<string>> second = source.ReadLineAsync().AsTask();
        await Assert.That(stdin.ActiveReaders).IsEqualTo(1)
            .Because("cancelling a read must not start a second competing reader on stdin");

        stdin.Enqueue("typed-after-cancel");
        Maybe<string> result = await second;
        await Assert.That(result.HasValue).IsTrue();
        await Assert.That(result.Value).IsEqualTo("typed-after-cancel")
            .Because("the line typed after cancellation belongs to the real prompt, not an orphan");
    }

    [Test]
    public async Task PreCancelledToken_ThrowsWithoutEverReadingStdin()
    {
        var stdin = new ManualStdin();
        using var source = new StdinLineSource(stdin.ReadLine);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await source.ReadLineAsync(cts.Token));

        await Assert.That(stdin.TotalCalls).IsEqualTo(0)
            .Because("a pre-cancelled read must throw before the single pump ever touches stdin");
    }

    [Test]
    public async Task Eof_YieldsAbsence_EvenForLaterReads()
    {
        var stdin = new ManualStdin();
        using var source = new StdinLineSource(stdin.ReadLine);

        stdin.Enqueue(null); // EOF
        Maybe<string> first = await source.ReadLineAsync();
        Maybe<string> later = await source.ReadLineAsync();

        await Assert.That(first.HasNoValue).IsTrue();
        await Assert.That(later.HasNoValue).IsTrue()
            .Because("EOF is absence, not a value, and stays absent after the channel closes (#589)");
    }

    [Test]
    public async Task EmptySubmission_IsPresent_NotAbsence()
    {
        var stdin = new ManualStdin();
        using var source = new StdinLineSource(stdin.ReadLine);

        stdin.Enqueue(string.Empty); // user pressed Enter on an empty line
        Maybe<string> result = await source.ReadLineAsync();

        await Assert.That(result.HasValue).IsTrue();
        await Assert.That(result.Value).IsEqualTo(string.Empty)
            .Because("an empty submission is distinct from EOF (#589)");
    }

    /// <summary>Controlled stdin: one blocking line reader plus call accounting.</summary>
    private sealed class ManualStdin
    {
        private readonly BlockingCollection<string?> _lines = new();
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active;
        private int _total;

        public string? ReadLine()
        {
            Interlocked.Increment(ref _total);
            Interlocked.Increment(ref _active);
            _entered.TrySetResult();
            try
            {
                return _lines.Take();
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public Task WaitForReaderAsync() => _entered.Task;

        public int ActiveReaders => Volatile.Read(ref _active);

        public int TotalCalls => Volatile.Read(ref _total);

        public void Enqueue(string? line) => _lines.Add(line);
    }
}
