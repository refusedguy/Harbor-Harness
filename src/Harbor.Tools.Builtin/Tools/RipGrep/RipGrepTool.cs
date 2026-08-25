using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Tools.Builtin;
/// <summary>
///     Wraps the <c>rg</c> (ripgrep) binary for fast content search. Falls back to
///     <see cref="GrepTool" /> semantics (returns a hint) when <c>rg</c> is not on PATH.
/// </summary>
public sealed class RipGrepTool : ITool
{
    private const int DefaultMaxResults = 100;
    private const int HardMaxResults = 10_000;
    private const int MatchContextChars = 400;
    private const int TimeoutSeconds = 30;

    private static readonly string RgPath = LocateRg();

    private readonly ILogger<RipGrepTool> _logger;

    /// <summary>
    ///     Construct a <see cref="RipGrepTool" />.
    /// </summary>
    /// <param name="logger">Logger for diagnostics.</param>
    public RipGrepTool(ILogger<RipGrepTool> logger) { _logger = logger; }

    /// <inheritdoc />
    public ToolName Name => ToolName.Create("ripgrep");

    /// <inheritdoc />
    public string DisplayName => "RipGrep";

    /// <inheritdoc />
    public string Description =>
        "Fast recursive content search via the `rg` binary. Returns file:line: match content. " +
        "Respects .gitignore automatically. Falls back to an error message hinting at `grep` " +
        "if `rg` is not installed.";

    /// <inheritdoc />
    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;

    /// <inheritdoc />
    public string? PromptSnippet => "ripgrep: Fast content search via ripgrep (rg)";

    /// <inheritdoc />
    public IReadOnlyList<string> PromptGuidelines { get; } =
    [
        "Use `ripgrep` (rg) for large-repo searches — much faster than `grep`",
        "Pattern is a regex by default; set regex=false for fixed-string",
        "Pass glob='*.cs' or glob='*.{cs,ts}' to limit file types",
        "Respects .gitignore automatically (use the `grep` tool for raw searches)"
    ];

    /// <inheritdoc />
    public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""
                                                                      {
                                                                        "type": "object",
                                                                        "properties": {
                                                                          "pattern":    { "type": "string",  "description": "Search pattern (regex by default)" },
                                                                          "path":       { "type": "string",  "description": "Base directory or file (default: cwd)" },
                                                                          "glob":       { "type": "string",  "description": "File name glob (e.g. '*.cs' or '*.{cs,ts}')" },
                                                                          "ignoreCase": { "type": "boolean", "description": "Case-insensitive (default: false)" },
                                                                          "regex":      { "type": "boolean", "description": "Treat pattern as regex (default: true). false = fixed-string" },
                                                                          "maxResults": { "type": "integer", "description": "Max matches (default: 100, max: 10000)" }
                                                                        },
                                                                        "required": ["pattern"]
                                                                      }
                                                                      """);

    /// <inheritdoc />
    public Result ValidateArguments(JsonElement args)
    {
        if (!args.TryGetProperty("pattern", out var pEl)
            || pEl.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(pEl.GetString()))
            return Result.Failure("Missing or empty 'pattern'.");

        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        if (RgPath.Length == 0)
        {
            return ToolResult.Error(
                "`rg` (ripgrep) is not installed or not on PATH. " +
                "Use the `grep` builtin tool instead, or install ripgrep " +
                "(https://github.com/BurntSushi/ripgrep/releases).");
        }

        string pattern = args.GetProperty("pattern").GetString()!;
        string path = JsonArgs.GetString(args, "path") ?? Environment.CurrentDirectory;
        string? glob = JsonArgs.GetString(args, "glob");
        bool ignoreCase = JsonArgs.GetBool(args, "ignoreCase");
        // §ARCH-007: absent or weird type → default true (regex mode on).
        bool regex = JsonArgs.GetBoolOrNull(args, "regex") ?? true;
        int maxResults = JsonArgs.GetInt(args, "maxResults") is { } results
            ? Math.Clamp(results, 1, HardMaxResults)
            : DefaultMaxResults;

        var resolvedPath = ToolPaths.Resolve(path);
        if (resolvedPath.IsFailure)
            return ToolResult.Error(resolvedPath.Error);
        path = resolvedPath.Value;

        if (!File.Exists(path) && !Directory.Exists(path))
            return ToolResult.Error($"Path not found: {path}");

        var psi = new ProcessStartInfo
        {
            FileName = RgPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
            WorkingDirectory = Directory.Exists(path) ? path : Environment.CurrentDirectory
        };

        // Output format: file:line:content, no colors, no headings.
        psi.ArgumentList.Add("--color=never");
        psi.ArgumentList.Add("--no-heading");
        psi.ArgumentList.Add("--line-number");
        psi.ArgumentList.Add("--with-filename");
        psi.ArgumentList.Add($"--max-count={maxResults}");
        // Trim long lines.
        psi.ArgumentList.Add($"--max-columns={MatchContextChars}");
        psi.ArgumentList.Add("--max-columns-preview");

        if (ignoreCase) psi.ArgumentList.Add("--ignore-case");
        if (!regex) psi.ArgumentList.Add("--fixed-strings");

        if (!string.IsNullOrWhiteSpace(glob))
            psi.ArgumentList.Add($"--glob={glob}");

        // Search hidden by default? no — respect gitignore and skip hidden.
        // rg CLI syntax is `rg [OPTIONS] PATTERN [PATH...]` — PATTERN must come
        // BEFORE any path argument. Putting the path first makes rg treat the path
        // as the pattern and the pattern as a (non-existent) path, producing an
        // IO-error exit code 2 (regression caught by RipGrepToolTests).
        psi.ArgumentList.Add(pattern);
        psi.ArgumentList.Add(path);

        using var process = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                if (stdout.Length < 200_000)
                    stdout.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) stderr.AppendLine(e.Data);
        };

        _logger.LogDebug("rg {Args}", string.Join(' ', psi.ArgumentList));

        if (!process.Start())
            return ToolResult.Error("Failed to start `rg` process.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); }
            catch
            { /* ignore */
            }
            return ToolResult.Error("ripgrep cancelled");
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch
            { /* ignore */
            }
            return ToolResult.Error($"`rg` timed out after {TimeoutSeconds}s.");
        }

        // rg exit codes: 0 = matches, 1 = no matches, 2 = error.
        if (process.ExitCode == 2)
        {
            return ToolResult.Error(
                $"`rg` error (exit 2): {stderr.ToString().Trim()}");
        }

        if (process.ExitCode == 1 || stdout.Length == 0)
        {
            return ToolResult.Success(
                $"No matches for pattern '{pattern}' in {path}",
                new { count = 0, pattern, path });
        }

        string output = stdout.ToString().TrimEnd();
        int count = CountLines(output);

        if (count > maxResults) count = maxResults;

        return ToolResult.Success(
            $"Found {count} match(es) for '{pattern}' in {path}:\n\n{output}",
            new { count, pattern, path, glob, ignoreCase, regex, exitCode = process.ExitCode });
    }

    private static int CountLines(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        int count = 1;
        foreach (char c in s)
            if (c == '\n')
                count++;
        return count;
    }

    private static string LocateRg()
    {
        // Respect $RG_PATH for tests / pinned binaries.
        string? envPath = Environment.GetEnvironmentVariable("RG_PATH");
        if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath))
            return envPath;

        string name = OperatingSystem.IsWindows() ? "rg.exe" : "rg";
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                 .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim(), name);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            { /* skip malformed PATH entries */
            }
        }
        return string.Empty;
    }
}
