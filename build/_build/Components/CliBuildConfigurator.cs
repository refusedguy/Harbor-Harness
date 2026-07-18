using System.Collections.Generic;
using System.Linq;
using Harbor.Build.Configuration;

namespace Harbor.Build.Components;

/// <summary>
///     Translates <see cref="FeatureFlags"/> into MSBuild <c>/p:</c> properties
///     and <c>&lt;DefineConstants&gt;</c> values that <c>Harbor.App.Cli.csproj</c>
///     and <c>HostBuilder.cs</c> understand.
/// </summary>
/// <remarks>
///     Single responsibility: build the dictionary of MSBuild args. Does NOT
///     invoke <c>dotnet</c> — that's <see cref="PublishVariantBuilder"/>'s job.
///     The csproj uses these properties to conditionally include
///     <c>&lt;ProjectReference&gt;</c> entries and to define the
///     <c>HARBOR_WITH_PLUGINS</c> / <c>HARBOR_WITH_SCRIPTING</c> /
///     <c>HARBOR_WITH_SPECTRE_TUI</c> / <c>HARBOR_WITH_ALL_PROVIDERS</c>
///     <c>#if</c> symbols that <c>HostBuilder.cs</c> switches on.
/// </remarks>
public sealed class CliBuildConfigurator
{
    /// <summary>
    ///     Returns the MSBuild property dictionary for the given feature flags.
    ///     Keys are property names (e.g. <c>HarborWithPlugins</c>); values are
    ///     always lowercased <c>true</c>/<c>false</c> strings — the convention
    ///     the csproj expects.
    /// </summary>
    public IReadOnlyDictionary<string, string> BuildProperties(FeatureFlags flags)
    {
        var resolved = flags.Resolved();
        return new Dictionary<string, string>
        {
            ["HarborWithPlugins"] = resolved.WithPlugins.ToString().ToLowerInvariant(),
            ["HarborWithScripting"] = resolved.WithScripting.ToString().ToLowerInvariant(),
            ["HarborWithSpectreTui"] = resolved.WithSpectreTui.ToString().ToLowerInvariant(),
            ["HarborWithAllProviders"] = resolved.WithAllProviders.ToString().ToLowerInvariant(),
            ["HarborWithAllTools"] = resolved.WithAllTools.ToString().ToLowerInvariant(),
            ["HARBOR_MINIMAL"] = resolved.Minimal.ToString().ToLowerInvariant(),
        };
    }

    /// <summary>
    ///     Returns <c>true</c> if the resolved flags are AOT-compatible and the
    ///     requested <paramref name="variant"/> is therefore allowed. AOT and
    ///     Trimmed variants require <see cref="FeatureFlags.IsAotCompatible"/>.
    /// </summary>
    public bool IsVariantAllowed(PublishVariant variant, FeatureFlags flags)
    {
        if (variant is PublishVariant.AOT or PublishVariant.Trimmed)
        {
            return flags.Resolved().IsAotCompatible;
        }
        return true;
    }

    /// <summary>
    ///     Throws <see cref="InvalidOperationException"/> if the variant is not
    ///     allowed for the given flags. Use in targets to fail fast with an
    ///     actionable message.
    /// </summary>
    public void EnsureVariantAllowed(PublishVariant variant, FeatureFlags flags)
    {
        if (!IsVariantAllowed(variant, flags))
        {
            throw new InvalidOperationException(
                $"Publish variant '{variant}' requires AOT-compatible feature flags " +
                $"(no plugins, no scripting, no Spectre.TUI). Current flags: {flags}. " +
                $"Pass --minimal to disable all AOT-incompatible features.");
        }
    }
}
