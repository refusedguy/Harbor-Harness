using TUnit.Assertions;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     Issue #423 Track 1 — session switch during a running turn: <c>/new</c>
///     while the agent runs is rejected with a marker, and once idle <c>/new</c>
///     opens a fresh session whose timeline is cleared and keeps working.
///
///     PTY mechanics: typing <c>/</c> on the empty composer opens the slash
///     palette overlay (it owns focus, so keys never reach the composer while
///     it is up). Committing the filtered <c>new</c> item with Enter runs it
///     through ReplCommandCatalog's NewSessionCommand, whose busy guard
///     appends the <c>Cannot create session while running</c> marker — the
///     Esc+Enter composer-submit route must NOT be used here, it lands in
///     PromptPipeline's busy branch instead (a different marker). The commit
///     leaves the palette open, so the test closes it with Esc (footer:
///     <c>esc close</c>) before asserting timeline markers. Marker asserts
///     only (streaming cadence is nondeterministic — celldiff §8).
/// </summary>
[NotInParallel("pty")]
public sealed class SessionSwitchMidTurnScenarioTests : CellForgePtyScenarioBase
{
    [Test]
    [Timeout(150_000)]
    public async Task NewSession_WhileRunning_Rejected_ThenFreshSessionWorks()
    {
        // ~1600 chars at the mock's 4-chars/50ms cadence ≈ a ~20 s turn.
        Server.SetResponse("test-model", new string('х', 1600));
        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        SubmitLine("first turn");

        // The turn is running (footer spinner). Chunk-content matching is
        // CI-flaky here (20 s turn, first paint timing varies) — the running
        // state is the in-flight proof this scenario needs.
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("running", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        // /new mid-turn is refused — the in-flight turn keeps its session.
        await CommitSlashAsync("/new").ConfigureAwait(false);
        Session.SendKey("\x1b"); // the commit leaves the palette open; close it
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("Cannot create session while running", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("idle", StringComparison.Ordinal) || x.Contains("○ idle", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(30)).ConfigureAwait(false);

        // Idle /new opens a fresh session and clears the timeline.
        await CommitSlashAsync("/new").ConfigureAwait(false);
        Session.SendKey("\x1b"); // same: close the palette left open by the commit
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("Started fresh session", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => !l.Any(x => x.Contains("ххх", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        Server.SetEchoResponse("test-model");
        SubmitLine("second turn");
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("echo-", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("idle", StringComparison.Ordinal) || x.Contains("○ idle", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        // Fresh timeline: the second answer is there, the first turn's long
        // rune-run is gone (no cross-session bleed; triple-х never occurs in UI chrome).
        string[] settled = NormalizedLines();
        await Assert.That(settled.Any(x => x.Contains("echo-", StringComparison.Ordinal))).IsTrue().Because($"screen:\n{ScreenText}");
        await Assert.That(settled.Any(x => x.Contains("ххх", StringComparison.Ordinal))).IsFalse().Because($"screen:\n{ScreenText}");
    }

    /// <summary>Commit a slash line through the palette overlay: <c>/</c> opens
    /// it, the rest filters it, Enter commits the top match. The palette has no
    /// "[slash]" caption — its stable open marker is the footer hint
    /// <c>enter run · esc close</c> (see CommandPaletteView), and the painted
    /// query row (<c>&gt; new</c>) proves the filter applied, so the commit
    /// deterministically resolves the <c>new</c> item (the only slash command
    /// matching it). Sent in two phases with a render-sync between them: a
    /// single burst could commit before the filter keystrokes are processed.
    /// </summary>
    private async Task CommitSlashAsync(string line)
    {
        Session.SendKey("/");
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("enter run", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        Session.SendKey(line[1..]);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("enter run", StringComparison.Ordinal))
                && l.Any(x => x.Contains("> new", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        Session.SendKey("\r");
    }
}
