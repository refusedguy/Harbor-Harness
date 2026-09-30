using System.Runtime.InteropServices;
using System.Text;
using Harbor.Abstractions.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Application.Sessions;
/// <summary>
///     Default system prompt builder. Implements Builder pattern (GOF).
///     Assembles: identity + tool policy + constraints + env + agent + tools + MCP + skills + context files.
///     Uses a pooled <see cref="StringBuilder" /> to avoid per-call allocation.
/// </summary>
public sealed class SystemPromptBuilder : ISystemPromptBuilder
{

    private const string DefaultBasePrompt = """
                                             You are Harbor, a coding agent. Solve tasks with tools. Be precise and minimal.
                                             Rules: read before edit; small targeted diffs; verify after change; prefer dedicated tools over bash.
                                             """;

    private const string ToolUsePolicy = """
                                         ## Tool Use
                                         - Call tools only via the provided function-calling interface. Args must match each tool's schema.
                                         - Never invent tool names, arguments, paths, or results.
                                         - Parallelize independent reads (read/glob/grep). Keep writes and bash sequential.
                                         - After edits: re-read or run a focused check before claiming success.
                                         - Prefer read/glob/grep/edit over bash. Use bash for build, test, git, package managers.
                                         """;

    private const string Constraints = """
                                       ## Constraints
                                       - Stay inside the working directory unless the user asks otherwise.
                                       - Do not exfiltrate secrets (.env, keys, tokens) into chat or tool arguments.
                                       - No destructive ops without explicit user intent (rm -rf, git push --force, drop db, format).
                                       """;

    /// <summary>
    ///     How many of a tool's prompt guidelines reach the prompt. The cap is a
    ///     budget decision, not a formatting detail: raising it grows every turn's
    ///     system prompt by the length of the extra guidelines, for every tool.
    /// </summary>
    /// <remarks>
    ///     #577: this was the bare literal <c>3</c>, inline in the assembly loop.
    ///     Nothing named it, so nothing could hold it — <c>PromptSectionPolicyRule</c>
    ///     now requires a bound that decides what the model reads to be declared.
    /// </remarks>
    public const int MaxGuidelinesPerTool = 3;

    /// <summary>
    ///     Guidelines longer than this are dropped. A guideline is a usage hint,
    ///     not a specification; an over-long one is a paragraph the prompt pays
    ///     for on every turn and the model rarely follows verbatim.
    /// </summary>
    /// <remarks>
    ///     #577: this was the bare literal <c>160</c>, inline in the same loop.
    /// </remarks>
    public const int MaxGuidelineLength = 160;

    /// <summary>
    ///     What the model is told when the turn resolved no tools at all. Without
    ///     it the section header renders over an empty list, and a model told
    ///     "## Available Tools" and nothing else is left to guess that it has
    ///     none — the failure mode is invented tool calls.
    /// </summary>
    /// <remarks>
    ///     #577: this text was asserted by no test anywhere in the repository —
    ///     the empty-tools test checked only that the HEADER renders, so this
    ///     sentence could be deleted, reworded, or replaced and every test would
    ///     still pass, while the guidance the model actually reads moved.
    /// </remarks>
    public const string NoToolsGuidance = "No tools available this turn. Answer from knowledge only.";

    /// <summary>
    ///     The peer-supervision section anchor. Named because consumers anchor on
    ///     it — the same reason <c>## Available Tools</c> renders over an empty
    ///     list — not because a tool name is being written down.
    /// </summary>
    private const string PeerSupervisionHeader = "## Peer Supervision";

    /// <summary>
    ///     Opens the recipe's tool list. #793: everything after this is composed
    ///     from the roles the tools DECLARE, so this file names no tool at all.
    /// </summary>
    private const string PeerSupervisionLead = "Peer sessions run in parallel and may need help: ";

    /// <summary>What the observing leg does, for whichever tool declared it.</summary>
    private const string PeerSupervisionObserveText =
        "shows a neighbor's status, outcome, and recent transcript";

    /// <summary>What the directing leg does, for whichever tool declared it.</summary>
    private const string PeerSupervisionDirectText =
        "delivers a message, a redirect, or a restart directive (asks for approval first)";

    /// <summary>
    ///     The protocol half of the recipe, printed for every leg combination. It
    ///     names no tool, so it cannot describe one the turn does not have.
    /// </summary>
    private const string PeerSupervisionProtocol = """
                                                   Recipe: read → verdict (ok / stuck / failed) → steer only when needed.
                                                   Rules: never steer yourself or your own supervisor (the session that steered you);
                                                   one level only. Reads are snapshots — re-read a working session before acting.
                                                   """;
    private readonly ILogger<SystemPromptBuilder> _logger;

    public SystemPromptBuilder() : this(NullLogger<SystemPromptBuilder>.Instance) { }

    public SystemPromptBuilder(ILogger<SystemPromptBuilder> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<string> BuildAsync(SystemPromptContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        using var sb = StringBuilderPool.Rent(4096);
        var builder = sb.Builder;

        // 1. Identity
        builder.AppendLine(DefaultBasePrompt);
        builder.AppendLine();

        // 2. Tool policy + constraints (high priority — before long context)
        builder.AppendLine(ToolUsePolicy);
        builder.AppendLine(Constraints);
        builder.AppendLine();

        // 3. Environment (compact)
        builder.AppendLine("## Environment");
        builder.Append("- Working directory: `").Append(context.WorkingDirectory).AppendLine("`");
        builder.Append("- Platform: ").Append(GetOsShort()).AppendLine();
        builder.Append("- Today: ").Append(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd")).AppendLine();
        builder.Append("- Model: ").Append(context.Model.ProviderId).Append('/').Append(context.Model.Id).AppendLine();
        builder.AppendLine();

        // 4. Agent-specific instructions
        if (!string.IsNullOrEmpty(context.Agent.SystemPromptAppend))
        {
            builder.AppendLine("## Additional Instructions");
            builder.AppendLine(context.Agent.SystemPromptAppend);
            builder.AppendLine();
        }

        // 5. Available Tools (descriptors already permission-filtered; schema lives in the API tool defs).
        //    Header is "## Available Tools" per specs/04-tools.md §3 and the
        //    ISystemPromptBuilder contract ("a Markdown document with sections for
        //    environment, agent instructions, available tools, ..."). The header is
        //    rendered even when the tool list is empty so consumers can rely on the
        //    anchor for parsing/scroll-target purposes.
        if (context.Tools.Count == 0)
        {
            builder.AppendLine("## Available Tools");
            builder.AppendLine(NoToolsGuidance);
            builder.AppendLine();
        }
        else
        {
            builder.AppendLine("## Available Tools");
            foreach (var tool in context.Tools.OrderBy(t => t.Name.Value, StringComparer.Ordinal))
            {
                builder.Append("- `").Append(tool.Name.Value).Append("`: ")
                    .AppendLine(tool.PromptSnippet ?? tool.Description);

                if (tool.PromptGuidelines.Count > 0)
                {
                    int n = 0;
                    foreach (string g in tool.PromptGuidelines)
                    {
                        if (n >= MaxGuidelinesPerTool) break;
                        if (string.IsNullOrWhiteSpace(g) || g.Length > MaxGuidelineLength) continue;
                        builder.Append("  - ").AppendLine(g);
                        n++;
                    }
                }
            }
            builder.AppendLine();
        }

        // 5b. Peer-supervision recipe (#165, #793): only when at least one
        // supervision tool resolved for this turn, and it names only the ones
        // that did. The cache key already covers tool names, so cached prompts
        // stay consistent.
        AppendPeerSupervision(builder, context.Tools);

        // 6. MCP
        if (!string.IsNullOrEmpty(context.McpInstructions))
        {
            builder.AppendLine("## MCP Servers");
            builder.AppendLine(context.McpInstructions);
            builder.AppendLine();
        }

        // 7. Available Skills. Per specs/04-tools.md §5 and specs/13-questions-and-answers
        //    the skills block is wrapped in <available_skills> XML so the model can
        //    reliably extract skill metadata (name/description/path) without parsing
        //    prose, and the UI can render a stable tree. The wrapper is a few extra
        //    bytes per prompt — negligible vs. the per-skill description text.
        if (context.Skills.Count > 0)
        {
            builder.AppendLine("## Available Skills");
            builder.AppendLine("The following skills provide specialized instructions:");
            builder.AppendLine();
            builder.AppendLine("<available_skills>");
            foreach (var skill in context.Skills.OrderBy(s => s.Name, StringComparer.Ordinal))
            {
                builder.Append("  <skill>").AppendLine();
                builder.Append("    <name>").Append(skill.Name).AppendLine("</name>");
                builder.Append("    <description>").Append(skill.Description).AppendLine("</description>");
                builder.Append("    <location>").Append(skill.FilePath).AppendLine("</location>");
                builder.AppendLine("  </skill>");
            }
            builder.AppendLine("</available_skills>");
            builder.AppendLine();
            builder.AppendLine("Use the `read` tool to read a skill file when the task matches its description.");
            builder.AppendLine();
        }

        // 8. Project context (last — long, lower priority for attention)
        if (context.ContextFiles.Count > 0)
        {
            builder.AppendLine("## Project Context");
            builder.AppendLine();
            builder.AppendLine("<project_context>");
            foreach (var file in context.ContextFiles)
            {
                builder.Append("<file path=\"").Append(file.Path).AppendLine("\">");
                builder.AppendLine(file.Content);
                builder.AppendLine("</file>");
            }
            builder.AppendLine("</project_context>");
            builder.AppendLine();
        }

        _logger.LogDebug(
            "System prompt built: {Length} chars, {ToolCount} tools, {SkillCount} skills, {FileCount} files",
            builder.Length,
            context.Tools.Count,
            context.Skills.Count,
            context.ContextFiles.Count);

        return Task.FromResult(builder.ToString());
    }

    /// <summary>
    ///     Appends the <c>## Peer Supervision</c> recipe (#165), naming only the
    ///     supervision tools this turn actually resolved.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>#793 — the roles are READ, the names are not written here.</b>
    ///         This used to string-match two tool names inline, which made a
    ///         rename silently drop the recipe: nothing in the repository compared
    ///         that literal against the tools that register. The roles come from
    ///         <see cref="BuiltinToolSafetyProfiles" />, the table
    ///         <c>BuiltinToolSafetyDeclarationsTests</c> keeps equal to what
    ///         actually registers, so a rename updates one row and the recipe
    ///         follows.
    ///     </para>
    ///     <para>
    ///         <see cref="Harbor.Application" /> cannot read
    ///         <c>ITool.SafetyProfile</c> — it does not reference
    ///         <c>Harbor.Tools.Builtin</c> — and neither axis it CAN read carries
    ///         this fact: the two tools are <c>Read</c> and <c>Write</c>
    ///         respectively, so no <see cref="ToolCategory" /> selects them, and
    ///         both are <c>Opaque</c>, so the safety profile cannot tell them
    ///         apart. Hence a declared role, not a derivation from an existing one.
    ///     </para>
    ///     <para>
    ///         <b>The bug the rename was hiding.</b> The predicate was an OR and
    ///         the text named both tools, so the recipe rendered while describing a
    ///         tool the turn could not call. That is the default configuration,
    ///         not an edge case: <c>PermissionRuleset.Default</c> allows
    ///         <c>session_read</c> and ASKS for <c>session_steer</c>,
    ///         <c>ResolveTools</c> keeps only <c>Allow</c>, and
    ///         <c>TurnRunner</c> hands that same resolved list to the API as the
    ///         tool definitions. Gating on both legs instead would have been the
    ///         smaller change and the wrong one: the observing half is real and
    ///         reachable on its own, so it keeps its paragraph.
    ///     </para>
    /// </remarks>
    private static void AppendPeerSupervision(StringBuilder builder, IReadOnlyList<ToolDescriptor> tools)
    {
        string? observer = null;
        string? director = null;

        foreach (ToolSafetyDeclaration declaration in BuiltinToolSafetyProfiles.All)
        {
            if (declaration.PeerSupervision == PeerSupervisionRole.None)
            {
                continue;
            }

            if (!IsResolved(tools, declaration.ToolName))
            {
                continue;
            }

            // First declaration of a leg wins. Two tools claiming the same leg
            // would make the recipe's sentence ambiguous, and the second tool's
            // name would be silently dropped — a claim nothing could check.
            if (declaration.PeerSupervision == PeerSupervisionRole.Observe)
            {
                observer ??= declaration.ToolName;
            }
            else
            {
                director ??= declaration.ToolName;
            }
        }

        if (observer is null && director is null)
        {
            return;
        }

        builder.AppendLine(PeerSupervisionHeader);
        builder.Append(PeerSupervisionLead);

        bool first = true;
        if (observer is not null)
        {
            AppendLeg(builder, ref first, observer, PeerSupervisionObserveText);
        }

        if (director is not null)
        {
            AppendLeg(builder, ref first, director, PeerSupervisionDirectText);
        }

        builder.AppendLine('.');
        builder.AppendLine(PeerSupervisionProtocol);
        builder.AppendLine();
    }

    /// <summary>
    ///     Appends one leg's clause, separator first when another clause is
    ///     already on the line. The tool name is the declaration's, so a rename
    ///     needs no edit here.
    /// </summary>
    private static void AppendLeg(StringBuilder builder, ref bool first, string toolName, string text)
    {
        if (!first)
        {
            builder.Append("; ");
        }

        builder.Append('`').Append(toolName).Append("` ").Append(text);
        first = false;
    }

    /// <summary>
    ///     Whether <paramref name="toolName" /> is among the turn's resolved
    ///     tools. Ordinal-ignore-case because that is how every other lookup
    ///     against the declaration table compares, so a name that differs only in
    ///     case is the same tool rather than a silent miss.
    /// </summary>
    private static bool IsResolved(IReadOnlyList<ToolDescriptor> tools, string toolName)
    {
        for (int i = 0; i < tools.Count; i++)
        {
            if (string.Equals(tools[i].Name.Value, toolName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string GetOsShort()
    {
        if (OperatingSystem.IsWindows()) return "windows";
        if (OperatingSystem.IsMacOS()) return "macos";
        if (OperatingSystem.IsLinux()) return "linux";
        return RuntimeInformation.OSDescription;
    }
}
