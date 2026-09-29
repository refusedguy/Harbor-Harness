using System.Reflection;
using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Harbor.Storage.Tests;

/// <summary>
///     #550 — the SQLite half of the tag → type factory contract. The JSONL half is
///     in <c>Harbor.Storage.Jsonl.Tests/JsonlForeignPartTagTests</c>; the two stores
///     must agree, because a session migrates between them and an older build reading
///     a newer session is exactly the case the silence used to hide.
/// </summary>
/// <remarks>
///     <para>
///         <c>ContentPartJsonConverter.Read</c> is a tag → type factory, not a walk over
///         the <see cref="ContentPart" /> union, so #461's
///         <see cref="ContentPartVisitor{TResult}" /> never reached it: an unknown tag
///         returned <see langword="null" /> and
///         <c>ContentPartListJsonConverter</c> — the array converter that has the last
///         word on whether a part survives — skipped the null. A message read back from
///         a row could be a DIFFERENT, shorter message than the one stored, with nothing
///         logged anywhere.
///     </para>
///     <para>
///         The policy: refuse the row by name. The store already collects unreadable
///         rows into a <c>skipped</c> list and <c>LogWarning</c>s them, so the cost of
///         refusing is one warning naming the tag; the cost of the old behaviour was an
///         assistant turn with a missing part that no one could account for.
///     </para>
/// </remarks>
[ParallelLimiter<SqliteStoreLimit>]
public class SqliteForeignPartTagTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 29, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A tag no build of Harbor has ever written.</summary>
    private const string ForeignTag = "video";

    // ── the row is refused, and the tag is named ────────────────────────────

    [Test]
    public async Task TryDeserializeMessage_AssistantWithAForeignPartTag_FailsNamingTheTag()
    {
        var decoded = SqliteMappers.TryDeserializeMessage("assistant", AssistantPayload(ForeignJson));

        await Assert.That(decoded.IsFailure).IsTrue();
        await Assert.That(decoded.Error).Contains(ForeignTag);
    }

    [Test]
    public async Task TryDeserializeMessage_ForeignTagInTheMiddleOfTheArray_RefusesTheMessage()
    {
        // The array converter is the last place that could hide a lost part, and it
        // used to: three parts in the row, the middle one foreign, and the reader
        // returned a message with two — indistinguishable from a shorter turn.
        string parts = TextPartJson("before") + "," + ForeignJson + "," + TextPartJson("after");
        var decoded = SqliteMappers.TryDeserializeMessage("assistant", AssistantPayload(parts));

        await Assert.That(decoded.IsFailure).IsTrue();
        await Assert.That(decoded.Error).Contains(ForeignTag);
    }

    [Test]
    public async Task TryDeserializeMessage_PartWithNoTypeAtAll_FailsNamingTheAbsence()
    {
        var decoded = SqliteMappers.TryDeserializeMessage(
            "assistant", AssistantPayload("""{"text":"no discriminator at all"}"""));

        await Assert.That(decoded.IsFailure).IsTrue();
        await Assert.That(decoded.Error).Contains("type");
    }

    [Test]
    public async Task TryDeserializeMessage_KnownTagMissingItsField_FailsNamingTheField()
    {
        // Same silence one step in: the tag is ours, the row is corrupt. A missing
        // field used to surface as "The given key was not present in the dictionary",
        // which tells nobody which row or which part is broken.
        var decoded = SqliteMappers.TryDeserializeMessage(
            "assistant", AssistantPayload("""{"type":"file","path":"a.png"}"""));

        await Assert.That(decoded.IsFailure).IsTrue();
        await Assert.That(decoded.Error).Contains("file");
    }

    [Test]
    public async Task TryDeserializeMessage_PreCamelCasePart_StillDecodes()
    {
        // The fix must not cost the legacy tolerance the converter was built for: rows
        // written before the naming policy still carry PascalCase members, and a reader
        // that now refuses what it used to accept would truncate every old session.
        // A raw literal keeps the JSON readable; a row is one document, so the line
        // breaks inside it are formatting only and are folded away before use.
        string legacy = """
            {"Id":"a-legacy","SessionId":"sess-1","CreatedAt":"2026-09-29T09:00:01.0000000+00:00","Role":"assistant",
             "Parts":[{"Type":"text","Text":"pascal case survives"}],"StopReason":"stop",
             "Usage":{"InputTokens":1,"OutputTokens":2},"Model":"p/m"}
            """.ReplaceLineEndings(string.Empty);

        var decoded = SqliteMappers.TryDeserializeMessage("assistant", legacy);
        await Assert.That(decoded.IsSuccess).IsTrue();
        var assistant = (AssistantMessage)decoded.Value;
        await Assert.That(assistant.Parts.Count).IsEqualTo(1);
        await Assert.That(((TextPart)assistant.Parts[0]).Text).IsEqualTo("pascal case survives");
    }

    // ── end to end: the store skips the row and says so ──────────────────────

    [Test]
    public async Task GetMessagesAsync_RowWithAForeignPartTag_KeepsTheRestAndWarnsWithTheTag()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"harbor-550-sqlite-{Guid.NewGuid():N}.db");
        var logger = new CapturingLogger<SqliteSessionStore>();
        var store = new SqliteSessionStore(dbPath, logger);
        try
        {
            var session = (await store.CreateAsync("/proj", "code", "kilocode", "kilo-auto/free")).Value;
            await store.AppendMessageAsync(session.Id,
                new UserMessage("u1", session.Id, T0, "list the files", "code", "kilo-auto/free"));
            await store.AppendMessageAsync(session.Id, new AssistantMessage(
                "a1", session.Id, T0.AddSeconds(1), [new TextPart("here they are")],
                StopReason.Stop, new Usage(3, 4), "kilo-auto/free"));

            // The row a newer build would have written, planted directly.
            PlantRow(dbPath, session.Id, "a-foreign", "assistant", T0.AddSeconds(2),
                AssistantPayload(ForeignJson));

            var read = await store.GetMessagesAsync(session.Id);

            // Both good messages survive — a refused row is not a lost session.
            await Assert.That(read.IsSuccess).IsTrue();
            await Assert.That(read.Value.Select(m => m.Id)).IsEquivalentTo(new[] { "u1", "a1" });

            // And the refusal is not silent.
            string warning = string.Join("\n", logger.Warnings);
            await Assert.That(logger.Warnings).IsNotEmpty();
            await Assert.That(warning).Contains(ForeignTag);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    // ── the guard: the tag table cannot drift from the union ─────────────────

    [Test]
    public async Task Every_ContentPart_Subtype_RoundTrips_Through_The_Sqlite_Codec()
    {
        // What a compiler cannot check. #461's visitor makes the COMPILER reject a
        // walker that forgets a kind; this factory is a table of string tags, so a
        // fifth subtype with no row in it decodes to null — silently dropped — until
        // something notices. Derived from the union by reflection on purpose: adding a
        // subtype fails HERE until the tag table is updated with it.
        Type[] subtypes = DeclaredSubtypes();
        var problems = new List<string>();

        foreach (Type subtype in subtypes)
        {
            var sample = (ContentPart)Sample(subtype);
            var message = new AssistantMessage(
                "a1", "sess-1", T0, [sample], StopReason.Stop, new Usage(1, 2), "p/m");

            string json = SqliteMappers.SerializeMessage(message);
            var decoded = SqliteMappers.TryDeserializeMessage("assistant", json);

            if (decoded.IsFailure)
            {
                problems.Add($"{subtype.Name} (tag '{sample.Type}'): {decoded.Error}");
                continue;
            }

            var parts = ((AssistantMessage)decoded.Value).Parts;
            if (parts.Count != 1)
            {
                problems.Add($"expected one part for {subtype.Name}, decoded {parts.Count}");
                continue;
            }

            if (parts[0].GetType() != subtype)
                problems.Add($"tag '{parts[0].Type}' decoded as {parts[0].GetType().Name}, not {subtype.Name}");
        }

        await Assert.That(subtypes.Length).IsGreaterThan(0);
        await Assert.That(string.Join(" | ", problems)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task PartTag_Outside_The_Union_Is_Refused()
    {
        // "rogue" is the tag a hypothetical fifth ContentPart subtype would carry —
        // the exact shape of the drift the guard above is about. Both are refused, by
        // name, and neither is quietly shortened away.
        foreach (string tag in new[] { ForeignTag, new RoguePart("payload").Type })
        {
            var decoded = SqliteMappers.TryDeserializeMessage(
                "assistant", AssistantPayload("{\"type\":\"" + tag + "\",\"payload\":\"x\"}"));

            await Assert.That(decoded.IsFailure).IsTrue();
            await Assert.That(decoded.Error).Contains(tag);
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>One assistant message payload whose <c>parts</c> array is <paramref name="partsJson" />.</summary>
    private static string AssistantPayload(string partsJson) =>
        "{\"id\":\"a-foreign\",\"sessionId\":\"sess-1\",\"createdAt\":\"2026-09-29T09:00:01.0000000+00:00\","
        + "\"role\":\"assistant\",\"parts\":[" + partsJson + "],\"stopReason\":\"stop\","
        + "\"usage\":{\"inputTokens\":1,\"outputTokens\":2},\"model\":\"p/m\"}";

    /// <summary>One persisted text part carrying <paramref name="text" />.</summary>
    private static string TextPartJson(string text) => $"{{\"type\":\"text\",\"text\":\"{text}\"}}";

    /// <summary>The persisted shape a newer build would have written for a video part.</summary>
    private const string ForeignJson =
        """{"type":"video","url":"https://example.invalid/v.mp4"}""";

    /// <summary>
    ///     Insert a message row behind the store's back, the way a database written by
    ///     a newer Harbor would look to this build. The store's own writer refuses a
    ///     foreign part by design, so the fixture has to bypass it.
    /// </summary>
    private static void PlantRow(
        string dbPath, string sessionId, string id, string role, DateTimeOffset createdAt, string payload)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO messages (id, session_id, parent_id, role, agent, model, created_at, created_at_ms, payload)
            VALUES (@id, @sid, NULL, @role, NULL, NULL, @created, @createdMs, @payload)
            """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@role", role);
        cmd.Parameters.AddWithValue("@created", createdAt.ToString("O"));
        cmd.Parameters.AddWithValue("@createdMs", createdAt.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("@payload", payload);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    ///     The concrete cases of the union, from the assembly that declares it (not
    ///     from every loaded assembly, so the <see cref="RoguePart" /> below cannot
    ///     poison the enumeration).
    /// </summary>
    private static Type[] DeclaredSubtypes() =>
        typeof(ContentPart).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ContentPart).IsAssignableFrom(t))
            .ToArray();

    /// <summary>
    ///     A throwaway instance of a union case. Only the tags matter here, so strings
    ///     get a placeholder, the args element a real one (an undefined
    ///     <see cref="JsonElement" /> is not writable), and value types their default.
    /// </summary>
    private static object Sample(Type caseType)
    {
        ConstructorInfo ctor = caseType.GetConstructors()
            .OrderBy(c => c.GetParameters().Length)
            .First();

        ParameterInfo[] parameters = ctor.GetParameters();
        var args = new object?[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            Type parameterType = parameters[i].ParameterType;
            args[i] = parameterType == typeof(string)
                ? "sample"
                : parameterType == typeof(JsonElement) ? SampleArgs()
                : parameterType.IsValueType ? Activator.CreateInstance(parameterType) : null;
        }

        return ctor.Invoke(args);
    }

    private static JsonElement SampleArgs()
    {
        using var doc = JsonDocument.Parse("""{"path":"README.md","limit":10}""");
        return doc.RootElement.Clone();
    }

    /// <summary>
    ///     A part kind the union does not know — the shape a future
    ///     <see cref="ContentPart" /> subtype has if someone adds one without teaching
    ///     the storage codecs about it.
    /// </summary>
    private sealed record RoguePart(string Text) : ContentPart
    {
        public override string Type => "rogue";
    }

    /// <summary>Keeps the warnings, so "the refusal is not silent" can be asserted.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        internal List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
