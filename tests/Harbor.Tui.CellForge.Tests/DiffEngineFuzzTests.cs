using System.Text;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
/// Fuzz invariant (celldiff §8): after ANY flush FRONT == BACK, unconditionally.
/// Deterministic seeds drive random mutations (narrow/wide runes, styled fills,
/// text runs) plus paired resizes; every step flushes through the real engine
/// and asserts the mirror property.
/// </summary>
public class DiffEngineFuzzTests
{
    private static readonly Rune[] RunePool =
    [
        new('a'), new('Z'), new('9'), new(' '), new('─'), new('│'),
        new(0x00E9),   // é — narrow
        new(0x4E2D),   // 中 — wide CJK
        new(0x3042),   // あ — wide kana
        new(0x1F44D),  // 👍 — wide past BMP
        new(0x2713),   // ✓ — narrow dingbat
    ];

    [Test]
    public async Task Fuzz_AfterEveryFlush_FrontEqualsNext()
    {
        foreach (int seed in new[] { 11, 23, 47 })
        {
            await RunFuzz(seed);
        }
    }

    private static async Task RunFuzz(int seed)
    {
        var rng = new Random(seed);
        var backend = new RecordingBackend();
        var writer = new AnsiWriter(backend, syncUpdates: true);
        var engine = new DiffEngine(24, 8);
        var back = new ScreenBuffer(24, 8);

        for (int step = 0; step < 80; step++)
        {
            if (step % 17 == 16)
            {
                // Paired resize (grow or shrink) — geometry must stay in
                // lockstep or Flush throws by contract.
                int cols = Math.Max(1, back.Cols + rng.Next(-5, 6));
                int rows = Math.Max(1, back.Rows + rng.Next(-3, 4));
                back.Resize(cols, rows);
                engine.Front.Resize(cols, rows);
            }
            else
            {
                Mutate(rng, back);
            }

            writer.BeginFrame();
            engine.Flush(back, writer);
            await writer.EndFrameAsync();

            if (!engine.FrontMatches(back))
            {
                throw new InvalidOperationException(
                    $"fuzz seed {seed} step {step}: FRONT != BACK after flush\n{GoldenDoc.Build($"fuzz-{seed}", back, backend)}");
            }
        }
    }

    private static void Mutate(Random rng, ScreenBuffer back)
    {
        int cols = back.Cols;
        int rows = back.Rows;
        switch (rng.Next(4))
        {
            case 0:
                back.SetRune(
                    rng.Next(cols), rng.Next(rows),
                    RunePool[rng.Next(RunePool.Length)],
                    RandomStyle(rng));
                break;
            case 1:
                string text = rng.Next(2) == 0 ? "harbor" : "中kern👍x";
                back.SetText(rng.Next(cols), rng.Next(rows), text, RandomStyle(rng));
                break;
            case 2:
                int w = Math.Min(cols - 1, 1 + rng.Next(6));
                int h = Math.Min(rows, 1 + rng.Next(3));
                back.Fill(new Rect(rng.Next(cols), rng.Next(rows), w, h), Cell.From(RunePool[rng.Next(RunePool.Length)], RandomStyle(rng)));
                break;
            default:
                int y = rng.Next(rows);
                for (int x = 0; x < cols; x += 1 + rng.Next(3))
                {
                    _ = back.SetRune(x, y, RunePool[rng.Next(RunePool.Length)], RandomStyle(rng));
                }

                break;
        }
    }

    private static CellStyle RandomStyle(Random rng) => rng.Next(5) switch
    {
        0 => CellStyle.Plain,
        1 => new CellStyle(PackedColor.Indexed((byte)rng.Next(256))),
        2 => new CellStyle(PackedColor.Rgb((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256))),
        3 => new CellStyle(attrs: StyleAttr.Bold | StyleAttr.Underline),
        _ => new CellStyle(PackedColor.Indexed((byte)rng.Next(256)), PackedColor.Indexed((byte)rng.Next(256)), StyleAttr.Reverse),
    };
}
