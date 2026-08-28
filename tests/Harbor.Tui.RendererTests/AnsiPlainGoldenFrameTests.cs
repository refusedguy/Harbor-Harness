namespace Harbor.Tui.RendererTests;

using System.Text;
using Harbor.Tui.AnsiPlain;
using Harbor.Tui.RendererTests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

/// <summary>
///     Golden-frame visual regression for the unified AnsiPlain renderer
///     (renderer-unification sprint Phase 5): the ansi and plain modes of the
///     SAME pipeline must both render the canonical stream, and their exact
///     output is pinned against committed golden frames.
/// </summary>
public class AnsiPlainGoldenFrameTests
{
    [Test]
    public async Task AnsiMode_RendersCanonicalStream()
    {
        var sb = new StringBuilder();
        await using var writer = new StringWriter(sb);
        var renderer = new AnsiTuiRenderer(NullLogger<AnsiTuiRenderer>.Instance, writer);

        try
        {
            await renderer.InitializeAsync();
            foreach (var evt in CanonicalStreams.ChatWithToolRoundTrip())
            {
                await renderer.RenderAsync(evt);
            }
        }
        finally
        {
            renderer.Dispose();
        }

        await writer.FlushAsync();
        await GoldenFrames.AssertGoldenAsync("ansiplain-ansi", sb.ToString());
    }

    [Test]
    public async Task PlainMode_RendersCanonicalStream()
    {
        var sb = new StringBuilder();
        await using var writer = new StringWriter(sb);

        var renderer = new PlainTuiRenderer(writer);
        await renderer.InitializeAsync();
        foreach (var evt in CanonicalStreams.ChatWithToolRoundTrip())
        {
            await renderer.RenderAsync(evt);
        }

        await writer.FlushAsync();
        renderer.Dispose();
        await GoldenFrames.AssertGoldenAsync("ansiplain-plain", sb.ToString());
    }

    [Test]
    public async Task PlainMode_ProducesSameVisibleText_AnsiMode()
    {
        // Strategy unification guarantee: with escape sequences stripped, the
        // ansi mode's VISIBLE text equals the plain mode's output for the same
        // event stream — the two backends differ only in styling.
        string ansi = await CaptureAnsiAsync();
        string plain = await CapturePlainAsync();

        // Strip escape sequences BEFORE normalizing: normalization turns the
        // repaint CR of `ESC[2K` + CR into a phantom line break.
        string ansiVisible = GoldenFrames.Normalize(StripAnsi(ansi));
        string plainVisible = GoldenFrames.Normalize(plain);

        await Assert.That(ansiVisible).IsEqualTo(plainVisible);
    }

    private static async Task<string> CaptureAnsiAsync()
    {
        var sb = new StringBuilder();
        await using var writer = new StringWriter(sb);
        var renderer = new AnsiTuiRenderer(NullLogger<AnsiTuiRenderer>.Instance, writer);
        try
        {
            await renderer.InitializeAsync();
            foreach (var evt in CanonicalStreams.ChatWithToolRoundTrip())
            {
                await renderer.RenderAsync(evt);
            }
        }
        finally
        {
            renderer.Dispose();
        }

        await writer.FlushAsync();
        return sb.ToString();
    }

    private static async Task<string> CapturePlainAsync()
    {
        var sb = new StringBuilder();
        await using var writer = new StringWriter(sb);
        var renderer = new PlainTuiRenderer(writer);
        await renderer.InitializeAsync();
        foreach (var evt in CanonicalStreams.ChatWithToolRoundTrip())
        {
            await renderer.RenderAsync(evt);
        }

        await writer.FlushAsync();
        renderer.Dispose();
        return sb.ToString();
    }

    /// <summary>
    ///     Removes ECMA-48 escape sequences (CSI … final byte), keeping only
    ///     the visible text — the "same pixels" contract between the ansi and
    ///     plain strategies. A CR directly after an erase-to-EOL CSI is part
    ///     of the in-place repaint idiom (<c>ESC[2K</c> + CR) and is consumed
    ///     with it, so it does not surface as a phantom line break.
    /// </summary>
    private static string StripAnsi(string text)
    {
        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '\x1b' && i + 1 < text.Length && text[i + 1] == '[')
            {
                i += 2;
                while (i < text.Length && (text[i] < '\x40' || text[i] > '\x7e'))
                {
                    i++;
                }

                i++; // consume the final byte
                if (i < text.Length && text[i] == '\r')
                {
                    i++; // consume the repaint CR that follows ClearLine
                }

                continue;
            }

            sb.Append(text[i]);
            i++;
        }

        return sb.ToString();
    }
}
