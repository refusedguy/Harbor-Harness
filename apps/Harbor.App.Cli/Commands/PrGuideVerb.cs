using System.Globalization;
using System.Text;
using System.Text.Json;
using CSharpFunctionalExtensions;
using ConsoleAppFramework;
using Harbor.Tools.Builtin;

namespace Harbor.App.Cli.Commands;

/// <summary>CAF handler for the <c>pr guide</c> verb (#2686 J04).</summary>
internal static class CafPrCommands
{
    /// <summary>Print a structured review guide for a pull request number.</summary>
    public static Task<int> Guide([Argument] string pullRequest) =>
        PrGuideRunner.RunAsync(pullRequest, CancellationToken.None);
}

/// <summary>
///     Fetches the pull request's diff through the already-drained
///     <see cref="IProcessProxy" /> seam and prints the projected guide as JSON.
///     The only child-process machinery lives behind that seam, so this verb
///     adds no new <c>ProcessStartInfo</c> to the product tree.
/// </summary>
internal static class PrGuideRunner
{
    private static readonly TimeSpan GhTimeout = TimeSpan.FromSeconds(60);

    internal static async Task<int> RunAsync(string? pullRequest, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pullRequest)
            || !int.TryParse(pullRequest, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            || number <= 0)
        {
            Console.Error.WriteLine("pr guide: expected a positive pull-request number.");
            return 1;
        }

        Result<string> diff = await FetchDiffAsync(number, cancellationToken).ConfigureAwait(false);
        if (diff.IsFailure)
        {
            Console.Error.WriteLine($"pr guide: {diff.Error}");
            return 1;
        }

        IReadOnlyList<PrGuideEntry> entries = PrReviewGuide.Build(diff.Value);
        if (entries.Count == 0)
        {
            Console.Error.WriteLine($"pr guide: PR #{number} has no reviewable hunks.");
            return 1;
        }

        Print(entries);
        return 0;
    }

    private static async Task<Result<string>> FetchDiffAsync(int number, CancellationToken cancellationToken)
    {
        var processes = new SystemProcessProxy();
        var spec = new ProcessSpec
        {
            FileName = "gh",
            Arguments = ["pr", "diff", "--unified=0", number.ToString(CultureInfo.InvariantCulture)],
            WorkingDirectory = Environment.CurrentDirectory
        };

        Result<IProcessHandle> started = processes.Start(spec);
        if (started.IsFailure) return Result.Failure<string>(started.Error);
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
            timeout.CancelAfter(GhTimeout);
            await handle.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(stdoutEof.Task, stderrEof.Task)
                .WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!handle.HasExited) handle.KillTree();
            return Result.Failure<string>("'gh pr diff' timed out.");
        }
        catch (TimeoutException)
        {
            if (!handle.HasExited) handle.KillTree();
            return Result.Failure<string>("'gh pr diff' output did not drain within the time limit.");
        }

        if (handle.ExitCode != 0)
        {
            string detail = stderr.ToString().Trim();
            return Result.Failure<string>(string.IsNullOrEmpty(detail)
                ? $"'gh pr diff' exited with {handle.ExitCode}."
                : detail);
        }

        return stdout.Length == 0
            ? Result.Failure<string>("'gh pr diff' returned no output. Check that gh is installed and authenticated.")
            : Result.Success(stdout.ToString());
    }

    private static void Print(IReadOnlyList<PrGuideEntry> entries)
    {
        using Stream stdout = Console.OpenStandardOutput();
        using (var writer = new Utf8JsonWriter(stdout, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartArray();
            foreach (PrGuideEntry entry in entries)
            {
                writer.WriteStartObject();
                writer.WriteString("file", entry.File);
                writer.WriteString("lines", entry.Lines);
                writer.WriteString("why", entry.Why);
                writer.WriteBoolean("lookClosely", entry.LookClosely);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        stdout.WriteByte((byte)'\n');
    }
}
