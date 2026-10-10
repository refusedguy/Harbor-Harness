namespace Harbor.Tui.RendererTests;

using System.Text.Json;
using Harbor.Abstractions.Events;
using Harbor.Tui.AnsiPlain;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

/// <summary>
///     Issue #1111 — <c>AnsiPlainTuiRenderer.ToolStartHandler</c> wrote
///     <c>tes.Args.GetRawText()</c> to the terminal verbatim, so model-
///     supplied args could inject terminal control sequences (C1 CSI,
///     DEL, CR). Args must paint as inert text: control bytes from the
///     payload never reach the terminal, while the visible payload text
///     still prints.
/// </summary>
/// <remarks>
///     Payloads stay valid JSON on purpose: <c>JsonDocument</c> rejects raw
///     C0 controls inside strings, so the tests use bytes that survive a
///     strict parse — single-char C1 CSI (U+009B), DEL (U+007F), and
///     inter-token CR — each of which the unfixed renderer echoed raw.
/// </remarks>
public class AnsiPlainArgsSanitizeTests
{
    [Test]
    public async Task ToolStart_C1CsiInArgs_DoesNotExecute()
    {
        string output = await RenderToolStartAsync("{\"path\":\"\u009b31mHACKED\"}");

        await Assert.That(output.Contains('\u009b')).IsFalse();
        await Assert.That(output.Contains("HACKED")).IsTrue();
    }

    [Test]
    public async Task ToolStart_DelAndCrInArgs_AreStripped()
    {
        string output = await RenderToolStartAsync("{\r\n\"path\": \"a\u007fb\"\r\n}");

        await Assert.That(output.Contains('\r')).IsFalse();
        await Assert.That(output.Contains('\u007f')).IsFalse();
        await Assert.That(output.Contains("\"ab\"")).IsTrue();
    }

    [Test]
    public async Task ToolStart_PlainArgs_RenderUnchanged()
    {
        string output = await RenderToolStartAsync("""{"path":"README.md","limit":10}""");

        await Assert.That(output.Contains("README.md")).IsTrue();
    }

    private static async Task<string> RenderToolStartAsync(string argsJson)
    {
        using var writer = new StringWriter();
        var renderer = new AnsiTuiRenderer(NullLogger<AnsiTuiRenderer>.Instance, writer);
        try
        {
            await renderer.InitializeAsync();
            using var doc = JsonDocument.Parse(argsJson);
            var evt = ToolExecutionStartEvent.Create("tc_1", "read", doc.RootElement);
            await renderer.RenderAsync(evt);
        }
        finally
        {
            renderer.Dispose();
        }

        return writer.ToString();
    }
}
