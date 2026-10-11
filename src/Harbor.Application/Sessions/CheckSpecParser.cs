using System.Globalization;

namespace Harbor.Application.Sessions;

/// <summary>
///     Checks-file parser (epic #42, slice S9, #397): turns the operator's
///     <c>--checks &lt;file&gt;</c> into <see cref="CheckSpec" /> values for
///     <see cref="CheckRunner" />. One non-blank, non-comment line is one
///     check, executed as <c>/bin/sh -c</c> and flagged <c>viaShell</c> in
///     <c>checks.json</c> — never silent. Names are derived
///     (<c>check-1..N</c>, 1-based over effective lines), so the file cannot
///     smuggle an unnamed check past the runner's refusal. A null path is
///     zero declared checks (legal, recorded as <c>checks: none</c>). Pure
///     file read: no git, no clock, deterministic.
/// </summary>
public static class CheckSpecParser
{
    /// <summary>
    ///     Max characters of one effective line. Over-limit is a failure
    ///     naming this cap — a check command is never silently truncated.
    /// </summary>
    public const int MaxLineChars = 8 * 1024;

    /// <summary>
    ///     Max declared checks per file. Over-limit is a failure naming this
    ///     cap — checks beyond it never run quietly, nor are they dropped.
    /// </summary>
    public const int MaxChecks = 128;

    /// <summary>
    ///     Parse <paramref name="checksFile" /> into operator-declared check
    ///     specs. Blank lines and <c>#</c> comments (after trimming) are
    ///     skipped; every other line is one shell check.
    /// </summary>
    /// <param name="checksFile">
    ///     Path to the checks file, or null for zero declared checks.
    /// </param>
    public static Result<IReadOnlyList<CheckSpec>> ParseFile(string? checksFile)
    {
        if (checksFile is null)
            return Result.Success<IReadOnlyList<CheckSpec>>(Array.Empty<CheckSpec>());
        if (!File.Exists(checksFile))
            return Result.Failure<IReadOnlyList<CheckSpec>>(
                $"Checks file '{checksFile}' does not exist.");

        string[] lines;
        try
        {
            lines = File.ReadAllLines(checksFile);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
        {
            return Result.Failure<IReadOnlyList<CheckSpec>>(
                $"Cannot read checks file '{checksFile}': {ex.Message}");
        }

        var specs = new List<CheckSpec>();
        for (int l = 0; l < lines.Length; l++)
        {
            string command = lines[l].Trim();
            if (command.Length == 0 || command[0] == '#')
                continue;
            if (command.Length > MaxLineChars)
                return Result.Failure<IReadOnlyList<CheckSpec>>(
                    $"Checks file '{checksFile}' line {(l + 1).ToString(CultureInfo.InvariantCulture)} " +
                    $"exceeds the per-line cap ({command.Length.ToString(CultureInfo.InvariantCulture)} chars, " +
                    $"cap {MaxLineChars.ToString(CultureInfo.InvariantCulture)}): refusing to truncate a check command.");
            if (command.Contains('\0'))
                return Result.Failure<IReadOnlyList<CheckSpec>>(
                    $"Checks file '{checksFile}' line {(l + 1).ToString(CultureInfo.InvariantCulture)} " +
                    "contains a null character: refusing to run a command the shell would silently cut.");
            if (specs.Count >= MaxChecks)
                return Result.Failure<IReadOnlyList<CheckSpec>>(
                    $"Checks file '{checksFile}' declares more than {MaxChecks.ToString(CultureInfo.InvariantCulture)} " +
                    "checks: refusing to run a file whose tail would never execute.");
            specs.Add(new CheckSpec(
                $"check-{(specs.Count + 1).ToString(CultureInfo.InvariantCulture)}",
                command,
                Array.Empty<string>(),
                Shell: true));
        }
        return Result.Success<IReadOnlyList<CheckSpec>>(specs);
    }
}
