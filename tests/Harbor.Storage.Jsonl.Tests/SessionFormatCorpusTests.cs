// SessionFormatCorpusTests.cs — the session-format corpus (#1749).
//
// WHAT THIS FILE IS
// -----------------
// The session file is a public artifact, so its format is a contract with a
// version, a catalog of generations, and a chain of migrations from a file's
// own generation to the current one. These tests pin the four things that
// make that contract real:
//
//   * the WRITER states the current generation;
//   * a file of an OLDER generation is read THROUGH the migration chain;
//   * a file of a NEWER generation is REFUSED, with the version in the
//     message — never read as if it were the present;
//   * every generation in the catalog, and every step of the chain, has a
//     COMMITTED FIXTURE. A migration nothing exercises is a migration nobody
//     knows works.
//
// THE CORPUS
// ----------
// The fixtures are real session files, committed under `fixtures/`, one per
// generation:
//
//   * session-format-v0-legacy.jsonl  — the legacy generation: a header that
//     states no format generation and none of the fields the current
//     generation states;
//   * session-format-v1-current.jsonl — the current generation: a header
//     that states the generation, plus the record kinds this build writes
//     (messages, a checkpoint, a rating).
//
// The file name of a fixture is the session id inside it, so a fixture is
// copied into a store's directory under the name the store will look it up
// by. The corpus is read from the checkout, the way the other test fixtures
// in this repository are read, so the file under test is the file that is
// committed.

using System.Text.Json;
using Harbor.Storage.Jsonl;
using Harbor.TestKit;

namespace Harbor.Storage.Jsonl.Tests;

/// <summary>
///     The session-format corpus: the version in the header, the migration
///     chain, and the committed fixture of every generation (#1749).
/// </summary>
/// <remarks>
///     The rules these tests pin:
///     <list type="bullet">
///         <item>the writer states the CURRENT generation;</item>
///         <item>a file of an older generation is read through the chain;</item>
///         <item>the chain is idempotent — the same header in, the same header out;</item>
///         <item>a file of a generation this build does not know is REFUSED, and the refusal names the version;</item>
///         <item>the listing reports such a file instead of skipping it in silence;</item>
///         <item>every generation has a committed fixture, and every step of the chain is covered by one.</item>
///     </list>
/// </remarks>
public class SessionFormatCorpusTests
{
    /// <summary>
    ///     The corpus: one committed fixture per generation of the session
    ///     format. A generation with no entry here has nothing that proves
    ///     it can be read — the policy test below is what says so out loud.
    /// </summary>
    private static readonly IReadOnlyDictionary<int, string> Corpus = new Dictionary<int, string>
    {
        [SessionFormatCatalog.Legacy] = "session-format-v0-legacy.jsonl",
        [SessionFormatCatalog.Current] = "session-format-v1-current.jsonl",
    };

    /// <summary>
    ///     The header of a file this build does not know how to read: the
    ///     current generation plus one. It is written as a literal rather
    ///     than built from the writer's own types, because a header this
    ///     build could WRITE is a header this build can read.
    /// </summary>
    private const string FutureHeader =
        """
        {"type":"session","version":1,"formatVersion":2,"id":"session-format-v2-future","projectId":"session-format-corpus","directory":"/tmp/harbor/session-format-v2","title":"From the future","agent":"code","model":"claude-opus-4","providerId":"anthropic","createdAt":"2026-03-04T05:06:07+00:00","updatedAt":"2026-03-04T05:06:07+00:00","parentSessionId":null,"status":0,"gitBranch":null,"gitIsDirty":false,"kind":0}
        """;

    /// <summary>The session id of the file written in a generation this build does not read.</summary>
    private const string FutureSessionId = "session-format-v2-future";

    // ── the writer ─────────────────────────────────────────────────────────

    [Test]
    public async Task CreateAsync_WritesTheCurrentFormatGeneration()
    {
        var (store, _) = CreateStore();
        try
        {
            var created = await store.CreateAsync("/proj", "code", "anthropic", "claude-opus-4");
            await Assert.That(created.IsSuccess).IsTrue();

            string header = await ReadHeaderLine(store, created.Value.Id);
            using JsonDocument document = JsonDocument.Parse(header);
            await Assert.That(document.RootElement.GetProperty("formatVersion").GetInt32())
                .IsEqualTo(SessionFormatCatalog.Current);
        }
        finally
        {
            Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task UpdateAsync_UpgradesALegacyFileToTheCurrentGeneration()
    {
        // The writer always writes the CURRENT generation, so the first
        // header rewrite is what upgrades a legacy file on disk.
        var (store, _) = CreateStore();
        try
        {
            CopyFixture(store, SessionFormatCatalog.Legacy);

            var read = await store.GetAsync(CorpusSessionId(SessionFormatCatalog.Legacy));
            await Assert.That(read.IsSuccess).IsTrue();

            var updated = await store.UpdateAsync(read.Value with { Title = "Renamed legacy session" });
            await Assert.That(updated.IsSuccess).IsTrue();

            string header = await ReadHeaderLine(store, CorpusSessionId(SessionFormatCatalog.Legacy));
            using JsonDocument document = JsonDocument.Parse(header);
            await Assert.That(document.RootElement.GetProperty("formatVersion").GetInt32())
                .IsEqualTo(SessionFormatCatalog.Current);
        }
        finally
        {
            Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    // ── the read path ──────────────────────────────────────────────────────

    [Test]
    public async Task CurrentGenerationFixture_ReadsAsTheCurrentGeneration()
    {
        var (store, _) = CreateStore();
        try
        {
            CopyFixture(store, SessionFormatCatalog.Current);

            var read = await store.GetAsync(CorpusSessionId(SessionFormatCatalog.Current));
            await Assert.That(read.IsSuccess).IsTrue();
            await Assert.That(read.Value.Title).IsEqualTo("Current session");

            // The fixture holds two messages, a checkpoint and a rating: the
            // records that are not messages are still not messages.
            var messages = await store.GetMessagesAsync(CorpusSessionId(SessionFormatCatalog.Current));
            await Assert.That(messages.IsSuccess).IsTrue();
            await Assert.That(messages.Value.Count).IsEqualTo(2);
        }
        finally
        {
            Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task LegacyGenerationFixture_ReadsThroughTheMigrationChain()
    {
        var (store, _) = CreateStore();
        try
        {
            CopyFixture(store, SessionFormatCatalog.Legacy);

            var read = await store.GetAsync(CorpusSessionId(SessionFormatCatalog.Legacy));
            await Assert.That(read.IsSuccess).IsTrue();
            await Assert.That(read.Value.Id).IsEqualTo(CorpusSessionId(SessionFormatCatalog.Legacy));
            await Assert.That(read.Value.Title).IsEqualTo("Legacy session");

            var messages = await store.GetMessagesAsync(CorpusSessionId(SessionFormatCatalog.Legacy));
            await Assert.That(messages.IsSuccess).IsTrue();
            await Assert.That(messages.Value.Count).IsEqualTo(2);
        }
        finally
        {
            Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task LegacyGenerationFixture_ReadsAsTheCurrentGeneration()
    {
        // The header the read path hands back is a CURRENT-generation
        // header, whatever the file said: the chain runs before anything
        // reads the header, not after.
        var (store, _) = CreateStore();
        try
        {
            CopyFixture(store, SessionFormatCatalog.Legacy);

            var header = await SessionFileReader.TryReadHeaderAsync(
                SessionFilePath(store, CorpusSessionId(SessionFormatCatalog.Legacy)),
                CorpusSessionId(SessionFormatCatalog.Legacy),
                CancellationToken.None);

            await Assert.That(header.IsSuccess).IsTrue();
            await Assert.That(header.Value.FormatVersion).IsEqualTo((int?)SessionFormatCatalog.Current);
        }
        finally
        {
            Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    // ── the chain itself ───────────────────────────────────────────────────

    [Test]
    public async Task Migration_TurnsTheLegacyHeaderIntoTheCurrentHeader()
    {
        // The "old record → new" of the one step the chain has today.
        var oldHeader = await ReadCorpusHeader(SessionFormatCatalog.Legacy);

        // The OLD record states no generation at all — that is what makes it
        // a legacy file.
        await Assert.That(oldHeader.FormatVersion).IsNull();
        await Assert.That(oldHeader.UpdatedAt).IsEqualTo(default(DateTimeOffset));

        var stamp = new DateTimeOffset(2026, 5, 6, 7, 8, 9, TimeSpan.Zero);
        var migrated = SessionFormatMigrator.MigrateToCurrent(
            oldHeader, stamp, CorpusSessionId(SessionFormatCatalog.Legacy), FixturePath(SessionFormatCatalog.Legacy));

        await Assert.That(migrated.IsSuccess).IsTrue();
        SessionHeaderEntry current = migrated.Value;

        // The NEW record states the generation, and carries the
        // last-activity stamp the legacy record could not state.
        await Assert.That(current.FormatVersion).IsEqualTo((int?)SessionFormatCatalog.Current);
        await Assert.That(current.UpdatedAt).IsEqualTo(stamp);

        // Everything else is the same session: a migration is not a rewrite.
        await Assert.That(current.Id).IsEqualTo(oldHeader.Id);
        await Assert.That(current.Title).IsEqualTo(oldHeader.Title);
        await Assert.That(current.CreatedAt).IsEqualTo(oldHeader.CreatedAt);
        await Assert.That(current.Status).IsEqualTo(oldHeader.Status);
        await Assert.That(current.Kind).IsEqualTo(oldHeader.Kind);
    }

    [Test]
    public async Task Migration_IsIdempotent()
    {
        var oldHeader = await ReadCorpusHeader(SessionFormatCatalog.Legacy);
        var stamp = new DateTimeOffset(2026, 5, 6, 7, 8, 9, TimeSpan.Zero);

        var once = SessionFormatMigrator.MigrateToCurrent(
            oldHeader, stamp, CorpusSessionId(SessionFormatCatalog.Legacy), FixturePath(SessionFormatCatalog.Legacy));
        await Assert.That(once.IsSuccess).IsTrue();

        // Applying the chain to the result again changes nothing — not even
        // with a different file stamp, because the current header already
        // states one.
        var twice = SessionFormatMigrator.MigrateToCurrent(
            once.Value, stamp.AddYears(1), CorpusSessionId(SessionFormatCatalog.Legacy), FixturePath(SessionFormatCatalog.Legacy));
        await Assert.That(twice.IsSuccess).IsTrue();
        await Assert.That(twice.Value).IsEqualTo(once.Value);
    }

    [Test]
    public async Task Migration_RefusesAGenerationThisBuildDoesNotRead()
    {
        var futureHeader = JsonSerializer.Deserialize<SessionHeaderEntry>(
            FutureHeader, JsonlCodecContext.Default.SessionHeaderEntry);
        await Assert.That(futureHeader).IsNotNull();

        var refused = SessionFormatMigrator.MigrateToCurrent(
            futureHeader!, new DateTimeOffset(2026, 5, 6, 7, 8, 9, TimeSpan.Zero),
            FutureSessionId, "/tmp/session-format-v2-future.jsonl");

        await Assert.That(refused.IsFailure).IsTrue();
        await Assert.That(refused.Error).Contains("session format version 2");
    }

    // ── a file from the future ─────────────────────────────────────────────

    [Test]
    public async Task FutureGenerationFile_IsRefusedWithAnExplicitError()
    {
        var (store, _) = CreateStore();
        try
        {
            await WriteFutureFile(store);

            var read = await store.GetAsync(FutureSessionId);
            await Assert.That(read.IsFailure).IsTrue();
            await Assert.That(read.Error).Contains("session format version 2");
            await Assert.That(read.Error).Contains(FutureSessionId);
        }
        finally
        {
            Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task FutureGenerationFile_MessageReadIsRefusedWithAnExplicitError()
    {
        // The messages under a header this build does not read were written
        // by a format this build does not know. Reading them as if they were
        // the present is how records go missing without a word.
        var (store, _) = CreateStore();
        try
        {
            await WriteFutureFile(store);

            var messages = await store.GetMessagesAsync(FutureSessionId);
            await Assert.That(messages.IsFailure).IsTrue();
            await Assert.That(messages.Error).Contains("session format version 2");
        }
        finally
        {
            Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task FutureGenerationFile_IsReportedByTheListing()
    {
        // Not in silence: the listing cannot show a session it cannot read,
        // so it says so — in the log, with the version in the reason.
        var (store, logger) = CreateStore();
        try
        {
            await WriteFutureFile(store);

            var list = await store.ListAsync();
            await Assert.That(list.IsSuccess).IsTrue();
            await Assert.That(list.Value).IsEmpty();

            await Assert.That(logger.Contains("session format version 2")).IsTrue();
        }
        finally
        {
            Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task FutureGenerationFile_FeedbackReadIsRefused()
    {
        // A session file is read as a whole or not at all: the feedback axis
        // scans the same file, so it refuses the same file.
        var (store, _) = CreateStore();
        try
        {
            await WriteFutureFile(store);

            var feedback = await store.GetFeedbackAsync(FutureSessionId);
            await Assert.That(feedback.IsFailure).IsTrue();
            await Assert.That(feedback.Error).Contains("session format version 2");
        }
        finally
        {
            Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    [Test]
    public async Task FutureGenerationFile_CheckpointReadIsRefused()
    {
        // The checkpoint axis scans the same file, so it refuses it too.
        var (store, _) = CreateStore();
        try
        {
            await WriteFutureFile(store);

            var checkpoints = await store.ListCheckpointsAsync(FutureSessionId);
            await Assert.That(checkpoints.IsFailure).IsTrue();
            await Assert.That(checkpoints.Error).Contains("session format version 2");
        }
        finally
        {
            Directory.Delete(store.GetRootDirectory(), true);
        }
    }

    // ── the corpus policy ──────────────────────────────────────────────────

    [Test]
    public async Task EveryGenerationInTheCatalog_HasACommittedFixture()
    {
        await Assert.That(SessionFormatCatalog.Generations).IsNotEmpty();

        foreach (int generation in SessionFormatCatalog.Generations)
        {
            await Assert.That(Corpus.ContainsKey(generation)).IsTrue()
                .Because($"session format generation {generation} has no fixture in the corpus.");
            await Assert.That(File.Exists(FixturePath(generation))).IsTrue()
                .Because($"the fixture of session format generation {generation} is not in the tree.");
        }
    }

    [Test]
    public async Task EveryMigrationStep_IsCoveredByAFixture()
    {
        // A step whose source generation has no committed fixture is a step
        // nothing proves.
        await Assert.That(SessionFormatMigrator.Steps).IsNotEmpty();

        foreach (ISessionFormatMigration step in SessionFormatMigrator.Steps)
        {
            await Assert.That(Corpus.ContainsKey(step.FromVersion)).IsTrue()
                .Because($"the migration {step.FromVersion}→{step.ToVersion} reads a generation the corpus does not cover.");
            await Assert.That(File.Exists(FixturePath(step.FromVersion))).IsTrue()
                .Because($"the migration {step.FromVersion}→{step.ToVersion} has no committed fixture.");
        }
    }

    // ── plumbing ───────────────────────────────────────────────────────────

    private static (JsonlSessionStore Store, CapturingLogger<JsonlSessionStore> Logger) CreateStore()
    {
        string tempDir = TestTempDirs.NewDirectory("harbor-jsonl-format");
        var logger = new CapturingLogger<JsonlSessionStore>();
        return (new JsonlSessionStore(tempDir, logger), logger);
    }

    /// <summary>
    ///     The path of the committed fixture of one generation, in the
    ///     checkout. The fixtures are read where they are committed, the way
    ///     the other test fixtures in this repository are read
    ///     (<c>RepoPaths</c> in the shared kit) — a copied fixture is a
    ///     fixture that can drift from the file it claims to be.
    /// </summary>
    private static string FixturePath(int generation) =>
        RepoPaths.Combine("tests", "Harbor.Storage.Jsonl.Tests", "fixtures", Corpus[generation]);

    /// <summary>
    ///     The session id inside the fixture of one generation. The fixture's
    ///     file name IS that id, so a fixture copied into a store's directory
    ///     is found under the name the store looks it up by.
    /// </summary>
    private static string CorpusSessionId(int generation) =>
        Path.GetFileNameWithoutExtension(Corpus[generation]);

    private static string SessionFilePath(JsonlSessionStore store, string sessionId) =>
        Path.Combine(store.GetRootDirectory(), $"{sessionId}.jsonl");

    private static void CopyFixture(JsonlSessionStore store, int generation)
    {
        string source = FixturePath(generation);
        string target = SessionFilePath(store, CorpusSessionId(generation));
        File.Copy(source, target, overwrite: true);
    }

    private static async Task WriteFutureFile(JsonlSessionStore store)
    {
        string path = SessionFilePath(store, FutureSessionId);
        await File.WriteAllTextAsync(path, FutureHeader + "\n");
    }

    private static async Task<string> ReadHeaderLine(JsonlSessionStore store, string sessionId)
    {
        string[] lines = await File.ReadAllLinesAsync(SessionFilePath(store, sessionId));
        return lines[0];
    }

    /// <summary>
    ///     The header record of a committed fixture, as that generation wrote
    ///     it — parsed with the store's own codec, so the fixture is read
    ///     the way the store reads it.
    /// </summary>
    private static async Task<SessionHeaderEntry> ReadCorpusHeader(int generation)
    {
        string[] lines = await File.ReadAllLinesAsync(FixturePath(generation));
        var header = JsonSerializer.Deserialize<SessionHeaderEntry>(
            lines[0], JsonlCodecContext.Default.SessionHeaderEntry);
        await Assert.That(header).IsNotNull();
        return header!;
    }
}
