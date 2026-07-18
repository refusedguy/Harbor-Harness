// Compilation layer — tsc-subprocess TypeScript→JavaScript compiler. See IScriptCompiler.cs for layering rules.
namespace Harbor.Scripting.Compilation;

/// <summary>
///     <see cref="IScriptCompiler" /> that transpiles TypeScript to JavaScript
///     by shelling out to the <c>tsc</c> CLI tool.
/// </summary>
/// <remarks>
///     <para>
///         Use with engines that don't speak TypeScript natively (e.g. Jint).
///         Pair with <see cref="PassThroughCompiler" /> for engines that do
///         (SharpTS) — there, the compiler is a no-op and the engine handles
///         TS parsing directly.
///     </para>
///     <para>
///         <c>tsc</c> is discovered on <c>PATH</c> once per process and cached.
///         If not found, <see cref="Compile" /> returns
///         <see cref="Result" />.<see cref="Result.Failure(string)" /> with an
///         actionable message. Transpiled output is cached by source hash.
///     </para>
/// </remarks>
public sealed class TscCompiler : IScriptCompiler
{
    private readonly ILogger<TscCompiler> _logger;
    private readonly Lazy<bool> _tscAvailable;
    private readonly ConcurrentDictionary<string, string> _cache = new();

    /// <summary>
    ///     Construct a tsc-backed compiler.
    /// </summary>
    /// <param name="logger">Logger for tsc detection and invocation events.</param>
    public TscCompiler(ILogger<TscCompiler> logger)
    {
        _logger = logger;
        _tscAvailable = new Lazy<bool>(DetectTsc, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Returns <see langword="true" /> if <c>tsc</c> is available on PATH.</summary>
    public bool IsAvailable => _tscAvailable.Value;

    /// <inheritdoc />
    public Result<string> Compile(string sourceName, string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return Result.Failure<string>("Script source is empty.");
        }
        // .js sources need no transpilation — pass through.
        if (sourceName.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
            || sourceName.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase))
        {
            return Result.Success(source);
        }
        if (!IsAvailable)
        {
            return Result.Failure<string>(
                "TypeScript compilation requires `tsc` on PATH. Either install TypeScript (`npm i -g typescript`), " +
                "write the script in plain JavaScript (.js), or use the SharpTS engine which handles TypeScript natively.");
        }

        string key = HashSource(source);
        if (_cache.TryGetValue(key, out var cached))
        {
            return Result.Success(cached);
        }

        var transpiled = RunTsc(source, sourceName);
        if (transpiled.IsFailure)
        {
            return transpiled;
        }
        _cache[key] = transpiled.Value;
        return transpiled;
    }

    private static string HashSource(string source)
    {
        var bytes = Encoding.UTF8.GetBytes(source);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private bool DetectTsc()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "tsc",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null)
            {
                return false;
            }
            if (!p.WaitForExit(3000))
            {
                try { p.Kill(); } catch { /* swallow */ }
                return false;
            }
            if (p.ExitCode != 0)
            {
                return false;
            }
            _logger.LogInformation("Detected tsc: {Version}", p.StandardOutput.ReadToEnd().Trim());
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "tsc detection failed");
            return false;
        }
    }

    private Result<string> RunTsc(string source, string? sourceName)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "harbor-tsc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var inPath = Path.Combine(tempDir, "input.ts");
        var outPath = Path.Combine(tempDir, "input.js");
        try
        {
            File.WriteAllText(inPath, source);
            var psi = new ProcessStartInfo
            {
                FileName = "tsc",
                Arguments = $"--target ES2020 --module none --moduleResolution node --strict false --skipLibCheck --outDir \"{tempDir}\" \"{inPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p is null)
            {
                return Result.Failure<string>("Failed to start tsc.");
            }
            if (!p.WaitForExit(15000))
            {
                try { p.Kill(); } catch { /* swallow */ }
                return Result.Failure<string>("tsc timed out (>15s).");
            }
            if (p.ExitCode != 0)
            {
                var stderr = p.StandardError.ReadToEnd().Trim();
                var stdout = p.StandardOutput.ReadToEnd().Trim();
                return Result.Failure<string>($"tsc failed for {sourceName ?? "input.ts"}: {stderr}{(stderr.Length > 0 && stdout.Length > 0 ? "\n" : "")}{stdout}");
            }
            if (!File.Exists(outPath))
            {
                return Result.Failure<string>($"tsc produced no output for {sourceName ?? "input.ts"}.");
            }
            return Result.Success(File.ReadAllText(outPath));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* swallow */ }
        }
    }
}
