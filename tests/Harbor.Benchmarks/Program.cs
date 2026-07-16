using BenchmarkDotNet.Running;

namespace Harbor.Benchmarks;

/// <summary>
/// Entry point for the Harbor.Benchmarks console runner.
///
/// Usage:
///   dotnet run -c Release -- --filter '*'
///   dotnet run -c Release -- --filter '*EventBusBenchmark*'
///   dotnet run -c Release -- --list flat
/// </summary>
internal static class Program
{
    private static void Main(string[] args)
    {
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
