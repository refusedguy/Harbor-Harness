using Harbor.Build.Components;
using Harbor.Build.Meta;
using Nuke.Common.IO;
namespace Harbor.Build.Targets;
/// <summary>
///     Clean target — deletes <c>bin</c>/<c>obj</c> directories under
///     <c>src/</c>, <c>apps/</c>, <c>tests/</c>, <c>samples/</c>, and clears
///     the <c>artifacts/</c> directory. No-op safe to run repeatedly.
///     Dry-run lists the glob results without deleting anything.
/// </summary>
public static class CleanTarget
{
    /// <summary>
    ///     Executes the clean operation against the directories resolved by
    ///     <paramref name="resolver" />.
    /// </summary>
    public static void Execute(ArtifactPathResolver resolver, BuildOutput output)
    {
        output.Info("Clean", "removing bin/obj directories and clearing artifacts/");
        DeleteBinObj(resolver.SourceDirectory, output);
        DeleteBinObj(resolver.AppsDirectory, output);
        DeleteBinObj(resolver.TestsDirectory, output);
        DeleteBinObj(resolver.SamplesDirectory, output);
        if (output.IsDryRun)
        {
            output.Info("Clean", $"dry-run: would clear {resolver.ArtifactsDirectory}");
            return;
        }
        resolver.ArtifactsDirectory.CreateOrCleanDirectory();
    }
    private static void DeleteBinObj(AbsolutePath root, BuildOutput output)
    {
        if (!Directory.Exists(root)) return;
        foreach (var dir in root.GlobDirectories("**/bin", "**/obj"))
        {
            if (output.IsDryRun)
            {
                output.Info("Clean", $"dry-run: would delete {dir}");
                continue;
            }
            try
            {
                dir.DeleteDirectory();
                output.Info("Clean", $"deleted {dir}");
            }
            catch (IOException ex)
            {
                output.Warn("Clean", $"skipped {dir} ({ex.Message})");
            }
        }
    }
}
