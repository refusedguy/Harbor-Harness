using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
namespace Harbor.Benchmarks;
/// <summary>
///     Entry point for the Harbor.Benchmarks console runner.
///     Usage:
///     dotnet run -c Release -- --filter '*'
///     dotnet run -c Release -- --filter '*EventBusBenchmark*'
///     dotnet run -c Release -- --list flat
/// </summary>
internal static class Program
{
    private static void Main(string[] args)
    {
        // BDN default build timeout is 120s — the auto-generated boilerplate
        // (223 benchmarks + all analyzers from Directory.Build.props with
        // UseSharedCompilation=false) exceeds it. Bump to 5 min.
        // https://benchmarkdotnet.org/articles/guides/troubleshooting.html
        var config = ManualConfig.Create(DefaultConfig.Instance)
            .WithBuildTimeout(TimeSpan.FromMinutes(5));
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
    }
}
