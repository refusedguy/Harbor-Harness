using Harbor.Abstractions.Tools;
using Harbor.Tools.Mcp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Tools.Builtin.Tests;

/// <summary>
///     The builtin tools that own a parameter schema, and the factory that
///     builds one of each.
/// </summary>
/// <remarks>
///     <para>
///         The key is the tool's CLASS name, because that is the key the pin
///         in <c>src/Harbor.Tools.Builtin/Schemas/tool-schemas.sha256.json</c>
///         uses. A tool name would not do for <see cref="McpToolAdapter" />:
///         its name is derived from the MCP server and the tool it wraps, so
///         it is not stable.
///     </para>
///     <para>
///         The list lives here so that the two suites that read it — the one
///         that checks the schema is cached and the one that checks it against
///         the pin — cannot drift apart. A tool added without a factory here
///         loses its coverage silently.
///     </para>
/// </remarks>
internal static class ToolSchemaFixtures
{
    /// <summary>
    ///     A notes root that is never written to — the schema tests only read
    ///     the schema, they never execute the tool.
    /// </summary>
    private const string NotebookRoot = "harbor-schema-test-notes";

    /// <summary>Every builtin tool that owns a parameter schema.</summary>
    internal static (string Key, Func<ITool> Factory)[] ToolFactories { get; } =
    [
        ("AskUserQuestionTool", () => new AskUserQuestionTool(
            new StubUserQuestionService(Harbor.Abstractions.Interaction.UserQuestionOutcome.Answered([])),
            NullLogger<AskUserQuestionTool>.Instance)),
        ("BashTool", () => new BashTool(new SystemProcessProxy(), NullLogger<BashTool>.Instance)),
        ("EditTool", () => new EditTool(NullLogger<EditTool>.Instance)),
        ("GlobTool", () => new GlobTool(NullLogger<GlobTool>.Instance)),
        ("GrepTool", () => new GrepTool(NullLogger<GrepTool>.Instance)),
        // #1732: the runtime catalog. The factories need the registries it
        // reads — the same objects the product hands it — so the schema the
        // test sees is the schema the model sees.
        ("InspectTool", () => new InspectTool(
            new Harbor.Registries.Tools.ToolRegistry(),
            new Harbor.Registries.Agents.AgentRegistry(),
            new Harbor.Registries.Providers.ProviderRegistry(),
            null,
            null,
            null,
            RuntimeApiFacts.Empty,
            NullLogger<InspectTool>.Instance)),
        ("LsTool", () => new LsTool(NullLogger<LsTool>.Instance)),
        ("LspTool", () => new LspTool(NullLogger<LspTool>.Instance)),
        ("McpPromptTool", () => new McpPromptTool(NullLogger<McpPromptTool>.Instance)),
        ("McpResourceTool", () => new McpResourceTool(NullLogger<McpResourceTool>.Instance)),
        ("McpToolAdapter", () => new McpToolAdapter(ScriptedMcpRegistry.Succeed("{}"), "server", "tool")),
        ("McpToolTool", () => new McpToolTool(NullLogger<McpToolTool>.Instance)),
        ("NotebookTool", () => new NotebookTool(NullLogger<NotebookTool>.Instance, NotebookRoot)),
        ("PatchTool", () => new PatchTool(NullLogger<PatchTool>.Instance)),
        ("PluginManagerTool", () => new PluginManagerTool(new FakePluginInventory(), NullLogger<PluginManagerTool>.Instance)),
        ("ReadTool", () => new ReadTool(NullLogger<ReadTool>.Instance)),
        ("RipGrepTool", () => new RipGrepTool(NullLogger<RipGrepTool>.Instance)),
        ("SessionReadTool", () => new SessionReadTool(new DeferredSessionStore(), NullLogger<SessionReadTool>.Instance)),
        ("SessionSteerTool", () => new SessionSteerTool(new DeferredSessionStore(), NullLogger<SessionSteerTool>.Instance)),
        ("SkillTool", () => new SkillTool(NullLogger<SkillTool>.Instance)),
        ("TaskTool", () => new TaskTool(new Harbor.Registries.Agents.AgentRegistry(), NullLogger<TaskTool>.Instance)),
        ("TreeTool", () => new TreeTool(NullLogger<TreeTool>.Instance)),
        ("WebFetchTool", () => new WebFetchTool(NullLogger<WebFetchTool>.Instance)),
        ("WriteTool", () => new WriteTool(NullLogger<WriteTool>.Instance)),
    ];
}
