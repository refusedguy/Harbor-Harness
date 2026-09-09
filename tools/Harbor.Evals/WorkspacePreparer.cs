using System.Security.Cryptography;

namespace Harbor.Evals;

/// <summary>Per-attempt workspace: copy fixture, manifest, optional git init.</summary>
internal static class WorkspacePreparer
{
    public sealed record PreparedWorkspace(string Root, string WorkspaceDir, string VerifierDir, FileManifest Initial);

    public static PreparedWorkspace Prepare(string attemptRoot, EvalTask task)
    {
        string ws = Path.Combine(attemptRoot, "workspace");
        string verifier = Path.Combine(attemptRoot, "verifier");
        Directory.CreateDirectory(ws);
        Directory.CreateDirectory(verifier);

        CopyDir(Path.Combine(task.TaskDir, task.SourceDirectory), ws);
        CopyDir(Path.Combine(task.TaskDir, "verifier"), verifier);
        var manifest = FileManifest.Capture(ws);
        return new PreparedWorkspace(attemptRoot, ws, verifier, manifest);
    }

    private static void CopyDir(string from, string to)
    {
        foreach (string dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(from, to));
        foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(from, to), overwrite: true);
    }

    /// <summary>relative path → sha256 (files only; symlinks recorded as-is).</summary>
    public sealed record FileManifest(Dictionary<string, string> Files)
    {
        public static FileManifest Capture(string root)
        {
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(root, file);
                files[rel] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
            }

            return new FileManifest(files);
        }
    }
}
