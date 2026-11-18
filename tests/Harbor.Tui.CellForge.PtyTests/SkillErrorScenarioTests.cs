using TUnit.Assertions;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     Issue #423 — skill-error path through the PTY. The mock answers with a
///     <c>skill</c> tool call whose <c>name</c> was never written to either
///     skills root, so <c>SkillTool.ExecuteAsync</c> fails deterministically
///     with "Skill … not found" on a read-only lookup: no skill file is opened,
///     no directory is created and no process or network call is made. This is
///     the hermetic property the bash-error (#2420) and notebook-error (#2487)
///     slices established for their own tool seams.
///
///     Unlike the rest of the <c>*-error</c> series, <c>skill</c> carries the
///     <c>Opaque</c> safety profile and the default rule is <c>Allow</c>, so
///     there is NO permission gate to answer. The single-turn budget ends the
///     run right after the failed lookup, so the mock is never re-served and
///     no follow-up completion is requested. The wait's done marker is the
///     error glyph <c>✖</c>, which paints only on the completed error card —
///     never in the pre-submit idle frame — so the wait ends after the failed
///     execution has actually landed. The error body is deliberately not
///     asserted: on the settled timeline it does not repaint behind the card
///     (stale 1-row layout — #1137), so the header glyph plus the echoed skill
///     name are the stable surface. Only tests; product code is untouched.
/// </summary>
public sealed class SkillErrorScenarioTests : CellForgePtyScenarioBase
{
    [Test]
    [Timeout(120_000)]
    public async Task SkillCall_MissingName_StreamsError_AndTurnSettles()
    {
        // A skill name that was never written to either root — unique marker,
        // absent from disk, and absent from the submitted user text below.
        const string missingName = "pty-skillerr-missing-423m";

        WriteHomeConfig("""
            {
              "provider": "mock",
              "model": "mock/test-model",
              "agent": "code",
              "onboarded": true,
              "maxSteps": 1
            }
            """);
        Server.SetToolCallResponse("test-model", "skill", new
        {
            name = missingName,
            scope = "any"
        });

        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        // The submitted text does not carry the skill name: the card's arg
        // summary is what must echo it, and the error glyph is the done marker.
        SubmitLine("load the requested skill");

        string[] errored = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("✖", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        await Assert.That(errored.Length).IsGreaterThan(0)
            .Because($"the failed skill call never painted an error card:\n{ScreenText}");

        // The card completed in the error state and echoed the requested name.
        // Both are read from the raw stream: the name is the arg fragment that
        // survives the card render, the glyph only paints after the failed call
        // (the body itself does not repaint on the settled timeline — #1137).
        string raw = Session.RawText;
        bool glyph = raw.Contains('✖');
        bool echoed = raw.Contains(missingName, StringComparison.Ordinal);
        await Assert.That(glyph && echoed).IsTrue()
            .Because($"error glyph={glyph}, name echo={echoed}:\n{ScreenText}");

        // The tool name survives on the settled timeline next to the card.
        string[] settled = NormalizedLines();
        await Assert.That(settled.Any(x => x.Contains("skill", StringComparison.Ordinal))).IsTrue()
            .Because($"the tool name must survive on the settled timeline:\n{ScreenText}");

        // A failed get writes nothing under either skills root: no SKILL.md
        // for the missing name may appear anywhere in the isolated home.
        string[] created = Directory.Exists(TempHome)
            ? Directory.GetFiles(TempHome, missingName + ".md", SearchOption.AllDirectories)
            : Array.Empty<string>();
        await Assert.That(created.Length).IsEqualTo(0)
            .Because("a failed skill load must not persist a SKILL.md file");

        // One turn, no follow-up completion — the budget stopped the loop.
        await Assert.That(Server.ReceivedRequests.Count).IsEqualTo(1)
            .Because("maxSteps=1 must end the run after the tool request, with no follow-up call");
    }
}
