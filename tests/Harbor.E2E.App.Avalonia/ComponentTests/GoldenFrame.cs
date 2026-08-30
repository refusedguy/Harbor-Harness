using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;

namespace Harbor.E2E.App.Avalonia.ComponentTests;

/// <summary>
///     Golden-frame plumbing for screenshot-diff tests (sprint Testing Strategy
///     Z.2). A golden is a SHA-256 hash of the captured PNG bytes plus the PNG
///     itself, stored under <c>tests/fixtures/golden/&lt;name&gt;.golden.png</c>
///     (+ <c>.sha256</c>). Any pixel-level diff from the baseline changes the
///     PNG bytes → changes the hash → the test FAILS. "File exists" is never
///     an assertion: a missing or empty fixture is an error, not a pass.
///
///     Regeneration contract: run with <c>HARBOR_UPDATE_GOLDENS=1</c> to
///     overwrite baselines; CI never regenerates.
///
///     Determinism contract: the frame is captured only after TWO consecutive
///     render passes produce identical PNG hashes (animations settled). If the
///     frame never settles within the iteration budget the capture throws — 
///     a nondeterministic frame can never pass a golden compare.
/// </summary>
internal static class GoldenFrame
{
    private static readonly Lazy<string> FixtureDirLazy = new(ResolveFixtureDir);

    /// <summary>Absolute path of <c>tests/fixtures/golden</c>.</summary>
    public static string FixtureDir => FixtureDirLazy.Value;

    private static bool UpdateMode =>
        string.Equals(Environment.GetEnvironmentVariable("HARBOR_UPDATE_GOLDENS"), "1", StringComparison.Ordinal);

    /// <summary>
    ///     Render <paramref name="window" /> in the headless compositor and
    ///     capture its pixels once the frame has settled (two consecutive
    ///     identical PNG hashes). Runs synchronously on the UI thread — call
    ///     through <see cref="HeadlessAvaloniaDriver.OnUIThread{T}(Func{T})" />.
    /// </summary>
    public static (byte[] Png, string Sha256) CaptureSettledFrame(Window window)
    {
        window.Show();

        string? previousHash = null;
        byte[] previousPng = [];
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
                return (png, hash);
            }

            previousHash = hash;
            previousPng = png;
        }

        throw new TimeoutException(
            "golden frame did not settle within 60 render passes — the control keeps animating " +
            $"(last hash {previousHash}). Make the state deterministic before goldenizing.");
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
            SystemDecorations = Avalonia.Controls.SystemDecorations.None,
            Background = Avalonia.Application.Current?.TryFindResource("BgAppBrush", out var brush) == true && brush is IBrush b
                ? b
                : Brushes.Black,
            Content = content,
        };
        return window;
    }

    /// <summary>
    ///     Compare <paramref name="png" />/<paramref name="sha256" /> against
    ///     the stored baseline for <paramref name="testName" />. Any pixel
    ///     difference (→ different PNG bytes → different hash) fails the test.
    /// </summary>
    public static void Verify(string testName, byte[] png, string sha256)
    {
        string pngPath = Path.Combine(FixtureDir, testName + ".golden.png");
        string shaPath = Path.Combine(FixtureDir, testName + ".golden.sha256");

        if (UpdateMode)
        {
            Directory.CreateDirectory(FixtureDir);
            File.WriteAllBytes(pngPath, png);
            File.WriteAllText(shaPath, sha256 + "\n");
            return;
        }

        if (!File.Exists(pngPath) || !File.Exists(shaPath))
        {
            throw new InvalidOperationException(
                $"golden fixture missing: {pngPath} (+ .sha256). Run once with HARBOR_UPDATE_GOLDENS=1 to seed it.");
        }

        string expected = File.ReadAllText(shaPath).Trim();
        if (!string.Equals(expected, sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"golden frame MISMATCH for {testName}: pixels differ from the baseline.\n" +
                $"  expected sha256: {expected}\n" +
                $"  actual   sha256: {sha256}\n" +
                $"  If the visual change is INTENDED, regenerate with HARBOR_UPDATE_GOLDENS=1.");
        }

        // Belt-and-braces: the stored PNG must hash to the stored value too — 
        // a corrupted/updated-by-half fixture fails loudly instead of silently.
        byte[] storedPng = File.ReadAllBytes(pngPath);
        string storedHash = Convert.ToHexString(SHA256.HashData(storedPng));
        if (!string.Equals(storedHash, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"golden fixture is inconsistent: {pngPath} does not hash to {shaPath} contents.");
        }
    }

    private static string ResolveFixtureDir()
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

        return Path.Combine(dir.FullName, "tests", "fixtures", "golden");
    }
}
