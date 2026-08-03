using System.Net;
using System.Text;

namespace Harbor.E2E.Framework;

/// <summary>
///     Renders the visible TUI screen buffer to a PNG image for docs and E2E
///     screenshot artifacts. Uses headless Chromium when available, with an
///     ImageMagick fallback.
/// </summary>
internal static class TerminalScreenshotRenderer
{
    private const int MaxLines = 48;

    public static async Task RenderPngAsync(string visibleText, string outputPngPath, CancellationToken ct = default)
    {
        string snapshot = TrimToViewport(visibleText);
        if (string.IsNullOrWhiteSpace(snapshot))
            snapshot = "(empty screen)";

        await RenderHtmlToPngAsync(BuildHtml(snapshot), outputPngPath, ct).ConfigureAwait(false);
    }

    public static async Task RenderHtmlToPngAsync(string htmlContent, string outputPngPath, CancellationToken ct = default)
    {
        string? dir = Path.GetDirectoryName(outputPngPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        string htmlPath = Path.ChangeExtension(outputPngPath, ".html");
        await File.WriteAllTextAsync(htmlPath, htmlContent, ct).ConfigureAwait(false);

        if (await TryChromiumScreenshotAsync(htmlPath, outputPngPath, ct).ConfigureAwait(false))
            return;

        await RenderWithImageMagickAsync(AnsiStripper.Strip(htmlContent), outputPngPath, ct).ConfigureAwait(false);
    }

    private static string TrimToViewport(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        if (lines.Length <= MaxLines)
            return text.TrimEnd();

        return string.Join('\n', lines[^MaxLines..]).TrimEnd();
    }

    private static string BuildHtml(string text)
    {
        return string.Concat(
            """
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8" />
              <style>
                html, body {
                  margin: 0;
                  padding: 12px;
                  background: #0d0d0f;
                  color: #e6e6e6;
                  font: 12px/1.25 "DejaVu Sans Mono", "Liberation Mono", monospace;
                  white-space: pre;
                }
              </style>
            </head>
            <body>
            """,
            WebUtility.HtmlEncode(text),
            """
            </body>
            </html>
            """);
    }

    private static async Task<bool> TryChromiumScreenshotAsync(string htmlPath, string outputPngPath, CancellationToken ct)
    {
        string? browser = FindChromiumExecutable();
        if (browser is null)
            return false;

        var psi = new ProcessStartInfo
        {
            FileName = browser,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("--headless");
        psi.ArgumentList.Add("--disable-gpu");
        psi.ArgumentList.Add("--hide-scrollbars");
        psi.ArgumentList.Add("--virtual-time-budget=2000");
        psi.ArgumentList.Add("--window-size=1280,2000");
        psi.ArgumentList.Add($"--screenshot={outputPngPath}");
        psi.ArgumentList.Add(new Uri(htmlPath).AbsoluteUri + "?t=" + Guid.NewGuid().ToString("N"));

        using var process = Process.Start(psi);
        if (process is null)
            return false;

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return process.ExitCode == 0 && File.Exists(outputPngPath) && new FileInfo(outputPngPath).Length > 0;
    }

    private static async Task RenderWithImageMagickAsync(string text, string outputPngPath, CancellationToken ct)
    {
        string textPath = Path.ChangeExtension(outputPngPath, ".txt");
        await File.WriteAllTextAsync(textPath, text, Encoding.UTF8, ct).ConfigureAwait(false);

        var psi = new ProcessStartInfo
        {
            FileName = "magick",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("-background");
        psi.ArgumentList.Add("#0d0d0f");
        psi.ArgumentList.Add("-fill");
        psi.ArgumentList.Add("#e6e6e6");
        psi.ArgumentList.Add("-font");
        psi.ArgumentList.Add("DejaVu-Sans-Mono");
        psi.ArgumentList.Add("-pointsize");
        psi.ArgumentList.Add("12");
        psi.ArgumentList.Add($"caption:@{textPath}");
        psi.ArgumentList.Add(outputPngPath);

        using var process = Process.Start(psi);
        if (process is null)
            throw new InvalidOperationException("Failed to start ImageMagick for terminal screenshot rendering.");

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        if (process.ExitCode != 0 || !File.Exists(outputPngPath))
            throw new InvalidOperationException("ImageMagick failed to render terminal screenshot PNG.");
    }

    private static string? FindChromiumExecutable()
    {
        string[] candidates =
        [
            "chromium",
            "chromium-browser",
            "google-chrome",
            "google-chrome-stable"
        ];

        foreach (string candidate in candidates)
        {
            string? path = FindOnPath(candidate);
            if (path is not null)
                return path;
        }

        return null;
    }

    private static string? FindOnPath(string executable)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
            return null;

        foreach (string dir in pathEnv.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string full = Path.Combine(dir, executable);
            if (File.Exists(full))
                return full;
        }

        return null;
    }
}
