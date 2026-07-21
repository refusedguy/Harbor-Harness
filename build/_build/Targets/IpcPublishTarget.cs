using Harbor.Build.Components;
using Harbor.Build.Extensions;
using Nuke.Common.IO;
using Nuke.Common.Tools.DotNet;
namespace Harbor.Build.Targets;
/// <summary>
///     IPC publish targets — produce two thin CLI variants for the
///     two-process Harbor deployment:
///     <list type="bullet">
///         <item>
///             <see cref="ExecuteIpcServer" /> — publishes
///             <c>Harbor.App.Cli</c> with <c>HarborMode=ipc-server</c>.
///             This binary hosts the AgentLoop + registries and exposes
///             them via MessagePack-over-pipe.
///         </item>
///         <item>
///             <see cref="ExecuteIpcClient" /> — publishes
///             <c>Harbor.App.Cli</c> with <c>HarborMode=ipc-client</c>.
///             This binary is a thin client that connects to a running
///             ipc-server via MessagePack-over-pipe.
///         </item>
///     </list>
///     Both targets output to <c>artifacts/ipc-{server|client}/</c>.
/// </summary>
public static class IpcPublishTarget
{
    /// <summary>
    ///     Publish the <c>ipc-server</c> variant. The resulting binary runs
    ///     the agent loop and accepts IPC client connections.
    /// </summary>
    /// <returns>The publish output directory.</returns>
    public static AbsolutePath ExecuteIpcServer(
        ArtifactPathResolver resolver,
        BuildSettings settings,
        FeatureFlags flags) => PublishIpcVariant(resolver, settings, flags, "ipc-server");

    /// <summary>
    ///     Publish the <c>ipc-client</c> variant. The resulting binary is a
    ///     thin client that talks to a separately-running <c>ipc-server</c>.
    /// </summary>
    /// <returns>The publish output directory.</returns>
    public static AbsolutePath ExecuteIpcClient(
        ArtifactPathResolver resolver,
        BuildSettings settings,
        FeatureFlags flags) => PublishIpcVariant(resolver, settings, flags, "ipc-client");

    private static AbsolutePath PublishIpcVariant(
        ArtifactPathResolver resolver,
        BuildSettings settings,
        FeatureFlags flags,
        string mode)
    {
        var resolvedFlags = flags.Resolved();
        Console.WriteLine($"==> PublishIpc: mode={mode} flags=[{resolvedFlags}]");

        var projectFile = resolver.GetAppProjectFile("Harbor.App.Cli");
        var outputDir = resolver.ArtifactsDirectory / $"ipc-{mode}";

        var publishSettings = new DotNetPublishSettings()
            .SetProject(projectFile)
            .SetConfiguration(settings.Configuration.ToString())
            .SetProperty("HarborMode", mode)
            .SetProperty("HarborWithPlugins", resolvedFlags.WithPlugins.ToString().ToLowerInvariant())
            .SetProperty("HarborWithScripting", resolvedFlags.WithScripting.ToString().ToLowerInvariant())
            .SetProperty("HarborWithSpectreTui", resolvedFlags.WithSpectreTui.ToString().ToLowerInvariant())
            .SetProperty("HarborWithAllProviders", resolvedFlags.WithAllProviders.ToString().ToLowerInvariant())
            .SetProperty("HarborWithAllTools", resolvedFlags.WithAllTools.ToString().ToLowerInvariant())
            .SetOutput(outputDir);

        DotNetTasks.DotNetPublish(publishSettings);

        string size = outputDir.GetHumanReadableSize();
        Console.WriteLine($"==> PublishIpc: done — {outputDir} ({size})");
        return outputDir;
    }
}
