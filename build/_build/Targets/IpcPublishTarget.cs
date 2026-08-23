using Harbor.Build.Components;
using Harbor.Build.Extensions;
using Harbor.Build.Meta;
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
///     Dry-run prints the expanded argv and planned output directory.
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
        FeatureFlags flags,
        BuildOutput output) => PublishIpcVariant(resolver, settings, flags, "ipc-server", output);
    /// <summary>
    ///     Publish the <c>ipc-client</c> variant. The resulting binary is a
    ///     thin client that talks to a separately-running <c>ipc-server</c>.
    /// </summary>
    /// <returns>The publish output directory.</returns>
    public static AbsolutePath ExecuteIpcClient(
        ArtifactPathResolver resolver,
        BuildSettings settings,
        FeatureFlags flags,
        BuildOutput output) => PublishIpcVariant(resolver, settings, flags, "ipc-client", output);
    private static AbsolutePath PublishIpcVariant(
        ArtifactPathResolver resolver,
        BuildSettings settings,
        FeatureFlags flags,
        string mode,
        BuildOutput output)
    {
        var resolvedFlags = flags.Resolved();
        output.Info("PublishIpc", $"mode={mode} flags=[{resolvedFlags}]");
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
        output.Cmd("PublishIpc", DotNetArgv.RenderPublish(publishSettings));
        if (output.IsDryRun)
        {
            output.Artifact("PublishIpc", outputDir.ToString(), bytes: null, planned: true);
            return outputDir;
        }
        DotNetTasks.DotNetPublish(publishSettings);
        long bytes = outputDir.GetDirectorySizeBytes();
        output.Artifact("PublishIpc", outputDir.ToString(), bytes);
        return outputDir;
    }
}
