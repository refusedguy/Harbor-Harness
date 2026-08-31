using System.Diagnostics;

namespace Harbor.E2E.Framework;

/// <summary>Options for one <see cref="TuiDemoRecorder.RecordAsync" /> run.</summary>
public sealed record TuiDemoRecordingOptions
{
    /// <summary>Demo scene to play (<c>hero | markdown | approval | all</c> — see <c>harbor demo</c>).</summary>
    public required string Scene { get; init; }

    /// <summary>Renderer id passed through to <c>harbor demo --tui</c> (<c>ansi</c> or <c>plain</c>).</summary>
    public string TuiName { get; init; } = "ansi";

    /// <summary>Final GIF path. Relative paths resolve against the repo root (lazygit-style <c>*-compressed.gif</c>).</summary>
    public string OutputGif { get; init; } = "assets/demo/demo.gif";

    /// <summary>Frame spacing in ms; 100 ms ⇒ 10 fps GIF.</summary>
    public int FrameIntervalMs { get; init; } = 100;

    /// <summary>Wall-clock cap for the recording so a hung child cannot spin the capture loop forever.</summary>
    public int MaxSeconds { get; init; } = 30;
}

/// <summary>Result of one demo recording: the assembled GIF plus its provenance.</summary>
public sealed record TuiDemoRecording(
    string Scene,
    string GifPath,
    int FrameCount,
    long GifBytes,
    TimeSpan Duration,
    int ExitCode,
    string Assembler);

/// <summary>
///     End-to-end demo recorder: drives <c>harbor --demo</c> (scripted playback,
///     mock LLM, no API keys) inside a PTY via <see cref="TuiDriver" />, captures
///     timed PNG frames of the live TUI grid, and assembles them into a README-ready
///     GIF — palette-optimized with ffmpeg (or ImageMagick), then lossily
///     compressed with gifsicle when available.
/// </summary>
/// <remarks>
///     Requirements on the host: python3 + PTY (see <see cref="TuiDriver.IsPtyAvailable" />),
///     the CLI built (<c>dotnet build</c>), headless Chromium or ImageMagick for
///     frame rendering, and ffmpeg or ImageMagick for GIF assembly. gifsicle is
///     optional (CI installs it; without it the GIF skips the final lossy pass).
/// </remarks>
public sealed class TuiDemoRecorder
{
    /// <summary>CLI project driven by the recorder, relative to the repo root.</summary>
    public const string CliProjectRelativePath = "apps/Harbor.App.Cli/Harbor.App.Cli.csproj";

    /// <summary>Boot banner text written by <c>harbor demo</c>; frames are only captured once it is visible.</summary>
    public const string StartMarker = "harbor demo";

    /// <summary>Record one demo scene and assemble the GIF.</summary>
    public static async Task<TuiDemoRecording> RecordAsync(TuiDemoRecordingOptions options, CancellationToken ct = default)
    {
        string repoRoot = HarborAppLocator.FindRepoRoot();
        string outputGif = Path.IsPathRooted(options.OutputGif)
            ? options.OutputGif
            : Path.Combine(repoRoot, options.OutputGif.Replace('\\', Path.DirectorySeparatorChar));
        string outputDir = Path.GetDirectoryName(outputGif);
        Directory.CreateDirectory(outputDir);

        string framesDir = Path.Combine(outputDir, "frames", options.Scene);
        if (Directory.Exists(framesDir))
        {
            Directory.Delete(framesDir, recursive: true);
        }

        int maxFrames = Math.Max(4, options.MaxSeconds * 1000 / Math.Max(1, options.FrameIntervalMs));
        var stopwatch = Stopwatch.StartNew();

        await using var driver = new TuiDriver(CliProjectRelativePath, options.TuiName);
        // HARBOR_LOGLEVEL=Warning keeps the PTY stream clean; the demo banner and
        // the streamed agent output are written by the renderer, not the logger.
        var env = new Dictionary<string, string> { ["HARBOR_LOGLEVEL"] = "Warning" };
        string[] args = ["--demo", "--scene", options.Scene, "--tui", options.TuiName];

        await driver.StartAsync(args, env, ct).ConfigureAwait(false);
        IReadOnlyList<string> frames = await driver
            .CaptureFramesAsync(framesDir, options.FrameIntervalMs, maxFrames, StartMarker, ct)
            .ConfigureAwait(false);
        int exitCode = await driver.WaitForExitAsync(TimeSpan.FromSeconds(options.MaxSeconds + 20), ct).ConfigureAwait(false);
        await driver.StopAsync(ct).ConfigureAwait(false);

        stopwatch.Stop();

        if (frames.Count == 0)
        {
            throw new InvalidOperationException(
                $"Demo recording captured no frames (scene '{options.Scene}', exit code {exitCode}). " +
                "Is the CLI built and is a frame renderer (Chromium/ImageMagick) available?");
        }

        string rawGif = Path.Combine(framesDir, "raw.gif");
        string assembler = await AssembleGifAsync(frames, rawGif, options.FrameIntervalMs, ct).ConfigureAwait(false);
        await CompressAsync(rawGif, outputGif, ct).ConfigureAwait(false);

        return new TuiDemoRecording(
            options.Scene,
            outputGif,
            frames.Count,
            new FileInfo(outputGif).Length,
            stopwatch.Elapsed,
            exitCode,
            assembler);
    }

    /// <summary>
    ///     Assemble frames into a GIF. Preferred path: ffmpeg with a two-pass
    ///     palette (true-color terminal palettes); fallback: ImageMagick with
    ///     layer optimization. Returns the assembler actually used.
    /// </summary>
    private static async Task<string> AssembleGifAsync(
        IReadOnlyList<string> frames, string outputGif, int frameIntervalMs, CancellationToken ct)
    {
        double fps = 1000.0 / Math.Max(1, frameIntervalMs);
        string fpsText = fps.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        string firstFrame = frames[0];

        if (FindOnPath("ffmpeg") is not null)
        {
            // split→palettegen→paletteuse in one pass; -loop 0 = animate forever.
            await RunToolAsync("ffmpeg",
            [
                "-y",
                "-framerate", fpsText,
                "-i", Path.Combine(Path.GetDirectoryName(firstFrame)!, "frame_%04d.png"),
                "-vf", "split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse",
                "-loop", "0",
                outputGif
            ], ct).ConfigureAwait(false);
            return "ffmpeg";
        }

        if (FindOnPath("magick") is not null)
        {
            await RunToolAsync("magick",
            [
                "-delay", Math.Max(1, frameIntervalMs / 10).ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-loop", "0",
                .. frames,
                "-layers", "Optimize",
                outputGif
            ], ct).ConfigureAwait(false);
            return "magick";
        }

        throw new InvalidOperationException(
            "GIF assembly requires ffmpeg or ImageMagick (magick) on PATH. " +
            $"Frames were captured to '{Path.GetDirectoryName(firstFrame)}'.");
    }

    /// <summary>Best-effort gifsicle lossy compression; without gifsicle the raw GIF is kept as-is.</summary>
    private static async Task CompressAsync(string rawGif, string outputGif, CancellationToken ct)
    {
        if (FindOnPath("gifsicle") is null)
        {
            if (!string.Equals(rawGif, outputGif, StringComparison.Ordinal))
            {
                File.Move(rawGif, outputGif, overwrite: true);
            }

            return;
        }

        await RunToolAsync("gifsicle",
        [
            "-O3", "--lossy=80",
            "-o", outputGif,
            rawGif
        ], ct).ConfigureAwait(false);
    }

    private static async Task RunToolAsync(string fileName, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi);
        if (process is null)
        {
            throw new InvalidOperationException($"Failed to start '{fileName}'.");
        }

        string stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"'{fileName}' failed with exit code {process.ExitCode}: {stderr.Trim()}");
        }
    }

    private static string? FindOnPath(string executable)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
        {
            return null;
        }

        foreach (string dir in pathEnv.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string full = Path.Combine(dir, executable);
            if (File.Exists(full))
            {
                return full;
            }
        }

        return null;
    }
}
