namespace Harbor.Abstractions.Models.Identifiers;

/// <summary>
///     The stable names of the tools Harbor ships, as typed <see cref="ToolName" />
///     values — the one place a builtin tool's name is written down (issue #1564).
/// </summary>
/// <remarks>
///     <para>
///         The names used to be written out again at every site that needed one:
///         a permission rule here, a safety declaration there, a comparison in a
///         renderer. A count over <c>src/</c> found <c>"bash"</c> seventy times and
///         two hundred-plus tool-name literals in total, spread over fifty files.
///         A name written out twice is two facts to keep in step, and the failure
///         mode is silent: a rule that names <c>"basn"</c> matches nothing and
///         reports nothing, because a permission rule that matches no rule falls
///         through to the default action and a rule that matches nothing looks
///         exactly like a rule that was never meant to fire.
///     </para>
///     <para>
///         This class is that single point. A site that needs the bash tool's name
///         writes <see cref="Bash" />, so a rename is one edit and a typo is a
///         compile error instead of a permission that quietly never applies.
///     </para>
///     <para>
///         The values are the tool names as they have always been: the same
///         strings, in the same spelling, so a rule written against this class
///         matches exactly what it matched before.
///     </para>
///     <para>
///         <b>Where a literal is still correct.</b> Two places cannot read a
///         <see cref="ToolName" /> and keep their literal, and both are guarded
///         rather than left to drift:
///     </para>
///     <list type="bullet">
///         <item>
///             <description>
///                 A tool's own <c>[ToolInfo]</c> attribute, whose argument the C#
///                 language requires to be a compile-time constant. It is also the
///                 tool's declaration of its own name, and
///                 <c>BuiltinToolSafetyDeclarationsTests</c> cross-checks it against
///                 <see cref="Harbor.Abstractions.Permissions.BuiltinToolSafetyProfiles" />
///                 — which reads this class — so the two cannot disagree.
///             </description>
///         </item>
///         <item>
///             <description>
///                 A <c>case</c> label, which the C# language also requires to be a
///                 compile-time constant. The architecture rule
///                 <c>ToolNameListRule</c> reads every quoted tool name in a
///                 product table and fails when one names a tool no tool
///                 registers, so a stale label there is a red test rather than a
///                 silently unmatched branch.
///             </description>
///         </item>
///     </list>
/// </remarks>
public static class WellKnownTools
{
    /// <summary>The <c>bash</c> tool — runs a shell command.</summary>
    public static readonly ToolName Bash = ToolName.Create("bash");

    /// <summary>The <c>edit</c> tool — replaces a string in a file.</summary>
    public static readonly ToolName Edit = ToolName.Create("edit");

    /// <summary>The <c>glob</c> tool — matches paths by glob pattern.</summary>
    public static readonly ToolName Glob = ToolName.Create("glob");

    /// <summary>The <c>grep</c> tool — searches file contents by pattern.</summary>
    public static readonly ToolName Grep = ToolName.Create("grep");

    /// <summary>The <c>ls</c> tool — lists a directory.</summary>
    public static readonly ToolName Ls = ToolName.Create("ls");

    /// <summary>The <c>lsp</c> tool — language-server queries.</summary>
    public static readonly ToolName Lsp = ToolName.Create("lsp");

    /// <summary>The <c>mcp</c> tool — calls a tool on an MCP server.</summary>
    public static readonly ToolName Mcp = ToolName.Create("mcp");

    /// <summary>The <c>mcp_prompt</c> tool — fetches an MCP prompt.</summary>
    public static readonly ToolName McpPrompt = ToolName.Create("mcp_prompt");

    /// <summary>The <c>notebook</c> tool — edits a notebook.</summary>
    public static readonly ToolName Notebook = ToolName.Create("notebook");

    /// <summary>The <c>patch</c> tool — applies a unified diff.</summary>
    public static readonly ToolName Patch = ToolName.Create("patch");

    /// <summary>The <c>read</c> tool — reads a file.</summary>
    public static readonly ToolName Read = ToolName.Create("read");

    /// <summary>The <c>read_mcp_resource</c> tool — reads an MCP resource.</summary>
    public static readonly ToolName ReadMcpResource = ToolName.Create("read_mcp_resource");

    /// <summary>The <c>ripgrep</c> tool — searches file contents with ripgrep.</summary>
    public static readonly ToolName RipGrep = ToolName.Create("ripgrep");

    /// <summary>The <c>session_read</c> tool — reads another session.</summary>
    public static readonly ToolName SessionRead = ToolName.Create("session_read");

    /// <summary>The <c>session_steer</c> tool — steers another session.</summary>
    public static readonly ToolName SessionSteer = ToolName.Create("session_steer");

    /// <summary>The <c>skill</c> tool — loads a skill.</summary>
    public static readonly ToolName Skill = ToolName.Create("skill");

    /// <summary>The <c>task</c> tool — delegates work to a sub-agent.</summary>
    public static readonly ToolName Task = ToolName.Create("task");

    /// <summary>The <c>tree</c> tool — renders a directory tree.</summary>
    public static readonly ToolName Tree = ToolName.Create("tree");

    /// <summary>The <c>webfetch</c> tool — fetches a URL.</summary>
    public static readonly ToolName WebFetch = ToolName.Create("webfetch");

    /// <summary>The <c>write</c> tool — writes a file.</summary>
    public static readonly ToolName Write = ToolName.Create("write");

    /// <summary>
    ///     The <c>session_broadcast</c> tool — a PLUGIN tool, declared here so a
    ///     plugin that loads later still finds its row in the safety table.
    /// </summary>
    public static readonly ToolName SessionBroadcast = ToolName.Create("session_broadcast");

    /// <summary>
    ///     The <c>session_inbox</c> tool — a PLUGIN tool, declared here so a
    ///     plugin that loads later still finds its row in the safety table.
    /// </summary>
    public static readonly ToolName SessionInbox = ToolName.Create("session_inbox");
}
