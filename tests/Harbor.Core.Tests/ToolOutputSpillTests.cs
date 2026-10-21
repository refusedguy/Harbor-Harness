using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Providers;
using Harbor.Abstractions.Tools;
using Harbor.Application.Filesystem;
using Harbor.Application.Sessions;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Core.Tests;

/// <summary>
///     Tests for the tool-output spill (#1748). A tool result that does not fit
///     the inline budget is written to Harbor's private spill area; the model
///     receives a preview plus the locator, and the file holds the FULL output
///     so nothing is lost.
/// </summary>
/// <remarks>
///     Pinned:
///     <list type="bullet">
///       <item>the OVERFLOW case: over budget the context copy is a preview plus a pointer, and the file holds the whole output;</item>
///       <item>the READ case: the spilled file reads back byte-for-byte;</item>
///       <item>the MISSING FILE case: reading a file that is gone fails with a message that says so;</item>
///       <item>a store that cannot take the bytes leaves the output UNCUT — an overfull context beats a silent one;</item>
///       <item>the domain message keeps the full text whatever the context copy says;</item>
///       <item>a within-budget output never touches the store.</item>
///     </list>
/// </remarks>
public class ToolOutputSpillTests
{
    private const string SessionId = "session-spill";

    private static readonly DateTimeOffset Ts = DateTimeOffset.UtcNow;

    /// <summary>A fresh, empty root under the test cache. The spill area is
    /// per-session state, so every test gets its own.</summary>
    private static string TempRoot() => TestTempDirs.NewDirectory("spill");

    private static FileToolOutputSpillStore Store(string root) =>
        new(NullLogger<FileToolOutputSpillStore>.Instance, root);

    private static ToolResultMessage Results(ToolResultEntry entry) =>
        new("m-spill", SessionId, Ts, [entry]);

    // ── the overflow case ────────────────────────────────────────────────

    [Test]
    public async Task ToLlmMessages_OverBudget_SpillsTheOutput_AndPointsAtTheFile()
    {
        string root = TempRoot();
        var store = Store(root);
        var converter = new MessageConverter(store, ToolResultContextTrim.MaxToolResultChars);
        // The payload is comfortably past the budget: the pointer the model
        // receives (path + size + instruction) is itself a few hundred
        // characters, and the assertion below compares the two lengths.
        string payload = new string('x', ToolResultContextTrim.MaxToolResultChars + 2000);

        var result = converter.ToLlmMessages(
            [Results(new ToolResultEntry("call-1", "bash", payload, false))]);

        var llm = (LlmToolResultMessage)result[0];
        await Assert.That(llm.Output).Contains("[spilled:");
        await Assert.That(llm.Output.Length).IsLessThan(payload.Length);

        // The pointer names the file the store wrote, and that file holds the
        // FULL output — the tail is not lost, it is somewhere the model can ask
        // for.
        var spilled = store.Spill(SessionId, "call-1", payload);
        await Assert.That(spilled.IsSuccess).IsTrue();
        await Assert.That(llm.Output).Contains(spilled.Value.Path);
        await Assert.That(llm.Output).Contains(spilled.Value.SizeBytes.ToString());

        await Assert.That(File.Exists(spilled.Value.Path)).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(spilled.Value.Path)).IsEqualTo(payload);
    }

    [Test]
    public async Task ToLlmMessages_OverBudget_KeepsTheFullTextInTheDomainMessage()
    {
        var converter = new MessageConverter(Store(TempRoot()), ToolResultContextTrim.MaxToolResultChars);
        string payload = new string('x', ToolResultContextTrim.MaxToolResultChars + 2000);
        var domain = Results(new ToolResultEntry("call-1", "bash", payload, false));

        _ = converter.ToLlmMessages([domain]);

        // The session log, the event bus and the TUI still see everything the
        // tool produced; only the provider-bound copy is shortened.
        await Assert.That(domain.Results[0].Output).IsEqualTo(payload);
    }

    [Test]
    public async Task ToLlmMessages_UnderBudget_NeverTouchesTheStore()
    {
        var store = new RecordingStore();
        var converter = new MessageConverter(store, 1000);
        const string payload = "small enough";

        var result = converter.ToLlmMessages(
            [Results(new ToolResultEntry("call-1", "read", payload, false))]);

        var llm = (LlmToolResultMessage)result[0];
        await Assert.That(llm.Output).IsEqualTo(payload);
        await Assert.That(store.SpillCount).IsEqualTo(0);
    }

    [Test]
    public async Task ToLlmMessages_StoreFails_KeepsTheOutputUncut()
    {
        // The store could not take the bytes. The original goes to the model:
        // an overfull context is visible and recoverable, a silently cut one
        // is not.
        var converter = new MessageConverter(new FailingStore(), 100);
        string payload = new string('z', 500);

        var result = converter.ToLlmMessages(
            [Results(new ToolResultEntry("call-1", "bash", payload, false))]);

        var llm = (LlmToolResultMessage)result[0];
        await Assert.That(llm.Output).IsEqualTo(payload);
    }

    // ── the read case ────────────────────────────────────────────────────

    [Test]
    public async Task Spill_ThenRead_ReturnsTheFullOutput()
    {
        var store = Store(TempRoot());
        string payload = new string('y', 5000);

        var spilled = store.Spill(SessionId, "call-1", payload);
        await Assert.That(spilled.IsSuccess).IsTrue();

        var read = await store.ReadAsync(SessionId, spilled.Value.Path, CancellationToken.None);
        await Assert.That(read.IsSuccess).IsTrue();
        await Assert.That(read.Value).IsEqualTo(payload);
    }

    [Test]
    public async Task Spill_Twice_PointsAtTheSameFile()
    {
        var store = Store(TempRoot());
        string payload = new string('y', 5000);

        var first = store.Spill(SessionId, "call-1", payload);
        var second = store.Spill(SessionId, "call-1", payload);

        await Assert.That(second.IsSuccess).IsTrue();
        await Assert.That(second.Value.Path).IsEqualTo(first.Value.Path);
        await Assert.That(second.Value.SizeBytes).IsEqualTo(first.Value.SizeBytes);
    }

    // ── the missing file case ────────────────────────────────────────────

    [Test]
    public async Task ReadAsync_MissingFile_Fails()
    {
        var store = Store(TempRoot());
        var spilled = store.Spill(SessionId, "call-1", "content");
        await Assert.That(spilled.IsSuccess).IsTrue();
        File.Delete(spilled.Value.Path);

        var read = await store.ReadAsync(SessionId, spilled.Value.Path, CancellationToken.None);

        await Assert.That(read.IsFailure).IsTrue();
        await Assert.That(read.Error).Contains("not found");
    }

    [Test]
    public async Task ReadAsync_PathOutsideTheSession_Fails()
    {
        string root = TempRoot();
        var store = Store(root);
        var spilled = store.Spill(SessionId, "call-1", "content");
        await Assert.That(spilled.IsSuccess).IsTrue();

        // Another session's file is not this session's to read.
        var read = await store.ReadAsync("another-session", spilled.Value.Path, CancellationToken.None);

        await Assert.That(read.IsFailure).IsTrue();
        await Assert.That(read.Error).Contains("not a spill file of this session");
    }

    [Test]
    public async Task ReadAsync_PathOutsideTheSpillArea_Fails()
    {
        var store = Store(TempRoot());
        string outside = Path.Combine(TempRoot(), "elsewhere.txt");
        await File.WriteAllTextAsync(outside, "not a spill file");

        var read = await store.ReadAsync(SessionId, outside, CancellationToken.None);

        await Assert.That(read.IsFailure).IsTrue();
        await Assert.That(read.Error).Contains("not a spill file of this session");
    }

    /// <summary>A store that records the calls it received.</summary>
    private sealed class RecordingStore : IToolOutputSpillStore
    {
        public int SpillCount { get; private set; }

        public string Root => "/recording";

        public Result<ToolOutputSpill> Spill(string sessionId, string toolCallId, string content)
        {
            SpillCount++;
            return Result.Success(new ToolOutputSpill("/recording/file.txt", content.Length, content.Length));
        }

        public Task<Result<string>> ReadAsync(string sessionId, string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Failure<string>("the recording store never holds a file."));

        public Result<int> Sweep() => Result.Success(0);
    }

    /// <summary>A store whose writes always fail — a full disk, a denial.</summary>
    private sealed class FailingStore : IToolOutputSpillStore
    {
        public string Root => "/failing";

        public Result<ToolOutputSpill> Spill(string sessionId, string toolCallId, string content) =>
            Result.Failure<ToolOutputSpill>("Cannot spill the tool output: disk full.");

        public Task<Result<string>> ReadAsync(string sessionId, string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Failure<string>("the failing store never holds a file."));

        public Result<int> Sweep() => Result.Failure<int>("the failing store never holds a directory.");
    }
}
