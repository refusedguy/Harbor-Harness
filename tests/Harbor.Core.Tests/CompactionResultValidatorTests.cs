using Harbor.Application.Sessions;
using TUnit.Assertions;

namespace Harbor.Core.Tests;

/// <summary>
///     #2680 F039: a compaction reply must be classified before its text is
///     allowed into session history. The validator is pure, so the tests pin
///     the whole contract with no LLM, store or product wiring.
/// </summary>
public sealed class CompactionResultValidatorTests
{
    [Test]
    public async Task Classify_NullAndWhitespace_AreEmpty()
    {
        await Assert.That(CompactionResultValidator.Classify(null))
            .IsEqualTo(CompactionResultKind.Empty);
        await Assert.That(CompactionResultValidator.Classify(""))
            .IsEqualTo(CompactionResultKind.Empty);
        await Assert.That(CompactionResultValidator.Classify("   \r\n\t "))
            .IsEqualTo(CompactionResultKind.Empty);
    }

    [Test]
    public async Task Classify_EmptyMarkers_AreEmpty()
    {
        string[] markers = ["null", "[]", "{}", "(empty)", "[EMPTY]", "<empty>", "N/A", "none", " {} "];
        for (int i = 0; i < markers.Length; i++)
        {
            await Assert.That(CompactionResultValidator.Classify(markers[i]))
                .IsEqualTo(CompactionResultKind.Empty);
        }
    }

    [Test]
    public async Task Classify_UsableText_IsValid()
    {
        string[] replies =
        [
            "The user wants to ship the feature before Friday.",
            "## Current work\nRewrote the reader to use Utf8JsonReader.",
            "Fixed the failing test by adding a using directive."
        ];
        for (int i = 0; i < replies.Length; i++)
        {
            await Assert.That(CompactionResultValidator.Classify(replies[i]))
                .IsEqualTo(CompactionResultKind.Valid);
        }
    }

    [Test]
    public async Task Classify_WellFormedJson_IsValid()
    {
        const string reply = """
            {"user_intent":["ship it"],"next_step":"merge"}
            """;

        await Assert.That(CompactionResultValidator.Classify(reply))
            .IsEqualTo(CompactionResultKind.Valid);
    }

    [Test]
    public async Task Classify_TrailingEllipsis_IsTruncated()
    {
        await Assert.That(CompactionResultValidator.Classify("The user was working on the reade..."))
            .IsEqualTo(CompactionResultKind.Truncated);
        await Assert.That(CompactionResultValidator.Classify("Still pending\u2026"))
            .IsEqualTo(CompactionResultKind.Truncated);
    }

    [Test]
    public async Task Classify_UnclosedFence_IsTruncated()
    {
        await Assert.That(CompactionResultValidator.Classify("Here is the diff:\n```csharp\nvar x = 1;"))
            .IsEqualTo(CompactionResultKind.Truncated);
        await Assert.That(CompactionResultValidator.Classify("```csharp\nvar x = 1;\n```\nDone."))
            .IsEqualTo(CompactionResultKind.Valid);
    }

    [Test]
    public async Task Classify_UnbalancedJson_IsTruncated()
    {
        await Assert.That(CompactionResultValidator.Classify("{\"user_intent\":[\"ship it\"]"))
            .IsEqualTo(CompactionResultKind.Truncated);
        await Assert.That(CompactionResultValidator.Classify("{\"files\":[{\"path\":\"a.cs\"}]"))
            .IsEqualTo(CompactionResultKind.Truncated);
    }

    [Test]
    public async Task Classify_TrailingContinuationPunctuation_IsTruncated()
    {
        await Assert.That(CompactionResultValidator.Classify("Pending tasks: run CI,"))
            .IsEqualTo(CompactionResultKind.Truncated);
        await Assert.That(CompactionResultValidator.Classify("Next steps;"))
            .IsEqualTo(CompactionResultKind.Truncated);
        await Assert.That(CompactionResultValidator.Classify("Current work:"))
            .IsEqualTo(CompactionResultKind.Truncated);
    }

    [Test]
    public async Task EmptyMarkerCheck_PrecedesTruncationSignals()
    {
        // "{}" balances and "[]" balances, but both are still no-content markers.
        await Assert.That(CompactionResultValidator.Classify("[]")).IsEqualTo(CompactionResultKind.Empty);
        await Assert.That(CompactionResultValidator.Classify("{}")).IsEqualTo(CompactionResultKind.Empty);
    }

    [Test]
    public async Task ShouldRetry_OnlyForEmptyAndTruncated()
    {
        await Assert.That(CompactionResultValidator.ShouldRetry(CompactionResultKind.Empty)).IsTrue();
        await Assert.That(CompactionResultValidator.ShouldRetry(CompactionResultKind.Truncated)).IsTrue();
        await Assert.That(CompactionResultValidator.ShouldRetry(CompactionResultKind.Valid)).IsFalse();
    }

    [Test]
    public async Task Describe_IsStableAndNonEmpty()
    {
        await Assert.That(CompactionResultValidator.Describe(CompactionResultKind.Empty))
            .IsEqualTo("Compaction produced an empty summary; retrying.");
        await Assert.That(CompactionResultValidator.Describe(CompactionResultKind.Truncated))
            .IsEqualTo("Compaction summary looks truncated; retrying.");
        await Assert.That(CompactionResultValidator.Describe(CompactionResultKind.Valid))
            .IsEqualTo("Compaction summary is usable.");
    }

    [Test]
    public async Task Classify_IsCultureInvariantAndIdempotent()
    {
        const string reply = "The user asked to keep the build green.";
        CompactionResultKind first = CompactionResultValidator.Classify(reply);
        CompactionResultKind second = CompactionResultValidator.Classify(reply);

        await Assert.That(second).IsEqualTo(first);
        await Assert.That(first).IsEqualTo(CompactionResultKind.Valid);
    }
}
