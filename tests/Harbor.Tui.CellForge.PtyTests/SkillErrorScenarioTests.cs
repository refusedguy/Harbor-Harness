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
///     execution has actually landed. Because <c>skill</c> has no approval
///     gate, there is no gate row to echo the tool args, and the card body
///     does not repaint on the settled timeline (#1137): the stable surface is
///     the completed header (glyph, tool name, <c>[err]</c> marker) plus the
///     read-only disk check. Only tests; product code is untouched.
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

        // Let the single-turn run settle before reading the settled timeline.
        // The error glyph above is the done marker; idle proves the loop ended
        // on the maxSteps budget with no follow-up completion requested.
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("idle", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);

        // With no approval gate there is no gate row to echo the tool args, and
        // the card body does not repaint on the settled timeline (#1137), so
        // the stable surface is the completed header: error glyph, tool name
        // and the [err] completion marker.
        string raw = Session.RawText;
        bool glyph = raw.Contains('✖');
        bool toolNamed = raw.Contains("skill", StringComparison.Ordinal);
        bool errMark = raw.Contains("[err]", StringComparison.Ordinal);
        await Assert.That(glyph && toolNamed && errMark).IsTrue()
            .Because($"glyph={glyph}, tool name={toolNamed}, [err]={errMark}:\n{ScreenText}");

        string[] settled = NormalizedLines();
        await Assert.That(settled.Any(x => x.Contains("skill", StringComparison.Ordinal))).IsTrue()
            .Because($"the tool name must survive on the settled timeline:\n{ScreenText}");

        // A failed load writes nothing under either skills root: no file or
        // directory named after the missing skill may appear in the home.
        string[] created = Directory.Exists(TempHome)
            ? Directory.GetFiles(TempHome, missingName + ".md", SearchOption.AllDirectories)
                .Concat(Directory.GetDirectories(TempHome, missingName, SearchOption.AllDirectories))
                .ToArray()
            : Array.Empty<string>();
        await Assert.That(created.Length).IsEqualTo(0)
            .Because("a failed skill load must not persist a SKILL.md file or a skill directory");

        // One turn, no follow-up completion — the budget stopped the loop.
        await Assert.That(Server.ReceivedRequests.Count).IsEqualTo(1)
            .Because("maxSteps=1 must end the run after the tool request, with no follow-up call");
    }
}
