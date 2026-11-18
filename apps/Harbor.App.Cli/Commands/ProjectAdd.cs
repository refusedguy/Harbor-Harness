using ConsoleAppFramework;
using CSharpFunctionalExtensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.App.Cli.Commands;

/// <summary>CAF handler for <c>harbor project add</c> (#2674 H12).</summary>
internal static class CafProjectCommands
{
    /// <summary>Clone a git repository into the local Harbor project folder.</summary>
    /// <param name="name">Optional directory name override.</param>
    /// <param name="dir">Optional parent directory override (must be absolute).</param>
    /// <param name="branch">Optional branch to check out.</param>
    /// <param name="dryRun">Print the resolved plan without cloning.</param>
    /// <param name="args">Subcommand tail: <c>add &lt;url&gt;</c>.</param>
    public static Task<int> Add(
        string? name = null,
        string? dir = null,
        string? branch = null,
        bool dryRun = false,
        [Argument] params string[] args)
    {
        // Flags are typed for CAF (a dash-led token is never positional); the
        // manual parser is order-insensitive, so re-appending them is enough.
        var tail = new List<string>(args);
        if (name is not null) { tail.Add("--name"); tail.Add(name); }
        if (dir is not null) { tail.Add("--dir"); tail.Add(dir); }
        if (branch is not null) { tail.Add("--branch"); tail.Add(branch); }
        if (dryRun) { tail.Add("--dry-run"); }
        return ProjectAddVerb.RunAsync(NullLogger.Instance, [.. tail], CancellationToken.None);
    }
}

/// <summary>
///     <c>harbor project add &lt;url&gt;</c>: the project-clone half of #2674
///     H12. A repository goes under <c>~/.harbor/projects</c> (or an explicit
///     parent), and an SSH clone that fails is retried over HTTPS. URL, argv
///     and plan shaping are pure; only the clone touches IO, through the same
///     drained process seam the <c>gh</c> verbs use.
/// </summary>
internal static class ProjectAddVerb
{
    /// <summary>Bound on one clone; a large repository can take minutes.</summary>
    internal static readonly TimeSpan CloneTimeout = TimeSpan.FromMinutes(10);

    internal static async Task<int> RunAsync(
        ILogger logger,
        string[] args,
        CancellationToken cancellationToken,
        string? rootDirectory = null)
    {
        Result<ProjectAddRequest> parsed = ProjectAddArgs.Parse(args);
        if (parsed.IsFailure)
        {
            Console.Error.WriteLine(parsed.Error);
            return 2;
        }

        ProjectAddRequest request = parsed.Value;
        string root = rootDirectory ?? DefaultRoot();
        ProjectClonePlan plan = ProjectClonePlan.Build(request, root);

        if (request.DryRun)
        {
            Console.Out.WriteLine(ProjectProjection.Describe(plan));
            return 0;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(plan.TargetDirectory)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine($"project add: cannot create parent directory: {ex.Message}");
            return 1;
        }

        Console.Out.WriteLine($"Cloning {plan.Url} into {plan.TargetDirectory} ...");
        // git clone reports progress on stderr and writes nothing to stdout on
        // success, so an empty stdout is not a failure for this program.
        Result<string> cloned = await GhProcessRunner
            .RunAsync("git", plan.Arguments, CloneTimeout, cancellationToken, allowEmptyStdout: true)
            .ConfigureAwait(false);

        if (cloned.IsFailure && plan.FallbackArguments is not null)
        {
            ProjectAddLog.SshCloneFailed(logger, plan.Url, cloned.Error);
            Console.Out.WriteLine($"Clone failed ({cloned.Error}); retrying over HTTPS.");
            cloned = await GhProcessRunner
                .RunAsync("git", plan.FallbackArguments, CloneTimeout, cancellationToken, allowEmptyStdout: true)
                .ConfigureAwait(false);
        }

        if (cloned.IsFailure)
        {
            Console.Error.WriteLine($"project add: {cloned.Error}");
            return 1;
        }

        ProjectAddLog.Added(logger, plan.Name, plan.TargetDirectory);
        Console.Out.WriteLine($"Added {plan.Name} at {plan.TargetDirectory}.");
        return 0;
    }

    /// <summary>The per-user projects folder, overridable in tests.</summary>
    internal static string DefaultRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".harbor",
        "projects");
}

/// <summary>Logger-message delegates for <see cref="ProjectAddVerb" />.</summary>
internal static partial class ProjectAddLog
{
    /// <summary>An SSH clone failed and the HTTPS fallback is about to run.</summary>
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Clone of {Url} failed ({Error}); retrying over HTTPS")]
    public static partial void SshCloneFailed(ILogger logger, string url, string error);

    /// <summary>A repository was cloned successfully.</summary>
    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Added project {Name} at {TargetDirectory}")]
    public static partial void Added(ILogger logger, string name, string targetDirectory);
}

/// <summary>Parsed <c>project add</c> arguments. Pure data.</summary>
internal readonly record struct ProjectAddRequest(
    string Url,
    string? Name,
    string? ParentDirectory,
    string? Branch,
    bool DryRun);

/// <summary>Pure parser for <c>project add</c>; no IO and no environment reads.</summary>
internal static class ProjectAddArgs
{
    /// <summary>The one supported project subcommand.</summary>
    internal const string Subcommand = "add";

    /// <summary>Parse the subcommand tail into a validated request.</summary>
    internal static Result<ProjectAddRequest> Parse(string[] args)
    {
        if (args.Length == 0 || !string.Equals(args[0], Subcommand, StringComparison.Ordinal))
            return Result.Failure<ProjectAddRequest>("usage: harbor project add <url> [--name <name>] [--dir <dir>] [--branch <branch>] [--dry-run]");

        string? url = null;
        string? name = null;
        string? directory = null;
        string? branch = null;
        bool dryRun = false;

        for (int i = 1; i < args.Length; i++)
        {
            string token = args[i];
            if (string.Equals(token, "--name", StringComparison.Ordinal))
            {
                if (name is not null) return Result.Failure<ProjectAddRequest>("--name was given twice.");
                Maybe<string> value = ReadValue(args, ref i);
                if (value.HasNoValue || string.IsNullOrEmpty(value.Value))
                    return Result.Failure<ProjectAddRequest>("--name needs exactly one value.");
                name = value.Value;
            }
            else if (string.Equals(token, "--dir", StringComparison.Ordinal))
            {
                if (directory is not null) return Result.Failure<ProjectAddRequest>("--dir was given twice.");
                Maybe<string> value = ReadValue(args, ref i);
                if (value.HasNoValue || string.IsNullOrEmpty(value.Value))
                    return Result.Failure<ProjectAddRequest>("--dir needs exactly one value.");
                directory = value.Value;
            }
            else if (string.Equals(token, "--branch", StringComparison.Ordinal))
            {
                if (branch is not null) return Result.Failure<ProjectAddRequest>("--branch was given twice.");
                Maybe<string> value = ReadValue(args, ref i);
                if (value.HasNoValue || string.IsNullOrEmpty(value.Value))
                    return Result.Failure<ProjectAddRequest>("--branch needs exactly one value.");
                branch = value.Value;
            }
            else if (string.Equals(token, "--dry-run", StringComparison.Ordinal))
            {
                if (dryRun) return Result.Failure<ProjectAddRequest>("--dry-run was given twice.");
                dryRun = true;
            }
            else if (token.StartsWith('-'))
            {
                return Result.Failure<ProjectAddRequest>($"unknown project add option: {token}");
            }
            else if (url is not null)
            {
                return Result.Failure<ProjectAddRequest>("project add takes exactly one URL.");
            }
            else
            {
                url = token;
            }
        }

        if (url is null || !ProjectUrls.IsSupported(url))
            return Result.Failure<ProjectAddRequest>("project add needs an https://, http://, ssh:// or git@ URL.");
        if (name is not null && !ProjectUrls.IsSafeSegment(name))
            return Result.Failure<ProjectAddRequest>("--name must be a single safe path segment.");
        if (name is null && !ProjectUrls.IsSafeSegment(ProjectUrls.RepoName(url)))
            return Result.Failure<ProjectAddRequest>("cannot derive a safe directory name from the URL; pass --name.");
        if (directory is not null && !Path.IsPathRooted(directory))
            return Result.Failure<ProjectAddRequest>("--dir must be an absolute path.");
        if (branch is not null && (branch.Length == 0 || branch.StartsWith('-')))
            return Result.Failure<ProjectAddRequest>("--branch needs a non-empty value.");

        return Result.Success(new ProjectAddRequest(url, name, directory, branch, dryRun));
    }

    /// <summary>
    ///     Read the token after a flag. A <see cref="Maybe{T}" /> makes the
    ///     absence explicit; an <c>out T?</c> plus a bool would make callers
    ///     re-check a null the signature already announced (#589's rule).
    /// </summary>
    private static Maybe<string> ReadValue(string[] args, ref int index)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith('-'))
            return Maybe<string>.None;

        return Maybe<string>.From(args[++index]);
    }
}

/// <summary>
///     Pure URL shaping for the clone: recognize the supported schemes, turn
///     an SSH URL into its HTTPS equivalent, and derive a safe directory name.
/// </summary>
internal static class ProjectUrls
{
    /// <summary>Whether the URL is one of the four forms the clone accepts.</summary>
    internal static bool IsSupported(string url) =>
        url.StartsWith("https://", StringComparison.Ordinal) ||
        url.StartsWith("http://", StringComparison.Ordinal) ||
        url.StartsWith("ssh://", StringComparison.Ordinal) ||
        url.StartsWith("git@", StringComparison.Ordinal);

    /// <summary>
    ///     HTTPS equivalent of an SSH URL, or the original URL when it is
    ///     already HTTP(S). Returns <c>null</c> when no equivalent exists.
    /// </summary>
    internal static string? ToHttps(string url)
    {
        if (url.StartsWith("https://", StringComparison.Ordinal) ||
            url.StartsWith("http://", StringComparison.Ordinal))
            return url;

        if (url.StartsWith("git@", StringComparison.Ordinal))
        {
            int colon = url.IndexOf(':');
            if (colon <= 4) return null;
            string host = url[4..colon];
            string path = url[(colon + 1)..];
            return host.Length == 0 || path.Length == 0 ? null : $"https://{host}/{path}";
        }

        if (url.StartsWith("ssh://", StringComparison.Ordinal))
        {
            string rest = url["ssh://".Length..];
            int at = rest.IndexOf('@');
            if (at >= 0) rest = rest[(at + 1)..];
            return rest.Length == 0 ? null : $"https://{rest}";
        }

        return null;
    }

    /// <summary>Last path segment, without a trailing slash or <c>.git</c>.</summary>
    internal static string RepoName(string url)
    {
        string trimmed = url.TrimEnd('/');
        int cut = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf(':'));
        string name = cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
        return name.EndsWith(".git", StringComparison.Ordinal) ? name[..^4] : name;
    }

    /// <summary>
    ///     A path segment that cannot escape its parent: non-empty, no slash,
    ///     no leading dot, and only letters, digits, dot, dash and underscore.
    /// </summary>
    internal static bool IsSafeSegment(string value)
    {
        if (value.Length == 0 || value[0] == '.' || value.Length > 100) return false;
        foreach (char character in value)
        {
            bool allowed = character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '-' or '_';
            if (!allowed) return false;
        }

        return true;
    }
}

/// <summary>
///     A fully resolved clone. Both argv lists are built once so the SSH
///     fallback can never drift from the primary command.
/// </summary>
/// <param name="Url">The requested clone URL.</param>
/// <param name="FallbackUrl">HTTPS equivalent of an SSH URL, else <c>null</c>.</param>
/// <param name="Name">Derived or explicit project name.</param>
/// <param name="TargetDirectory">Absolute clone destination.</param>
/// <param name="Arguments">argv for the primary clone (without <c>git</c>).</param>
/// <param name="FallbackArguments">argv for the HTTPS retry, or <c>null</c>.</param>
/// <param name="Branch">Requested branch, if any.</param>
internal sealed record ProjectClonePlan(
    string Url,
    string? FallbackUrl,
    string Name,
    string TargetDirectory,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<string>? FallbackArguments,
    string? Branch)
{
    /// <summary>Build a plan from a parsed request and a projects root.</summary>
    internal static ProjectClonePlan Build(ProjectAddRequest request, string rootDirectory)
    {
        string name = request.Name ?? ProjectUrls.RepoName(request.Url);
        string parent = request.ParentDirectory ?? rootDirectory;
        string target = Path.Combine(parent, name);
        string? fallbackUrl = ProjectUrls.ToHttps(request.Url);
        if (string.Equals(fallbackUrl, request.Url, StringComparison.Ordinal)) fallbackUrl = null;

        var arguments = new List<string> { "clone", "--progress" };
        if (request.Branch is not null)
        {
            arguments.Add("--branch");
            arguments.Add(request.Branch);
        }

        arguments.Add(request.Url);
        arguments.Add(target);

        IReadOnlyList<string>? fallback = null;
        if (fallbackUrl is not null)
        {
            var retry = new List<string>(arguments);
            retry[retry.Count - 2] = fallbackUrl;
            fallback = retry;
        }

        return new ProjectClonePlan(request.Url, fallbackUrl, name, target, arguments, fallback, request.Branch);
    }
}

/// <summary>Stable, user-facing text for a clone plan.</summary>
internal static class ProjectProjection
{
    /// <summary>One-line description, also used by <c>--dry-run</c>.</summary>
    internal static string Describe(ProjectClonePlan plan) =>
        plan.Branch is null
            ? $"project add: {plan.Name} <- {plan.Url} -> {plan.TargetDirectory}"
            : $"project add: {plan.Name} <- {plan.Url} (branch: {plan.Branch}) -> {plan.TargetDirectory}";
}
