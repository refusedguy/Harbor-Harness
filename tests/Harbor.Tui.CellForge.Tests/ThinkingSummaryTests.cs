using Harbor.Tui.CellForge.Widgets;
using TUnit.Core;

namespace Harbor.Tui.CellForge.Tests;

// O10 #1179: unit pins for ThinkingSummary (opencode reasoningSummary /
// ReasoningHeader port). Pure helpers — no rendering involved.
public class ThinkingSummaryTests
{
    [Test]
    public async Task BoldTitle_ExtractsTitleAndBody()
    {
        var summary = ThinkingSummary.Summarize("**Inspecting PR workflow**\n\nFirst step");
        await Assert.That(summary.Title).IsEqualTo("Inspecting PR workflow");
        await Assert.That(summary.Body).IsEqualTo("First step");
    }

    [Test]
    public async Task BoldTitle_StreamingWithoutBody_YieldsTitleOnly()
    {
        var summary = ThinkingSummary.Summarize("**Inspecting PR workflow**");
        await Assert.That(summary.Title).IsEqualTo("Inspecting PR workflow");
        await Assert.That(summary.Body).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task BoldTitle_SingleNewline_DoesNotMatch_UsesFirstLine()
    {
        // Parity with the opencode regex (requires \n\n or end after **Title**).
        var summary = ThinkingSummary.Summarize("**Draft**\nbody on next line");
        await Assert.That(summary.Title).IsEqualTo("**Draft**");
        await Assert.That(summary.Body.Contains("body on next line")).IsTrue();
    }

    [Test]
    public async Task PlainText_FallsBackToFirstLine()
    {
        var summary = ThinkingSummary.Summarize("weighing options here\nsecond line");
        await Assert.That(summary.Title).IsEqualTo("weighing options here");
        await Assert.That(summary.Body.Contains("second line")).IsTrue();
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

    [Test]
    public async Task Header_Done_WithTitleAndDuration()
    {
        string header = ThinkingSummary.CollapsedHeader("weighing options", "2.5s", done: true);
        await Assert.That(header).IsEqualTo("+ Thought: weighing options · 2.5s");
    }

    [Test]
    public async Task Header_Done_WithoutTitle_ShowsDuration()
    {
        string header = ThinkingSummary.CollapsedHeader("   ", "300ms", done: true);
        await Assert.That(header).IsEqualTo("+ Thought: 300ms");
    }

    [Test]
    public async Task Header_Done_WithoutDuration_OmitsSegment()
    {
        string header = ThinkingSummary.CollapsedHeader("weighing options", durationText: null, done: true);
        await Assert.That(header).IsEqualTo("+ Thought: weighing options");
    }

    [Test]
    public async Task Header_Streaming_ShowsNoDuration()
    {
        string header = ThinkingSummary.CollapsedHeader("first thought\nsecond", "9.9s", done: false);
        await Assert.That(header).IsEqualTo("+ Thinking: first thought");
    }

    [Test]
    public async Task Header_Empty_IsBareLabel()
    {
        await Assert.That(ThinkingSummary.CollapsedHeader(null, null, done: true)).IsEqualTo("+ Thought");
        await Assert.That(ThinkingSummary.CollapsedHeader(null, null, done: false)).IsEqualTo("+ Thinking");
    }
}
