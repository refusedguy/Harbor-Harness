using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Plugins;
using Harbor.Abstractions.Tools;
using Microsoft.Extensions.Logging;

namespace Harbor.Plugin.Pomodoro;

/// <summary>
///     Showcase slice for #1434: a dependency-free focus-timer tool plugin that
///     runs through the out-of-process plugin host. It contributes one
///     <c>pomodoro</c> tool on the EXISTING tool axis only, with no new
///     extension axis (#555 freeze). Install in one command with install.sh;
///     the .cs drop-in is compiled by the separate host process, never by the
///     AOT CLI.
/// </summary>
public sealed class PomodoroPlugin : IToolPlugin
{
    public string Name => "pomodoro";
    public Version Version => new(1, 0, 0);
    public Version RequiredHarborVersion => new(0, 4, 0);
    public string Description => "Exemplary out-of-proc focus timer tool plugin.";

    public void Initialize(PluginContext context)
        => context.CreateLogger<PomodoroPlugin>().LogInformation("Pomodoro plugin initialized");

    public void RegisterTools(IToolRegistryBuilder builder) => builder.AddTool<PomodoroTool>();

    public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
///     A pomodoro/focus timer that keeps one active session in a small JSON
///     file under the Harbor home directory. Demonstrates the minimal
///     <see cref="ITool" /> surface on the out-of-proc route: schema, argument
///     validation, execution, and a structured result. No panel, provider, or
///     other in-process-only axis is used.
/// </summary>
public sealed class PomodoroTool : ITool
{
    private static readonly JsonDocument Schema = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["start", "status", "stop", "reset"], "description": "start, status, stop or reset the timer" },
            "minutes": { "type": "integer", "description": "Focus length in minutes for 'start' (default: 25)" },
            "label": { "type": "string", "description": "Optional label for the session" }
          },
          "required": ["action"]
        }
        """);

    public ToolName Name => ToolName.Create("pomodoro");

    /// <inheritdoc />
    public ToolSafetyProfile SafetyProfile => ToolSafetyProfile.Opaque;

    public string DisplayName => "Pomodoro";
    public string Description => "Start, inspect or stop a focus timer. State lives in ~/.harbor/pomodoro.json.";
    public JsonDocument ParameterSchema => Schema;
    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
    public string? PromptSnippet => "pomodoro: start/status/stop/reset a focus timer";
    public IReadOnlyList<string> PromptGuidelines { get; } = Array.Empty<string>();

    public Result ValidateArguments(JsonElement args)
    {
        if (!args.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(action.GetString()))
            return Result.Failure("'action' must be a non-empty string (start, status, stop or reset).");

        string value = action.GetString()!;
        if (value is not ("start" or "status" or "stop" or "reset"))
            return Result.Failure("'action' must be start, status, stop or reset.");

        if (args.TryGetProperty("minutes", out var minutes)
            && (minutes.ValueKind != JsonValueKind.Number || !minutes.TryGetInt32(out int m) || m is < 1 or > 480))
            return Result.Failure("'minutes' must be an integer between 1 and 480.");

        if (args.TryGetProperty("label", out var label) && label.ValueKind != JsonValueKind.String)
            return Result.Failure("'label' must be a string.");

        return Result.Success();
    }

    public Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        string action = args.GetProperty("action").GetString()!;
        var now = DateTimeOffset.UtcNow;

        switch (action)
        {
            case "start":
            {
                int minutes = args.TryGetProperty("minutes", out var m) ? m.GetInt32() : 25;
                string label = args.TryGetProperty("label", out var l) ? l.GetString() ?? "focus" : "focus";
                var endsAt = now.AddMinutes(minutes);
                Save(new TimerState(label, minutes, now, endsAt));
                return Task.FromResult(ToolResult.Success(
                    $"Pomodoro started: {label}, {minutes} min, ends at {endsAt:u}.",
                    new { action, label, minutes, endsAt = endsAt.ToString("O") }));
            }

            case "status":
            {
                var state = Load();
                if (state is null)
                    return Task.FromResult(ToolResult.Success(
                        "No active pomodoro. Start one with {\"action\":\"start\"}.",
                        new { action, active = false }));

                double remainingSeconds = (state.EndsAt - now).TotalSeconds;
                bool active = remainingSeconds > 0;
                string message = active
                    ? $"Pomodoro '{state.Label}': {Math.Ceiling(remainingSeconds / 60)} min left of {state.Minutes}."
                    : $"Pomodoro '{state.Label}' finished at {state.EndsAt:u}.";
                return Task.FromResult(ToolResult.Success(message, new
                {
                    action,
                    active,
                    label = state.Label,
                    minutes = state.Minutes,
                    remainingSeconds = Math.Max(0, (long)remainingSeconds)
                }));
            }

            case "stop":
            case "reset":
            {
                var state = Load();
                long elapsedSeconds = state is null ? 0 : (long)Math.Max(0, (now - state.StartedAt).TotalSeconds);
                Save(null);
                string message = state is null
                    ? "No active pomodoro to stop."
                    : $"Pomodoro '{state.Label}' stopped after {elapsedSeconds / 60} min {elapsedSeconds % 60} s.";
                return Task.FromResult(ToolResult.Success(message, new { action, elapsedSeconds }));
            }

            default:
                return Task.FromResult(ToolResult.Error($"Unknown action '{action}'."));
        }
    }

    private sealed record TimerState(string Label, int Minutes, DateTimeOffset StartedAt, DateTimeOffset EndsAt);

    private static string StatePath =>
        Environment.GetEnvironmentVariable("HARBOR_POMODORO_STATE")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".harbor", "pomodoro.json");

    private static TimerState? Load()
    {
        try
        {
            if (!File.Exists(StatePath))
                return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(StatePath));
            JsonElement root = doc.RootElement;
            string label = root.GetProperty("label").GetString() ?? "focus";
            int minutes = root.GetProperty("minutes").GetInt32();
            var started = DateTimeOffset.Parse(root.GetProperty("startedAt").GetString()!);
            var ends = DateTimeOffset.Parse(root.GetProperty("endsAt").GetString()!);
            return new TimerState(label, minutes, started, ends);
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static void Save(TimerState? state)
    {
        string dir = Path.GetDirectoryName(StatePath)!;
        Directory.CreateDirectory(dir);

        if (state is null)
        {
            if (File.Exists(StatePath))
                File.Delete(StatePath);
            return;
        }

        var payload = new
        {
            label = state.Label,
            minutes = state.Minutes,
            startedAt = state.StartedAt.ToString("O"),
            endsAt = state.EndsAt.ToString("O")
        };
        File.WriteAllText(StatePath, JsonSerializer.Serialize(payload));
    }
}
