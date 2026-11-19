// RuleImporter.cs: #2680 F149. The pure conversion core of a rule-importer CLI.
// It converts a legacy editor rule file (.cursorrules, .roorules, .kilocoderules)
// into a markdown section that can be appended to AGENTS.md. The leaf is pure:
// it reads no file, resolves no path, reads no environment and knows no clock.
// AOT-safe and golden-neutral.
using CSharpFunctionalExtensions;

namespace Harbor.Tools.Builtin;

/// <summary>
///     The legacy editor-rule format a file was written in. The value is derived
///     from the file name only; no file system probe is performed.
/// </summary>
public enum LegacyRuleFormat
{
    /// <summary>The file name is not a recognised legacy rule file.</summary>
    Unknown,

    /// <summary>A Cursor <c>.cursorrules</c> file.</summary>
    CursorRules,

    /// <summary>A Roo Code <c>.roorules</c> or <c>.roorules.json</c> file.</summary>
    RooRules,

    /// <summary>A Kilo Code <c>.kilocoderules</c> file.</summary>
    KiloCodeRules,
}

/// <summary>
///     One legacy rule file to migrate. Both fields are supplied by the caller;
///     this type never touches the file system.
/// </summary>
/// <param name="FileName">The legacy file name, used to classify the format.</param>
/// <param name="Content">The raw text of the legacy rule file.</param>
public readonly record struct LegacyRuleFile(string FileName, string Content);

/// <summary>
///     A single converted rule section, ready to be appended to AGENTS.md.
/// </summary>
/// <param name="SourceFileName">The name of the legacy file this section came from.</param>
/// <param name="Format">The detected legacy format.</param>
/// <param name="Markdown">The self-contained markdown block, ending with a newline.</param>
public sealed record ImportedRuleSection(
    string SourceFileName,
    LegacyRuleFormat Format,
    string Markdown);

/// <summary>
///     Pure migration of legacy editor rule files to AGENTS.md-ready markdown
///     (#2680 F149). Stateless, deterministic and thread-safe.
/// </summary>
/// <remarks>
///     <para>
///         Conversion is deliberately conservative: the legacy body is preserved
///         verbatim apart from a UTF-8 BOM strip and CRLF/CR to LF normalisation.
///         Front matter, if any, is kept inside the block rather than interpreted.
///     </para>
///     <para>
///         Content is bounded by <see cref="MaxContentChars" /> so a runaway rule
///         file cannot bloat AGENTS.md. An oversized or blank file is rejected
///         with a <see cref="Result{T}" /> failure, never truncated.
///     </para>
/// </remarks>
public static class RuleImporter
{
    /// <summary>The maximum number of characters accepted from one rule file.</summary>
    public const int MaxContentChars = 64 * 1024;

    private const string BeginMarker = "<!-- harbor:imported-rules:";
    private const string EndMarker = "<!-- /harbor:imported-rules -->";

    /// <summary>
    ///     Classify a legacy rule file by name. The comparison is
    ///     case-insensitive and looks at the file name only.
    /// </summary>
    /// <param name="fileName">A file name or path; directories are ignored.</param>
    /// <returns>The detected format, or <see cref="LegacyRuleFormat.Unknown" />.</returns>
    public static LegacyRuleFormat DetectFormat(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return LegacyRuleFormat.Unknown;
        }

        string name = Path.GetFileName(fileName.Trim());
        if (name.Equals(".cursorrules", StringComparison.OrdinalIgnoreCase))
        {
            return LegacyRuleFormat.CursorRules;
        }

        if (name.Equals(".roorules", StringComparison.OrdinalIgnoreCase)
            || name.Equals(".roorules.json", StringComparison.OrdinalIgnoreCase))
        {
            return LegacyRuleFormat.RooRules;
        }

        return name.Equals(".kilocoderules", StringComparison.OrdinalIgnoreCase)
            ? LegacyRuleFormat.KiloCodeRules
            : LegacyRuleFormat.Unknown;
    }

    /// <summary>
    ///     Convert one legacy rule file into a marked AGENTS.md section.
    /// </summary>
    /// <param name="file">The file name and raw content to convert.</param>
    /// <returns>A section on success, or a failure describing the rejected input.</returns>
    public static Result<ImportedRuleSection> Import(LegacyRuleFile file)
    {
        if (string.IsNullOrWhiteSpace(file.FileName))
        {
            return Result.Failure<ImportedRuleSection>("A legacy rule file name is required.");
        }

        LegacyRuleFormat format = DetectFormat(file.FileName);
        if (format == LegacyRuleFormat.Unknown)
        {
            return Result.Failure<ImportedRuleSection>(
                $"'{file.FileName}' is not a recognised legacy rule file.");
        }

        if (string.IsNullOrEmpty(file.Content))
        {
            return Result.Failure<ImportedRuleSection>(
                $"'{file.FileName}' has no content to migrate.");
        }

        if (file.Content.Length > MaxContentChars)
        {
            return Result.Failure<ImportedRuleSection>(
                $"'{file.FileName}' exceeds the {MaxContentChars}-character migration limit.");
        }

        string body = Normalise(file.Content);
        if (body.Length == 0)
        {
            return Result.Failure<ImportedRuleSection>(
                $"'{file.FileName}' has no content to migrate.");
        }

        string name = Path.GetFileName(file.FileName.Trim());
        string markdown = string.Concat(
            BeginMarker,
            name,
            " -->\n",
            "## Project rules (migrated from `",
            name,
            "`)\n\n",
            body,
            "\n\n",
            EndMarker,
            "\n");

        return Result.Success(new ImportedRuleSection(name, format, markdown));
    }

    /// <summary>
    ///     Convert a batch of legacy rule files, preserving input order and
    ///     dropping duplicate file names (first occurrence wins).
    /// </summary>
    /// <param name="files">The files to convert.</param>
    /// <returns>The converted sections in input order, or the first conversion error.</returns>
    public static Result<IReadOnlyList<ImportedRuleSection>> ImportMany(
        IReadOnlyList<LegacyRuleFile> files)
    {
        if (files is null)
        {
            return Result.Failure<IReadOnlyList<ImportedRuleSection>>("The rule file list is required.");
        }

        var sections = new List<ImportedRuleSection>(files.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (LegacyRuleFile file in files)
        {
            string name = string.IsNullOrWhiteSpace(file.FileName)
                ? string.Empty
                : Path.GetFileName(file.FileName.Trim());

            if (seen.Add(name))
            {
                Result<ImportedRuleSection> converted = Import(file);
                if (converted.IsFailure)
                {
                    return converted.ConvertFailure<IReadOnlyList<ImportedRuleSection>>();
                }

                sections.Add(converted.Value);
            }
        }

        return Result.Success<IReadOnlyList<ImportedRuleSection>>(sections);
    }

    /// <summary>
    ///     Join converted sections into a single AGENTS.md appendix. The output
    ///     is deterministic: no clock, environment or file system is consulted.
    /// </summary>
    /// <param name="sections">Sections previously produced by <see cref="Import" />.</param>
    /// <returns>The markdown appendix, or an empty string when there are no sections.</returns>
    public static string ToAgentsMarkdown(IReadOnlyList<ImportedRuleSection>? sections)
    {
        if (sections is null || sections.Count == 0)
        {
            return string.Empty;
        }

        return string.Concat(
            "\n<!-- harbor:imported-rules:start -->\n",
            string.Concat(sections.Select(section => section.Markdown)),
            "<!-- harbor:imported-rules:end -->\n");
    }

    private static string Normalise(string content)
    {
        string withoutBom = content.Length > 0 && content[0] == '\uFEFF'
            ? content[1..]
            : content;

        return withoutBom
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimEnd('\n', ' ', '\t');
    }
}
