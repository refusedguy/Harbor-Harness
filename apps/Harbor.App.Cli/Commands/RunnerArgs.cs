using CSharpFunctionalExtensions;
using Harbor.Abstractions.Env;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     Runner flags from issue #2676 item 133: <c>--runner-id</c>,
///     <c>--runner-dir</c> and <c>--amp-env</c>. They are global flags, stripped
///     in <c>Program.Main</c> before verb dispatch like <c>--script</c>, so a
///     runner process can identify itself, pin its state directory and overlay
///     secrets into the environment that every child process inherits.
/// </summary>
/// <param name="Id">Value of <c>--runner-id</c>, or <see langword="null" /> when absent.</param>
/// <param name="Directory">Value of <c>--runner-dir</c>, or <see langword="null" /> when absent.</param>
/// <param name="EnvFile">Value of <c>--amp-env</c>, or <see langword="null" /> when absent.</param>
/// <param name="Remaining">The argument list with the three flags and their values removed.</param>
internal sealed record RunnerInvocation(
    string? Id,
    string? Directory,
    string? EnvFile,
    IReadOnlyList<string> Remaining);

internal static class RunnerArgs
{
    /// <summary>Identity of the runner process. Surfaced on the daemon startup line.</summary>
    internal const string RunnerIdVariable = "HARBOR_RUNNER_ID";

    /// <summary>Directory the runner keeps its state in, also applied as Harbor home.</summary>
    internal const string RunnerDirVariable = "HARBOR_RUNNER_DIR";

    private const int MaxValueLength = 512;

    /// <summary>
    ///     Parse and strip the runner flags. Both <c>--flag value</c> and
    ///     <c>--flag=value</c> forms are accepted; a flag with no value fails
    ///     closed instead of consuming the next verb. Pure over its argument.
    /// </summary>
    internal static Result<RunnerInvocation> Extract(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? id = null;
        string? directory = null;
        string? envFile = null;
        string? failure = null;
        var remaining = new List<string>(args.Length);

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            FlagMatch match = Match(arg, "--runner-id");
            string flag = "--runner-id";
            if (!match.IsMatch)
            {
                match = Match(arg, "--runner-dir");
                flag = "--runner-dir";
            }
            if (!match.IsMatch)
            {
                match = Match(arg, "--amp-env");
                flag = "--amp-env";
            }
            if (!match.IsMatch)
            {
                remaining.Add(arg);
                continue;
            }

            (bool ok, string value, string? error) = TakeValue(args, ref i, match, flag);
            if (!ok)
            {
                failure ??= error;
                continue;
            }

            if (flag == "--runner-id") id = value;
            else if (flag == "--runner-dir") directory = value;
            else envFile = value;
        }

        if (failure is not null)
            return Result.Failure<RunnerInvocation>(failure);

        return Normalize(id, "--runner-id", false)
            .Bind(normalizedId => Normalize(directory, "--runner-dir", true)
                .Bind(normalizedDirectory => Normalize(envFile, "--amp-env", true)
                    .Map(normalizedEnvFile =>
                        new RunnerInvocation(normalizedId, normalizedDirectory, normalizedEnvFile, remaining))));
    }

    /// <summary>
    ///     Apply a parsed invocation to the process environment. Called once,
    ///     before host composition, so providers, hooks, git and MCP children
    ///     all inherit the overlaid values. File values win over values already
    ///     present in the process environment.
    /// </summary>
    internal static Result<IReadOnlyList<KeyValuePair<string, string>>> Apply(RunnerInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        if (invocation.Id is not null)
            Environment.SetEnvironmentVariable(RunnerIdVariable, invocation.Id);

        if (invocation.Directory is not null)
        {
            Environment.SetEnvironmentVariable(RunnerDirVariable, invocation.Directory);
            Environment.SetEnvironmentVariable(HarborEnv.Home, invocation.Directory);
        }

        if (invocation.EnvFile is null)
            return Result.Success<IReadOnlyList<KeyValuePair<string, string>>>(
                Array.Empty<KeyValuePair<string, string>>());

        return ReadEnvFile(invocation.EnvFile).Map(pairs =>
        {
            for (int i = 0; i < pairs.Count; i++)
                Environment.SetEnvironmentVariable(pairs[i].Key, pairs[i].Value);
            return pairs;
        });
    }

    /// <summary>
    ///     Parse dotenv-style <c>KEY=VALUE</c> lines. Blank lines and comments
    ///     are ignored, an optional <c>export </c> prefix is accepted, and one
    ///     matching pair of quotes around the value is removed. A malformed
    ///     line names its number so the operator can fix the file.
    /// </summary>
    internal static Result<IReadOnlyList<KeyValuePair<string, string>>> ParseEnvFile(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var result = new List<KeyValuePair<string, string>>();
        string[] lines = content.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            if (line.StartsWith("export ", StringComparison.Ordinal))
                line = line["export ".Length..].TrimStart();

            int equals = line.IndexOf('=');
            if (equals <= 0)
                return Result.Failure<IReadOnlyList<KeyValuePair<string, string>>>(
                    $"line {i + 1}: expected KEY=VALUE.");

            string key = line[..equals].Trim();
            if (!IsValidKey(key))
                return Result.Failure<IReadOnlyList<KeyValuePair<string, string>>>(
                    $"line {i + 1}: invalid variable name '{key}'.");

            string value = line[(equals + 1)..].Trim();
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            result.Add(new KeyValuePair<string, string>(key, value));
        }

        return result;
    }

    private static Result<IReadOnlyList<KeyValuePair<string, string>>> ReadEnvFile(string path)
    {
        if (!File.Exists(path))
            return Result.Failure<IReadOnlyList<KeyValuePair<string, string>>>(
                $"--amp-env: file not found: '{path}'.");

        string content;
        try
        {
            content = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            return Result.Failure<IReadOnlyList<KeyValuePair<string, string>>>(
                $"--amp-env: cannot read '{path}': {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result.Failure<IReadOnlyList<KeyValuePair<string, string>>>(
                $"--amp-env: cannot read '{path}': {ex.Message}");
        }

        return ParseEnvFile(content).MapError(error => $"--amp-env '{path}': {error}");
    }

    private static Result<string?> Normalize(string? value, string flag, bool normalizePath)
    {
        if (value is null)
            return Result.Success<string?>(null);
        if (value.Length == 0)
            return Result.Failure<string?>($"{flag} requires a non-empty value.");
        if (value.Length > MaxValueLength)
            return Result.Failure<string?>($"{flag} value exceeds {MaxValueLength} characters.");
        foreach (char c in value)
        {
            if (char.IsControl(c))
                return Result.Failure<string?>($"{flag} value contains a control character.");
        }

        if (!normalizePath)
            return Result.Success<string?>(value);

        try
        {
            return Result.Success<string?>(Path.GetFullPath(value));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Result.Failure<string?>($"{flag} is not a valid path: {ex.Message}");
        }
    }

    private static FlagMatch Match(string arg, string flag)
    {
        if (arg.Equals(flag, StringComparison.OrdinalIgnoreCase))
            return new FlagMatch(true, false, string.Empty);

        string prefix = flag + "=";
        if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return new FlagMatch(true, true, arg[prefix.Length..]);

        return new FlagMatch(false, false, string.Empty);
    }

    private static (bool Ok, string Value, string? Error) TakeValue(
        string[] args, ref int index, FlagMatch match, string flag)
    {
        if (match.HasInlineValue)
            return (true, match.InlineValue, null);

        if (index + 1 >= args.Length)
            return (false, string.Empty, $"{flag} requires a value.");

        index++;
        return (true, args[index], null);
    }

    private static bool IsValidKey(string key)
    {
        if (key.Length == 0 || !(key[0] == '_' || char.IsAsciiLetter(key[0])))
            return false;
        for (int i = 1; i < key.Length; i++)
        {
            if (!(key[i] == '_' || char.IsAsciiLetterOrDigit(key[i])))
                return false;
        }
        return true;
    }

    private readonly record struct FlagMatch(bool IsMatch, bool HasInlineValue, string InlineValue);
}
