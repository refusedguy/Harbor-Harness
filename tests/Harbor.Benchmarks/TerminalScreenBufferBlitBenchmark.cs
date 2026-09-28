using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Benchmarks;

/// <summary>
///     Benchmarks the ANSI terminal screen-buffer blit path — writing a
///     full frame (120x40 chars) to the console output. Measures the cost
///     of ANSI escape sequence emission vs raw Console.Write for the same
///     content volume.
/// </summary>
/// <para><b>Measurement contract (#408)</b> — what this number includes:</para>
/// <list type="bullet">
///          <item><c>Operation:</c> write one 120×40 frame either as ANSI-styled lines or as
///          plain text (40 <c>Console.Write</c> calls per op).</item>
///          <item><c>Payload:</c> 40 pre-built lines per variant (ANSI row ≈ 130 B with a
///          24-bit colour escape; plain row ≈ 120 B), built once in <c>Setup</c>.</item>
///          <item><c>StateReset:</c> per invocation — the rows write to <see cref="Console" />
///          directly and hold no state between ops.</item>
///          <item><c>Drain:</c> none — <c>Console.Write</c> hands the data to the standard
///          output stream; when the host is redirected to a file or pipe this measures the
///          redirected write, which is why CI and local numbers must not be mixed.</item>
///          <item><c>RetainedState:</c> none.</item>
///          <item><c>AwaitSemantics:</c> n/a — no async in any row.</item>
///          <item><c>AllocAttribution:</c> the string concatenation (<c>line + "\n"</c>) is
///          inside the measurement; the underlying console/stream buffers are not attributed
///          to the row.</item>
/// </list>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class TerminalScreenBufferBlitBenchmark
{
    private string[] _ansiLines = null!;
    private string[] _plainLines = null!;
    private byte[] _ansiBytes = null!;
    private byte[] _plainBytes = null!;

    [GlobalSetup]
    public void Setup()
    {
        const int cols = 120;
        const int rows = 40;

        _ansiLines = new string[rows];
        _plainLines = new string[rows];
        _ansiBytes = new byte[rows * (cols + 20)]; // escape overhead
        _plainBytes = new byte[rows * (cols + 2)];

        for (int r = 0; r < rows; r++)
        {
            string line = $"Line {r}: " + new string('x', cols - 10);
            _ansiLines[r] = $"\x1b[38;2;200;200;200m{line}\x1b[0m";
            _plainLines[r] = line;
        }
    }

    [Benchmark(Description = "WriteLine N ANSI lines", Baseline = true)]
    public void WriteAnsiLines()
    {
        for (int i = 0; i < _ansiLines.Length; i++)
            Console.Write(_ansiLines[i] + "\n");
    }

    [Benchmark(Description = "Write N plain lines")]
    public void WritePlainLines()
    {
        for (int i = 0; i < _plainLines.Length; i++)
            Console.Write(_plainLines[i] + "\n");
    }
}
