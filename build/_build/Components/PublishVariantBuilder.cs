using Nuke.Common.IO;
using Nuke.Common.Tools.DotNet;
namespace Harbor.Build.Components;
/// <summary>
///     Builds the <c>DotNetPublishSettings</c> for each <see cref="PublishVariant" />.
///     Knows which <c>dotnet publish</c> properties to set for framework-dependent
///     vs. self-contained vs. single-file vs. trimmed vs. NativeAOT.
/// </summary>
/// <remarks>
///     Single responsibility: translate a <see cref="PublishVariant" /> + flags
///     into a <c>DotNetPublishSettings</c> configurator. Does NOT execute the
///     publish — that's <c>PublishTarget</c>'s job.
/// </remarks>
public sealed class PublishVariantBuilder
{
    private readonly CliBuildConfigurator _configurator;
    private readonly BuildSettings _settings;

    /// <summary>Construct a builder bound to the given build settings.</summary>
    public PublishVariantBuilder(BuildSettings settings, CliBuildConfigurator configurator)
    {
        _settings = settings;
        _configurator = configurator;
    }

    /// <summary>
    ///     Applies variant-specific + feature-flag-specific MSBuild properties
    ///     to the given <c>DotNetPublishSettings</c> configurator.
    ///     Returns a new configurator (functional — does not mutate input).
    /// </summary>
    public DotNetPublishSettings Configure(
        DotNetPublishSettings baseSettings,
        PublishVariant variant,
        FeatureFlags flags,
        AbsolutePath outputDir)
    {
        _configurator.EnsureVariantAllowed(variant, flags);

        var settings = baseSettings
            .SetConfiguration(_settings.ConfigurationString)
            .SetOutput(outputDir)
            .SetProperty("TargetFramework", _settings.TargetFramework)
            // Directory.Build.props sets <PublishSingleFile>true</PublishSingleFile>
            // globally. We must explicitly disable it for variants that aren't
            // single-file (FrameworkDependent, SelfContained, AOT, Trimmed);
            // otherwise the bundler kicks in unconditionally and fails on
            // non-PE files like Spectre.Console's embedded resources.
            .SetProperty("PublishSingleFile", "false");

        settings = ApplyVariant(settings, variant);
        settings = ApplyFeatureFlags(settings, flags);

        // Variants that need a runtime (SelfContained, SingleFile, SingleFileSelfContained,
        // Trimmed, AOT) require a runtime-specific build output. The upstream Compile target
        // builds for the default RID (no RID), so the runtime-specific artifacts don't exist
        // and `dotnet publish --no-build` would fail with MSB3030 "could not copy file".
        // For these variants, disable NoBuild so publish re-builds for the target RID.
        if (variant != PublishVariant.FrameworkDependent)
        {
            settings = settings.DisableNoBuild();
        }
        return settings;
    }

    private DotNetPublishSettings ApplyVariant(DotNetPublishSettings settings, PublishVariant variant)
    {
        switch (variant)
        {
            case PublishVariant.FrameworkDependent:
                // Smallest non-AOT. No RID, no self-contained, no single-file.
                return settings;

            case PublishVariant.SelfContained:
                return settings
                    .SetRuntime(_settings.Runtime)
                    .SetSelfContained(true);

            case PublishVariant.SingleFile:
                // Single-file but NOT self-contained (still requires .NET runtime).
                return settings
                    .SetRuntime(_settings.Runtime)
                    .SetProperty("PublishSingleFile", "true")
                    .SetProperty("IncludeNativeLibrariesForSelfExtract", "true");

            case PublishVariant.SingleFileSelfContained:
                // True single-file self-contained — bundled runtime extracted to temp.
                return settings
                    .SetRuntime(_settings.Runtime)
                    .SetSelfContained(true)
                    .SetProperty("PublishSingleFile", "true")
                    .SetProperty("IncludeNativeLibrariesForSelfExtract", "true")
                    .SetProperty("IncludeAllContentForSelfExtract", "true");

            case PublishVariant.Trimmed:
                // Trimmed self-contained — experimental, breaks reflection.
                return settings
                    .SetRuntime(_settings.Runtime)
                    .SetSelfContained(true)
                    .SetProperty("PublishTrimmed", "true")
                    .SetProperty("TrimMode", "partial")
                    .SetProperty("DebuggerSupport", "false")
                    .SetProperty("EnableUnsafeBinaryFormatterSerialization", "false")
                    .SetProperty("EnableUnsafeUTF7Encoding", "false")
                    .SetProperty("EventSourceSupport", "false")
                    .SetProperty("HttpActivityPropagationSupport", "false")
                    .SetProperty("InvariantGlobalization", "true");

            case PublishVariant.AOT:
                return settings
                    .SetRuntime(_settings.Runtime)
                    .SetProperty("PublishAot", "true")
                    .SetProperty("TrimMode", "full")
                    .SetProperty("IlcOptimizationPreference", "Speed");

            default:
                throw new ArgumentOutOfRangeException(nameof(variant), variant, "Unknown publish variant");
        }
    }

    private DotNetPublishSettings ApplyFeatureFlags(DotNetPublishSettings settings, FeatureFlags flags)
    {
        var props = _configurator.BuildProperties(flags);
        foreach ((string key, string value) in props)
        {
            settings = settings.SetProperty(key, value);
        }
        return settings;
    }
}
