using System.Text.Json;

namespace Harbor.Evals;

/// <summary>Task + constraints + prompt loading. No execution here.</summary>
internal static class TaskLoader
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static EvalTask Load(string taskDir)
    {
        string taskJson = File.ReadAllText(Path.Combine(taskDir, "task.json"));
        var task = JsonSerializer.Deserialize<EvalTask>(taskJson, Json)
            ?? throw new InvalidOperationException($"Invalid task.json in {taskDir}: null.");
        if (task.SchemaVersion != 1)
            throw new InvalidOperationException($"Unsupported task schema {task.SchemaVersion} in {taskDir}.");
        task.TaskDir = Path.GetFullPath(taskDir);
        task.Prompt = File.ReadAllText(Path.Combine(taskDir, task.PromptFile));
        task.Constraints = LoadConstraints(taskDir, task.ConstraintsFile);
        return task;
    }

    private static EvalConstraints LoadConstraints(string taskDir, string file)
    {
        string path = Path.Combine(taskDir, file);
        if (!File.Exists(path))
            return new EvalConstraints();
        var c = JsonSerializer.Deserialize<EvalConstraints>(File.ReadAllText(path), Json);
        return c ?? new EvalConstraints();
    }
}

internal sealed class EvalTask
{
    public int SchemaVersion { get; set; }
    public string Id { get; set; } = string.Empty;
    public int FixtureVersion { get; set; }
    public string PromptFile { get; set; } = "prompt.md";
    public string SourceDirectory { get; set; } = "repo";
    public int TimeoutSeconds { get; set; } = 240;
    public int VerifierTimeoutSeconds { get; set; } = 60;
    public VerifierSpec Verifier { get; set; } = new();

    public string ConstraintsFile { get; set; } = "constraints.json";

    // Loaded, not serialized from task.json:
    public string TaskDir { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public EvalConstraints Constraints { get; set; } = new();
}

internal sealed class VerifierSpec
{
    public VerifierPlatform Unix { get; set; } = new();
    public VerifierPlatform Windows { get; set; } = new();
}

internal sealed class VerifierPlatform
{
    public string File { get; set; } = string.Empty;
    public string[] Args { get; set; } = [];
}

internal sealed class EvalConstraints
{
    public bool ReadOnly { get; set; }
    public string[] MustRemainUnchanged { get; set; } = [];
    public string[] MustNotCreate { get; set; } = [];
}
