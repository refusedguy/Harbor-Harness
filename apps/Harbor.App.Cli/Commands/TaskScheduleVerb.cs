using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSharpFunctionalExtensions;
using ConsoleAppFramework;
using Harbor.Tools.Builtin;

namespace Harbor.App.Cli.Commands;

/// <summary>CAF handler for the <c>harbor task</c> verb (#2674 G11).</summary>
internal static class CafTaskCommands
{
    /// <summary>Schedule, list, remove, probe cron-driven tasks and fire a webhook delivery.</summary>
    /// <param name="cron">Five-field cron expression for 'schedule'.</param>
    /// <param name="prompt">Prompt to fire for 'schedule'.</param>
    /// <param name="name">Stable task name for 'schedule'/'fire'.</param>
    /// <param name="webhook">Optional https webhook target for 'schedule'.</param>
    /// <param name="payload">Raw webhook body for 'fire'.</param>
    /// <param name="signature">Signature header for 'fire'.</param>
    /// <param name="args">Subcommand tail: schedule, list, remove, next or fire.</param>
    public static Task<int> TaskVerb(
        string? cron = null,
        string? prompt = null,
        string? name = null,
        string? webhook = null,
        string? payload = null,
        string? signature = null,
        [Argument] params string[] args)
    {
        // CAF binds dash-led tokens to the typed parameters above. Re-appending
        // them makes the manual, order-insensitive parser the single authority.
        var tail = new List<string>(args);
        if (cron is not null) { tail.Add("--cron"); tail.Add(cron); }
        if (prompt is not null) { tail.Add("--prompt"); tail.Add(prompt); }
        if (name is not null) { tail.Add("--name"); tail.Add(name); }
        if (webhook is not null) { tail.Add("--webhook"); tail.Add(webhook); }
        if (payload is not null) { tail.Add("--payload"); tail.Add(payload); }
        if (signature is not null) { tail.Add("--signature"); tail.Add(signature); }
        return Task.FromResult(TaskScheduleVerb.Run([.. tail]));
    }
}

/// <summary>One parsed <c>harbor task</c> invocation.</summary>
internal sealed record TaskRequest(
    string Action,
    string Name,
    string Cron,
    string Prompt,
    string? Webhook,
    string? Payload = null,
    string? Signature = null);

/// <summary>
///     Pure argument parsing for <c>harbor task</c>. No clock, no file and no
///     process is touched here, so every rejection is a testable value.
/// </summary>
internal static class TaskArgs
{
    internal const int MaxNameLength = 64;
    internal const int MaxPromptLength = 8000;

    internal const string Usage =
        "usage: harbor task schedule --cron '<minute hour day month weekday>' --prompt TEXT [--name NAME] [--webhook URL]\n" +
        "       harbor task list\n" +
        "       harbor task remove <name>\n" +
        "       harbor task next <name>\n" +
        "       harbor task fire <name> --payload TEXT --signature HEADER";

    internal static Result<TaskRequest> Parse(string[] args)
    {
        if (args is null || args.Length == 0)
            return Result.Failure<TaskRequest>(Usage);

        string action = args[0];
        if (action is not ("schedule" or "list" or "remove" or "next" or "fire"))
            return Result.Failure<TaskRequest>($"task: unknown subcommand '{args[0]}'. {Usage}");

        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        var positional = new List<string>();
        for (int i = 1; i < args.Length; i++)
        {
            string token = args[i];
            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                if (token is not ("--cron" or "--prompt" or "--name" or "--webhook" or "--payload" or "--signature"))
                    return Result.Failure<TaskRequest>($"task: unknown flag '{token}'. {Usage}");
                if (flags.ContainsKey(token))
                    return Result.Failure<TaskRequest>($"task: duplicate flag '{token}'.");
                if (++i >= args.Length)
                    return Result.Failure<TaskRequest>($"task: flag '{token}' needs a value.");
                flags[token] = args[i];
            }
            else
            {
                positional.Add(token);
            }
        }

        bool isSchedule = action == "schedule";
        bool isList = action == "list";
        bool isFire = action == "fire";
        foreach (string flag in flags.Keys)
        {
            bool allowed = isSchedule
                ? flag is "--cron" or "--prompt" or "--name" or "--webhook"
                : isFire && (flag is "--name" or "--payload" or "--signature");
            if (!allowed)
                return Result.Failure<TaskRequest>($"task {action}: flag '{flag}' is not valid for '{action}'.");
        }

        int expectedPositional = isSchedule || isList ? 0 : 1;
        if (positional.Count != expectedPositional)
            return Result.Failure<TaskRequest>($"task {action}: expected {expectedPositional} name argument(s). {Usage}");

        if (isList)
            return Result.Success(new TaskRequest(action, string.Empty, string.Empty, string.Empty, null));

        if (isSchedule)
        {
            string? cron = flags.GetValueOrDefault("--cron");
            if (string.IsNullOrWhiteSpace(cron))
                return Result.Failure<TaskRequest>("task schedule: --cron is required.");
            string? prompt = flags.GetValueOrDefault("--prompt");
            if (string.IsNullOrWhiteSpace(prompt))
                return Result.Failure<TaskRequest>("task schedule: --prompt is required.");
            if (prompt.Length > MaxPromptLength)
                return Result.Failure<TaskRequest>($"task schedule: --prompt must be at most {MaxPromptLength} characters.");

            string? supplied = flags.GetValueOrDefault("--name");
            string name = !string.IsNullOrWhiteSpace(supplied)
                ? supplied
                : DeriveName(prompt);
            Result nameValidation = ValidateName(name);
            if (nameValidation.IsFailure)
                return nameValidation.ConvertFailure<TaskRequest>();

            string? webhook = null;
            string? hook = flags.GetValueOrDefault("--webhook");
            if (!string.IsNullOrWhiteSpace(hook))
            {
                bool validHook = false;
                try
                {
                    var parsedUri = new Uri(hook, UriKind.Absolute);
                    validHook = parsedUri.Scheme == Uri.UriSchemeHttps || parsedUri.Scheme == Uri.UriSchemeHttp;
                }
                catch (UriFormatException)
                {
                    validHook = false;
                }
                if (!validHook)
                    return Result.Failure<TaskRequest>("task schedule: --webhook must be an absolute http(s) URL.");
                webhook = hook;
            }

            return Result.Success(new TaskRequest(action, name, cron, prompt, webhook));
        }

        if (isFire)
        {
            string? payload = flags.GetValueOrDefault("--payload");
            if (string.IsNullOrEmpty(payload))
                return Result.Failure<TaskRequest>("task fire: --payload is required.");
            string? signature = flags.GetValueOrDefault("--signature");
            if (string.IsNullOrEmpty(signature))
                return Result.Failure<TaskRequest>("task fire: --signature is required.");
            string fireName = positional[0];
            Result fireNameValidation = ValidateName(fireName);
            if (fireNameValidation.IsFailure)
                return fireNameValidation.ConvertFailure<TaskRequest>();
            return Result.Success(new TaskRequest(
                action, fireName, string.Empty, string.Empty, null, payload, signature));
        }

        string targetName = positional[0];
        Result targetValidation = ValidateName(targetName);
        if (targetValidation.IsFailure)
            return targetValidation.ConvertFailure<TaskRequest>();
        return Result.Success(new TaskRequest(action, targetName, string.Empty, string.Empty, null));
    }

    /// <summary>Build a stable, filesystem-safe default name from a prompt.</summary>
    internal static string DeriveName(string prompt)
    {
        var sb = new StringBuilder();
        foreach (string word in prompt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (sb.Length > 0)
                sb.Append('-');
            foreach (char c in word.ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
                    sb.Append(c);
                else if (c == '-' || c == '_')
                    sb.Append('-');
            }
            if (sb.Length > MaxNameLength)
                break;
        }
        string name = sb.ToString().Trim('-');
        return name.Length == 0 ? "task" : name[..Math.Min(name.Length, MaxNameLength)];
    }

    private static Result ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxNameLength)
            return Result.Failure($"task: name must be 1..{MaxNameLength} characters.");
        foreach (char c in name)
        {
            bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                || (c >= '0' && c <= '9') || c == '-' || c == '_';
            if (!ok)
                return Result.Failure("task: name may contain only letters, digits, '-' and '_'.");
        }
        return Result.Success();
    }
}

/// <summary>One persisted scheduled task.</summary>
internal sealed record ScheduledTask(
    string Name,
    string Cron,
    string Prompt,
    string? Webhook,
    string CreatedAtUtc);

/// <summary>
///     JSON persistence for scheduled tasks at <c>~/.harbor/tasks.json</c>.
///     Serialization uses Utf8JsonWriter/JsonDocument only, so it stays AOT-safe.
/// </summary>
internal static class TaskStore
{
    internal static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".harbor",
        "tasks.json");

    internal static Result<IReadOnlyList<ScheduledTask>> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Result.Success<IReadOnlyList<ScheduledTask>>(Array.Empty<ScheduledTask>());

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return Result.Failure<IReadOnlyList<ScheduledTask>>("tasks.json: expected a JSON array.");

            var tasks = new List<ScheduledTask>();
            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                    return Result.Failure<IReadOnlyList<ScheduledTask>>("tasks.json: expected object entries.");
                if (!element.TryGetProperty("name", out JsonElement name)
                    || !element.TryGetProperty("cron", out JsonElement cron)
                    || !element.TryGetProperty("prompt", out JsonElement prompt)
                    || name.ValueKind != JsonValueKind.String
                    || cron.ValueKind != JsonValueKind.String
                    || prompt.ValueKind != JsonValueKind.String)
                {
                    return Result.Failure<IReadOnlyList<ScheduledTask>>("tasks.json: entry missing name/cron/prompt.");
                }

                string? webhook = null;
                if (element.TryGetProperty("webhook", out JsonElement hook) && hook.ValueKind == JsonValueKind.String)
                    webhook = hook.GetString();

                string created = string.Empty;
                if (element.TryGetProperty("createdAtUtc", out JsonElement createdElement)
                    && createdElement.ValueKind == JsonValueKind.String)
                {
                    created = createdElement.GetString() ?? string.Empty;
                }

                tasks.Add(new ScheduledTask(name.GetString()!, cron.GetString()!, prompt.GetString()!, webhook, created));
            }
            return Result.Success<IReadOnlyList<ScheduledTask>>(tasks);
        }
        catch (JsonException ex)
        {
            return Result.Failure<IReadOnlyList<ScheduledTask>>($"tasks.json: {ex.Message}");
        }
    }

    internal static string Serialize(IReadOnlyList<ScheduledTask> tasks)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartArray();
            foreach (ScheduledTask task in tasks)
            {
                writer.WriteStartObject();
                writer.WriteString("name", task.Name);
                writer.WriteString("cron", task.Cron);
                writer.WriteString("prompt", task.Prompt);
                if (task.Webhook is not null)
                    writer.WriteString("webhook", task.Webhook);
                writer.WriteString("createdAtUtc", task.CreatedAtUtc);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        buffer.WriteByte((byte)'\n');
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    internal static Result<IReadOnlyList<ScheduledTask>> Load(string path)
    {
        if (!File.Exists(path))
            return Result.Success<IReadOnlyList<ScheduledTask>>(Array.Empty<ScheduledTask>());
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (IOException ex)
        {
            return Result.Failure<IReadOnlyList<ScheduledTask>>($"tasks.json: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result.Failure<IReadOnlyList<ScheduledTask>>($"tasks.json: {ex.Message}");
        }
    }

    internal static Result Save(string path, IReadOnlyList<ScheduledTask> tasks)
    {
        try
        {
            string? parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);
            File.WriteAllText(path, Serialize(tasks));
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Result.Failure($"tasks.json: {ex.Message}");
        }
    }
}

/// <summary>
///     HMAC-SHA256 webhook delivery proof, the verification half of the G11
///     webhook slice. Kept pure so the signing contract can be pinned without
///     standing up an HTTP listener.
/// </summary>
internal static class WebhookSignature
{
    internal const string HeaderPrefix = "sha256=";

    internal static string Sign(string secret, byte[] payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return HeaderPrefix + Convert.ToHexString(hmac.ComputeHash(payload)).ToLowerInvariant();
    }

    internal static bool Verify(string secret, byte[] payload, string? header)
    {
        if (string.IsNullOrEmpty(header))
            return false;
        string expected = Sign(secret, payload);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(header));
    }
}

/// <summary>Dispatch for <c>harbor task</c>. Only this class touches the file system.</summary>
internal static class TaskScheduleVerb
{
    /// <summary>
    ///     Resolves a schedule argument to its next fire instant (#3114, #3395).
    ///     A relative delay ("30s", "2h") or an absolute ISO 8601 timestamp is
    ///     handled by <see cref="WakeupScheduleParser" /> through
    ///     <see cref="WakeupSchedule.ComputeAt" />; anything else is a cron
    ///     expression parsed by <see cref="CronScheduleParser.Parse" /> and
    ///     advanced with <see cref="CronSchedule.NextOccurrence" />.
    ///     Internal for the schedule behavior test (InternalsVisibleTo).
    /// </summary>
    internal static Result<DateTimeOffset?> ResolveNextFire(string schedule, DateTimeOffset now)
    {
        WakeupSchedule? wakeup = WakeupScheduleParser.Parse(schedule);
        if (wakeup is not null)
            return Result.Success<DateTimeOffset?>(wakeup.Value.ComputeAt(now));

        CronSchedule? cron = CronScheduleParser.Parse(schedule);
        if (cron is null)
            return Result.Failure<DateTimeOffset?>($"cron: '{schedule}' is not a valid five-field expression.");
        return Result.Success<DateTimeOffset?>(cron.NextOccurrence(now));
    }

    /// <summary>
    ///     Check an inbound webhook proof before a task is launched (#3380).
    ///     The shared secret is a deployment setting, not part of the delivery,
    ///     so an unset secret fails closed: no delivery can be authenticated
    ///     without one. The constant-time comparison lives in
    ///     <see cref="WebhookSignature.Verify" />; this gate only decides whether
    ///     the proof is usable and turns a mismatch into a diagnosable failure.
    /// </summary>
    internal static Result VerifyWebhookDelivery(string? secret, byte[] payload, string? header)
    {
        if (string.IsNullOrEmpty(secret))
            return Result.Failure("task fire: webhook secret is not configured.");
        if (string.IsNullOrEmpty(header))
            return Result.Failure("task fire: webhook signature header is missing.");
        return WebhookSignature.Verify(secret, payload, header)
            ? Result.Success()
            : Result.Failure("task fire: invalid webhook signature.");
    }

    internal static int Run(string[] args) => Run(
        args,
        TaskStore.DefaultPath,
        Environment.GetEnvironmentVariable("HARBOR_WEBHOOK_SECRET"));

    /// <summary>
    ///     Dispatch overload with an explicit store path and webhook secret.
    ///     The behavior test uses it to drive a real delivery through a temp
    ///     store without touching the user's tasks.json or the process env.
    /// </summary>
    internal static int Run(string[] args, string path, string? webhookSecret)
    {
        Result<TaskRequest> parsed = TaskArgs.Parse(args);
        if (parsed.IsFailure)
        {
            Console.Error.WriteLine(parsed.Error);
            return 2;
        }

        TaskRequest request = parsed.Value;
        Result<IReadOnlyList<ScheduledTask>> loaded = TaskStore.Load(path);
        if (loaded.IsFailure)
        {
            Console.Error.WriteLine(loaded.Error);
            return 1;
        }
        var tasks = loaded.Value.ToList();

        if (request.Action == "schedule")
        {
            Result<DateTimeOffset?> nextFire = ResolveNextFire(request.Cron, DateTimeOffset.UtcNow);
            if (nextFire.IsFailure)
            {
                Console.Error.WriteLine(nextFire.Error);
                return 1;
            }
            if (tasks.Exists(t => t.Name == request.Name))
            {
                Console.Error.WriteLine($"task '{request.Name}' already exists.");
                return 1;
            }

            tasks.Add(new ScheduledTask(
                request.Name,
                request.Cron,
                request.Prompt,
                request.Webhook,
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)));
            Result saved = TaskStore.Save(path, tasks);
            if (saved.IsFailure)
            {
                Console.Error.WriteLine(saved.Error);
                return 1;
            }

            Console.Out.WriteLine(
                $"Scheduled '{request.Name}' ({request.Cron}); next fire {(nextFire.Value?.ToString("O", CultureInfo.InvariantCulture) ?? "never")}.");
            return 0;
        }

        if (request.Action == "list")
        {
            if (tasks.Count == 0)
            {
                Console.Out.WriteLine("No scheduled tasks.");
                return 0;
            }
            foreach (ScheduledTask task in tasks)
                Console.Out.WriteLine($"{task.Name}\t{task.Cron}\t{task.Prompt}");
            return 0;
        }

        int index = tasks.FindIndex(t => t.Name == request.Name);
        if (index < 0)
        {
            Console.Error.WriteLine($"task '{request.Name}' not found.");
            return 1;
        }

        if (request.Action == "fire")
        {
            // #3380: an inbound webhook never launches its task until the HMAC
            // proof over the exact body has been checked. Verify against the
            // raw delivery bytes, before any dispatch side effect.
            byte[] payload = Encoding.UTF8.GetBytes(request.Payload!);
            Result proof = VerifyWebhookDelivery(webhookSecret, payload, request.Signature);
            if (proof.IsFailure)
            {
                Console.Error.WriteLine(proof.Error);
                return 1;
            }

            Console.Out.WriteLine($"Fired '{request.Name}': {tasks[index].Prompt}");
            return 0;
        }

        if (request.Action == "remove")
        {
            tasks.RemoveAt(index);
            Result saved = TaskStore.Save(path, tasks);
            if (saved.IsFailure)
            {
                Console.Error.WriteLine(saved.Error);
                return 1;
            }
            Console.Out.WriteLine($"Removed '{request.Name}'.");
            return 0;
        }

        Result<DateTimeOffset?> nextFireForTask = ResolveNextFire(tasks[index].Cron, DateTimeOffset.UtcNow);
        if (nextFireForTask.IsFailure)
        {
            Console.Error.WriteLine(nextFireForTask.Error);
            return 1;
        }
        Console.Out.WriteLine(nextFireForTask.Value?.ToString("O", CultureInfo.InvariantCulture) ?? "never");
        return 0;
    }
}
