using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;

namespace Harbor.E2E.App.Avalonia.ComponentTests;

/// <summary>
///     Golden-frame plumbing for the component golden lane (#1795). A
///     golden is a TEXT snapshot of the rendered frame — the content of the
///     visual tree, written by <see cref="FrameSnapshot" /> and stored under
///     <c>tests/fixtures/golden/&lt;name&gt;.golden.txt</c> (and
///     <c>tests/fixtures/avalonia/frames/&lt;surface&gt;/&lt;scenario&gt;.golden.txt</c>
///     for the frames lane).
///
///     <para>
///         The lane used to compare SHA-256 hashes of PNG rasters, and every
///         one of those comparisons was skipped on CI: a raster is a function
///         of the machine's Skia build and font stack, so a baseline captured
///         on one host never matched another. The snapshot is a function of
///         the code alone, so the SAME baseline is checked on every host —
///         CI included.
///     </para>
///
///     <para>
///         Regeneration contract: run with <c>HARBOR_UPDATE_GOLDENS=1</c> to
///         overwrite the baselines; CI never regenerates, it compares. A
///         change of the rendered content therefore fails the build unless
///         the baseline was regenerated in the same change — the same
///         deliberate re-sign the public-API snapshot gate asks for.
///     </para>
///
///     <para>
///         Determinism contract: the frame is captured only after TWO
///         consecutive render passes produce identical PNG hashes (the
///         animations have settled). If the frame never settles within the
///         iteration budget the capture throws — a nondeterministic frame can
///         never pass a golden compare.
///     </para>
/// </summary>
internal static class GoldenFrame
{
    private static readonly Lazy<string> RepoRootLazy = new(ResolveRepoRoot);

    /// <summary>Absolute path of the repository root.</summary>
    public static string RepoRoot => RepoRootLazy.Value;

    private static bool UpdateMode =>
        string.Equals(Environment.GetEnvironmentVariable("HARBOR_UPDATE_GOLDENS"), "1", StringComparison.Ordinal);

    /// <summary>
    ///     Render <paramref name="window" /> in the headless compositor and
    ///     snapshot its content once the frame has settled (two consecutive
    ///     identical PNG hashes). Runs synchronously on the UI thread — call
    ///     through <see cref="HeadlessAvaloniaDriver.OnUIThread{T}(Func{T})" />.
    /// </summary>
    /// <param name="window">The window to capture.</param>
    /// <returns>The frame's content snapshot.</returns>
    public static string CaptureSettledSnapshot(Window window)
    {
        window.Show();

        string? previousHash = null;
        for (int i = 0; i < 60; i++)
        {
            window.UpdateLayout();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);

            var bitmap = window.CaptureRenderedFrame()
                ?? throw new InvalidOperationException("CaptureRenderedFrame returned null — headless Skia pipeline did not produce a frame.");
            byte[] png;
            using (var ms = new MemoryStream())
            {
                bitmap.Save(ms);
                png = ms.ToArray();
            }

            string hash = Convert.ToHexString(SHA256.HashData(png));
            if (hash == previousHash)
            {
                // The frame has settled: the tree below is the tree the user
                // would see, and the snapshot of it is stable.
                return FrameSnapshot.Dump(window);
            }

            previousHash = hash;
        }

        throw new TimeoutException(
            "golden frame did not settle within 60 render passes — the control keeps animating " +
            $"(last hash {previousHash}). Make the state deterministic before goldenizing.");
    }

    /// <summary>
    ///     Pin the app-level palette + theme variant so the captured frame is
    ///     independent of which host ran last: other test classes and orphaned
    ///     dispatcher continuations mutate <c>Application.Current</c> resources
    ///     between tests (dark↔light flips), which would otherwise change every
    ///     resolved brush. Mirrors <c>ThemeService.ApplyDark</c>: HDS palette
    ///     slot replaced with CatppuccinMocha, variant forced to Dark.
    ///
    ///     MUST run on the UI thread in the SAME synchronous delegate as
    ///     control construction and capture — the dispatcher cannot pump
    ///     mid-delegate, so nothing can interleave between pin and pixels.
    /// </summary>
    public static void PinDarkTheme()
    {
        var app = global::Avalonia.Application.Current
            ?? throw new InvalidOperationException("Application.Current not initialized.");
        var merged = app.Resources.MergedDictionaries;
        if (merged.Count > 1)
        {
            merged[1] = new ResourceInclude(new Uri("avares://Harbor.App.Avalonia/", UriKind.Absolute))
            {
                Source = new Uri("avares://Harbor.App.Avalonia/Themes/Hds/CatppuccinMocha.axaml", UriKind.Absolute),
            };
        }

        app.RequestedThemeVariant = global::Avalonia.Styling.ThemeVariant.Dark;
    }

    /// <summary>
    ///     Build a fixed-size window hosting <paramref name="content" /> on the
    ///     app background — the deterministic canvas for component goldens.
    /// </summary>
    public static Window CreateHostWindow(Control content, double width, double height)
    {
        var window = new Window
        {
            Width = width,
            Height = height,
            CanResize = false,
            ShowInTaskbar = false,
            Background = global::Avalonia.Application.Current?.TryFindResource("BgAppBrush", out var brush) == true && brush is IBrush b
                ? b
                : global::Avalonia.Media.Brushes.Black,
            Content = content,
        };
        return window;
    }

    /// <summary>
    ///     Compare the captured snapshot against the stored baseline at
    ///     <paramref name="relativePath" /> (a repo-relative path). Any
    ///     difference in the frame's content fails the test and names the
    ///     first differing line.
    /// </summary>
    /// <param name="relativePath">Repo-relative path of the baseline file.</param>
    /// <param name="actual">The snapshot the frame just produced.</param>
    public static void VerifySnapshot(string relativePath, string actual)
    {
        string path = Path.Combine(RepoRoot, relativePath);

        if (UpdateMode)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            return;
        }

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"golden baseline missing: {relativePath}. " +
                "Regenerate it with HARBOR_UPDATE_GOLDENS=1 (or the `goldens` workflow) and commit it in the same change.");
        }

        string expected = Normalize(File.ReadAllText(path));
        string got = Normalize(actual);
        if (string.Equals(expected, got, StringComparison.Ordinal))
        {
            return;
        }

        // The captured snapshot is written next to the baseline so a human can
        // diff the two files instead of reading the message. It is NOT a
        // baseline: the goldens workflow never commits it.
        string actualPath = path + ".actual.txt";
        File.WriteAllText(actualPath, got);

        throw new InvalidOperationException(
            $"golden frame MISMATCH for {relativePath}: the rendered content differs from the baseline.\n" +
            $"{DescribeDifference(expected, got)}\n" +
            $"  the captured snapshot was written to: {actualPath}\n" +
            "  If the change is INTENDED, regenerate the baseline (HARBOR_UPDATE_GOLDENS=1 locally, " +
            "or the `goldens` workflow on this branch) and commit it in the SAME change.");
    }

    /// <summary>
    ///     The first difference between two snapshots, with a little context —
    ///     the same shape of report the public-API snapshot gate prints.
    /// </summary>
    private static string DescribeDifference(string expected, string got)
    {
        string[] expectedLines = expected.Split('\n');
        string[] gotLines = got.Split('\n');
        int count = Math.Max(expectedLines.Length, gotLines.Length);

        for (int i = 0; i < count; i++)
        {
            string expectedLine = i < expectedLines.Length ? expectedLines[i] : "<missing>";
            string gotLine = i < gotLines.Length ? gotLines[i] : "<missing>";
            if (string.Equals(expectedLine, gotLine, StringComparison.Ordinal))
            {
                continue;
            }

            var sb = new StringBuilder();
            sb.Append("  first difference at line ").Append(i + 1).Append(":\n");
            sb.Append("    baseline: ").Append(expectedLine.TrimEnd('\r')).Append('\n');
            sb.Append("    actual:  ").Append(gotLine.TrimEnd('\r')).Append('\n');
            sb.Append("  (baseline ").Append(expectedLines.Length)
              .Append(" lines, actual ").Append(gotLines.Length).Append(" lines)");
            return sb.ToString();
        }

        return "  the snapshots differ only in their line endings.";
    }

    /// <summary>
    ///     Normalize line endings so a baseline written on Windows (CRLF) and
    ///     a snapshot produced on Linux (LF) compare equal.
    /// </summary>
    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);

    private static string ResolveRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Harbor.slnx")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("repo root (Harbor.slnx) not found from " + AppContext.BaseDirectory);
        }

        return dir.FullName;
    }
}
