using System.Security.Cryptography;

namespace Harbor.Evals;

/// <summary>Per-attempt workspace: copy fixture, manifest, optional git init.</summary>
internal static class WorkspacePreparer
{
    public sealed record PreparedWorkspace(string Root, string WorkspaceDir, string VerifierDir, string HomeDir, FileManifest Initial);

    public static PreparedWorkspace Prepare(string attemptRoot, EvalTask task, string provider, string model)
    {
        string ws = Path.Combine(attemptRoot, "workspace");
        string verifier = Path.Combine(attemptRoot, "verifier");
        Directory.CreateDirectory(ws);
        Directory.CreateDirectory(verifier);

        CopyDir(Path.Combine(task.TaskDir, task.SourceDirectory), ws);
        CopyDir(Path.Combine(task.TaskDir, "verifier"), verifier);
        string home = WriteIsolatedHome(attemptRoot, provider, model);
        var manifest = FileManifest.Capture(ws);
        return new PreparedWorkspace(attemptRoot, ws, verifier, home, manifest);
    }

    /// <summary>Isolated $HOME with a permissive config: evals must never touch
    /// the user's real ~/.harbor, and headless runs have no approver — Ask
    /// would hang forever. No secrets here (keys flow via env).</summary>
    private static string WriteIsolatedHome(string attemptRoot, string provider, string model)
    {
        string home = Path.Combine(attemptRoot, "home");
        Directory.CreateDirectory(Path.Combine(home, ".harbor"));
        string Allow(string tool) =>
            $$"""{"Permission":"{{tool}}","Pattern":"*","Action":0}""";
        string config = $$"""
            {
              "provider": "{{provider}}",
              "model": "{{model}}",
              "agent": "code",
              "onboarded": true,
              "permissions": {
                "code": [{{Allow("bash")}},{{Allow("write")}},{{Allow("edit")}},{{Allow("read")}},{{Allow("glob")}},{{Allow("grep")}},{{Allow("patch")}},{{Allow("tree")}},{{Allow("lsp")}}]
              }
            }
            """;
        File.WriteAllText(Path.Combine(home, ".harbor", "config.json"), config);
        return home;
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
