using System.IO;
using System.Security.Cryptography;
using System.Text;
using Harbor.Tui.ConsoleEx.Rendering;
using Harbor.Tui.ConsoleEx.Widgets;

namespace Harbor.Tui.ConsoleEx.Tests;

/// <summary>
/// Screenshot-diff harness with baseline hashes: renders canonical widget
/// states (chat screen, status variants, tool card, user block), hashes each
/// capture (SHA-256 over the grid art + cell dump) and compares against a
/// checked-in manifest — tests/fixtures/celldiff/screenshot-baselines.txt.
///
/// Stricter and cheaper than existence checks: any color/style/geometry drift
/// changes the hash and fails loudly with the offending name, while
/// regeneration stays one HARBOR_UPDATE_GOLDENS=1 run away.
/// </summary>
public class ScreenshotHashTests
{
    private const string ManifestName = "screenshot-baselines";

    [Test]
    public async Task All_Baseline_Hashes_Match_Manifest()
    {
        Dictionary<string, string> actual = [];
        foreach ((string name, ScreenBuffer buffer) in RenderCaptures())
        {
            actual[name] = HashOf(buffer);
        }

        string manifestPath = ManifestPath();
        if (Golden.IsUpdateMode())
        {
            File.WriteAllLines(
                manifestPath,
                actual.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                      .Select(kv => $"{kv.Key}:{kv.Value}"));
            return;
        }

        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException(
                $"baseline manifest missing: {manifestPath} (run once with HARBOR_UPDATE_GOLDENS=1)");
        }

        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in File.ReadAllLines(manifestPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            int sep = line.IndexOf(':');
            if (sep <= 0)
            {
                continue;
            }

            expected[line[..sep]] = line[(sep + 1)..];
        }

        List<string> diffs = [];
        foreach (string key in actual.Keys.Union(expected.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!expected.TryGetValue(key, out string want))
            {
                diffs.Add($"{key}: not in manifest");
            }
            else if (!actual.TryGetValue(key, out string got))
            {
                diffs.Add($"{key}: registered baseline was never captured");
            }
            else if (!string.Equals(want, got, StringComparison.OrdinalIgnoreCase))
            {
                diffs.Add($"{key}: {want[..12]}… != {got[..12]}…");
            }
        }

        await Assert.That(diffs).IsEmpty()
            .Because("rendered captures drifted from baselines; regenerate with " +
                     "HARBOR_UPDATE_GOLDENS=1 only for intentional changes. Diffs:\n" +
                     string.Join("\n", diffs));
    }

    internal static string ManifestPath() =>
        Path.Combine(Golden.FixtureDir, ManifestName + ".txt");

    internal static string HashOf(ScreenBuffer buffer)
    {
        string payload = GridDump.Art(buffer) + "\n" + GridDump.Cells(buffer);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static IEnumerable<(string Name, ScreenBuffer Buffer)> RenderCaptures()
    {
        yield return ("shot-chat-screen", ChatScreenCapture());
        yield return ("shot-status-running", StatusCapture(StatusBarMode.Running));
        yield return ("shot-status-approval", StatusCapture(StatusBarMode.AwaitingApproval));
        yield return ("shot-tool-card", ToolCardCapture());
        yield return ("shot-user-block", UserBlockCapture());
    }

    private static ScreenBuffer ChatScreenCapture()
    {
        var composer = new ComposerController();
        composer.Buffer.InsertText("fix the parser|");
        var status = new StatusViewModel
        {
            Model = "kilocode/tencent/hy3:free",
            Mode = StatusBarMode.Running,
        };
        status.SetContext(7200, 10_000);
        status.SetUsage(120_000, 45_500, 0.0042m);

        var screen = ChatScreen.Build(composer, status);
        var tl = screen.Timeline.Timeline;
        tl.Append(new UserBlock("please fix it"));
        tl.Append(new ToolCallBlock(new ToolCallInfo("tc1", "edit", "{\"path\":\"src/app.cs\"}")));

        screen.Tree.Solve(72, 18);
        _ = tl.PrepareFrame(72, screen.Timeline.Rect.Height);

        var back = new ScreenBuffer(72, 18);
        foreach (var panel in screen.Tree.Panels)
        {
            panel.Paint(back);
        }

        return back;
    }

    private static ScreenBuffer StatusCapture(StatusBarMode mode)
    {
        var back = new ScreenBuffer(64, 1);
        var vm = new StatusViewModel { Model = "kilocode/tencent/hy3:free", Mode = mode };
        vm.SetContext(7200, 10_000);
        vm.SetUsage(120_000, 45_500, 0.0042m);

        var ws = new StatusSeg[8];
        int n = vm.BuildSegments(ws);
        int kept = StatusBarLayout.Fit(ws.AsSpan()[..n], 64);
        StatusBarWidget.Paint(back, new Rect(0, 0, 64, 1), ws.AsSpan()[..kept]);
        return back;
    }

    private static ScreenBuffer ToolCardCapture()
    {
        var back = new ScreenBuffer(48, 4);
        var block = new ToolCallBlock(new ToolCallInfo("tc2", "edit", "{\"path\":\"src/app.cs\"}"));
        block.Measure(48);
        block.Paint(new BlockPaintContext(back, new Rect(0, 0, 48, 4), tick: 0));
        return back;
    }

    private static ScreenBuffer UserBlockCapture()
    {
        var back = new ScreenBuffer(40, 2);
        var block = new UserBlock("refactor the renderer loop");
        block.Paint(new BlockPaintContext(back, new Rect(0, 0, 40, 2), tick: 0));
        return back;
    }
}
