using CSharpFunctionalExtensions;
using Harbor.Abstractions.Extensions;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     Which surface of <c>harbor sessions rewind</c> the caller asked for.
/// </summary>
public enum RewindMode
{
    /// <summary>Rewind messages — the existing <c>sessions rewind</c> behaviour.</summary>
    Messages,

    /// <summary>Restore only the files a checkpoint recorded as touched (#2659, U07).</summary>
    Files
}

/// <summary>
///     Parsed arguments of <c>harbor sessions rewind</c>. The <c>--files</c>
///     flag selects <see cref="RewindMode.Files"/>: restore only the paths
///     recorded as touched at the checkpoint, leaving the rest of the workspace
///     and any user edits made after it alone. Without the flag the command
///     keeps its existing message-rewind behaviour.
/// </summary>
/// <remarks>
///     This is a pure CLI-surface helper for #2659. It deliberately does not
///     touch the restore engine, write tools or dependency injection: the
///     touched-path registry is captured by the U02/U04 helpers but is not yet
///     persisted in the session log, so wiring a real restore here would be a
///     lie. <c>--messages</c> and <c>--both</c> are reserved for U08 and are
///     rejected rather than silently treated as the default.
/// </remarks>
/// <param name="Mode">The rewind surface to run.</param>
/// <param name="SessionId">Session whose checkpoint is being rewound.</param>
/// <param name="CheckpointId">Checkpoint that carries the rewind target.</param>
/// <param name="ShowHelp">Print usage instead of running a rewind.</param>
public sealed record RewindOptions(RewindMode Mode, string SessionId, string CheckpointId, bool ShowHelp)
{
    /// <summary>True when <c>--files</c> was passed.</summary>
    public bool IsFiles => Mode == RewindMode.Files;

    /// <summary>
    ///     Parse the args that follow <c>sessions rewind</c>. Positional args are
    ///     <c>&lt;session-id&gt; &lt;checkpoint-id&gt;</c> in either order relative
    ///     to flags; <c>--files</c> may appear anywhere before, between or after
    ///     them.
    /// </summary>
    /// <param name="args">Args after the <c>rewind</c> keyword.</param>
    /// <returns>The parsed options, or a failure carrying the diagnostic.</returns>
    public static Result<RewindOptions> Parse(string[] args)
    {
        Ensure.NotNull(args);

        bool files = false;
        var positional = new List<string>(2);
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a is "--help" or "-h")
                return Result.Success(new RewindOptions(RewindMode.Messages, string.Empty, string.Empty, true));

            if (a == "--files")
            {
                if (files)
                    return Result.Failure<RewindOptions>("Duplicate --files flag.");
                files = true;
                continue;
            }

            if (a is "--messages" or "--both")
                return Result.Failure<RewindOptions>(
                    $"'{a}' is reserved for a future slice (U08) and is not accepted by this command.");

            if (a.StartsWith('-'))
                return Result.Failure<RewindOptions>($"Unknown flag: {a}");

            positional.Add(a);
        }

        if (positional.Count < 2)
        {
            return Result.Failure<RewindOptions>(positional.Count == 0
                ? "Missing <session-id> and <checkpoint-id>."
                : "Missing <checkpoint-id>.");
        }

        if (positional.Count > 2)
            return Result.Failure<RewindOptions>($"Unexpected argument: {positional[2]}");

        if (string.IsNullOrWhiteSpace(positional[0]) || string.IsNullOrWhiteSpace(positional[1]))
            return Result.Failure<RewindOptions>("<session-id> and <checkpoint-id> must be non-empty.");

        return Result.Success(new RewindOptions(
            files ? RewindMode.Files : RewindMode.Messages,
            positional[0],
            positional[1],
            false));
    }

    /// <summary>Print rewind usage.</summary>
    /// <param name="output">Where usage goes.</param>
    public static void PrintUsage(TextWriter output)
    {
        Ensure.NotNull(output);
        output.WriteLine("""
                         harbor sessions rewind <session-id> <checkpoint-id> [--files] [--help]

                           <session-id>    session whose checkpoint is rewound
                           <checkpoint-id> checkpoint that carries the rewind target
                           --files         restore only the paths recorded as touched at the
                                           checkpoint; leave the rest of the workspace and any
                                           user edits made after it untouched
                           --help          show this help

                         Without --files this command keeps its existing message-rewind
                         behaviour. --messages and --both are reserved for a future slice.
                         """);
    }
}
