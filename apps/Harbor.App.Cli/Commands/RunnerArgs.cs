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
        var remaining = new List<string>(args.Length);

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (!TryMatch(arg, "--runner-id", out string? inlineId))
            {
                if (!TryMatch(arg, "--runner-dir", out string? inlineDirectory))
                {
                    if (!TryMatch(arg, "--amp-env", out string? inlineEnvFile))
                    {
                        remaining.Add(arg);
                        continue;
                    }

                    var envResult = ResolveValue(args, ref i, "--amp-env", inlineEnvFile);
                    if (envResult.IsFailure) return Result.Failure<RunnerInvocation>(envResult.Error);
                    envFile = envResult.Value;
                    continue;
                }

                var dirResult = ResolveValue(args, ref i, "--runner-dir", inlineDirectory);
                if (dirResult.IsFailure) return Result.Failure<RunnerInvocation>(dirResult.Error);
                directory = dirResult.Value;
                continue;
            }

            var idResult = ResolveValue(args, ref i, "--runner-id", inlineId);
            if (idResult.IsFailure) return Result.Failure<RunnerInvocation>(idResult.Error);
            id = idResult.Value;
        }

        return new RunnerInvocation(id, directory, envFile, remaining);
    }

    /// <summary>
    ///     Apply a parsed invocation to the process environment. Called once,
    ///     before host composition, so providers, hooks, git and MCP children
    ///     all inherit the overlaid values. File values win over values already
    ///     present in the process environment.
    /// </summary>
    internal static Result Apply(RunnerInvocation invocation)
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
            return Result.Success();

        if (!File.Exists(invocation.EnvFile))
            return Result.Failure($"--amp-env: file not found: '{invocation.EnvFile}'.");

        string content;
        try
        {
            content = File.ReadAllText(invocation.EnvFile);
        }
        catch (IOException ex)
        {
            return Result.Failure($"--amp-env: cannot read '{invocation.EnvFile}': {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result.Failure($"--amp-env: cannot read '{invocation.EnvFile}': {ex.Message}");
        }

        var parsed = ParseEnvFile(content);
        if (parsed.IsFailure)
            return Result.Failure($"--amp-env '{invocation.EnvFile}': {parsed.Error}");

        for (int i = 0; i < parsed.Value.Count; i++)
            Environment.SetEnvironmentVariable(parsed.Value[i].Key, parsed.Value[i].Value);

        return Result.Success();
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

    private static bool TryMatch(string arg, string flag, out string? inlineValue)
    {
        if (arg.Equals(flag, StringComparison.OrdinalIgnoreCase))
        {
            inlineValue = null;
            return true;
        }

        if (arg.StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase))
        {
            inlineValue = arg[(flag.Length + 1)..];
            return true;
        }

        inlineValue = null;
        return false;
    }

    private static Result<string> ResolveValue(string[] args, ref int index, string flag, string? inlineValue)
    {
        string? value = inlineValue;
        if (value is null)
        {
            if (index + 1 >= args.Length)
                return Result.Failure<string>($"{flag} requires a value.");
            value = args[++index];
        }

        if (value.Length == 0)
            return Result.Failure<string>($"{flag} requires a non-empty value.");
        if (value.Length > MaxValueLength)
            return Result.Failure<string>($"{flag} value exceeds {MaxValueLength} characters.");
        foreach (char c in value)
        {
            if (char.IsControl(c))
                return Result.Failure<string>($"{flag} value contains a control character.");
        }

        if (flag.Equals("--amp-env", StringComparison.OrdinalIgnoreCase) ||
            flag.Equals("--runner-dir", StringComparison.OrdinalIgnoreCase))
        {
            value = Path.GetFullPath(value);
        }

        return value;
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
}
