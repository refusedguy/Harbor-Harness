using System.Globalization;
using System.Text;
using Harbor.Abstractions.Extensions;
using Microsoft.Extensions.Logging;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Tools.Builtin;
/// <summary>
///     Applies a unified-diff patch to a single file. Validates context lines match before
///     applying; writes to a temp file and renames atomically. Returns a compact preview of
///     what changed.
/// </summary>
public sealed class PatchTool : ITool
{
    private const int MaxFileChars = 5_000_000;
    private const int MaxPatchLines = 5000;
    private const int MaxDiffPreviewLines = 80;

    private readonly ILogger<PatchTool> _logger;

    /// <summary>
    ///     Construct a <see cref="PatchTool" />.
    /// </summary>
    /// <param name="logger">Logger for diagnostics.</param>
    public PatchTool(ILogger<PatchTool> logger) { _logger = logger; }

    /// <inheritdoc />
    public ToolName Name => ToolName.Create("patch");

    /// <inheritdoc />
    public string DisplayName => "Patch";

    /// <inheritdoc />
    public string Description =>
        "Apply a unified-diff patch to a file. Context lines must match exactly. " +
        "Atomic: writes a temp file then renames. Returns a compact preview of changes.";

    /// <inheritdoc />
    public ExecutionMode ExecutionMode => ExecutionMode.Sequential;

    /// <inheritdoc />
    public string? PromptSnippet => "patch: Apply a unified-diff patch to a file";

    /// <inheritdoc />
    public IReadOnlyList<string> PromptGuidelines { get; } =
    [
        "Use `patch` for multi-line or multi-hunk edits produced by `git diff` or similar",
        "For single-token changes prefer `edit` (faster, simpler)",
        "Patch must include 3 lines of context around each change (unified diff convention)",
        "Hunks with mismatched context are rejected — the file is left untouched"
    ];

    /// <inheritdoc />
    public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""
                                                                      {
                                                                        "type": "object",
                                                                        "properties": {
                                                                          "path":  { "type": "string", "description": "File to patch" },
                                                                          "patch": { "type": "string", "description": "Unified diff (one or more hunks starting with @@ ... @@)" }
                                                                        },
                                                                        "required": ["path", "patch"]
                                                                      }
                                                                      """);

    /// <inheritdoc />
    public Result ValidateArguments(JsonElement args)
    {
        if (!args.TryGetProperty("path", out var pathEl)
            || pathEl.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(pathEl.GetString()))
            return Result.Failure("Missing or empty 'path'.");

        if (!args.TryGetProperty("patch", out var patchEl)
            || patchEl.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(patchEl.GetString()))
            return Result.Failure("Missing or empty 'patch'.");

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        string rawPath = args.GetProperty("path").GetString()!;
        string patch = args.GetProperty("patch").GetString()!;

        var resolvedPath = ToolPaths.Resolve(rawPath);
        if (resolvedPath.IsFailure)
            return ToolResult.Error(resolvedPath.Error);
        string path = resolvedPath.Value;

        // ROP-A Z1 п.3: the six sequential guards became one named railway —
        // a single failure exit instead of six early returns.
        var prep = await LoadPatchInputAsync(path, patch, cancellationToken).ConfigureAwait(false);
        if (prep.IsFailure)
            return ToolResult.Error(prep.Error);

        string original = prep.Value.Original;
        string[] originalLines = prep.Value.Lines;
        List<Hunk> hunks = prep.Value.Hunks;

        // B7perf: stream applied lines straight into the temp file instead of
        // materializing List<string> + one giant string.Join copy (~9 MB at
        // 5000 hunks). The historical semantics — LF-normalized output,
        // trailing-newline TrimEnd rule, "no changes" detection against the
        // raw original — are reproduced exactly by ApplyHunks + the verdict
        // logic below.
        bool originalEndsWithNewline = original.EndsWith('\n');
        bool originalHasCr = original.IndexOf('\r') >= 0;

        // Atomic write: write to temp file in the same directory, then rename.
        string? dir = Path.GetDirectoryName(path);
        string tempPath = Path.Combine(dir ?? Path.GetTempPath(), $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        try
        {
            await using var file = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.Read,
                bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var writer = new StreamWriter(file, utf8);

            var applied = ApplyHunks(originalLines, hunks, writer, originalEndsWithNewline, originalHasCr);
            if (applied.IsFailure)
            {
                TryDelete(tempPath);
                // ROP-A Z1 п.4: pure Result return instead of a mutable
                // Failure field on stateful DTO.
                return ToolResult.Error(applied.Error);
            }
            PatchApplyState apply = applied.Value;

            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);

            if (!originalEndsWithNewline && apply.TrailingArtifactLfBytes > 0)
            {
                // Reproduce the historical TrimEnd('\n'): the streamed output
                // ends with blank-line separators that the joined-string path
                // would have trimmed. Each artifact contributed exactly one
                // LF byte.
                file.SetLength(file.Length - apply.TrailingArtifactLfBytes);
            }

            if (apply.ProducedNoChanges)
                return ToolResult.Error("Patch applied but produced no changes (already applied?).");

            // File.Move with overwrite = true is atomic on POSIX same-filesystem; on Windows
            // it's atomic too as of .NET 5+ when overwriting on the same drive.
            File.Move(tempPath, path, true);
        }
        catch (Exception ex)
        {
            TryDelete(tempPath);
            // ROP-A П.13: boundary message policy lives in one handler.
            return ToolResult.Error(ToolErrors.Handler("patch", cancellationToken, failurePrefix: "Failed to write: ")(ex));
        }

        _logger.LogInformation("Patched {Path} ({Hunks} hunks)", path, hunks.Count);

        string preview = BuildPreview(hunks, MaxDiffPreviewLines);

        var msg = new StringBuilder(128);
        msg.Append("Patched ").Append(path)
            .Append(": ").Append(hunks.Count).Append(" hunk(s) applied");
        if (preview.Length > 0)
            msg.Append("\n\n").Append(preview);

        return ToolResult.Success(
            msg.ToString(),
            new { path, hunks = hunks.Count });
    }

    /// <summary>
    ///     Outcome of <see cref="ApplyHunks" /> (ROP-A Z1 п.4: failures travel
    ///     through <c>Result</c>, not through a mutable field).
    /// </summary>
    private sealed class PatchApplyState
    {
        /// <summary>True when the patched output is byte-identical to the original (historical "already applied?" error).</summary>
        public bool ProducedNoChanges;

        /// <summary>
        ///     Trailing LF bytes that the historical <c>TrimEnd('\n')</c> would
        ///     have removed from the joined output (blank-line artifacts of the
        ///     no-trailing-newline rule). Zero when nothing needs trimming.
        /// </summary>
        public long TrailingArtifactLfBytes;
    }

    /// <summary>
    ///     Walks hunks in order, validates context, and streams output lines
    ///     straight into <paramref name="output" /> — byte-identical to the
    ///     historical <c>string.Join("\n", applied)</c> (first line has no
    ///     leading separator). Simultaneously performs a positional equality
    ///     check between the emitted line sequence and
    ///     <paramref name="originalLines" /> so the "produced no changes"
    ///     verdict matches the old <c>updated == original</c> string
    ///     comparison without materializing either string:
    ///     <list type="bullet">
    ///         <item>
    ///             The old comparison compared the LF-normalized join against
    ///             the RAW original — for files containing <c>\r</c> it could
    ///             never be equal, hence the <paramref name="originalHasCr" />
    ///             gate.
    ///         </item>
    ///         <item>
    ///             When the original lacked a trailing newline, the old code
    ///             applied <c>TrimEnd('\n')</c> before comparing; trailing
    ///             blank-line emissions beyond the original's length are
    ///             therefore exempt from the mismatch check and are reported
    ///             in <see cref="PatchApplyState.TrailingArtifactLfBytes" />
    ///             so the caller can truncate them from the temp file.
    ///         </item>
    ///     </list>
    /// </summary>
    private static Result<PatchApplyState> ApplyHunks(
        string[] originalLines,
        List<Hunk> hunks,
        TextWriter output,
        bool originalEndsWithNewline,
        bool originalHasCr)
    {
        var state = new PatchApplyState();
        int originalCursor = 0;

        // ── Positional-equality bookkeeping ──
        bool mismatch = false;
        int emitted = 0;         // total lines emitted so far (incl. blank lines)
        int pendingEmpties = 0;  // consecutive blank emissions not yet judged (always a suffix run)
        bool wroteAny = false;

        void FlushPendingVerdict()
        {
            for (int k = pendingEmpties; k > 0; k--)
            {
                int p = emitted - k;
                mismatch |= p >= originalLines.Length || originalLines[p].Length != 0;
            }
            pendingEmpties = 0;
        }

        void Emit(string line)
        {
            if (wroteAny)
            {
                output.Write('\n');
            }
            wroteAny = true;

            if (line.Length == 0)
            {
                pendingEmpties++;
                emitted++;
                return;
            }

            FlushPendingVerdict();
            mismatch |= emitted >= originalLines.Length || originalLines[emitted] != line;
            emitted++;
            output.Write(line);
        }

        foreach (var h in hunks)
        {
            // The hunk header is 1-based; we use 0-based internally.
            int targetStart = h.OldStart - 1;

            // If header is 0 or negative, fall back to "find best window".
            if (targetStart < 0)
                targetStart = originalCursor;

            // Allow some slack: if exact position doesn't match, search ±N lines.
            int? resolvedStart = ResolveHunkStart(originalLines, h, targetStart);
            if (resolvedStart is null)
            {
                return Result.Failure<PatchApplyState>(
                    $"Hunk at line {h.OldStart} did not match (context mismatch). " +
                    "File left untouched.");
            }

            // Copy unchanged lines up to hunk start.
            while (originalCursor < resolvedStart.Value)
            {
                Emit(originalLines[originalCursor]);
                originalCursor++;
            }

            // Apply the hunk: walk its lines.
            foreach (var hl in h.Lines)
            {
                switch (hl.Type)
                {
                    case HunkLineType.Context:
                        if (originalCursor >= originalLines.Length
                            || originalLines[originalCursor] != hl.Text)
                        {
                            return Result.Failure<PatchApplyState>(
                                $"Context line did not match at file line {originalCursor + 1}: " +
                                $"expected «{hl.Text}». File left untouched.");
                        }
                        Emit(originalLines[originalCursor]);
                        originalCursor++;
                        break;
                    case HunkLineType.Deletion:
                        if (originalCursor >= originalLines.Length
                            || originalLines[originalCursor] != hl.Text)
                        {
                            return Result.Failure<PatchApplyState>(
                                $"Deletion line did not match at file line {originalCursor + 1}: " +
                                $"expected «{hl.Text}». File left untouched.");
                        }
                        originalCursor++;
                        break;
                    case HunkLineType.Addition:
                        Emit(hl.Text);
                        break;
                }
            }
        }

        // Copy any tail lines after the last hunk.
        while (originalCursor < originalLines.Length)
        {
            Emit(originalLines[originalCursor]);
            originalCursor++;
        }

        // ── Final verdict ──
        int artifacts = 0;
        for (int k = pendingEmpties; k > 0; k--)
        {
            int p = emitted - k;
            if (!originalEndsWithNewline && p >= originalLines.Length)
            {
                // TrimEnd('\n') artifact: beyond the original sequence and the
                // original had no trailing newline. Exempt from the mismatch
                // check; its separator byte gets truncated from the temp file.
                artifacts++;
                continue;
            }

            mismatch |= p >= originalLines.Length || originalLines[p].Length != 0;
        }

        bool sequencesEqual = !mismatch && emitted - artifacts == originalLines.Length;
        state.ProducedNoChanges = sequencesEqual && !originalHasCr;
        // Each artifact wrote exactly one separator byte — except when the
        // artifact run includes the very first output line (the first line is
        // written without a leading separator).
        state.TrailingArtifactLfBytes = artifacts > 0 ? artifacts - (emitted == artifacts ? 1 : 0) : 0;

        return Result.Success(state);
    }

    /// <summary>
    ///     ROP-A Z1 п.3: prelude railway — existence guards, capped read and
    ///     hunk parsing compose into one result; FormatException stops being a
    ///     cross-method control-flow channel.
    /// </summary>
    private sealed record PatchInput(string Original, string[] Lines, List<Hunk> Hunks);

    private async Task<Result<PatchInput>> LoadPatchInputAsync(string path, string patch, CancellationToken ct)
    {
        Result<string> exists =
            Result.Success(path)
                .Ensure(static p => !Directory.Exists(p), p => $"Path is a directory: {p}")
                .Ensure(static p => File.Exists(p), p => $"File not found: {p}");
        if (exists.IsFailure)
            return Result.Failure<PatchInput>(exists.Error);

        Result<string> read = await Result.Try(
                () => File.ReadAllTextAsync(path, Encoding.UTF8, ct),
                ex => $"Failed to read: {ex.Message}")
            .ConfigureAwait(false);
        if (read.IsFailure)
            return Result.Failure<PatchInput>(read.Error);

        Result<string> sized =
            read.Ensure(s => s.Length <= MaxFileChars,
                s => $"File too large ({s.Length} chars; max {MaxFileChars}).");
        if (sized.IsFailure)
            return Result.Failure<PatchInput>(sized.Error);

        Result<List<Hunk>> parsed = Result.Try(
                () => ParseHunks(patch),
                ex => $"Failed to parse patch: {ex.Message}");
        if (parsed.IsFailure)
            return Result.Failure<PatchInput>(parsed.Error);

        return Result.Success(new PatchInput(sized.Value, SplitLines(sized.Value), parsed.Value))
            .Ensure(pi => pi.Hunks.Count > 0,
                "Patch contains no hunks (no @@ ... @@ headers found).")
            .Ensure(pi => pi.Hunks.Count <= MaxPatchLines,
                pi => $"Patch has too many hunks ({pi.Hunks.Count}; max {MaxPatchLines}).");
    }

    private static int? ResolveHunkStart(string[] lines, Hunk h, int targetStart)
    {
        // Try the targetStart as-is, then ±1, ±2, ±3 for whitespace/line-number drift.
        for (int delta = 0; delta <= 3; delta++)
        {
            foreach (int sign in delta == 0 ? new[] { 0 } : new[] { -1, 1 })
            {
                int candidate = targetStart + sign * delta;
                if (candidate < 0 || candidate >= lines.Length) continue;
                if (ContextMatches(lines, candidate, h))
                    return candidate;
            }
        }
        return null;
    }

    private static bool ContextMatches(string[] lines, int start, Hunk h)
    {
        int cursor = start;
        foreach (var hl in h.Lines)
        {
            if (hl.Type == HunkLineType.Addition)
                continue;
            if (cursor >= lines.Length) return false;
            if (lines[cursor] != hl.Text) return false;
            cursor++;
        }
        return true;
    }

    private static List<Hunk> ParseHunks(string patch)
    {
        var hunks = new List<Hunk>();
        string[] lines = patch.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        int i = 0;
        // Skip past the diff header lines (origin and destination file markers)
        // until we reach the first hunk header line beginning with double-at sign.
        while (i < lines.Length && !lines[i].StartsWith("@@", StringComparison.Ordinal))
        {
            i++;
        }

        while (i < lines.Length)
        {
            if (!lines[i].StartsWith("@@", StringComparison.Ordinal))
            {
                i++;
                continue;
            }

            // Parse "@@ -oldStart,oldCount +newStart,newCount @@"
            var header = ParseHunkHeader(lines[i]);
            i++;

            var hunkLines = new List<HunkLine>(header.OldCount + header.NewCount);
            int seenOld = 0, seenNew = 0;

            while (i < lines.Length
                   && (seenOld < header.OldCount || seenNew < header.NewCount)
                   && !lines[i].StartsWith("@@", StringComparison.Ordinal))
            {
                string line = lines[i];
                if (line.Length == 0)
                {
                    // Empty line in the patch is treated as a blank context line.
                    hunkLines.Add(new HunkLine(HunkLineType.Context, string.Empty));
                    seenOld++;
                    seenNew++;
                    i++;
                    continue;
                }

                char type = line[0];
                string text = line[1..];
                switch (type)
                {
                    case ' ':
                        hunkLines.Add(new HunkLine(HunkLineType.Context, text));
                        seenOld++;
                        seenNew++;
                        break;
                    case '-':
                        hunkLines.Add(new HunkLine(HunkLineType.Deletion, text));
                        seenOld++;
                        break;
                    case '+':
                        hunkLines.Add(new HunkLine(HunkLineType.Addition, text));
                        seenNew++;
                        break;
                    case '\\':
                        // "\ No newline at end of file" — ignore, we handle newlines ourselves.
                        break;
                    default:
                        // Unknown line — stop hunk.
                        i = lines.Length;
                        break;
                }
                i++;
            }

            hunks.Add(new Hunk(header.OldStart, header.OldCount, header.NewStart, header.NewCount, hunkLines));
        }

        return hunks;
    }

    private static HunkHeader ParseHunkHeader(string line)
    {
        // @@ -10,7 +10,8 @@ context
        int atAt = line.IndexOf("@@", 2, StringComparison.Ordinal);
        string body = atAt > 0 ? line[3..atAt].Trim() : line[3..].Trim();

        // "-10,7 +10,8"
        int plusIdx = body.IndexOf('+');
        if (plusIdx <= 0)
            throw new FormatException($"Malformed hunk header: {line}");

        string oldPart = body[..plusIdx].Trim();
        string newPart = body[plusIdx..].Trim();

        (int oldStart, int oldCount) = ParseRange(oldPart);
        (int newStart, int newCount) = ParseRange(newPart);

        return new HunkHeader(oldStart, oldCount, newStart, newCount);
    }

    private static (int start, int count) ParseRange(string s)
    {
        // s like "-10,7" or "-10"
        if (s.StartsWith('-')) s = s[1..];
        else if (s.StartsWith('+')) s = s[1..];

        int comma = s.IndexOf(',');
        if (comma < 0)
            return (int.Parse(s, CultureInfo.InvariantCulture), 1);

        int start = int.Parse(s.AsSpan(0, comma), CultureInfo.InvariantCulture);
        int count = int.Parse(s.AsSpan(comma + 1), CultureInfo.InvariantCulture);
        return (start, count);
    }

    private static string BuildPreview(List<Hunk> hunks, int maxLines)
    {
        using var sb = StringBuilderPool.Rent(1024);
        var b = sb.Builder;
        b.Append("Patch preview:");
        int lines = 0;
        foreach (var h in hunks)
        {
            if (lines >= maxLines) break;
            b.Append("\n@@ -").Append(h.OldStart).Append(',').Append(h.OldCount)
                .Append(" +").Append(h.NewStart).Append(',').Append(h.NewCount).Append(" @@");
            lines++;
            foreach (var hl in h.Lines)
            {
                if (lines >= maxLines)
                {
                    b.Append("\n… preview truncated");
                    break;
                }
                char prefix = hl.Type switch
                {
                    HunkLineType.Context => ' ',
                    HunkLineType.Deletion => '-',
                    HunkLineType.Addition => '+',
                    _ => ' '
                };
                b.Append('\n').Append(prefix).Append(' ').Append(hl.Text);
                lines++;
            }
        }
        return b.ToString();
    }

    private static string[] SplitLines(string text)
    {
        // Keep the same convention as EditTool: split on \n, treating \r\n as \n.
        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        { /* best effort */
        }
    }

    private enum HunkLineType { Context, Deletion, Addition }

    private readonly record struct HunkLine(HunkLineType Type, string Text);

    private sealed record Hunk(
        int OldStart,
        int OldCount,
        int NewStart,
        int NewCount,
        IReadOnlyList<HunkLine> Lines);

    private readonly record struct HunkHeader(int OldStart, int OldCount, int NewStart, int NewCount);
}
