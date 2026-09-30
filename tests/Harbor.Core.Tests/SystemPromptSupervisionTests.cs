using System.Text.Json;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Sessions;
using Harbor.Abstractions.Tools;
using Harbor.Application.Sessions;

namespace Harbor.Core.Tests;

/// <summary>
///     Tests for the peer-supervision prompt recipe (#165): the
///     <c>## Peer Supervision</c> section renders only when the supervision
///     tools are resolved for the turn.
/// </summary>
/// <remarks>
///     <para>
///         <b>#793 — the guard is LIVENESS, not a name list.</b> The section used
///         to decide whether to render by string-matching two tool names written
///         inline, so renaming a tool made the recipe vanish with nothing red.
///         The obvious guard for that is a list of the same two names, and it
///         cannot work: renaming the tool and renaming the fixture together keeps
///         it green, which is a mirror, not a check. So this file asserts the
///         property that survives a rename instead — <b>the recipe never names a
///         tool the turn did not resolve</b> — and reads the names out of the
///         rendered prompt rather than writing them down.
///     </para>
///     <para>
///         That property is also the one with teeth today, which is why it is worth
///         having on its own account: the section rendered when <em>either</em>
///         supervision tool resolved, and it printed <em>both</em> names. With the
///         default <c>code</c> agent that is the live configuration —
///         <c>PermissionRuleset.Default</c> allows <c>session_read</c> and asks for
///         <c>session_steer</c>, <c>ResolveTools</c> keeps only <c>Allow</c>, and
///         the very same resolved list is what <c>TurnRunner</c> hands the API as
///         tool definitions. So the prompt was naming a tool the model could not
///         call, which is the same failure <c>NoToolsGuidance</c> exists to
///         prevent.
///     </para>
/// </remarks>
public class SystemPromptSupervisionTests
{
    /// <summary>
    ///     The section anchor. Named here because consumers anchor on it (the
    ///     builder renders the header even for an empty tool list so a parser can
    ///     rely on it), not because a tool name is being mirrored.
    /// </summary>
    private const string SectionHeader = "## Peer Supervision";

    private static readonly ModelInfo TestModel = new(
        "test-model",
        "test",
        "Test Model",
        200_000,
        4_096,
        false,
        false,
        true,
        Pricing.Unknown,
        "openai");

    private static AgentDefinition Agent() => new(
        AgentName.Create("code"),
        "Code",
        "Default coding agent.",
        "test-model",
        "test",
        PermissionRuleset.Default);

    private static SystemPromptContext Context(IReadOnlyList<ToolDescriptor> tools) => new(
        Agent(),
        TestModel,
        tools,
        Array.Empty<ContextFile>(),
        Array.Empty<SkillDescriptor>(),
        null,
        "~/.cache/harbor-tests");

    private static ToolDescriptor Tool(string name) => new(
        ToolName.Create(name),
        name,
        name + " tool",
        JsonDocument.Parse("{}"),
        ExecutionMode.Parallel,
        null,
        Array.Empty<string>());

    private static ToolDescriptor[] Tools(params string[] names)
    {
        var tools = new ToolDescriptor[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            tools[i] = Tool(names[i]);
        }

        return tools;
    }

    /// <summary>
    ///     Every builtin tool name, read off the declaration table rather than
    ///     written here. The table is the one place a tool's name may be written
    ///     down (#595), and <c>BuiltinToolSafetyDeclarationsTests</c> keeps it equal
    ///     to what actually registers — so this list cannot go stale, and adding a
    ///     tool re-points the guard instead of disarming it.
    /// </summary>
    /// <remarks>
    ///     It includes the two plugin-vocabulary rows (<c>session_broadcast</c>,
    ///     <c>session_inbox</c>) that the builtin host does not register. Extra
    ///     resolved tools are harmless here — the guard is about what the section
    ///     may NAME, not about which of them drive it.
    /// </remarks>
    private static string[] DeclaredBuiltinNames() =>
        [.. BuiltinToolSafetyProfiles.All
            .Select(d => d.ToolName)
            .OrderBy(name => name, StringComparer.Ordinal)];

    [Test]
    public async Task BuildAsync_WithSupervisionTools_IncludesRecipe()
    {
        var builder = new SystemPromptBuilder();
        var ctx = Context(Tools("read", "session_read", "session_steer"));

        string prompt = await builder.BuildAsync(ctx);

        await Assert.That(prompt).Contains("## Peer Supervision");
        await Assert.That(prompt).Contains("session_read");
        await Assert.That(prompt).Contains("session_steer");
        await Assert.That(prompt).Contains("read → verdict");
    }

    [Test]
    public async Task BuildAsync_WithoutSupervisionTools_OmitsRecipe()
    {
        var builder = new SystemPromptBuilder();
        var ctx = Context(Tools("read", "bash"));

        string prompt = await builder.BuildAsync(ctx);

        await Assert.That(prompt.Contains("## Peer Supervision")).IsFalse();
    }

    /// <summary>
    ///     THE INVARIANT (#793). Whatever the section decides to render, every
    ///     tool it names must be one the turn actually resolved. The names are read
    ///     out of the rendered prose — the guard writes none of its own — so a
    ///     rename that reaches the prose and the registry together stays green,
    ///     and a rename that reaches only one of them does not.
    /// </summary>
    /// <remarks>
    ///     The check is a universal property over single-tool removals rather than
    ///     one hand-picked case, which is what lets it stay honest as the tool set
    ///     changes: it says nothing about WHICH tool is a supervision tool, only
    ///     that a name printed in this section has to correspond to a tool the
    ///     model can call. The section may legitimately survive a removal (the other
    ///     half of the recipe is still real) — what it may not do is name the tool
    ///     that is gone.
    /// </remarks>
    [Test]
    public async Task PeerSupervision_NeverNamesAToolTheTurnDidNotResolve()
    {
        var builder = new SystemPromptBuilder();
        string[] declared = DeclaredBuiltinNames();

        // ---- 1. The section exists and names something.
        //
        // Without this the rest of the test is satisfied by a section that prints
        // no tool name at all — a guard that passes because the thing it watches
        // stopped happening. It is the failure mode every rule in this repository
        // has had to guard against separately.
        string full = await builder.BuildAsync(Context(Tools(declared)));
        string[] named = BacktickedWords(PeerSupervisionSection(full));

        await Assert.That(full.Contains(SectionHeader, StringComparison.Ordinal)).IsTrue()
            .Because("with every declared builtin tool resolved the section must render; if it does "
                   + "not, the recipe is dead and every assertion below is vacuously satisfied");

        await Assert.That(named.Length).IsGreaterThan(0)
            .Because("the section exists to tell the model which tools drive peer supervision, so it "
                   + "must name at least one. An empty extraction would make the liveness check pass "
                   + "for the wrong reason — which is exactly how a section stops naming anything and "
                   + "no test notices");

        // ---- 2. Every name it prints is a tool the product declares.
        //
        // The prose cannot invent a tool the way the resolver cannot: a name that
        // matches nothing is the `web_fetch` shape #595 found, and it would make
        // step 3 pass for a name no registry row backs.
        foreach (string name in named)
        {
            await Assert.That(declared.Contains(name, StringComparer.Ordinal)).IsTrue()
                .Because($"the section names `{name}`, which no row in "
                       + "BuiltinToolSafetyProfiles declares. A name that resolves to nothing is a "
                       + "dead reference in prose: nothing can ever make it true");
        }

        // ---- 3. THE INVARIANT: drop one named tool at a time.
        foreach (string missing in named)
        {
            string[] reduced = [.. declared.Where(n => !string.Equals(n, missing, StringComparison.Ordinal))];
            string prompt = await builder.BuildAsync(Context(Tools(reduced)));

            foreach (string name in BacktickedWords(PeerSupervisionSection(prompt)))
            {
                await Assert.That(reduced.Contains(name, StringComparer.Ordinal)).IsTrue()
                    .Because($"with `{missing}` unresolved the section still names `{name}`, which this "
                           + "turn cannot call. TurnRunner hands this same resolved list to the API as "
                           + "the tool definitions, so the prompt is advertising a call the model cannot "
                           + "make — the invented-tool-call failure NoToolsGuidance exists to prevent");
            }
        }
    }

    /// <summary>
    ///     The <c>## Peer Supervision</c> section of a rendered prompt, or an empty
    ///     string when it is absent. Sliced at the NEXT <c>## </c> header so the
    ///     tools listed under <c>## Available Tools</c> — which the builder also
    ///     backticks, one per resolved tool — cannot leak in and satisfy the
    ///     liveness check trivially.
    /// </summary>
    private static string PeerSupervisionSection(string prompt)
    {
        int start = prompt.IndexOf(SectionHeader, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        int next = prompt.IndexOf("\n## ", start + SectionHeader.Length, StringComparison.Ordinal);
        return next < 0 ? prompt[start..] : prompt[start..next];
    }

    /// <summary>
    ///     The distinct backticked words in a prompt section, ordinal-sorted so a
    ///     failure message reads the same on every run. In this prompt a backtick
    ///     marks an identifier the model is being told to use, which is precisely
    ///     what the liveness check is about.
    /// </summary>
    private static string[] BacktickedWords(string section)
    {
        var found = new List<string>();
        int cursor = 0;

        while (cursor < section.Length)
        {
            int open = section.IndexOf('`', cursor);
            if (open < 0)
            {
                break;
            }

            int close = section.IndexOf('`', open + 1);
            if (close < 0)
            {
                break;
            }

            found.Add(section[(open + 1)..close]);
            cursor = close + 1;
        }

        found.Sort(StringComparer.Ordinal);
        return [.. found.Distinct(StringComparer.Ordinal)];
    }
}
