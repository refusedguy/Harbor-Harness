using Harbor.TestKit;
using Harbor.Tui.CellForge.Panels;
using Harbor.Ui.Framework.Panels;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     #1224 (TR5): priority-ordered <see cref="IPanelWidgetDecoder" /> list
///     extends the three legacy <c>WidgetToRows</c> shapes without breaking them.
/// </summary>
public class PanelWidgetDecoderTests
{
    private sealed record ProbeWidget(string Value);

    private sealed class ProbeDecoder(int priority, string row) : IPanelWidgetDecoder
    {
        public int Priority => priority;

        public bool TryDecode(object widget, PanelContext ctx, out IReadOnlyList<string>? rows)
        {
            if (widget is ProbeWidget)
            {
                rows = [row];
                return true;
            }

            rows = null;
            return false;
        }
    }

    private static string Joined(object? widget) =>
        string.Join("\n", CellForgePanelAdapter.RenderToRows(
            new TestPanelProvider("stub-decoder", "Stub Decoder", widget), new PanelContext(new UiState(), 80, 24)));

    [Test]
    public async Task LegacyForms_Still_Pass_Through()
    {
        await Assert.That(Joined(null)).IsEqualTo(string.Empty);
        await Assert.That(Joined("a\nb")).IsEqualTo("a\nb");
        await Assert.That(Joined((IReadOnlyList<string>)["x"])).IsEqualTo("x");
        await Assert.That(Joined((IEnumerable<string>)["y"])).IsEqualTo("y");
        await Assert.That(Joined(42)).IsEqualTo("42");
    }

    [Test]
    public async Task Richer_Widgets_Decode_To_Rows()
    {
        await Assert.That(Joined(new PanelProgress("build", 0.5))).IsEqualTo("build [########--------] 50%");
        await Assert.That(Joined(new PanelTable(["a", "b"], [["1", "2"]]))).IsEqualTo("a | b\n-----\n1 | 2");
        await Assert.That(Joined(new PanelTree([(0, "root"), (1, "leaf")]))).IsEqualTo("root\n  leaf");
        await Assert.That(Joined(new PanelForm("opts", [("k", "v")]))).IsEqualTo("opts\nk: v");
    }

    [Test]
    public async Task Higher_Priority_Decoder_Wins()
    {
        try
        {
            CellForgePanelAdapter.RegisterDecoder(new ProbeDecoder(10, "low"));
            CellForgePanelAdapter.RegisterDecoder(new ProbeDecoder(200, "high"));
            await Assert.That(Joined(new ProbeWidget("v"))).IsEqualTo("high");
        }
        finally
        {
            CellForgePanelAdapter.ResetDecoders();
        }

        await Assert.That(Joined("plain")).IsEqualTo("plain");
    }
}
