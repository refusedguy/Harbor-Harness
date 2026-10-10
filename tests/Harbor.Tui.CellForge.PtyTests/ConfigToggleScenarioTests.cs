using TUnit.Assertions;

namespace Harbor.Tui.CellForge.PtyTests;

/// <summary>
///     Issue #423 Track 1 — config reload / <c>ui.consoleEx.enabled</c> toggled
///     at runtime: rewriting <c>$HOME/.harbor/config.json</c> mid-session must
///     not crash the PTY app — the next turn still runs and lands, the status
///     bar keeps rendering. This pins survival, not live rebind (no config
///     watcher exists today — live-reload semantics is a separate issue).
/// </summary>
[NotInParallel("pty")]
public sealed class ConfigToggleScenarioTests : CellForgePtyScenarioBase
{
    [Test]
    [Timeout(60_000)]
    public async Task UiConsoleExDisabled_MidSession_AppSurvivesAndKeepsRunning()
    {
        Server.SetResponse("test-model", "toggle-ok-423");
        await StartAppAsync(100, 30).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).ConfigureAwait(false);

        WriteHomeConfig("""
            {
              "provider": "mock",
              "model": "mock/test-model",
              "agent": "code",
              "onboarded": true,
              "ui": {
                "consoleEx": {
                  "enabled": false,
                  "syncUpdates": true
                }
              }
            }
            """);

        SubmitLine("after-toggle");
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("toggle-ok-423", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        _ = await WaitForScreenAsync(
            l => l.Any(x => x.Contains("idle", StringComparison.Ordinal) || x.Contains("○ idle", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        string[] settled = NormalizedLines();
        await Assert.That(settled.Any(x => x.Contains("toggle-ok-423", StringComparison.Ordinal))).IsTrue().Because($"screen:\n{ScreenText}");
        await Assert.That(settled.Any(x => x.Contains("model: mock/test-model", StringComparison.Ordinal))).IsTrue().Because($"screen:\n{ScreenText}");
        await Assert.That(Session.HasExited).IsFalse();
    }
}
