using System.Text;
using CSharpFunctionalExtensions;
using Harbor.Tools.Builtin;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     Runs one local CLI process through the already-drained
///     <see cref="IProcessProxy" /> seam. The issue and PR verbs use it for
///     <c>gh</c>, and <c>project add</c> uses it for <c>git</c>, so neither
///     adds a new <c>ProcessStartInfo</c> to the product tree and all share
///     the same timeout, drain and failure semantics.
/// </summary>
internal static class GhProcessRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Execute <paramref name="arguments" /> (without the <c>gh</c> program
    ///     name) and return stdout, or a diagnostic built from stderr.
    /// </summary>
    internal static Task<Result<string>> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        RunAsync("gh", arguments, Timeout, cancellationToken);

    /// <summary>
    ///     Execute one CLI process and return stdout, or a diagnostic built
    ///     from stderr. Other local verbs (<c>project add</c>, #2674 H12)
    ///     reuse the same drain/kill semantics with their own program and
    ///     timeout rather than adding a second <c>ProcessStartInfo</c> seam.
    /// </summary>
    internal static async Task<Result<string>> RunAsync(
        string program,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool allowEmptyStdout = false)
    {
        var processes = new SystemProcessProxy();
        var spec = new ProcessSpec
        {
            FileName = program,
            Arguments = arguments,
            WorkingDirectory = Environment.CurrentDirectory,
        };

        Result<IProcessHandle> started = processes.Start(spec);
        if (started.IsFailure)
            return started.ConvertFailure<string>();

        using IProcessHandle handle = started.Value;
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var stdoutEof = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrEof = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        handle.StdoutLine += (_, e) =>
        {
            if (e.Line is null) stdoutEof.TrySetResult();
            else stdout.AppendLine(e.Line);
        };
        handle.StderrLine += (_, e) =>
        {
            if (e.Line is null) stderrEof.TrySetResult();
            else stderr.AppendLine(e.Line);
        };
        handle.BeginReading();

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            await handle.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            await Task.WhenAll(stdoutEof.Task, stderrEof.Task)
                .WaitAsync(DrainTimeout, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!handle.HasExited) handle.KillTree();
            return Result.Failure<string>($"'{program} {string.Join(' ', arguments)}' timed out.");
        }
        catch (TimeoutException)
        {
            if (!handle.HasExited) handle.KillTree();
            return Result.Failure<string>($"'{program} {string.Join(' ', arguments)}' output did not drain within the time limit.");
        }

        if (handle.ExitCode != 0)
        {
            string detail = stderr.ToString().Trim();
            return Result.Failure<string>(string.IsNullOrEmpty(detail)
                ? $"'{program} {string.Join(' ', arguments)}' exited with {handle.ExitCode}."
                : detail);
        }

        return stdout.Length == 0 && !allowEmptyStdout
            ? Result.Failure<string>($"'{program}' returned no output. Check that it is installed and available on PATH.")
            : Result.Success(stdout.ToString());
    }
}
