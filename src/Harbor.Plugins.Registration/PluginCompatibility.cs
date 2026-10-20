using Harbor.Abstractions.Plugins;

namespace Harbor.Plugins.Registration;

/// <summary>
///     The compatibility gate for <see cref="IPlugin.RequiredHarborVersion" />. A
///     plugin declares the minimum Harbor it needs. This gate answers one question:
///     may this plugin run against the Harbor that is running right now?
/// </summary>
/// <remarks>
///     <para>The policy, in full:</para>
///     <list type="bullet">
///         <item>
///             <description>
///                 The running Harbor is at or above the declared floor. The plugin may
///                 run. A Harbor newer than the plugin asks for is fine, because the
///                 declaration is a floor and not a pin: Harbor keeps its promises
///                 across minor and patch releases, so a plugin written for an older
///                 Harbor keeps working on a newer one.
///             </description>
///         </item>
///         <item>
///             <description>
///                 The running Harbor is below the declared floor. The plugin is
///                 refused before it is initialized, so it contributes nothing to the
///                 host: no tool, no provider, no agent, no panel, and no
///                 half-initialized state left behind.
///             </description>
///         </item>
///         <item>
///             <description>
///                 The plugin declared no floor. There is nothing to enforce, so the
///                 plugin loads. The caller logs the missing declaration.
///             </description>
///         </item>
///     </list>
///     <para>
///         Versions are compared component by component, and an unspecified component
///         is read as 0, so "0.4" and "0.4.0" are the same requirement. The plugin
///         spec lets a plugin declare major and minor only. Comparing through
///         <see cref="Version" />'s own ordering would read "0.4" as older than
///         "0.4.0" and refuse a plugin that the running Harbor does satisfy.
///     </para>
/// </remarks>
public static class PluginCompatibility
{
    /// <summary>
    ///     True when a plugin asking for <paramref name="requiredHarborVersion" /> may
    ///     run against <paramref name="runtimeVersion" />.
    /// </summary>
    /// <param name="runtimeVersion">The Harbor version that is running.</param>
    /// <param name="requiredHarborVersion">
    ///     The minimum the plugin declared. Null means the plugin declared no floor.
    /// </param>
    /// <returns>True when the plugin may run.</returns>
    public static bool IsCompatible(Version runtimeVersion, Version? requiredHarborVersion)
    {
        if (requiredHarborVersion is null)
            return true;
        return Compare(runtimeVersion, requiredHarborVersion) >= 0;
    }

    /// <summary>
    ///     The gate. Success when the plugin may run. Failure otherwise, naming the
    ///     plugin, the Harbor version it needs and the Harbor version that is
    ///     running, so the refusal says what to fix instead of only that something
    ///     went wrong.
    /// </summary>
    /// <param name="pluginName">The plugin's stable id.</param>
    /// <param name="requiredHarborVersion">The minimum the plugin declared.</param>
    /// <param name="runtimeVersion">The Harbor version that is running.</param>
    /// <returns>Success when compatible, failure with the reason otherwise.</returns>
    public static Result Check(string pluginName, Version? requiredHarborVersion, Version runtimeVersion)
    {
        if (requiredHarborVersion is null)
            return Result.Success();
        if (IsCompatible(runtimeVersion, requiredHarborVersion))
            return Result.Success();

        return Result.Failure(
            $"Plugin '{pluginName}' requires Harbor >= {requiredHarborVersion}, "
            + $"but this Harbor is {runtimeVersion}. The plugin was not loaded.");
    }

    /// <summary>
    ///     Component by component comparison, with an unspecified component read as 0.
    ///     Returns a negative number when <paramref name="left" /> is older, zero when
    ///     the two name the same Harbor, a positive number when
    ///     <paramref name="left" /> is newer.
    /// </summary>
    private static int Compare(Version left, Version right)
    {
        int major = left.Major.CompareTo(right.Major);
        if (major != 0)
            return major;

        int minor = left.Minor.CompareTo(right.Minor);
        if (minor != 0)
            return minor;

        int build = Component(left.Build).CompareTo(Component(right.Build));
        if (build != 0)
            return build;

        return Component(left.Revision).CompareTo(Component(right.Revision));
    }

    /// <summary>
    ///     Read one version component, treating "not specified" (-1) as 0 so that
    ///     "0.4" and "0.4.0" are the same Harbor.
    /// </summary>
    private static int Component(int value) => value < 0 ? 0 : value;
}
