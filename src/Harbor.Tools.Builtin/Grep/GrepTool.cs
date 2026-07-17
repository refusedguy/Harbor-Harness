using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Tools;
using Microsoft.Extensions.Logging;
using Result = CSharpFunctionalExtensions.Result;

namespace Harbor.Tools.Builtin;

/// <summary>
/// Fast recursive content search. Local disk: sync bulk I/O + dir prune + binary skip.
/// Parallel across files; stops at maxResults.
/// </summary>
public sealed class GrepTool : ITool
{
    private readonly ILogger<GrepTool> _logger;

    public GrepTool(ILogger<GrepTool> logger) { _logger = logger; }

    private static readonly HashSet<string> IgnoredDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", ".vs", ".idea", ".vscode",
        "node_modules", "bin", "obj", "dist", "build", "out",
        "target", "vendor", "__pycache__", ".next", ".nuxt",
        "coverage", "packages", ".turbo", ".cache"
    };

    private static readonly HashSet<string> IgnoredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico", ".bmp", ".svg",
        ".woff", ".woff2", ".ttf", ".eot", ".otf",
        ".zip", ".gz", ".7z", ".rar", ".tar", ".bz2",
        ".dll", ".exe", ".so", ".dylib", ".a", ".o", ".pdb",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx",
        ".mp3", ".mp4", ".wav", ".avi", ".mov",
        ".nupkg", ".jar", ".class", ".pyc", ".pyo"
    };

    private const int MaxFileBytes = 2 * 1024 * 1024; // 2 MiB — skip monsters
    private const int BinaryProbeBytes = 8192;

    public ToolName Name => ToolName.Create("grep");
    public string DisplayName => "Grep";
    public string Description =>
        "Search file contents with regex. Returns matching lines as file:line:content. " +
        "Recurses from path; skips VCS/build dirs and binary files.";
    public ExecutionMode ExecutionMode => ExecutionMode.Parallel;
    public string? PromptSnippet => "grep: Search file contents";

    public IReadOnlyList<string> PromptGuidelines { get; } =
    [
        "Use `grep` to find code or text by content",
        "Pattern is a regular expression",
        "Use `include` to limit search (e.g. '*.cs' or '*.{cs,ts}')"
    ];

    public JsonDocument ParameterSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "pattern":    { "type": "string",  "description": "Regular expression pattern" },
            "path":       { "type": "string",  "description": "Base directory or file (default: cwd)" },
            "include":    { "type": "string",  "description": "File name glob (e.g. '*.cs')" },
            "ignoreCase": { "type": "boolean", "description": "Case-insensitive (default: false)" },
            "maxResults": { "type": "integer", "description": "Max matches (default: 100)" }
          },
          "required": ["pattern"]
        }
        """);

    public Result ValidateArguments(JsonElement args)
    {
        if (!args.TryGetProperty("pattern", out var p) || p.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(p.GetString()))
            return Result.Failure("Missing required argument 'pattern'.");

        try
        {
            _ = new Regex(p.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure($"Invalid regex: {ex.Message}");
        }
    }

    public Task<ToolResult> ExecuteAsync(
        JsonElement args,
        ToolContext context,
        CancellationToken cancellationToken = default)
    {
        // Local FS grep is CPU/disk bound — run sync work on threadpool once,
        // not await-per-line (that was the main slowness).
        return Task.Run(() => ExecuteCore(args, cancellationToken), cancellationToken);
    }

    private ToolResult ExecuteCore(JsonElement args, CancellationToken ct)
    {
        var pattern = args.GetProperty("pattern").GetString()!;
        var path = args.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()!
            : Environment.CurrentDirectory;
        var include = args.TryGetProperty("include", out var i) && i.ValueKind == JsonValueKind.String
            ? i.GetString()
            : null;
        var ignoreCase = args.TryGetProperty("ignoreCase", out var ic)
                         && ic.ValueKind == JsonValueKind.True;
        var maxResults = args.TryGetProperty("maxResults", out var m)
                         && m.ValueKind == JsonValueKind.Number
            ? Math.Clamp(m.GetInt32(), 1, 10_000)
            : 100;

        // One-shot search: Compiled is often slower (JIT of regex). MatchTimeout = safety.
        var options = RegexOptions.CultureInvariant
                      | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        Regex regex;
        try
        {
            regex = new Regex(pattern, options, TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Invalid regex: {ex.Message}");
        }

        Regex? includeRx = null;
        if (!string.IsNullOrWhiteSpace(include))
            includeRx = GlobToRegex(include!);

        _logger.LogDebug("Grep: {Pattern} from {Path}", pattern, path);

        var results = new List<string>(Math.Min(maxResults, 128));
        var truncated = false;

        try
        {
            if (File.Exists(path))
            {
                if (IsSearchableFile(path, includeRx))
                    GrepFile(path, regex, results, maxResults, ct);
            }
            else if (Directory.Exists(path))
            {
                // Parallel over files; shared results list guarded by lock.
                // Early stop via cts when maxResults hit.
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var stop = linked.Token;

                var files = EnumerateFilesFast(path, includeRx);

                Parallel.ForEach(
                    files,
                    new ParallelOptions
                    {
                        CancellationToken = stop,
                        MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount)
                    },
                    (file, state) =>
                    {
                        if (stop.IsCancellationRequested || results.Count >= maxResults)
                        {
                            state.Stop();
                            return;
                        }

                        var local = new List<string>(4);
                        GrepFile(file, regex, local, maxResults, stop);

                        if (local.Count == 0)
                            return;

                        lock (results)
                        {
                            foreach (var line in local)
                            {
                                if (results.Count >= maxResults)
                                {
                                    truncated = true;
                                    linked.Cancel();
                                    state.Stop();
                                    break;
                                }
                                results.Add(line);
                            }

                            if (results.Count >= maxResults)
                            {
                                truncated = true;
                                linked.Cancel();
                                state.Stop();
                            }
                        }
                    });
            }
            else
            {
                return ToolResult.Error($"Path not found: {path}");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return ToolResult.Error("grep cancelled");
        }
        catch (OperationCanceledException)
        {
            // maxResults short-circuit via linked CTS — fine
        }

        if (results.Count == 0)
            return ToolResult.Success($"No matches for pattern '{pattern}' in {path}");

        _logger.LogDebug("Grep complete: {Count} matches, Truncated={Truncated}", results.Count, truncated);

        var header = truncated || results.Count >= maxResults
            ? $"Found {results.Count}+ matches (showing {results.Count}):"
            : $"Found {results.Count} matches:";

        // Join without LINQ
        var sb = new StringBuilder(results.Count * 64);
        sb.AppendLine(header);
        foreach (var t in results)
            sb.AppendLine(t);

        return ToolResult.Success(
            sb.ToString().TrimEnd(),
            new { count = results.Count, pattern, path, truncated });
    }

    private static void GrepFile(
        string file,
        Regex regex,
        List<string> sink,
        int maxResults,
        CancellationToken ct)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length == 0 || info.Length > MaxFileBytes)
                return;

            // Binary probe
            if (IsBinaryFile(file, info.Length))
                return;

            // Sync line scan — orders of magnitude faster than ReadLineAsync per line.
            // FileShare.ReadWrite so we don't fail on locked logs/editor files.
            using var stream = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024);

            var lineNum = 0;
            while (reader.ReadLine() is { } line)
            {
                ct.ThrowIfCancellationRequested();
                lineNum++;

                if (sink.Count >= maxResults)
                    return;

                // MatchTimeout on regex prevents catastrophic backtracking freezes
                if (!regex.IsMatch(line))
                    continue;

                // Cap absurdly long lines (minified)
                var display = line.Length > 400 ? string.Concat(line.AsSpan(0, 400), "…") : line;
                sink.Add($"{file}:{lineNum}: {display}");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // unreadable / encoding / race — skip
        }
    }

    /// <summary>
    /// Stack-based walk that never enters ignored directories (unlike AllDirectories + filter).
    /// </summary>
    private static IEnumerable<string> EnumerateFilesFast(string root, Regex? includeRx)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            IEnumerable<string> subdirs;
            try
            {
                subdirs = Directory.EnumerateDirectories(dir);
            }
            catch
            {
                continue;
            }

            foreach (var sub in subdirs)
            {
                var name = Path.GetFileName(sub);
                if (IgnoredDirNames.Contains(name))
                    continue;
                stack.Push(sub);
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                if (!IsSearchableFile(file, includeRx))
                    continue;
                yield return file;
            }
        }
    }

    private static bool IsSearchableFile(string file, Regex? includeRx)
    {
        var ext = Path.GetExtension(file);
        if (ext.Length > 0 && IgnoredExtensions.Contains(ext))
            return false;

        if (includeRx is not null && !includeRx.IsMatch(Path.GetFileName(file)))
            return false;

        return true;
    }

    private static bool IsBinaryFile(string path, long length)
    {
        try
        {
            var toRead = (int)Math.Min(length, BinaryProbeBytes);
            var buffer = ArrayPool<byte>.Shared.Rent(toRead);
            try
            {
                using var fs = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                    bufferSize: toRead, FileOptions.SequentialScan);
                var n = fs.Read(buffer, 0, toRead);
                // NUL byte ⇒ binary
                return buffer.AsSpan(0, n).IndexOf((byte)0) >= 0;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch
        {
            return true; // treat unreadable as skip
        }
    }

    private static Regex GlobToRegex(string glob)
    {
        // support simple "*.cs" and "*.{cs,ts}" lightly
        if (glob.Contains('{', StringComparison.Ordinal) && glob.Contains('}', StringComparison.Ordinal))
        {
            // very small brace expand: *.{cs,ts} → *.(cs|ts)
            var start = glob.IndexOf('{');
            var end = glob.IndexOf('}');
            if (start >= 0 && end > start)
            {
                var before = glob[..start];
                var after = glob[(end + 1)..];
                var alts = glob.Substring(start + 1, end - start - 1).Split(',');
                glob = before + "(" + string.Join("|", alts.Select(a => a.Trim())) + ")" + after;
            }
        }

        var pattern = "^" + Regex.Escape(glob)
            .Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal)
            // un-escape grouping we introduced for braces
            .Replace("\\(", "(", StringComparison.Ordinal)
            .Replace("\\)", ")", StringComparison.Ordinal)
            .Replace("\\|", "|", StringComparison.Ordinal)
            + "$";

        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
    }
}
