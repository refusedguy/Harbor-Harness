using System.Reflection;
using System.Text;
using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Storage.Memory;
using Microsoft.Extensions.Logging;

namespace Harbor.Storage.Jsonl.Tests;

/// <summary>
///     #550 — a persisted part whose <c>type</c> tag this build does not know is
///     data to refuse, not data to drop.
/// </summary>
/// <remarks>
///     <para>
///         The JSONL read path is a <em>tag → type factory</em>, not a walk over the
///         <see cref="ContentPart" /> union, so #461's
///         <see cref="ContentPartVisitor{TResult}" /> never touched it: a part whose tag
///         is unknown came back as <see langword="null" /> and the array loop skipped
///         the null. The observable bug is a session written by a NEWER Harbor opening
///         in an older one with its newest turns quietly shortened — the data does not
///         arrive and nothing says why.
///     </para>
///     <para>
///         The policy these tests pin: the record is refused <em>by name</em>. The
///         store already has a per-record failure channel (it aggregates per-line
///         errors and <c>LogWarning</c>s them), so refusing costs one warning line and
///         keeps the rest of the transcript; the old behaviour cost a silently
///         truncated session nobody could explain.
///     </para>
/// </remarks>
public class JsonlForeignPartTagTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 29, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A tag no build of Harbor has ever written.</summary>
    private const string ForeignTag = "video";

    // ── the record is refused, and the tag is named ─────────────────────────

    [Test]
    public async Task Parse_LineWithAForeignPartTag_FailsNamingTheTag()
    {
        var parsed = JsonlLineParser.Parse(
            Encoding.UTF8.GetBytes(AssistantLine(ForeignJson)), "sess-1");

        await Assert.That(parsed.IsFailure).IsTrue();
        await Assert.That(parsed.Error).Contains(ForeignTag);
    }

    [Test]
    public async Task Parse_LineWithAForeignPartTag_RefusesTheRecord_NotShortensTheParts()
    {
        // The exact shape of the reported bug: three parts, the middle one written by
        // a newer Harbor. The old parser returned a message with TWO parts and no
        // error anywhere, so the middle part simply was not there any more. Refusing
        // the record is the difference between a visible hole and a silent one.
        string parts = TextPartJson("before") + "," + ForeignJson + "," + TextPartJson("after");
        var parsed = JsonlLineParser.Parse(Encoding.UTF8.GetBytes(AssistantLine(parts)), "sess-1");

        await Assert.That(parsed.IsFailure).IsTrue();
        await Assert.That(parsed.Error).Contains(ForeignTag);
    }

    [Test]
    public async Task Parse_PartWithNoTypeAtAll_FailsNamingTheAbsence()
    {
        var parsed = JsonlLineParser.Parse(
            Encoding.UTF8.GetBytes(AssistantLine("""{"text":"no discriminator at all"}""")), "sess-1");

        await Assert.That(parsed.IsFailure).IsTrue();
        await Assert.That(parsed.Error).Contains("type");
    }

    [Test]
    public async Task Parse_KnownTagMissingItsField_FailsInsteadOfDroppingThePart()
    {
        // Same silence, one step in: the tag is ours, the record is corrupt. Also
        // refused by name — a part the line claims to contain but that cannot be
        // rebuilt is not a part the reader may invent.
        var parsed = JsonlLineParser.Parse(
            Encoding.UTF8.GetBytes(AssistantLine("""{"type":"file","path":"a.png"}""")), "sess-1");

        await Assert.That(parsed.IsFailure).IsTrue();
        await Assert.That(parsed.Error).Contains("file");
    }

    // ── end to end: the store skips the record and says so ──────────────────

    [Test]
    public async Task GetMessagesAsync_LineWithAForeignPartTag_KeepsTheRestAndWarnsWithTheTag()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"harbor-550-jsonl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var logger = new CapturingLogger<JsonlSessionStore>();
        var store = new JsonlSessionStore(tempDir, logger);
        try
        {
            var session = (await store.CreateAsync("/proj", "code", "kilocode", "kilo-auto/free")).Value;
            await store.AppendMessageAsync(session.Id,
                new UserMessage("u1", session.Id, T0, "list the files", "code", "kilo-auto/free"));
            await store.AppendMessageAsync(session.Id, new AssistantMessage(
                "a1", session.Id, T0.AddSeconds(1), [new TextPart("here they are")],
                StopReason.Stop, new Usage(3, 4), "kilo-auto/free"));

            // The line a newer build would have written, dropped into the file.
            string path = Path.Combine(store.GetRootDirectory(), $"{session.Id}.jsonl");
            await File.AppendAllTextAsync(path, AssistantLine(ForeignJson) + "\n");

            var read = await store.GetMessagesAsync(session.Id);

            // Both good messages survive — a refused record is not a lost session.
            await Assert.That(read.IsSuccess).IsTrue();
            await Assert.That(read.Value.Select(m => m.Id)).IsEquivalentTo(new[] { "u1", "a1" });

            // And the refusal is not silent: the warning carries the offending tag,
            // which is the whole difference this issue is about.
            string warning = string.Join("\n", logger.Warnings);
            await Assert.That(logger.Warnings).IsNotEmpty();
            await Assert.That(warning).Contains(ForeignTag);
            await Assert.That(warning).Contains("a-foreign");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // ── the import path (JsonElement factory) agrees with the store path ────

    [Test]
    public async Task DeserializeMessage_LineWithAForeignPartTag_FailsNamingTheTag()
    {
        using var doc = JsonDocument.Parse(AssistantLine(ForeignJson));

        var decoded = JsonlMessageCodec.DeserializeMessage("sess-1", doc.RootElement.Clone());

        await Assert.That(decoded.IsFailure).IsTrue();
        await Assert.That(decoded.Error).Contains(ForeignTag);
    }

    [Test]
    public async Task DeserializeMessage_KnownTagMissingItsField_FailsInsteadOfThrowing()
    {
        // Pre-#550 this reached `element.GetProperty("mimeType")` and threw
        // KeyNotFoundException straight out of the porter's decode — an unhandled
        // exception that would abort the whole import instead of skipping one line.
        using var doc = JsonDocument.Parse(AssistantLine("""{"type":"file","path":"a.png"}"""));

        var decoded = JsonlMessageCodec.DeserializeMessage("sess-1", doc.RootElement.Clone());

        await Assert.That(decoded.IsFailure).IsTrue();
        await Assert.That(decoded.Error).Contains("file");
    }

    [Test]
    public async Task ImportAsync_BodyLineWithAForeignPartTag_SkipsThatLineAndImportsTheRest()
    {
        string root = Path.Combine(Path.GetTempPath(), $"harbor-550-import-{Guid.NewGuid():N}");
        string jsonlDir = Path.Combine(root, "jsonl");
        Directory.CreateDirectory(jsonlDir);
        try
        {
            var store = new JsonlSessionStore(jsonlDir, new CapturingLogger<JsonlSessionStore>());
            var session = (await store.CreateAsync("/proj", "code", "kilocode", "kilo-auto/free")).Value;
            await store.AppendMessageAsync(session.Id,
                new UserMessage("u1", session.Id, T0, "list the files", "code", "kilo-auto/free"));

            var porter = new JsonlSessionPorter(new CapturingLogger<JsonlSessionPorter>());
            var exported = new StringWriter();
            await Assert.That((await porter.ExportAsync(store, session.Id, exported)).IsSuccess).IsTrue();

            // Splice a foreign-part line into the exported body, before a good one.
            var spliced = new StringBuilder(exported.ToString());
            spliced.Append(AssistantLine(ForeignJson, "a-skewed")).Append('\n');
            spliced.Append(AssistantLine("""{"type":"text","text":"tail"}""", "a-tail")).Append('\n');

            var target = new MemorySessionStore();
            var import = await porter.ImportAsync(target, new StringReader(spliced.ToString()));

            // The import COMPLETES — a refused line is a skipped line, not a failed
            // import — and the messages around it are all there.
            await Assert.That(import.IsSuccess).IsTrue();
            var messages = await target.GetMessagesAsync(import.Value);
            await Assert.That(messages.IsSuccess).IsTrue();
            await Assert.That(messages.Value.Select(m => m.Id))
                .IsEquivalentTo(new[] { "u1", "a-tail" });
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    // ── the write side: the store cannot emit a tag its reader would refuse ──

    [Test]
    public async Task SerializePart_APartKindOutsideTheUnion_RefusesInsteadOfWritingAPlaceholder()
    {
        // #461 already made the SQLite writer refuse an unrecognised kind. Before this
        // fix the JSONL writer wrote {"type":"unknown"} instead — a record its OWN
        // reader could not decode, i.e. a store that corrupts what it writes.
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => JsonlMessageCodec.SerializePart(new RoguePart("payload")));

        await Assert.That(ex.Message.Contains("RoguePart", StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task SerializeMessage_AMessageKindOutsideTheUnion_RefusesInsteadOfWritingAPlaceholder()
    {
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => JsonlMessageCodec.SerializeMessagePayload(new RogueMessage("id", "sess-1", T0)));

        await Assert.That(ex.Message.Contains("RogueMessage", StringComparison.Ordinal)).IsTrue();
    }

    // ── the guard: the tag table cannot drift from the union ─────────────────

    [Test]
    public async Task Every_ContentPart_Subtype_RoundTrips_Through_The_Line_Parser()
    {
        // What a compiler cannot check. #461's visitor makes the COMPILER reject a
        // walker that forgets a kind; these factories are a table of string tags, and
        // a fifth subtype with no row in the table used to cost one silently dropped
        // part per session. Derived from the union by reflection on purpose: adding a
        // subtype fails HERE until the tag tables are updated with it.
        Type[] subtypes = DeclaredSubtypes();
        var problems = new List<string>();

        foreach (Type subtype in subtypes)
        {
            var sample = (ContentPart)Sample(subtype);

            var parsed = JsonlLineParser.Parse(
                Encoding.UTF8.GetBytes(AssistantLine(PartJson(sample))), "sess-1");

            if (parsed.IsFailure)
            {
                problems.Add($"{subtype.Name} (tag '{sample.Type}'): {parsed.Error}");
                continue;
            }

            problems.AddRange(Mismatch(subtype, (AssistantMessage)parsed.Value));
        }

        await Assert.That(subtypes.Length).IsGreaterThan(0);
        await Assert.That(string.Join(" | ", problems)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task Every_ContentPart_Subtype_RoundTrips_Through_The_Import_Factory()
    {
        // The same table, the other reader. Two factories, one policy — the issue is
        // explicit that the JSONL and SQLite paths must agree, and the JSONL store
        // has two factories of its own.
        Type[] subtypes = DeclaredSubtypes();
        var problems = new List<string>();

        foreach (Type subtype in subtypes)
        {
            var sample = (ContentPart)Sample(subtype);
            using var doc = JsonDocument.Parse(AssistantLine(PartJson(sample)));

            var decoded = JsonlMessageCodec.DeserializeMessage("sess-1", doc.RootElement.Clone());
            if (decoded.IsFailure)
            {
                problems.Add($"{subtype.Name} (tag '{sample.Type}'): {decoded.Error}");
                continue;
            }

            problems.AddRange(Mismatch(subtype, (AssistantMessage)decoded.Value));
        }

        await Assert.That(subtypes.Length).IsGreaterThan(0);
        await Assert.That(string.Join(" | ", problems)).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task PartTag_Outside_The_Union_Is_Refused_By_Both_Factories()
    {
        // …and the other direction: a tag the reader accepts but the union does not
        // declare is as much a drift as a subtype the reader cannot decode.
        using var doc = JsonDocument.Parse(AssistantLine(ForeignJson));
        var viaImport = JsonlMessageCodec.DeserializeMessage("sess-1", doc.RootElement.Clone());
        var viaSpan = JsonlLineParser.Parse(
            Encoding.UTF8.GetBytes(AssistantLine(ForeignJson)), "sess-1");

        await Assert.That(viaImport.IsFailure).IsTrue();
        await Assert.That(viaSpan.IsFailure).IsTrue();
        await Assert.That(viaImport.Error).Contains(ForeignTag);
        await Assert.That(viaSpan.Error).Contains(ForeignTag);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    ///     What the guard is really after: the tag the writer emits for
    ///     <paramref name="subtype" /> decodes back to exactly that subtype, and
    ///     exactly one part comes back.
    /// </summary>
    private static List<string> Mismatch(Type subtype, AssistantMessage decoded)
    {
        if (decoded.Parts.Count != 1)
        {
            return [$"expected one part for {subtype.Name}, decoded {decoded.Parts.Count}"];
        }

        ContentPart part = decoded.Parts[0];
        return part.GetType() == subtype
            ? []
            : [$"tag '{part.Type}' decoded as {part.GetType().Name}, not {subtype.Name}"];
    }

    /// <summary>
    ///     One assistant record whose <c>parts</c> array is <paramref name="partsJson" />.
    ///     Built by concatenation rather than as a multi-line raw literal because a
    ///     JSONL record is a single line: an embedded newline would split it into two
    ///     records and the test would be measuring the wrong thing.
    /// </summary>
    private static string AssistantLine(string partsJson, string id = "a-foreign") =>
        "{\"type\":\"message\",\"id\":\"" + id + "\",\"createdAt\":\"2026-09-29T09:00:01.0000000+00:00\","
        + "\"role\":\"assistant\",\"payload\":{\"parts\":[" + partsJson + "],"
        + "\"stopReason\":\"stop\",\"usage\":{\"inputTokens\":1,\"outputTokens\":2},"
        + "\"model\":\"kilo-auto/free\"}}";

    /// <summary>One persisted text part carrying <paramref name="text" />.</summary>
    private static string TextPartJson(string text) => $"{{\"type\":\"text\",\"text\":\"{text}\"}}";

    /// <summary>The persisted shape a newer build would have written for a video part.</summary>
    private const string ForeignJson =
        """{"type":"video","url":"https://example.invalid/v.mp4"}""";

    /// <summary>
    ///     The exact JSON the store's writer emits for <paramref name="part" /> — the
    ///     read path's input is written by the read path's sibling, so the guard pairs
    ///     the two rather than trusting a hand-written fixture.
    /// </summary>
    private static string PartJson(ContentPart part)
    {
        object payload = JsonlMessageCodec.SerializePart(part);
        return JsonSerializer.Serialize(payload, payload.GetType(), JsonlCodecContext.JsonOptions);
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
    ///     <see cref="ContentPart" /> subtype has if someone adds one without
    ///     teaching the storage codecs about it.
    /// </summary>
    private sealed record RoguePart(string Text) : ContentPart
    {
        public override string Type => "rogue";
    }

    private sealed record RogueMessage(string Id, string SessionId, DateTimeOffset CreatedAt)
        : AgentMessage(Id, SessionId, CreatedAt)
    {
        public override string Role => "rogue";
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
