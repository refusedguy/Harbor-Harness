using System.Globalization;
using System.Text.Json;
using CSharpFunctionalExtensions;
using ConsoleAppFramework;

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

    private static Task<Result<string>> FetchDiffAsync(int number, CancellationToken cancellationToken) =>
        // H8 moved the drained process seam into the shared runner so this verb
        // and `harbor issue` cannot diverge on timeout/kill/drain behaviour.
        GhProcessRunner.RunAsync(
            ["pr", "diff", "--unified=0", number.ToString(CultureInfo.InvariantCulture)],
            cancellationToken);

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
