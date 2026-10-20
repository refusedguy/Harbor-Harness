using Harbor.Tui.CellForge.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// O10 #1179: unit pins for ThinkingSummary (opencode reasoningSummary /
// ReasoningHeader port). Pure helpers — no rendering involved.
public class ThinkingSummaryTests
{
    // #1770: the summarize vectors used to be one copy-pasted method each.
    // One table per assert shape keeps the expectation literal in the assert.
    [Test]
    [Arguments("**Inspecting PR workflow**\n\nFirst step", "Inspecting PR workflow", "First step", DisplayName = "BoldTitle_ExtractsTitleAndBody")]
    [Arguments("**Inspecting PR workflow**", "Inspecting PR workflow", "", DisplayName = "BoldTitle_StreamingWithoutBody_YieldsTitleOnly")]
    public async Task Summarize_SplitsTitleAndBody(string text, string expectedTitle, string expectedBody)
    {
        var summary = ThinkingSummary.Summarize(text);
        await Assert.That(summary.Title).IsEqualTo(expectedTitle);
        await Assert.That(summary.Body).IsEqualTo(expectedBody);
    }

    [Test]
    [Arguments("**Draft**\nbody on next line", "**Draft**", "body on next line", DisplayName = "BoldTitle_SingleNewline_DoesNotMatch_UsesFirstLine")]
    [Arguments("weighing options here\nsecond line", "weighing options here", "second line", DisplayName = "PlainText_FallsBackToFirstLine")]
    public async Task Summarize_TitleIsFirstLine_BodyKeepsTheRest(string text, string expectedTitle, string expectedBodyPart)
    {
        // Parity with the opencode regex (requires \n\n or end after **Title**).
        var summary = ThinkingSummary.Summarize(text);
        await Assert.That(summary.Title).IsEqualTo(expectedTitle);
        await Assert.That(summary.Body.Contains(expectedBodyPart)).IsTrue();
    }

    [Test]
    public async Task RedactedMarker_IsStripped()
    {
        var summary = ThinkingSummary.Summarize("[REDACTED]quiet plan");
        await Assert.That(summary.Title).IsEqualTo("quiet plan");
    }

    [Test]
    public async Task Empty_YieldsNullTitle()
    {
        var summary = ThinkingSummary.Summarize("   ");
        await Assert.That(summary.Title).IsNull();
        await Assert.That(summary.Body).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task LongFirstLine_IsClamped()
    {
        string line = new('x', ThinkingSummary.MaxTitleChars + 20);
        var summary = ThinkingSummary.Summarize(line);
        await Assert.That(summary.Title!.Length).IsLessThanOrEqualTo(ThinkingSummary.MaxTitleChars);
    }

    // #1770: the header vectors used to be one copy-pasted method each. The
    // assert never changed — only the (text, duration, done) triple did. Every
    // row keeps its original test name as the case display name.
    [Test]
    [Arguments("weighing options", "2.5s", true, "+ Thought: weighing options · 2.5s", DisplayName = "Header_Done_WithTitleAndDuration")]
    [Arguments("   ", "300ms", true, "+ Thought: 300ms", DisplayName = "Header_Done_WithoutTitle_ShowsDuration")]
    [Arguments("weighing options", null, true, "+ Thought: weighing options", DisplayName = "Header_Done_WithoutDuration_OmitsSegment")]
    [Arguments("first thought\nsecond", "9.9s", false, "+ Thinking: first thought", DisplayName = "Header_Streaming_ShowsNoDuration")]
    public async Task Header_CollapsesToTheExpectedLine(string text, string? durationText, bool done, string expected)
    {
        string header = ThinkingSummary.CollapsedHeader(text, durationText, done);
        await Assert.That(header).IsEqualTo(expected);
    }

    [Test]
    [Arguments(true, "+ Thought", DisplayName = "Header_Empty_IsBareLabel_Done")]
    [Arguments(false, "+ Thinking", DisplayName = "Header_Empty_IsBareLabel_Streaming")]
    public async Task Header_Empty_IsBareLabel(bool done, string expected)
    {
        await Assert.That(ThinkingSummary.CollapsedHeader(null, null, done)).IsEqualTo(expected);
    }
}
