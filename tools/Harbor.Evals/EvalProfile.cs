using System.Text.Json;

namespace Harbor.Evals;

/// <summary>Profile: Harbor entry, model, timeouts. Secrets pass via env, never artifacts.</summary>
internal sealed class EvalProfile
{
    public string RepoRoot { get; set; } = string.Empty;
    public HarborEntry Harbor { get; set; } = new();
    public string Model { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;

    public static EvalProfile Load(string path, string repoRoot)
    {
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var p = JsonSerializer.Deserialize<EvalProfile>(File.ReadAllText(path), opts)
            ?? throw new InvalidOperationException($"Invalid profile {path}.");
        p.RepoRoot = repoRoot;
        return p;
    }

    public sealed class HarborEntry
    {
        public string Dotnet { get; set; } = "dotnet";
        public string CliDll { get; set; } = string.Empty;
        public Dictionary<string, string> Env { get; set; } = new();
    }
}
