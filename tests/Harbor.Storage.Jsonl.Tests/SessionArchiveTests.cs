// SessionArchiveTests.cs — the session archive, end to end (#1750).
//
// WHAT IS PROVEN HERE
// -------------------
//
//   1. ROUND-TRIP. A session exported by this build reads back whole: the
//      same messages, the same attachments, the same record order.
//   2. SHADOWED RECORDS. The archive holds the WHOLE log — the version of a
//      message an edit replaced, and the event that says a rewind took the
//      tail out of the surface — and the manifest says which of the records
//      are shadowed.
//   3. DEDUPLICATION. The same bytes attached twice are ONE file in the
//      ZIP, referenced twice. That is what "content-addressed" has to mean,
//      and a zip that carried the picture three times would not be it.
//   4. NO SECRETS. The archive is the artifact that leaves the machine, so
//      it must not carry a credential — neither one the process holds (a
//      provider key) nor one whose SHAPE is public knowledge.
//
// WHY THE SECRET TEST READS THE DECOMPRESSED ENTRIES
// ----------------------------------------------------
// The archive is DEFLATE-compressed, so a scan of the raw bytes of the zip
// would find nothing and prove nothing. The test opens the archive and
// scans what each entry actually holds.

using System.IO.Compression;
using System.Text;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Storage.Jsonl;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Storage.Jsonl.Tests;

/// <summary>
///     Issue #1750 — a session exported as ONE zip: the canonical log, the
///     content-addressed attachments and the manifest that says which format
///     wrote them.
/// </summary>
public class SessionArchiveTests
{
    /// <summary>A credential-shaped literal: the shape every provider key has.</summary>
    private const string ShapedSecret = "sk-abcdefghijklmnopqrstuvwxyz012345";

    /// <summary>
    ///     A credential the SHAPE rules cannot know: the value of an
    ///     environment variable the process holds.
    /// </summary>
    private const string HeldSecret = "held-by-the-process-9f2c";

    // ── the round trip ────────────────────────────────────────────────────

    [Test]
    public async Task ExportThenRead_TheSessionComesBackWhole()
    {
        var store = CreateStore();
        var session = (await store.CreateAsync("/work", "code", "kilocode", "kilo-auto/free")).Value;
        await store.AppendMessageAsync(session.Id, TestMessages.User(
            "read the file", id: "m1", sessionId: session.Id));
        await store.AppendMessageAsync(session.Id, TestMessages.Assistant(
            "done", id: "m2", sessionId: session.Id));
        await store.AppendMessageAsync(session.Id, TestMessages.ToolResult(
            [new ToolResultEntry("tc1", "read", "file body", false)], id: "m3", sessionId: session.Id));

        byte[] archive = await ExportAsync(store, session.Id);

        var read = await new SessionArchiveReader().ReadAsync(new MemoryStream(archive));
        await Assert.That(read.IsSuccess).IsTrue();

        SessionArchive decoded = read.Value;
        await Assert.That(decoded.Session.Id).IsEqualTo(session.Id);
        await Assert.That(decoded.Log.Count).IsEqualTo(3);

        var user = (UserMessage)decoded.Log[0].Message!;
        await Assert.That(user.Content).IsEqualTo("read the file");

        var assistant = (AssistantMessage)decoded.Log[1].Message!;
        await Assert.That(((TextPart)assistant.Parts[0]).Text).IsEqualTo("done");

        var toolResult = (ToolResultMessage)decoded.Log[2].Message!;
        await Assert.That(toolResult.Results[0].Output).IsEqualTo("file body");
    }

    [Test]
    public async Task Export_TheManifestCarriesTheFormatVersion()
    {
        var store = CreateStore();
        var session = (await store.CreateAsync("/work", "code", "kilocode", "kilo-auto/free")).Value;

        var written = await WriteAsync(store, session.Id);

        await Assert.That(written.IsSuccess).IsTrue();
        SessionArchiveManifest manifest = written.Value;
        await Assert.That(manifest.Marker).IsEqualTo("$harbor-session-archive");
        await Assert.That(manifest.Version).IsEqualTo(1);
        await Assert.That(manifest.SessionId).IsEqualTo(session.Id);
    }

    // ── the whole log, shadowed records included ──────────────────────────

    [Test]
    public async Task Export_HoldsTheShadowedRecordsAndCountsThem()
    {
        var session = Session.Create("/work", "code", "kilocode", "kilo-auto/free");
        var log = new List<SessionLogEntry>
        {
            new()
            {
                Kind = SessionLogEntryKind.Message,
                Id = "m1",
                Position = 0,
                Message = TestMessages.User("first draft", id: "m1", sessionId: session.Id),
            },
            new()
            {
                Kind = SessionLogEntryKind.Message,
                Id = "m1",
                Position = 1,
                Message = TestMessages.User("second draft", id: "m1", sessionId: session.Id),
            },
            new()
            {
                Kind = SessionLogEntryKind.Message,
                Id = "m2",
                Position = 2,
                Message = TestMessages.User("still here", id: "m2", sessionId: session.Id),
            },
        };
        var store = new LogStore(session, log);

        var written = await WriteAsync(store, session.Id);
        await Assert.That(written.IsSuccess).IsTrue();

        SessionArchiveManifest manifest = written.Value;
        await Assert.That(manifest.LogSource).IsEqualTo("log");
        await Assert.That(manifest.LogEntries).IsEqualTo(3);
        await Assert.That(manifest.CurrentEntries).IsEqualTo(2);
        await Assert.That(manifest.ShadowedEntries).IsEqualTo(1);
        await Assert.That(manifest.LogOnlyEntries).IsEqualTo(0);

        // The shadowed record is IN the archive: a rewind that erased its own
        // tail would be a log that cannot answer the question it was
        // collected for.
        var read = await new SessionArchiveReader().ReadAsync(
            new MemoryStream(await ExportAsync(store, session.Id)));
        await Assert.That(read.IsSuccess).IsTrue();
        await Assert.That(read.Value.Log.Count).IsEqualTo(3);

        var shadowed = read.Value.Log[0];
        await Assert.That(shadowed.State).IsEqualTo(SessionLogEntryState.Shadowed);
        await Assert.That(((UserMessage)shadowed.Message!).Content).IsEqualTo("first draft");

        var current = read.Value.Log[1];
        await Assert.That(current.State).IsEqualTo(SessionLogEntryState.Current);
        await Assert.That(((UserMessage)current.Message!).Content).IsEqualTo("second draft");
    }

    [Test]
    public async Task Export_SaysLogWhenTheStoreHasOne()
    {
        var store = CreateStore();
        var session = (await store.CreateAsync("/work", "code", "kilocode", "kilo-auto/free")).Value;
        await store.AppendMessageAsync(session.Id, TestMessages.User(
            "hello", id: "m1", sessionId: session.Id));

        var written = await WriteAsync(store, session.Id);

        // The JSONL backend exposes its append-only log (#1752), so the
        // archive holds the WHOLE log — and says so, so a reader is never
        // left to infer which of the two it is holding.
        await Assert.That(written.IsSuccess).IsTrue();
        await Assert.That(written.Value.LogSource).IsEqualTo("log");
    }

    [Test]
    public async Task Export_SaysSurfaceWhenTheLogIsEmpty()
    {
        var store = CreateStore();
        var session = (await store.CreateAsync("/work", "code", "kilocode", "kilo-auto/free")).Value;

        var written = await WriteAsync(store, session.Id);

        // A store that has the log API but no entries yet holds no log: the
        // manifest must not claim one the archive does not hold.
        await Assert.That(written.IsSuccess).IsTrue();
        await Assert.That(written.Value.LogSource).IsEqualTo("surface");
    }

    [Test]
    public async Task Export_SaysSurfaceWhenTheStoreHasNoLog()
    {
        var session = Session.Create("/work", "code", "kilocode", "kilo-auto/free");
        var store = new LogStore(session, log: null);

        var written = await WriteAsync(store, session.Id);

        // A store that cannot hand back a log offers the surface, and the
        // manifest says so rather than letting a reader infer it.
        await Assert.That(written.IsSuccess).IsTrue();
        await Assert.That(written.Value.LogSource).IsEqualTo("surface");
    }

    // ── the attachments ───────────────────────────────────────────────────

    [Test]
    public async Task Export_StoresIdenticalAttachmentsOnce()
    {
        var store = CreateStore();
        var session = (await store.CreateAsync("/work", "code", "kilocode", "kilo-auto/free")).Value;
        byte[] png = OnePixelPng();

        await store.AppendMessageAsync(session.Id, TestMessages.User(
            "look", id: "m1", sessionId: session.Id,
            attachments: [new ImageAttachment("/tmp/a.png", "image/png", 1, 1, png)]));
        await store.AppendMessageAsync(session.Id, TestMessages.User(
            "look again", id: "m2", sessionId: session.Id,
            attachments: [new ImageAttachment("/tmp/b.png", "image/png", 1, 1, png)]));

        var written = await WriteAsync(store, session.Id);
        await Assert.That(written.IsSuccess).IsTrue();
        await Assert.That(written.Value.Attachments.Length).IsEqualTo(1);

        byte[] archive = await ExportAsync(store, session.Id);
        List<string> names = await EntryNamesAsync(archive);
        int attachmentFiles = names.Count(n => n.StartsWith("attachments/", StringComparison.Ordinal));
        await Assert.That(attachmentFiles).IsEqualTo(1);

        // Both turns still point at the one file, and the bytes come back.
        var read = await new SessionArchiveReader().ReadAsync(new MemoryStream(archive));
        await Assert.That(read.IsSuccess).IsTrue();

        var first = (UserMessage)read.Value.Log[0].Message!;
        var second = (UserMessage)read.Value.Log[1].Message!;
        await Assert.That(first.Attachments![0].Data.Length).IsEqualTo(png.Length);
        await Assert.That(second.Attachments![0].Data.Length).IsEqualTo(png.Length);
        await Assert.That(Convert.ToHexString(second.Attachments![0].Data))
            .IsEqualTo(Convert.ToHexString(png));
    }

    [Test]
    public async Task Export_StoresDifferentAttachmentsSeparately()
    {
        var store = CreateStore();
        var session = (await store.CreateAsync("/work", "code", "kilocode", "kilo-auto/free")).Value;

        await store.AppendMessageAsync(session.Id, TestMessages.User(
            "two pictures", id: "m1", sessionId: session.Id,
            attachments:
            [
                new ImageAttachment("/tmp/a.png", "image/png", 1, 1, OnePixelPng()),
                new ImageAttachment("/tmp/b.jpg", "image/jpeg", 2, 2, [1, 2, 3, 4]),
            ]));

        var written = await WriteAsync(store, session.Id);
        await Assert.That(written.IsSuccess).IsTrue();
        await Assert.That(written.Value.Attachments.Length).IsEqualTo(2);

        byte[] archive = await ExportAsync(store, session.Id);
        List<string> names = await EntryNamesAsync(archive);
        await Assert.That(names.Count(n => n.StartsWith("attachments/", StringComparison.Ordinal))).IsEqualTo(2);
    }

    // ── the secrets ───────────────────────────────────────────────────────

    [Test]
    public async Task Export_NeverCarriesACredentialTheLogHolds()
    {
        // The LOCAL tier: the log on disk keeps the agent's text verbatim.
        // The archive is still the artifact that leaves the machine, so the
        // credential must not travel with it.
        var store = CreateStore(tier: SessionLogTier.Local);
        var session = (await store.CreateAsync("/work", "code", "kilocode", "kilo-auto/free")).Value;
        await store.AppendMessageAsync(session.Id, TestMessages.User(
            $"my key is {ShapedSecret} and the other is {HeldSecret}",
            id: "m1",
            sessionId: session.Id));
        await store.AppendMessageAsync(session.Id, TestMessages.Assistant(
            $"you said {HeldSecret}",
            id: "m2",
            sessionId: session.Id));
        await store.AppendMessageAsync(session.Id, TestMessages.ToolResult(
            [new ToolResultEntry("tc1", "read", $"the .env holds {HeldSecret}", false)],
            id: "m3",
            sessionId: session.Id));

        byte[] archive = await ExportAsync(store, session.Id, HeldSecret);

        List<byte[]> entries = await EntryBytesAsync(archive);
        await Assert.That(entries.Count).IsGreaterThan(0);
        foreach (byte[] entry in entries)
        {
            await Assert.That(Contains(entry, Encoding.UTF8.GetBytes(ShapedSecret))).IsFalse();
            await Assert.That(Contains(entry, Encoding.UTF8.GetBytes(HeldSecret))).IsFalse();
        }
    }

    [Test]
    public async Task Export_OfAKeylessLogCarriesNoCredentialEither()
    {
        // The keyless tier (#1740): the log on disk is ALREADY scrubbed, and
        // the archive is built from that log — so the credential is gone
        // before the archive writer ever sees it.
        var store = CreateStore(tier: SessionLogTier.Keyless, literalSecrets: [HeldSecret]);
        var session = (await store.CreateAsync("/work", "code", "kilocode", "kilo-auto/free")).Value;
        await store.AppendMessageAsync(session.Id, TestMessages.User(
            $"my key is {ShapedSecret} and the other is {HeldSecret}",
            id: "m1",
            sessionId: session.Id));

        byte[] archive = await ExportAsync(store, session.Id, HeldSecret);

        List<byte[]> entries = await EntryBytesAsync(archive);
        foreach (byte[] entry in entries)
        {
            await Assert.That(Contains(entry, Encoding.UTF8.GetBytes(ShapedSecret))).IsFalse();
            await Assert.That(Contains(entry, Encoding.UTF8.GetBytes(HeldSecret))).IsFalse();
        }
    }

    [Test]
    public async Task Read_RefusesAnArchiveThatIsNotOne()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("readme.txt");
            await using var stream = entry.Open();
            await stream.WriteAsync(Encoding.UTF8.GetBytes("not an archive"));
        }

        buffer.Position = 0;
        var read = await new SessionArchiveReader().ReadAsync(buffer);

        await Assert.That(read.IsSuccess).IsFalse();
        await Assert.That(read.Error).Contains("manifest.json");
    }

    // ── the helpers ───────────────────────────────────────────────────────

    private static JsonlSessionStore CreateStore(
        SessionLogTier tier = SessionLogTier.Local,
        IReadOnlyCollection<string>? literalSecrets = null)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"harbor-archive-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        return new JsonlSessionStore(tempDir, NullLogger<JsonlSessionStore>.Instance, tier, literalSecrets);
    }

    private static SessionArchiveWriter CreateWriter(params string[] literalSecrets) =>
        new(NullLogger<SessionArchiveWriter>.Instance, literalSecrets);

    private static async Task<Result<SessionArchiveManifest>> WriteAsync(
        ISessionStore store,
        string sessionId,
        params string[] literalSecrets)
    {
        using var buffer = new MemoryStream();
        return await CreateWriter(literalSecrets).WriteAsync(store, sessionId, buffer);
    }

    private static async Task<byte[]> ExportAsync(
        ISessionStore store,
        string sessionId,
        params string[] literalSecrets)
    {
        using var buffer = new MemoryStream();
        var written = await CreateWriter(literalSecrets).WriteAsync(store, sessionId, buffer);
        if (written.IsFailure)
        {
            throw new InvalidOperationException(written.Error);
        }

        return buffer.ToArray();
    }

    private static Task<List<string>> EntryNamesAsync(byte[] archive)
    {
        using var stream = new MemoryStream(archive);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var names = new List<string>();
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            names.Add(entry.FullName);
        }

        return Task.FromResult(names);
    }

    private static async Task<List<byte[]>> EntryBytesAsync(byte[] archive)
    {
        using var stream = new MemoryStream(archive);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var contents = new List<byte[]>();
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            using var entryStream = entry.Open();
            using var buffer = new MemoryStream();
            await entryStream.CopyToAsync(buffer);
            contents.Add(buffer.ToArray());
        }

        return contents;
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || needle.Length > haystack.Length)
        {
            return false;
        }

        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Minimal but structurally real 1×1 PNG.</summary>
    private static byte[] OnePixelPng() =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41,
        0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00,
        0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
        0x42, 0x60, 0x82,
    ];

    /// <summary>
    ///     A store whose log the test decides: it hands one back, or it has
    ///     none at all (and the archive falls back to the surface). The JSONL
    ///     backend exposes its own log now (#1784), so the "no log" case needs
    ///     a store that says so.
    /// </summary>
    private sealed class LogStore(Session session, IReadOnlyList<SessionLogEntry>? log) : ISessionStore
    {
        public Task<Result<Session>> CreateAsync(
            string directory, string agentName, string providerId, string modelId, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(session));

        public Task<Result<Session>> GetAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult(session.Id == sessionId
                ? Result.Success(session)
                : Result.Failure<Session>($"Session '{sessionId}' was not found."));

        public Task<Result<IReadOnlyList<Session>>> ListAsync(
            string? projectId = null, CancellationToken ct = default) =>
            Task.FromResult(Result.Success<IReadOnlyList<Session>>([session]));

        public Task<Result> AppendMessageAsync(
            string sessionId, AgentMessage message, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> UpdateMessageAsync(
            string sessionId, AgentMessage message, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result<IReadOnlyList<AgentMessage>>> GetMessagesAsync(
            string sessionId, CancellationToken ct = default) =>
            Task.FromResult(Result.Success<IReadOnlyList<AgentMessage>>(
                log is null
                    ? []
                    : [.. log.Where(e => e.Message is not null).Select(e => e.Message!)]));

        public Task<Result<IReadOnlyList<SessionLogEntry>>> GetSessionLogAsync(
            string sessionId, CancellationToken ct = default) =>
            Task.FromResult(log is null
                ? Result.Failure<IReadOnlyList<SessionLogEntry>>(
                    "this store keeps no append-only log (#1784).")
                : Result.Success(log));

        public Task<Result> DeleteAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result> UpdateAsync(Session updated, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());

        public Task<Result<SessionMetadata>> GetStatsAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult(Result.Success(session.Metadata));

        public Task<Result> UpdateStatsAsync(
            string sessionId, SessionMetadata metadata, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());
    }
}
