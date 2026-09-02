using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
namespace Harbor.Benchmarks;

/// <summary>
///     Benchmarks the ANSI terminal screen-buffer blit path — writing a
///     full frame (120x40 chars) to the console output. Measures the cost
///     of ANSI escape sequence emission vs raw Console.Write for the same
///     content volume.
/// </summary>
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
