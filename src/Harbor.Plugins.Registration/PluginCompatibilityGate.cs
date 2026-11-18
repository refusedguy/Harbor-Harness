using Harbor.Abstractions.Plugins;
using Harbor.Plugins.Abstractions;
using Microsoft.Extensions.Logging;

namespace Harbor.Plugins.Registration;

/// <summary>
///     Decides whether a loaded plugin may run against the running Harbor, before
///     the registrar initializes it. The version floor and the manifest claims are
///     one responsibility, moved here from <see cref="PluginRegistrar" /> (#2614).
///     The registrar only consumes the verdict.
/// </summary>
/// <remarks>
///     Both load routes pass through the registrar, so the in-process
///     CsPluginLoader and the out-of-process plugins host get the same gate.
/// </remarks>
internal sealed class PluginCompatibilityGate
{
    private readonly ILogger _logger;
    private readonly Version _harborVersion;

    internal PluginCompatibilityGate(ILogger logger, Version harborVersion)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _harborVersion = harborVersion ?? throw new ArgumentNullException(nameof(harborVersion));
    }

    /// <summary>
    ///     Check the plugin's declared version floor and the manifest it shipped.
    /// </summary>
    /// <param name="plugin">The loaded plugin.</param>
    /// <param name="markers">The extension axes the plugin implements.</param>
    /// <returns>Success when the plugin may run, or a failure naming the reason.</returns>
    internal Result Check(LoadedPlugin plugin, PluginAxisMarkers markers)
    {
        // #1721: a plugin that declares a Harbor newer than this runtime must not
        // run at all. No Initialize, no Register*, no tool in the registry. The
        // gate sits here, in front of IPlugin.Initialize, because this is the one
        // seam both load routes pass through: the in-process CsPluginLoader and
        // the out-of-process harbor-plugins-host both register through a
        // PluginRegistrar.
        Version? requiredHarborVersion = plugin.Instance.RequiredHarborVersion;
        if (requiredHarborVersion is null)
        {
            _logger.LogWarning(
                "Plugin {DisplayName} declares no RequiredHarborVersion. Loading it without a compatibility check.",
                plugin.DisplayName);
        }

        var compatibility = PluginCompatibility.Check(
            plugin.Instance.Name,
            requiredHarborVersion,
            _harborVersion);
        if (compatibility.IsFailure)
        {
            _logger.LogWarning(
                "Plugin {DisplayName} is not compatible with this Harbor: {Reason}",
                plugin.DisplayName,
                compatibility.Error);
            return compatibility;
        }

        // #1739: the manifest is the plugin's own declaration, and it is checked
        // against the plugin. The code stays the truth for what the plugin DOES;
        // the manifest says what it CLAIMS, and a claim that does not match the
        // code is refused. A plugin that ships two different names, or two
        // different sets of axes, is a plugin the host cannot describe.
        string? manifestRefusal = CheckManifest(plugin, markers);
        if (manifestRefusal is not null)
        {
            _logger.LogWarning(
                "Plugin {DisplayName} does not match its manifest: {Reason}",
                plugin.DisplayName,
                manifestRefusal);
            return Result.Failure(manifestRefusal);
        }

        return Result.Success();
    }

    // ── The manifest gate (#1739) ───────────────────────────────────────────────

    /// <summary>
    ///     Why the plugin does not match the manifest it shipped, or
    ///     <c>null</c> when it does.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Three things are checked, and each of them is a claim the manifest
    ///         makes about the plugin:
    ///     </para>
    ///     <list type="bullet">
    ///         <item>
    ///             <description>
    ///                 The Harbor version the manifest asks for. It goes through
    ///                 the same gate as the in-code floor, so the manifest and
    ///                 the code cannot disagree about what the rule is.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 The name and the version. A manifest that names the
    ///                 plugin differently from the plugin itself is a plugin the
    ///                 host cannot describe. An index built from the manifest
    ///                 would list a plugin that does not exist.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 The extension axes. The manifest says what the plugin
    ///                 contributes; the plugin says what it implements. When the
    ///                 manifest declares axes, the two must be the same set.
    ///             </description>
    ///         </item>
    ///     </list>
    ///     <para>
    ///         A manifest that declares NOTHING is not a disagreement: it simply
    ///         says less than the code, and the code wins.
    ///     </para>
    /// </remarks>
    /// <param name="plugin">The loaded plugin.</param>
    /// <param name="markers">The extension axes the plugin implements.</param>
    /// <returns>The reason to refuse the plugin, or <c>null</c>.</returns>
    private string? CheckManifest(LoadedPlugin plugin, PluginAxisMarkers markers)
    {
        PluginManifest? manifest = plugin.Manifest;
        if (manifest is null)
            return null;

        if (manifest.RequiredHarborVersion is { } floor)
        {
            var compatibility = PluginCompatibility.Check(plugin.Instance.Name, floor, _harborVersion);
            if (compatibility.IsFailure)
                return compatibility.Error;
        }

        if (manifest.Name is { } declaredName
            && !string.Equals(declaredName, plugin.Instance.Name, StringComparison.Ordinal))
        {
            return $"The manifest of plugin '{plugin.Instance.Name}' declares the name '{declaredName}', "
                   + $"but the plugin reports '{plugin.Instance.Name}'. The manifest and the plugin must "
                   + "agree, or the manifest must not declare a name.";
        }

        if (manifest.Version is { } declaredVersion
            && !PluginCompatibility.IsSameVersion(declaredVersion, plugin.Instance.Version))
        {
            return $"The manifest of plugin '{plugin.Instance.Name}' declares the version '{declaredVersion}', "
                   + $"but the plugin reports '{plugin.Instance.Version}'. The manifest and the plugin must "
                   + "agree, or the manifest must not declare a version.";
        }

        if (manifest.Axes is { } declaredAxes)
        {
            var declared = new HashSet<string>(declaredAxes, StringComparer.Ordinal);
            var implemented = new HashSet<string>(markers.Names, StringComparer.Ordinal);
            if (!declared.SetEquals(implemented))
            {
                return $"The manifest of plugin '{plugin.Instance.Name}' declares the extension axes "
                       + $"[{string.Join(", ", declared.Order(StringComparer.Ordinal))}], but the plugin "
                       + $"implements [{string.Join(", ", implemented.Order(StringComparer.Ordinal))}]. The "
                       + "manifest and the plugin must agree, or the manifest must not declare axes.";
            }
        }

        return null;
    }
}
