using System.Text;
using CSharpFunctionalExtensions;
using Harbor.Tools.Builtin;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     Runs one <c>gh</c> command through the already-drained
///     <see cref="IProcessProxy" /> seam. Both issue and PR verbs use it, so
///     neither adds a new <c>ProcessStartInfo</c> to the product tree and both
///     share the same timeout, drain and failure semantics.
/// </summary>
internal static class GhProcessRunner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Execute <paramref name="arguments" /> (without the <c>gh</c> program
    ///     name) and return stdout, or a diagnostic built from stderr.
    /// </summary>
    internal static async Task<Result<string>> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var processes = new SystemProcessProxy();
        var spec = new ProcessSpec
        {
            FileName = "gh",
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
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);
            await handle.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(stdoutEof.Task, stderrEof.Task)
                .WaitAsync(DrainTimeout, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!handle.HasExited) handle.KillTree();
            return Result.Failure<string>($"'gh {string.Join(' ', arguments)}' timed out.");
        }
        catch (TimeoutException)
        {
            if (!handle.HasExited) handle.KillTree();
            return Result.Failure<string>($"'gh {string.Join(' ', arguments)}' output did not drain within the time limit.");
        }

        if (handle.ExitCode != 0)
        {
            string detail = stderr.ToString().Trim();
            return Result.Failure<string>(string.IsNullOrEmpty(detail)
                ? $"'gh {string.Join(' ', arguments)}' exited with {handle.ExitCode}."
                : detail);
        }

        return stdout.Length == 0
            ? Result.Failure<string>("gh returned no output. Check that gh is installed and authenticated.")
            : Result.Success(stdout.ToString());
    }
}
