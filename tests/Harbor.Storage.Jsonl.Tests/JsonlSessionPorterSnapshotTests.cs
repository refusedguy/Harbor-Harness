using System.Text;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Storage.Jsonl.Tests;

/// <summary>
///     #1785 — the export must be a SNAPSHOT: every part of it assembled from
///     ONE read of the session, so the stats it carries describe the history it
///     carries.
/// </summary>
/// <remarks>
/// <para>
///     The export used to read the session three times — the session record,
///     the stats, the messages — and a write landing between those reads (a
///     live agent appending, a compaction rewriting the file) put the stats of
///     one generation next to the history of another. An export is a "send me
///     the log" artefact, so a log whose header counts disagree with its body
///     misleads whoever reads it.
/// </para>
/// <para>
///     The three tests below pin the three ways that can go wrong: the export
///     reading more than one generation (the split store), a write landing
///     DURING the export (the appending writer), and a file whose records
///     contradict each other (the crafted file).
/// </para>
/// </remarks>
public class JsonlSessionPorterSnapshotTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static JsonlSessionStore CreateStore()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"harbor-porter-snapshot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        return new JsonlSessionStore(tempDir, NullLogger<JsonlSessionStore>.Instance);
    }

    private static void Drop(JsonlSessionStore store)
    {
        if (Directory.Exists(store.GetRootDirectory()))
            Directory.Delete(store.GetRootDirectory(), true);
    }

    /// <summary>One assistant message, so the derived stats have something to count.</summary>
    private static AssistantMessage Assistant(string sessionId, string id, int input, int output) =>
        new(id, sessionId, Epoch, [new TextPart("hello")], StopReason.Stop, new Usage(input, output), "test-model");

    /// <summary>
    ///     The export payload: line 1 is the envelope, every following line is
    ///     one exported message.
    /// </summary>
    private static (JsonElement Envelope, List<JsonElement> Messages) ParseExport(string payload)
    {
        string[] lines = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        JsonElement envelope;
        using (JsonDocument document = JsonDocument.Parse(lines[0]))
        {
            envelope = document.RootElement.Clone();
        }

        var messages = new List<JsonElement>();
        for (int i = 1; i < lines.Length; i++)
        {
            using JsonDocument document = JsonDocument.Parse(lines[i]);
            messages.Add(document.RootElement.Clone());
        }

        return (envelope, messages);
    }

    // ── 1. the export is assembled from ONE generation ─────────────────────

    [Test]
    public async Task Export_AssemblesEveryPartFromOneSnapshot()
    {
        // The store's own snapshot is generation A. The double's individual
        // read APIs answer with generation B — a different title, a different
        // history, a different stats record. An export assembled from more
        // than one read mixes them; an export assembled from the snapshot is
        // generation A all the way down.
        var store = CreateStore();
        try
        {
            Session created = (await store.CreateAsync("/proj", "code", "anthropic", "claude-opus-4")).Value;
            await store.UpdateAsync(created with { Title = "generation A" });
            await store.AppendMessageAsync(created.Id, Assistant(created.Id, "m-a", 10, 5));

            var snapshot = await ((ISessionSnapshotSource)store).ReadSessionSnapshotAsync(created.Id, default);
            await Assert.That(snapshot.IsSuccess).IsTrue();

            var split = new SplitGenerationStore(store, snapshot.Value);
            var porter = new JsonlSessionPorter(NullLogger<JsonlSessionPorter>.Instance);
            var payload = new StringWriter();

            var exported = await porter.ExportAsync(split, created.Id, payload);
            await Assert.That(exported.IsSuccess).IsTrue();

            var (envelope, messages) = ParseExport(payload.ToString());
            await Assert.That(envelope.GetProperty("session").GetProperty("title").GetString())
                .IsEqualTo("generation A");
            await Assert.That(messages.Count).IsEqualTo(1);
            await Assert.That(messages[0].GetProperty("id").GetString()).IsEqualTo("m-a");

            // The stats describe the history under them.
            int counted = messages.Count(m => m.GetProperty("role").GetString() == "assistant");
            await Assert.That(envelope.GetProperty("metadata").GetProperty("messageCount").GetInt32())
                .IsEqualTo(counted);
            await Assert.That(envelope.GetProperty("session").GetProperty("metadata").GetProperty("messageCount").GetInt32())
                .IsEqualTo(counted);
        }
        finally
        {
            Drop(store);
        }
    }

    // ── 2. a write landing DURING the export ──────────────────────────────

    [Test]
    public async Task Export_AppendDuringTheExport_KeepsTheSnapshotConsistent()
    {
        // The writer appends a message to the session while the export is
        // running — the "live agent keeps talking" case. The export may or may
        // not contain that message (which instant it was taken at is not
        // promised), but its stats and its body must agree with EACH OTHER:
        // a log whose header counts disagree with its body misleads the
        // incident review it was collected for.
        var store = CreateStore();
        try
        {
            Session created = (await store.CreateAsync("/proj", "code", "anthropic", "claude-opus-4")).Value;
            await store.AppendMessageAsync(created.Id, Assistant(created.Id, "m-1", 10, 5));

            var porter = new JsonlSessionPorter(NullLogger<JsonlSessionPorter>.Instance);
            var payload = new AppendingWriter(async () =>
            {
                await store.AppendMessageAsync(created.Id, Assistant(created.Id, "m-2", 777, 777));
            });

            var exported = await porter.ExportAsync(store, created.Id, payload);
            await Assert.That(exported.IsSuccess).IsTrue();

            var (envelope, messages) = ParseExport(payload.ToString());
            int counted = messages.Count(m => m.GetProperty("role").GetString() == "assistant");
            int declared = envelope.GetProperty("metadata").GetProperty("messageCount").GetInt32();

            await Assert.That(declared).IsEqualTo(counted)
                .Because("the export is one snapshot: the stats it declares must describe the "
                    + "history it carries, whichever instant the snapshot was taken at");
            await Assert.That(envelope.GetProperty("session").GetProperty("metadata").GetProperty("messageCount").GetInt32())
                .IsEqualTo(counted)
                .Because("the session record inside the envelope carries the same stats as the "
                    + "envelope's own metadata field — two derivations of one number cannot disagree");
        }
        finally
        {
            Drop(store);
        }
    }

    // ── 3. a file whose records contradict each other ─────────────────────

    [Test]
    public async Task Export_CraftedFileWithContradictingRecords_DoesNotMixThem()
    {
        // A file crafted to make "read it twice" visible: the header is
        // repeated with a different title, and one message id appears twice
        // with different usage. The export must apply ONE rule to ONE read —
        // the first header wins, the latest record for an id wins — and its
        // stats must describe the record it kept.
        var store = CreateStore();
        try
        {
            Session created = (await store.CreateAsync("/proj", "code", "anthropic", "claude-opus-4")).Value;
            string path = Path.Combine(store.GetRootDirectory(), $"{created.Id}.jsonl");

            byte[] header = SessionFileIO.EncodeLine(
                new SessionHeaderEntry(
                    "session", 1, created.Id, "project-1", "/proj", "first header",
                    "code", "test-model", "anthropic", Epoch, Epoch),
                JsonlCodecContext.Default.SessionHeaderEntry);
            byte[] secondHeader = SessionFileIO.EncodeLine(
                new SessionHeaderEntry(
                    "session", 1, created.Id, "project-1", "/proj", "second header",
                    "code", "test-model", "anthropic", Epoch, Epoch),
                JsonlCodecContext.Default.SessionHeaderEntry);
            byte[] firstRecord = MessageLine(created.Id, "m-1", 10, 5);
            byte[] secondRecord = MessageLine(created.Id, "m-1", 999, 999);

            await File.WriteAllBytesAsync(
                path,
                [.. header, .. firstRecord, .. secondRecord, .. secondHeader]);

            var porter = new JsonlSessionPorter(NullLogger<JsonlSessionPorter>.Instance);
            var payload = new StringWriter();

            var exported = await porter.ExportAsync(store, created.Id, payload);
            await Assert.That(exported.IsSuccess).IsTrue();

            var (envelope, messages) = ParseExport(payload.ToString());

            // One id, one line: the contradicting record replaced the first one
            // instead of joining it.
            await Assert.That(messages.Count).IsEqualTo(1);
            await Assert.That(messages[0].GetProperty("id").GetString()).IsEqualTo("m-1");

            // The stats describe the record that was kept (the later one).
            await Assert.That(envelope.GetProperty("metadata").GetProperty("tokensInput").GetInt32())
                .IsEqualTo(999);
            await Assert.That(envelope.GetProperty("metadata").GetProperty("messageCount").GetInt32())
                .IsEqualTo(1);

            // The header the export used is the FIRST one — a repeated header
            // is a leftover, not a second generation.
            await Assert.That(envelope.GetProperty("session").GetProperty("title").GetString())
                .IsEqualTo("first header");
        }
        finally
        {
            Drop(store);
        }
    }

    /// <summary>
    ///     One exported message record, encoded exactly as the store writes it.
    /// </summary>
    private static byte[] MessageLine(string sessionId, string id, int input, int output)
    {
        var message = new AssistantMessage(
            id, sessionId, Epoch, [new TextPart("hello")], StopReason.Stop, new Usage(input, output), "test-model");
        var entry = new MessageEntry(
            "message",
            message.Id,
            message.ParentId,
            message.Role,
            message.CreatedAt,
            JsonlMessageCodec.SerializeMessagePayload(message));
        return SessionFileIO.EncodeLine(entry, JsonlCodecContext.Default.MessageEntry);
    }

    /// <summary>
    ///     A writer that runs <c>onFirstWrite</c> while the export is still
    ///     writing — the deterministic stand-in for "the live agent appended a
    ///     message while the export was running".
    /// </summary>
    private sealed class AppendingWriter : TextWriter
    {
        private readonly StringBuilder _builder = new();
        private readonly Func<Task> _onFirstWrite;
        private bool _fired;

        public AppendingWriter(Func<Task> onFirstWrite) => _onFirstWrite = onFirstWrite;

        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override async Task WriteLineAsync(string? value)
        {
            await _builder.AppendLineAsync(value).ConfigureAwait(false);
            if (_fired)
            {
                return;
            }

            _fired = true;
            await _onFirstWrite().ConfigureAwait(false);
        }

        public override string ToString() => _builder.ToString();
    }

    /// <summary>
    ///     A store whose individual read APIs answer with a DIFFERENT
    ///     generation than its snapshot: the snapshot is generation A, the
    ///     reads are generation B. An export that reads more than once mixes
    ///     them; an export that reads the snapshot once does not.
    /// </summary>
    private sealed class SplitGenerationStore : ISessionStore, ISessionSnapshotSource
    {
        private readonly ISessionStore _inner;
        private readonly SessionSnapshot _snapshot;

        public SplitGenerationStore(ISessionStore inner, SessionSnapshot snapshot)
        {
            _inner = inner;
            _snapshot = snapshot;
        }

        // ── generation B: what the individual reads answer ──

        public Task<Result<Session>> GetAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(_snapshot.Session with { Title = "generation B" }));

        public Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult(Result.Success<IReadOnlyList<AgentMessage>>(
                [.. _snapshot.Messages, Assistant(sessionId, "m-generation-b", 1, 1)]));

        public Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(_snapshot.Session.Metadata with { MessageCount = 99 }));

        // ── generation A: the one-generation snapshot ──

        async Task<Result<SessionSnapshot>> ISessionSnapshotSource.ReadSessionSnapshotAsync(
            string sessionId, CancellationToken ct) =>
            await ((ISessionSnapshotSource)_inner).ReadSessionSnapshotAsync(sessionId, ct).ConfigureAwait(false);

        // ── everything else is not part of this story ──

        public Task<Result<Session>> CreateAsync(
            string directory, string agentName, string providerId, string modelId, CancellationToken ct = default) =>
            _inner.CreateAsync(directory, agentName, providerId, modelId, ct);

        public Task<Result<IReadOnlyList<Session>>> ListAsync(string? projectId = null, CancellationToken ct = default) =>
            _inner.ListAsync(projectId, ct);

        public Task<Result> AppendMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default) =>
            _inner.AppendMessageAsync(sessionId, message, ct);

        public Task<Result> UpdateMessageAsync(string sessionId, AgentMessage message, CancellationToken ct = default) =>
            _inner.UpdateMessageAsync(sessionId, message, ct);

        public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default) =>
            _inner.DeleteAsync(sessionId, ct);

        public Task<Result> UpdateAsync(Session session, CancellationToken ct = default) =>
            _inner.UpdateAsync(session, ct);

        public Task<Result> UpdateStatsAsync(string sessionId, SessionMetadata metadata, CancellationToken ct = default) =>
            _inner.UpdateStatsAsync(sessionId, metadata, ct);

        public Task<Result<int>> DeleteMessagesAfterAsync(string sessionId, string messageId, CancellationToken ct = default) =>
            _inner.DeleteMessagesAfterAsync(sessionId, messageId, ct);
    }
}
