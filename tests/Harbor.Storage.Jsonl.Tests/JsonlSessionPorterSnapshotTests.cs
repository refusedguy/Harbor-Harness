using System.Text;
using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Storage.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Harbor.TestKit;

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
///     The tests below pin the ways that can go wrong: the export drifting
///     away from the store's own one-generation snapshot, a write landing
///     DURING the export, a file whose records contradict each other, and a
///     store that cannot serve a snapshot at all.
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

    // ── 1. the export IS the store's one-generation snapshot ───────────────

    [Test]
    public async Task Export_IsTheStoresOneGenerationSnapshot()
    {
        // The store reads the file once and hands over a snapshot: the session
        // record (header + the stats derived from those very messages) and the
        // history. The export must be THAT — not a re-derivation of the same
        // numbers from a second read, which is where the generations used to
        // drift apart.
        var store = CreateStore();
        try
        {
            Session created = (await store.CreateAsync("/proj", "code", "anthropic", "claude-opus-4")).Value;
            await store.UpdateAsync(created with { Title = "generation A" });
            await store.AppendMessageAsync(created.Id, Assistant(created.Id, "m-a", 10, 5));

            var snapshot = await store.ReadSessionSnapshotAsync(created.Id, default);
            await Assert.That(snapshot.IsSuccess).IsTrue();

            var porter = new JsonlSessionPorter(NullLogger<JsonlSessionPorter>.Instance);
            var payload = new StringWriter();

            var exported = await porter.ExportAsync(store, created.Id, payload);
            await Assert.That(exported.IsSuccess).IsTrue();

            var (envelope, messages) = ParseExport(payload.ToString());
            await Assert.That(envelope.GetProperty("session").GetProperty("title").GetString())
                .IsEqualTo(snapshot.Value.Session.Title);
            await Assert.That(messages.Count).IsEqualTo(snapshot.Value.Messages.Count);
            await Assert.That(messages[0].GetProperty("id").GetString()).IsEqualTo("m-a");

            // The stats in the envelope are the snapshot's stats.
            await Assert.That(envelope.GetProperty("metadata").GetProperty("tokensInput").GetInt32())
                .IsEqualTo(snapshot.Value.Session.Metadata.TokensInput);
            await Assert.That(envelope.GetProperty("metadata").GetProperty("messageCount").GetInt32())
                .IsEqualTo(snapshot.Value.Session.Metadata.MessageCount);
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
        //
        // On the old code this test FAILS: the body was read after the append
        // while the stats had been derived before it.
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
                    "session", 1, SessionFormatCatalog.Current, created.Id, "project-1", "/proj", "first header",
                    "code", "test-model", "anthropic", Epoch, Epoch),
                JsonlCodecContext.Default.SessionHeaderEntry);
            byte[] secondHeader = SessionFileIO.EncodeLine(
                new SessionHeaderEntry(
                    "session", 1, SessionFormatCatalog.Current, created.Id, "project-1", "/proj", "second header",
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

    // ── 4. a store that cannot serve a snapshot ───────────────────────────

    [Test]
    public async Task Export_StoreWithoutASnapshot_KeepsTheStoresOwnStats()
    {
        // The fallback path (a store that cannot promise one generation) must
        // still carry THAT store's stats — the persisted ones, cost included.
        // Re-deriving the stats from the history instead would silently drop
        // the price the core computed, and the export is what a person sends
        // to an incident review.
        var store = new MemorySessionStore();
        Session created = (await store.CreateAsync("/proj", "code", "anthropic", "claude-opus-4")).Value;
        await store.AppendMessageAsync(created.Id, Assistant(created.Id, "m-1", 10, 5));

        var stats = SessionMetadata.Empty with
        {
            Cost = 0.42m,
            TokensInput = 99,
            TokensOutput = 11,
            MessageCount = 3,
        };
        await store.UpdateStatsAsync(created.Id, stats);

        var porter = new JsonlSessionPorter(NullLogger<JsonlSessionPorter>.Instance);
        var payload = new StringWriter();

        var exported = await porter.ExportAsync(store, created.Id, payload);
        await Assert.That(exported.IsSuccess).IsTrue();

        var (envelope, messages) = ParseExport(payload.ToString());
        await Assert.That(messages.Count).IsEqualTo(1);
        await Assert.That(envelope.GetProperty("metadata").GetProperty("cost").GetDecimal()).IsEqualTo(0.42m);
        await Assert.That(envelope.GetProperty("metadata").GetProperty("tokensInput").GetInt32()).IsEqualTo(99);
        await Assert.That(envelope.GetProperty("metadata").GetProperty("messageCount").GetInt32()).IsEqualTo(3);
    }

    /// <summary>
    ///     One exported message record, encoded exactly as the store writes it.
    /// </summary>
    private static byte[] MessageLine(string sessionId, string id, int input, int output)
    {
        var message = TestMessages.Assistant("hello", id: id, sessionId: sessionId, createdAt: Epoch, usage: new Usage(input, output));
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

        public override Task WriteLineAsync(string? value)
        {
            _builder.AppendLine(value);
            if (_fired)
            {
                return Task.CompletedTask;
            }

            _fired = true;
            return _onFirstWrite();
        }

        public override string ToString() => _builder.ToString();
    }
}
