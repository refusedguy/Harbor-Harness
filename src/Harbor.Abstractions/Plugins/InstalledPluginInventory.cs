namespace Harbor.Abstractions.Plugins;

/// <summary>
///     Which scope an installed plugin lives in.
/// </summary>
/// <remarks>
///     <para>
///         The two scopes are the ones the loader reads
///         (<c>~/.harbor/plugins</c> and <c>&lt;cwd&gt;/.harbor/plugins</c>), and
///         the ones the CLI's <c>harbor plugin</c> verbs and the
///         <c>/plugins</c> panel already act on. A plugin in the project scope
///         shadows nothing and is shadowed by nothing: both scopes are read,
///         and the file name is what identifies a plugin inside one of them.
///     </para>
/// </remarks>
public enum PluginScope
{
    /// <summary>The user's own scope: <c>~/.harbor/plugins</c>.</summary>
    Global,

    /// <summary>The project's scope: <c>&lt;cwd&gt;/.harbor/plugins</c>.</summary>
    Project
}

/// <summary>
///     One installed plugin, as the inventory sees it.
/// </summary>
/// <remarks>
///     <para>
///         A plugin in Harbor is ONE <c>.cs</c> file, and the facts about it
///         come from two places: the FILE (which scope, which path, whether it
///         is enabled) and the MANIFEST it ships next to itself (which axes it
///         declares, which Harbor it needs). Both are reported here, because an
///         agent deciding whether to install or remove a plugin needs both and
///         a file listing alone answers neither.
///     </para>
///     <para>
///         The declared fields are empty when the plugin ships no manifest:
///         a plugin without a manifest is a plugin the host knows nothing
///         extra about, and the inventory says so rather than inventing a
///         declaration for it.
///     </para>
/// </remarks>
/// <param name="Name">
///     The plugin's file name inside its scope, without the
///     <c>.disabled</c> suffix — <c>TodoWrite.cs</c>, never
///     <c>TodoWrite.cs.disabled</c>.
/// </param>
/// <param name="Scope">The scope the plugin is installed in.</param>
/// <param name="FullPath">The absolute path of the plugin's source file.</param>
/// <param name="Enabled">
///     False for a <c>.cs.disabled</c> file: the loader's glob skips those, so
///     a disabled plugin is installed but does not load.
/// </param>
/// <param name="DeclaredVersion">
///     The version the plugin's manifest declares, or null when it declares
///     none.
/// </param>
/// <param name="DeclaredAxes">
///     The extension axes the plugin's manifest declares, named as
///     <c>PluginAxisRegistry</c> names them. Empty when the manifest declares
///     none.
/// </param>
/// <param name="RequiredHarborVersion">
///     The minimum Harbor the plugin's manifest declares, or null when it
///     declares none.
/// </param>
/// <param name="Compatible">
///     Whether the plugin may run against the running Harbor. True when the
///     plugin declares no floor — there is nothing to enforce.
/// </param>
public sealed record InstalledPluginEntry(
    string Name,
    PluginScope Scope,
    string FullPath,
    bool Enabled,
    string? DeclaredVersion,
    IReadOnlyList<string> DeclaredAxes,
    string? RequiredHarborVersion,
    bool Compatible)
{
    /// <summary>
    ///     Why the plugin's manifest could not be read, or null when it was
    ///     read (or when the plugin ships none).
    /// </summary>
    /// <remarks>
    ///     A broken manifest is a REFUSAL of the plugin, not a fallback to the
    ///     no-manifest behaviour: the plugin shipped a declaration and the host
    ///     failed to apply it. The inventory reports the reason so an agent
    ///     does not read "no declaration" where the truth is "a declaration
    ///     this Harbor cannot understand".
    /// </remarks>
    public string? ManifestError { get; init; }
}

/// <summary>
///     What a caller asks the plugin inventory to change.
/// </summary>
public enum PluginInventoryAction
{
    /// <summary>Copy a reviewed plugin source into a scope.</summary>
    Install,

    /// <summary>Remove an installed plugin from a scope.</summary>
    Uninstall,

    /// <summary>
    ///     Make an installed plugin loadable again — the loader's glob skips
    ///     <c>.cs.disabled</c> files, so enabling is the rename that drops the
    ///     suffix.
    /// </summary>
    Enable,

    /// <summary>
    ///     Stop an installed plugin from loading — the rename that adds the
    ///     <c>.disabled</c> suffix.
    /// </summary>
    Disable
}

/// <summary>
///     One requested change to the plugin inventory.
/// </summary>
/// <remarks>
///     <para>
///         A change names the plugin by FILE NAME inside a scope
///         (<c>TodoWrite.cs</c>), never by an absolute path: the inventory
///         resolves the name against the scope roots, so a crafted name can
///         never name a file outside them.
///     </para>
/// </remarks>
/// <param name="Action">What to change.</param>
/// <param name="Name">
///     The plugin's file name. For
///     <see cref="PluginInventoryAction.Install" /> it is the file name the
///     source is installed under — the source's own file name, because a
///     plugin in Harbor is one file and its name is that file's name.
/// </param>
/// <param name="SourcePath">
///     The reviewed plugin source to install — install only. Null for every
///     other action.
/// </param>
/// <param name="Scope">The scope to install into — install only.</param>
public sealed record PluginInventoryChange(
    PluginInventoryAction Action,
    string Name,
    string? SourcePath = null,
    PluginScope Scope = PluginScope.Global);

/// <summary>
///     What a change to the plugin inventory actually did.
/// </summary>
/// <param name="Action">The action that was applied.</param>
/// <param name="Name">The plugin's file name.</param>
/// <param name="FullPath">The absolute path the action landed on.</param>
/// <param name="Summary">A one-line description of the change, for the model.</param>
public sealed record PluginInventoryChangeResult(
    PluginInventoryAction Action,
    string Name,
    string FullPath,
    string Summary);

/// <summary>
///     The plugin inventory: what is INSTALLED on disk, and the one way to
///     change it.
/// </summary>
/// <remarks>
///     <para>
///         <b>Not the live composition.</b> The plugin host has its own
///         inventory (#1722, <c>Harbor.Plugins.Hosting.PluginInventory</c>):
///         the plugins it LOADED, the phase each one is in, the doors it
///         called. This one is the other half — the plugin FILES in the two
///         scopes, whether they are enabled, and what their manifests
///         declare. The host runs out of process, so the file side is what
///         the CLI process can answer.
///     </para>
///     <para>
///         <b>Why a port, and not a static helper.</b> Two surfaces answer
///         "what is installed" today — the agent-facing tool
///         (<c>plugin_manager</c>) and the <c>/plugins</c> panel — and a
///         helper each of them called would be two implementations that
///         drift: the agent would propose to remove a plugin the panel does
///         not list. A port they both resolve is one implementation and one
///         definition of an installed plugin.
///     </para>
///     <para>
///         <b>Why it lives in the Domain.</b> The tool that consumes it
///         (<c>Harbor.Tools.Builtin</c>) may not reference the plugin layers —
///         the layer matrix gives Infrastructure the Domain and nothing else.
///         A contract in the Domain is what lets the tool reach the inventory
///         without the plugin stack reaching into the tool.
///     </para>
///     <para>
///         <b>Reading and changing are one port on purpose.</b> The gate that
///         protects a change lives in the CALLER (the tool asks the user
///         before it calls <see cref="ApplyAsync" />), so a port that could
///         read and write separately would invite a second, ungated writer.
///     </para>
/// </remarks>
public interface IInstalledPluginInventory
{
    /// <summary>
    ///     List the installed plugins across both scopes.
    /// </summary>
    /// <returns>
    ///     Success with the entries (an empty list when no plugin is
    ///     installed), or failure when a scope that EXISTS could not be read —
    ///     an incomplete list is reported as a failure, never as "no plugins".
    /// </returns>
    /// <remarks>
    ///     The read is SYNCHRONOUS on purpose: the <c>/plugins</c> panel
    ///     paints on the render thread and needs the list there and then, and
    ///     the read is a directory enumeration plus a few small manifest files.
    ///     The write is asynchronous because it copies a file.
    /// </remarks>
    Result<IReadOnlyList<InstalledPluginEntry>> List();

    /// <summary>
    ///     Describe what a plugin SOURCE declares, without installing it.
    /// </summary>
    /// <param name="sourcePath">The reviewed plugin source to describe.</param>
    /// <param name="scope">The scope it would be installed into.</param>
    /// <returns>
    ///     Success with the entry the source WOULD produce — the declared
    ///     version, the declared axes, the declared Harbor floor and whether
    ///     this Harbor satisfies it — or failure with the reason the source
    ///     cannot be installed at all.
    /// </returns>
    /// <remarks>
    ///     The approval prompt needs these facts BEFORE the file is copied
    ///     anywhere, and the file is not in the inventory until it is
    ///     installed. Without this member the human would approve an install
    ///     whose only outcome is a refusal at load time.
    /// </remarks>
    Result<InstalledPluginEntry> DescribeSource(string sourcePath, PluginScope scope);

    /// <summary>
    ///     Apply one change to the inventory.
    /// </summary>
    /// <param name="change">The change to apply.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    ///     Success with what the change did, or failure with the reason it did
    ///     not happen. Nothing outside the scope roots is ever touched.
    /// </returns>
    Task<Result<PluginInventoryChangeResult>> ApplyAsync(
        PluginInventoryChange change,
        CancellationToken ct = default);
}
