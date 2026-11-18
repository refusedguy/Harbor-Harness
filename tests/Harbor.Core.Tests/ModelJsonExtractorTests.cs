using Harbor.Application.Sessions;
using TUnit.Assertions;

namespace Harbor.Core.Tests;

/// <summary>
///     goose-features #60: JSON must be pulled out of a model answer by
///     brace-balancing, not by a greedy substring or a regex, and a truncated
///     answer must stay truncated so the caller can fall back to the raw text.
/// </summary>
public sealed class ModelJsonExtractorTests
{
    [Test]
    public async Task TryExtract_LeadingObjectWithNestedBraces_ReturnsBalancedSlice()
    {
        const string response = """
            {
              "files": [{"path": "a{b}.cs", "note": "brace } and a ``` fence inside a string"}],
              "next_step": "finish"
            }
            trailing prose that is not part of the object
            """;

        bool ok = ModelJsonExtractor.TryExtract(response, out string? json);

        await Assert.That(ok).IsTrue();
        await Assert.That(json).IsEqualTo("""
            {
              "files": [{"path": "a{b}.cs", "note": "brace } and a ``` fence inside a string"}],
              "next_step": "finish"
            }
            """);
    }

    [Test]
    public async Task TryExtract_FencedJsonBlock_ReturnsObjectWithoutFence()
    {
        const string response = "Here is the summary:\n```json\n{\"answer\": 42}\n```\nDone.";

        bool ok = ModelJsonExtractor.TryExtract(response, out string? json);

        await Assert.That(ok).IsTrue();
        await Assert.That(json).IsEqualTo("{\"answer\": 42}");
    }

    [Test]
    public async Task TryExtract_ProseWithQuotedJson_ReturnsFalse()
    {
        // JSON merely mentioned in a sentence is not the answer. The leading
        // object rule keeps a prose quote from silently replacing the fallback.
        const string response = "I thought about {\"answer\": 1} but then changed my mind.";

        bool ok = ModelJsonExtractor.TryExtract(response, out string? json);

        await Assert.That(ok).IsFalse();
        await Assert.That(json).IsNull();
    }

    [Test]
    public async Task TryExtract_TruncatedObject_ReturnsFalse()
    {
        // No repair: inventing the missing brace would drop the continuation-
        // critical tail that the raw-text fallback still has.
        const string response = "```json\n{\"next_step\": \"keep going\", \"files\": [";

        bool ok = ModelJsonExtractor.TryExtract(response, out string? json);

        await Assert.That(ok).IsFalse();
        await Assert.That(json).IsNull();
    }

    [Test]
    public async Task TryExtract_AnalysisTerminator_IgnoresScratchpadFence()
    {
        const string response = """
            <analysis>
            Here is a scratch example, not the answer:
            ```json
            {"scratch": true}
            ```
            </analysis>

            ```json
            {"answer": 42}
            ```
            """;

        bool ok = ModelJsonExtractor.TryExtract(response, out string? json);

        await Assert.That(ok).IsTrue();
        await Assert.That(json).IsEqualTo("{\"answer\": 42}");
    }

    [Test]
    public async Task TryExtract_TerminatorQuotedInsideJson_KeepsTheRealAnswer()
    {
        const string response = """
            <analysis>thinking</analysis>
            ```json
            {"note": "quoted </analysis> inside", "answer": 7}
            ```
            """;

        bool ok = ModelJsonExtractor.TryExtract(response, out string? json);

        await Assert.That(ok).IsTrue();
        await Assert.That(json).IsEqualTo("{\"note\": \"quoted </analysis> inside\", \"answer\": 7}");
    }

    [Test]
    public async Task TryExtract_EarlierScratchFence_DoesNotShadowLaterAnswer()
    {
        // The last fence is truncated, so extraction falls back to an earlier
        // cut. The scratchpad fence there fails the "contains every later
        // terminator" filter and must not be returned.
        const string response = """
            <analysis>
            ```json
            {"trap": true}
            ```
            </analysis>
            ```json
            {"answer": 9, "tail": "cut off
            """;

        bool ok = ModelJsonExtractor.TryExtract(response, out string? json);

        await Assert.That(ok).IsFalse();
        await Assert.That(json).IsNull();
    }

    [Test]
    public async Task TryExtract_InvalidFirstFence_UsesTheNextValidOne()
    {
        const string response = """
            ```json
            {"broken": }
            ```
            ```json
            {"valid": true}
            ```
            """;

        bool ok = ModelJsonExtractor.TryExtract(response, out string? json);

        await Assert.That(ok).IsTrue();
        await Assert.That(json).IsEqualTo("{\"valid\": true}");
    }

    [Test]
    public async Task TryExtract_NullOrEmpty_ReturnsFalse()
    {
        bool empty = ModelJsonExtractor.TryExtract(string.Empty, out string? emptyJson);
        bool whitespace = ModelJsonExtractor.TryExtract("   \n", out string? whitespaceJson);
        bool none = ModelJsonExtractor.TryExtract(null, out string? noneJson);

        await Assert.That(empty).IsFalse();
        await Assert.That(emptyJson).IsNull();
        await Assert.That(whitespace).IsFalse();
        await Assert.That(whitespaceJson).IsNull();
        await Assert.That(none).IsFalse();
        await Assert.That(noneJson).IsNull();
    }
}
