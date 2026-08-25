using System.Text;
using Harbor.Tui.ConsoleEx.Input;
using Harbor.Tui.ConsoleEx.Parsing;

namespace Harbor.Tui.ConsoleEx.Tests;

/// <summary>
/// Zero-allocation budget verification (design §5.4): key/mouse/wheel/resize
/// event paths allocate NOTHING in steady state. Char runes are structs; the
/// only sanctioned heap allocation is one string per completed paste.
/// </summary>
public class AllocationBudgetTests
{
    [Test]
    public async Task Steady_State_Key_Mouse_Wheel_Parse_Is_Allocation_Free()
    {
        var parser = new EscapeSequenceParser();
        var sink = new List<InputEvent>(32);

        // Mixed golden traffic: legacy keys, kitty CSI-u, SGR mouse, wheel,
        // controls, printable text, split UTF-8 across calls.
        byte[][] traffic =
        [
            "\u001B[A"u8.ToArray(),
            "\u001B[1;5A"u8.ToArray(),
            "\u001B[13;2u"u8.ToArray(),
            "\u001B[97;5u"u8.ToArray(),
            "\u001B[<0;10;5M"u8.ToArray(),
            "\u001B[<0;10;5m"u8.ToArray(),
            "\u001B[<65;4;4M"u8.ToArray(),
            "\r\t\u007Fabc"u8.ToArray(),
            [0xF0, 0x9F],
            [0x98, 0x80], // 😀 split across two reads
        ];

        const int iterations = 10_000;

        // Warmup: JIT + parser ring growth + decoder state settle.
        for (var i = 0; i < 2_000; i++)
        {
            foreach (var chunk in traffic)
            {
                parser.Parse(chunk);
            }

            parser.DrainEvents(sink);
            sink.Clear();
        }

        GC.WaitForPendingFinalizers();

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
        {
            foreach (var chunk in traffic)
            {
                parser.Parse(chunk);
            }

            parser.DrainEvents(sink);
            sink.Clear();
        }

        var after = GC.GetAllocatedBytesForCurrentThread();

        await Assert.That(after - before).IsEqualTo(0); // thread-scoped: immune to parallel test traffic
        await Assert.That(sink.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Paste_Path_Allocates_Exactly_One_String_Per_Block()
    {
        var parser = new EscapeSequenceParser(new ParserOptions { MaxPasteBytes = 1024 });
        var sink = new List<InputEvent>(4);

        var payload = Encoding.UTF8.GetBytes("payload");
        byte[] block =
        [
            0x1B, (byte)'[', (byte)'2', (byte)'0', (byte)'0', (byte)'~',
            .. payload,
            0x1B, (byte)'[', (byte)'2', (byte)'0', (byte)'1', (byte)'~',
        ];

        // Warmup (ring + first-string paths).
        for (var i = 0; i < 100; i++)
        {
            parser.Parse(block);
            parser.DrainEvents(sink);
            sink.Clear();
        }

        GC.WaitForPendingFinalizers();

        const int iterations = 1_000;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
        {
            parser.Parse(block);
            parser.DrainEvents(sink);
            sink.Clear();
        }

        var after = GC.GetAllocatedBytesForCurrentThread();

        // Exactly `iterations` paste strings (small ASCII strings ≈ 26–32 B
        // each incl. object header).
        var allocated = after - before;
        await Assert.That(allocated).IsGreaterThan(iterations * 24L);
        await Assert.That(allocated).IsLessThan(iterations * 64L);
    }
}
