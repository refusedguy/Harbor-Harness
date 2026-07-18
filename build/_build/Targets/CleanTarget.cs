using Harbor.Build.Components;
using Harbor.Build.Extensions;
using Nuke.Common.IO;

namespace Harbor.Build.Targets;

/// <summary>
///     Clean target — deletes <c>bin</c>/<c>obj</c> directories under
///     <c>src/</c>, <c>apps/</c>, <c>tests/</c>, <c>samples/</c>, and clears
///     the <c>artifacts/</c> directory. No-op safe to run repeatedly.
/// </summary>
public static class CleanTarget
{
    /// <summary>
    ///     Executes the clean operation against the directories resolved by
    ///     <paramref name="resolver"/>.
    /// </summary>
    public static void Execute(ArtifactPathResolver resolver)
    {
        Console.WriteLine("==> Clean: removing bin/obj directories and clearing artifacts/");

        DeleteBinObj(resolver.SourceDirectory);
        DeleteBinObj(resolver.AppsDirectory);
        DeleteBinObj(resolver.TestsDirectory);
        DeleteBinObj(resolver.SamplesDirectory);

        resolver.ArtifactsDirectory.CreateOrCleanDirectory();
        Console.WriteLine("==> Clean: done");
    }

    private static void DeleteBinObj(AbsolutePath root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var dir in root.GlobDirectories("**/bin", "**/obj"))
        {
            try
            {
                dir.DeleteDirectory();
                Console.WriteLine($"    deleted {dir}");
            }
            catch (IOException ex)
            {
                Console.WriteLine($"    skipped {dir} ({ex.Message})");
            }
        }
    }
}
