using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSharpFunctionalExtensions;
using ConsoleAppFramework;

namespace Harbor.App.Cli.Commands;

/// <summary>CAF handler for the <c>harbor task</c> verb (#2674 G11).</summary>
internal static class CafTaskCommands
{
    /// <summary>Schedule, list, remove and probe cron-driven tasks.</summary>
    /// <param name="cron">Five-field cron expression for 'schedule'.</param>
    /// <param name="prompt">Prompt to fire for 'schedule'.</param>
    /// <param name="name">Stable task name for 'schedule'.</param>
    /// <param name="webhook">Optional https webhook target for 'schedule'.</param>
    /// <param name="args">Subcommand tail: schedule, list, remove or next.</param>
    public static Task<int> TaskVerb(
        string? cron = null,
        string? prompt = null,
        string? name = null,
        string? webhook = null,
        [Argument] params string[] args)
    {
        // CAF binds dash-led tokens to the typed parameters above. Re-appending
        // them makes the manual, order-insensitive parser the single authority.
        var tail = new List<string>(args);
        if (cron is not null) { tail.Add("--cron"); tail.Add(cron); }
        if (prompt is not null) { tail.Add("--prompt"); tail.Add(prompt); }
        if (name is not null) { tail.Add("--name"); tail.Add(name); }
        if (webhook is not null) { tail.Add("--webhook"); tail.Add(webhook); }
        return Task.FromResult(TaskScheduleVerb.Run([.. tail]));
    }
}

/// <summary>One parsed <c>harbor task</c> invocation.</summary>
internal sealed record TaskRequest(
    string Action,
    string Name,
    string Cron,
    string Prompt,
    string? Webhook);

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
        "       harbor task next <name>";

    internal static Result<TaskRequest> Parse(string[] args)
    {
        if (args is null || args.Length == 0)
            return Result.Failure<TaskRequest>(Usage);

        string action = args[0];
        if (action is not ("schedule" or "list" or "remove" or "next"))
            return Result.Failure<TaskRequest>($"task: unknown subcommand '{args[0]}'. {Usage}");

        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        var positional = new List<string>();
        for (int i = 1; i < args.Length; i++)
        {
            string token = args[i];
            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                if (token is not ("--cron" or "--prompt" or "--name" or "--webhook"))
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
        foreach (string flag in flags.Keys)
        {
            if (!isSchedule)
                return Result.Failure<TaskRequest>($"task {action}: flag '{flag}' is only valid for 'schedule'.");
        }

        int expectedPositional = isSchedule || isList ? 0 : 1;
        if (positional.Count != expectedPositional)
            return Result.Failure<TaskRequest>($"task {action}: expected {expectedPositional} name argument(s). {Usage}");

        if (isList)
            return Result.Success(new TaskRequest(action, string.Empty, string.Empty, string.Empty, null));

        if (isSchedule)
        {
            if (!flags.TryGetValue("--cron", out string? cron) || string.IsNullOrWhiteSpace(cron))
                return Result.Failure<TaskRequest>("task schedule: --cron is required.");
            if (!flags.TryGetValue("--prompt", out string? prompt) || string.IsNullOrWhiteSpace(prompt))
                return Result.Failure<TaskRequest>("task schedule: --prompt is required.");
            if (prompt.Length > MaxPromptLength)
                return Result.Failure<TaskRequest>($"task schedule: --prompt must be at most {MaxPromptLength} characters.");

            string name = flags.TryGetValue("--name", out string? supplied) && !string.IsNullOrWhiteSpace(supplied)
                ? supplied
                : DeriveName(prompt);
            Result nameValidation = ValidateName(name);
            if (nameValidation.IsFailure)
                return Result.Failure<TaskRequest>(nameValidation.Error);

            string? webhook = null;
            if (flags.TryGetValue("--webhook", out string? hook) && !string.IsNullOrWhiteSpace(hook))
            {
                if (!Uri.TryCreate(hook, UriKind.Absolute, out Uri? parsedUri)
                    || (parsedUri.Scheme != Uri.UriSchemeHttps && parsedUri.Scheme != Uri.UriSchemeHttp))
                    return Result.Failure<TaskRequest>("task schedule: --webhook must be an absolute http(s) URL.");
                webhook = hook;
            }

            return Result.Success(new TaskRequest(action, name, cron, prompt, webhook));
        }

        string targetName = positional[0];
        Result targetValidation = ValidateName(targetName);
        if (targetValidation.IsFailure)
            return Result.Failure<TaskRequest>(targetValidation.Error);
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

/// <summary>
///     A five-field cron expression (minute hour day-of-month month weekday)
///     with '*', single values, lists, ranges and steps. Pure: no clock is read
///     inside <see cref="Parse" />, and <see cref="NextAfter" /> is given its
///     reference time explicitly.
/// </summary>
internal sealed class CronExpression
{
    private readonly bool[] _minutes = new bool[60];
    private readonly bool[] _hours = new bool[24];
    private readonly bool[] _days = new bool[31];
    private readonly bool[] _months = new bool[12];
    private readonly bool[] _weekdays = new bool[7];

    private CronExpression()
    {
    }

    internal static Result<CronExpression> Parse(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return Result.Failure<CronExpression>("cron: empty expression.");

        string[] fields = expression.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
            return Result.Failure<CronExpression>("cron: expected 5 fields (minute hour day month weekday).");

        var cron = new CronExpression();
        Result r = Fill(fields[0], 0, 59, cron._minutes, "minute", false);
        if (r.IsFailure) return Result.Failure<CronExpression>(r.Error);
        r = Fill(fields[1], 0, 23, cron._hours, "hour", false);
        if (r.IsFailure) return Result.Failure<CronExpression>(r.Error);
        r = Fill(fields[2], 1, 31, cron._days, "day", false);
        if (r.IsFailure) return Result.Failure<CronExpression>(r.Error);
        r = Fill(fields[3], 1, 12, cron._months, "month", false);
        if (r.IsFailure) return Result.Failure<CronExpression>(r.Error);
        r = Fill(fields[4], 0, 7, cron._weekdays, "weekday", true);
        if (r.IsFailure) return Result.Failure<CronExpression>(r.Error);
        return Result.Success(cron);
    }

    /// <summary>First matching minute strictly after <paramref name="from"/>, or null within four years.</summary>
    internal DateTimeOffset? NextAfter(DateTimeOffset from)
    {
        var candidate = new DateTimeOffset(from.Year, from.Month, from.Day, from.Hour, from.Minute, 0, from.Offset)
            .AddMinutes(1);
        DateTimeOffset limit = candidate.AddYears(4);
        while (candidate <= limit)
        {
            if (_months[candidate.Month - 1]
                && _days[candidate.Day - 1]
                && _weekdays[(int)candidate.DayOfWeek]
                && _hours[candidate.Hour]
                && _minutes[candidate.Minute])
            {
                return candidate;
            }
            candidate = candidate.AddMinutes(1);
        }
        return null;
    }

    private static Result Fill(string text, int min, int max, bool[] target, string fieldName, bool normalizeSeven)
    {
        foreach (string part in text.Split(','))
        {
            if (part.Length == 0)
                return Result.Failure($"cron {fieldName}: empty list element.");

            string body = part;
            int step = 1;
            int slash = part.IndexOf('/');
            if (slash >= 0)
            {
                body = part[..slash];
                string stepText = part[(slash + 1)..];
                if (body.Length == 0
                    || !int.TryParse(stepText, NumberStyles.None, CultureInfo.InvariantCulture, out step)
                    || step < 1)
                {
                    return Result.Failure($"cron {fieldName}: invalid step '{stepText}'.");
                }
            }

            int start;
            int end;
            int dash = body.IndexOf('-');
            if (body == "*")
            {
                start = min;
                end = max;
            }
            else if (dash >= 0)
            {
                Result<int> a = ParseValue(body[..dash], fieldName);
                Result<int> b = ParseValue(body[(dash + 1)..], fieldName);
                if (a.IsFailure) return Result.Failure(a.Error);
                if (b.IsFailure) return Result.Failure(b.Error);
                start = a.Value;
                end = b.Value;
            }
            else
            {
                Result<int> v = ParseValue(body, fieldName);
                if (v.IsFailure) return Result.Failure(v.Error);
                start = end = v.Value;
            }

            if (start < min || end > max || start > end)
                return Result.Failure($"cron {fieldName}: value outside {min}..{max}.");

            for (int value = start; value <= end; value += step)
            {
                int normalized = normalizeSeven && value == 7 ? 0 : value;
                target[normalized - min] = true;
            }
        }
        return Result.Success();
    }

    private static Result<int> ParseValue(string text, string fieldName) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            ? Result.Success(value)
            : Result.Failure<int>($"cron {fieldName}: '{text}' is not a number.");
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
    internal static int Run(string[] args)
    {
        Result<TaskRequest> parsed = TaskArgs.Parse(args);
        if (parsed.IsFailure)
        {
            Console.Error.WriteLine(parsed.Error);
            return 2;
        }

        TaskRequest request = parsed.Value;
        string path = TaskStore.DefaultPath;
        Result<IReadOnlyList<ScheduledTask>> loaded = TaskStore.Load(path);
        if (loaded.IsFailure)
        {
            Console.Error.WriteLine(loaded.Error);
            return 1;
        }
        var tasks = loaded.Value.ToList();

        if (request.Action == "schedule")
        {
            Result<CronExpression> cron = CronExpression.Parse(request.Cron);
            if (cron.IsFailure)
            {
                Console.Error.WriteLine(cron.Error);
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

            DateTimeOffset? next = cron.Value.NextAfter(DateTimeOffset.UtcNow);
            Console.Out.WriteLine(
                $"Scheduled '{request.Name}' ({request.Cron}); next fire {(next?.ToString("O", CultureInfo.InvariantCulture) ?? "never")}.");
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

        Result<CronExpression> parsedCron = CronExpression.Parse(tasks[index].Cron);
        if (parsedCron.IsFailure)
        {
            Console.Error.WriteLine(parsedCron.Error);
            return 1;
        }
        DateTimeOffset? nextFire = parsedCron.Value.NextAfter(DateTimeOffset.UtcNow);
        Console.Out.WriteLine(nextFire?.ToString("O", CultureInfo.InvariantCulture) ?? "never");
        return 0;
    }
}
