using Harbor.Abstractions.Providers;
using TUnit.Assertions;

namespace Harbor.Core.Tests;

/// <summary>
///     #2231: the summary's system prompt is the hand-off contract for the
///     takeover agent. These tests hold its section set and the
///     "do not restate instructions" rule in place, and pin the output budget
///     the summary request advertises to the provider.
/// </summary>
public sealed class CompactionSummaryPromptTests : CompactionTestBase
{
    private static readonly string[] ExpectedSections =
    [
        "## Goal",
        "## Constraints & Preferences",
        "## Progress",
        "### Done",
        "### In Progress",
        "### Blocked",
        "## Key Decisions",
        "## Next Steps",
        "## Critical Context",
        "## Files",
        "### Read",
        "### Modified",
    ];

    private static async Task<LlmRequest> CaptureSummaryRequestAsync()
    {
        var client = new CapturingLlmClient("summary text", [TestModel()]);
        var svc = CreateService(client);
        var result = await svc.CompactAsync("session-1", SixMessageHistory(), TestModel());

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(client.Requests.Count).IsEqualTo(1);
        return client.Requests[0];
    }

    [Test]
    public async Task SummaryPrompt_ContainsExactlyTheDocumentedSections()
    {
        LlmRequest request = await CaptureSummaryRequestAsync();

        string[] headers = request.SystemPrompt
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("##", StringComparison.Ordinal))
            .ToArray();

        await Assert.That(headers).IsEquivalentTo(ExpectedSections)
            .Because("the summary's section set is the hand-off contract: dropping or adding one "
                   + "silently changes what the takeover agent reads, and no other test names it");
    }

    [Test]
    public async Task SummaryPrompt_ForbidsRestatingInstructionsAndEnvironment()
    {
        LlmRequest request = await CaptureSummaryRequestAsync();

        await Assert.That(request.SystemPrompt).Contains("Do not restate system instructions")
            .Because("AGENTS.md, tool guidelines and environment details reach the next agent "
                   + "separately; repeating them wastes the larger output budget");

        await Assert.That(request.SystemPrompt).Contains("the next agent receives those separately")
            .Because("the rule must give the reason, not just a bare prohibition");
    }

    [Test]
    public async Task SummaryRequest_Reserves8192OutputTokens()
    {
        LlmRequest request = await CaptureSummaryRequestAsync();

        await Assert.That(request.MaxOutputTokens).IsEqualTo(8192)
            .Because("the summary needs enough output to stay complete without re-stating "
                   + "instructions the next agent already has");
    }
}
