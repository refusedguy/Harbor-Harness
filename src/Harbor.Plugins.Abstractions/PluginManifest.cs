// PluginManifest.cs — a plugin's declaration, as DATA (#1739).
//
// WHY THIS FILE EXISTS
// --------------------
// Until now a plugin could say three things about itself, and all three were
// read from the plugin's own code:
//
//   * its name and version — REFLECTION over the compiled assembly, so the host
//     learned them only after the code was compiled and instantiated;
//   * the Harbor version it needs — a property on the interface, read after
//     the plugin was already built;
//   * the capabilities it wants — a COMMENT DIRECTIVE in the source, parsed by a
//     regular expression over the whole file.
//
// A declaration that lives in the code is not readable without the code. The
// host cannot know whether a plugin is compatible before it pays to compile
// it, and it cannot build an index of the installed plugins without running
// them. This file is the shape of the alternative: the declaration travels
// WITH the plugin, in a JSON file, and is read as data.
//
// WHAT THE MANIFEST IS NOT
// ------------------------
// It is not a second source of truth for what the plugin DOES. The code stays
// the truth for that, and the host checks the manifest against the code
// instead of believing one of them:
//
//   * the name and the version, when the manifest declares them, must equal
//     what the loaded plugin reports;
//   * the axes, when the manifest declares them, must equal the axes the
//     plugin actually implements.
//
// A manifest that disagrees with its plugin is refused, with a reason. That is
// the whole point of the check: two declarations of one fact that are allowed
// to drift apart are two truths, and the second one is the one nobody reads.
//
// The manifest also does NOT carry the plugin's dependencies. Those live on
// `IPlugin.Dependencies` (#1733) and they are a runtime concern: the host
// resolves a dependency name against what is loaded and what it publishes, so
// a name in a file would be a second spelling of a name the host owns.
//
// WHAT THE FIELDS MEAN
// --------------------
//   schemaVersion  the manifest format. A manifest that asks for a format this
//                  build does not understand is refused, not guessed at.
//   name           the plugin id. Optional; when present it is checked
//                  against `IPlugin.Name`.
//   version        the plugin's own version. Optional; when present it is
//                  checked against `IPlugin.Version`.
//   harbor         the minimum Harbor version the plugin needs — the same
//                  floor `IPlugin.RequiredHarborVersion` declares, checked by
//                  the same gate (`PluginCompatibility`), so the manifest
//                  version of the rule and the in-code version cannot differ.
//   capabilities   the capabilities the plugin may use. Optional; when present
//                  it REPLACES the comment directive, and the trust gate
//                  narrows it further to what the user approved.
//   axes           the extension axes the plugin contributes, named exactly as
//                  `PluginAxisRegistry` names them (`IToolPlugin`,
//                  `IProviderPlugin`, ...). Optional; when present it is
//                  checked against the axes the plugin actually implements.

namespace Harbor.Plugins.Abstractions;

/// <summary>
///     A plugin's manifest: what the plugin declares about itself, in data, next
///     to its code.
/// </summary>
/// <remarks>
///     <para>
///         Every field is optional except the format itself. A manifest that
///         declares nothing but a version is a valid manifest, and a plugin
///         with no manifest at all is a plugin the host knows nothing extra
///         about — it loads exactly as it did before the manifest existed.
///     </para>
///     <para>
///         The manifest is READ, never executed: it is a JSON document, so
///         reading it needs no reflection and no code from the plugin, and it
///         works the same under NativeAOT.
///     </para>
/// </remarks>
public sealed record PluginManifest
{
    /// <summary>
    ///     The manifest format this build understands. A manifest that declares
    ///     a different one is refused rather than guessed at.
    /// </summary>
    public const int SupportedSchemaVersion = 1;

    /// <summary>
    ///     The manifest format version. Absent in the file means
    ///     <see cref="SupportedSchemaVersion" />.
    /// </summary>
    public int SchemaVersion { get; init; } = SupportedSchemaVersion;

    /// <summary>
    ///     The plugin's stable id, as the manifest declares it. When present it
    ///     must equal the loaded plugin's <c>IPlugin.Name</c>.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    ///     The plugin's version, as the manifest declares it. When present it
    ///     must equal the loaded plugin's <c>IPlugin.Version</c>.
    /// </summary>
    public Version? Version { get; init; }

    /// <summary>
    ///     The minimum Harbor version the plugin needs, as the manifest
    ///     declares it. Checked by the same gate that checks
    ///     <c>IPlugin.RequiredHarborVersion</c>, so the two declarations of
    ///     the rule are one rule.
    /// </summary>
    public Version? RequiredHarborVersion { get; init; }

    /// <summary>
    ///     The capabilities the plugin declares, or <c>null</c> when the
    ///     manifest does not declare them (in which case the comment directive
    ///     in the source is used, exactly as before the manifest existed).
    /// </summary>
    /// <remarks>
    ///     An EMPTY set is a declaration: the plugin declares that it needs no
    ///     capability, and the source directive is not consulted at all.
    /// </remarks>
    public IReadOnlySet<PluginCapability>? Capabilities { get; init; }

    /// <summary>
    ///     The extension axes the plugin declares it contributes, named as
    ///     <see cref="PluginAxisRegistry" /> names them, or <c>null</c> when
    ///     the manifest does not declare them.
    /// </summary>
    public IReadOnlyList<string>? Axes { get; init; }

    /// <summary>
    ///     The SHA-256 of the manifest file's raw bytes, as lowercase hex.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The manifest is part of the plugin's CONTENT, so its bytes are
    ///         part of the plugin's content hash: editing the manifest changes
    ///         the hash, which invalidates the compiled-assembly cache entry
    ///         and the persisted trust decision. Without this, a manifest
    ///         could widen the declared capability set behind a trust decision
    ///         the user gave for a different declaration.
    ///     </para>
    ///     <para>
    ///         The hash is of the RAW BYTES, not of the parsed values, so a
    ///         field this build does not understand still changes the hash when
    ///         it is edited.
    ///     </para>
    /// </remarks>
    public string ContentHash { get; init; } = string.Empty;
}
