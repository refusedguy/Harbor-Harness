// PluginBundleDeclarations.cs: the non-code things a plugin ships (#2229).
//
// WHAT THIS IS
// ------------
// A plugin is more than the types it compiles. It can bring a skill a user
// would otherwise install by hand, a hook file that runs at a lifecycle event,
// and a declaration of an MCP server it needs. Those things live in the plugin
// manifest, next to the source, and they are READ as data — only their shape is
// checked here, before any plugin code is compiled or run.
//
// The three declarations are deliberately DATA, not registrations. Nothing in
// this file reaches ToolRegistry, McpRegistry or the skill registry: wiring a
// declaration into a running system is a separate seam, and doing it here would
// make the manifest reader depend on subsystems that are not part of loading a
// plugin from disk. The host owns that decision. What this file guarantees is
// that a plugin can SAY what it brings, and that a broken declaration is
// refused with a reason instead of being mistaken for an empty one.
//
// WHY EACH ENTRY NEEDS A LOCATOR
// ------------------------------
// A skill and a hook are files; an MCP server is a named process. The locator
// (path, or name plus command) is required. A declaration without one cannot be
// installed, and silently skipping it would be exactly the quiet plugin loss
// this change is meant to prevent.

namespace Harbor.Plugins.Abstractions;

/// <summary>
///     A skill a plugin declares in its manifest, relative to the plugin's own
///     directory.
/// </summary>
public sealed class PluginSkillDeclaration
{
    /// <summary>The path to the skill, relative to the plugin source.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>The skill's display name, when the manifest gives one.</summary>
    public string? Name { get; init; }

    /// <summary>What the skill does, when the manifest says.</summary>
    public string? Description { get; init; }
}

/// <summary>
///     A hook file a plugin declares in its manifest.
/// </summary>
public sealed class PluginHookDeclaration
{
    /// <summary>The path to the hook file, relative to the plugin source.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>The lifecycle event the hook attaches to, when it is scoped to one.</summary>
    public string? Event { get; init; }
}

/// <summary>
///     An MCP server a plugin declares in its manifest. The declaration is data
///     for the host to act on; it does not start a process by itself.
/// </summary>
public sealed class PluginMcpServerDeclaration
{
    /// <summary>The server's unique name, as the manifest gives it.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The executable to launch for the server.</summary>
    public string Command { get; init; } = string.Empty;

    /// <summary>Arguments passed to the executable, in order.</summary>
    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();

    /// <summary>Environment variables passed to the server process.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
