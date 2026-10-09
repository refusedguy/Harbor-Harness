// AcceptedStopCensusRule — ratchet for the number 401 remainder, the B2 core.
//
// Number 997 closed the start of NEW calls. Both ToolDispatcher loops consult
// the run token and the seven terminal ToolExecutionEndEvent publishes ride
// TerminalEventToken. In-flight calls were left alone deliberately. This rule
// pins that merged boundary and the remainder. After the Accepted boundary no
// new action may start, and an in-flight call that outlives the boundary must
// read Abandoned, never success.
//
// The full 53-site audit lives in census401.py at the repo root, shipped in
// number 997. This rule does not re-list it. It pins the two load-bearing
// properties of that list plus the missing one, namely the dispatch gates,
// the terminal token, and the Abandoned visibility.

namespace Harbor.Architecture.Tests;

using TUnit.Assertions;

/// <summary>
///     Pins the #997 Accepted boundary and the #401 in-flight remainder.
/// </summary>
public sealed class AcceptedStopCensusRule
{
    private static string DispatcherSource()
    {
        string? root = RepoPaths.RepoRoot;
        if (root is null)
        {
            return string.Empty;
        }

        string path = Path.Combine(root, "src", "Harbor.Application", "Agents", "ToolDispatcher.cs");
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    private static int Count(string source, string literal)
    {
        int hits = 0;
        int at = 0;
        while ((at = source.IndexOf(literal, at, StringComparison.Ordinal)) >= 0)
        {
            hits++;
            at += literal.Length;
        }

        return hits;
    }

    /// <summary>
    ///     #997 fix #1, pinned: both dispatch loops (sequential + parallel) test
    ///     the run token before starting each call.
    /// </summary>
    [Test]
    public async Task DispatchLoops_StillConsultRunTokenBeforeStart()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the rule reads ToolDispatcher.cs from the checkout");

        string source = DispatcherSource();
        await Assert.That(source).IsNotEmpty()
            .Because("ToolDispatcher.cs must be readable from the checkout");

        await Assert.That(Count(source, "ct.IsCancellationRequested")).IsGreaterThanOrEqualTo(2)
            .Because("the sequential and the parallel dispatch loops each gate on "
                   + "ct.IsCancellationRequested before starting a call (#997 fix #1); "
                   + "losing either re-opens post-Stop starts. Full site list: census401.py");
    }

    /// <summary>
    ///     #997 fix #2, pinned: terminal ToolExecutionEndEvent publishes ride
    ///     TerminalEventToken, never the fired run token.
    /// </summary>
    [Test]
    public async Task TerminalToolEndEvents_StillRideTerminalToken()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the rule reads ToolDispatcher.cs from the checkout");

        string[] lines = DispatcherSource().Split('\n');
        var terminalSites = new List<int>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("new ToolExecutionEndEvent(", StringComparison.Ordinal))
            {
                terminalSites.Add(i);
            }
        }

        await Assert.That(terminalSites.Count).IsGreaterThanOrEqualTo(7)
            .Because("the census (#997) found seven terminal ToolExecutionEndEvent "
                   + "publishes; fewer means the dispatcher's terminal surface moved "
                   + "and this rule must be re-pointed, not silently weakened");

        var onRunToken = new List<int>();
        for (int k = 0; k < terminalSites.Count; k++)
        {
            string window = string.Join("\n", lines.Skip(terminalSites[k]).Take(3));
            if (!window.Contains("TerminalEventToken", StringComparison.Ordinal))
            {
                onRunToken.Add(terminalSites[k] + 1);
            }
        }

        await Assert.That(onRunToken).IsEmpty()
            .Because("a terminal record published with the fired run token is dropped "
                   + "or strikes the subscriber (InMemoryEventBus.DispatchToOneAsync) — "
                   + "the one event that must not depend on the cancellation it reports "
                   + "(#997 fix #2). Offending 1-based lines: " + string.Join(", ", onRunToken));
    }

    /// <summary>
    ///     #401 remainder, pinned: the in-flight side of the Accepted boundary
    ///     is named in the dispatcher. A post-Stop completion reads Abandoned.
    /// </summary>
    [Test]
    public async Task InFlightRemainder_IsNamedAbandoned()
    {
        await Assert.That(RepoPaths.RepoRoot).IsNotNull()
            .Because("the rule reads ToolDispatcher.cs from the checkout");

        string source = DispatcherSource();

        await Assert.That(source).Contains("Abandoned")
            .Because("after the Accepted boundary an in-flight call that outlives "
                   + "the stop must be marked Abandoned, never reported as success "
                   + "and never dropped silently (#401 B2 core)");
    }
}
