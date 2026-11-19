// #2680 F174. Behaviour tests for the agent title glyph map: each built-in
// agent gets a stable mark, unknown names fall back, and the glyph reaches the
// composed OSC title. Assertions pin what a caller receives, not how the map
// is stored.

using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

public class AgentTitleGlyphTests
{
    [Test]
    [Arguments("code", "🧠")]
    [Arguments("plan", "📐")]
    [Arguments("ask", "💬")]
    [Arguments("explore", "🔍")]
    [Arguments("architect", "🏗")]
    [Arguments("debug", "🐛")]
    [Arguments("orchestrator", "🎛")]
    public async Task For_KnownAgent_ReturnsItsGlyph(string name, string expected)
    {
        await Assert.That(AgentTitleGlyph.For(name)).IsEqualTo(expected);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("unknown")]
    [Arguments("Codex")]
    public async Task For_MissingOrUnknownAgent_ReturnsFallback(string? name)
    {
        await Assert.That(AgentTitleGlyph.For(name)).IsEqualTo(AgentTitleGlyph.Fallback);
    }

    [Test]
    public async Task For_IgnoresCaseAndSurroundingWhitespace()
    {
        await Assert.That(AgentTitleGlyph.For("  PLAN ")).IsEqualTo("📐");
        await Assert.That(AgentTitleGlyph.For("dEbUg")).IsEqualTo("🐛");
    }

    [Test]
    public async Task Build_PutsAgentGlyphIntoOscTitle()
    {
        var sequence = AgentTitleGlyph.Build("my session", "plan", TerminalActivityState.Busy);

        await Assert.That(sequence).IsEqualTo("\u001b]0;● busy 📐 my session\u0007");
    }

    [Test]
    public async Task Build_UnknownAgentUsesFallback()
    {
        var sequence = AgentTitleGlyph.Build("my session", "mystery", TerminalActivityState.Done);

        await Assert.That(sequence).IsEqualTo("\u001b]0;✓ done 🤖 my session\u0007");
    }

    [Test]
    public async Task Build_EmptySubjectFallsBackToDefaultLabel()
    {
        var sequence = AgentTitleGlyph.Build(null, "code", TerminalActivityState.Idle);

        await Assert.That(sequence).IsEqualTo("\u001b]0;○ idle 🧠 Harbor\u0007");
    }
}
