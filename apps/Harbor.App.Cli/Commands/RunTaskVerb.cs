using Harbor.App.Cli.Hosting;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     <c>harbor run task agent=&lt;name&gt; &lt;prompt&gt;</c> — drive the sub-agent
///     runner directly (same isolation path as the <c>task</c> tool) without a
///     parent agent turn. Extracted from <c>Program</c> (#176), 1:1 behavior.
/// </summary>
internal static class RunTaskVerb
{
    internal static async Task<int> RunAsync(string[] args)
    {
        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : string.Empty;
        if (sub != "task")
        {
            Console.Error.WriteLine("""
                                    Usage: harbor run task agent=<name> <prompt>
                                      run task agent=explore "find all .cs files"
                                    """);
            return sub.Length == 0 ? 2 : 1;
        }

        CliArgs.MarkApproverless(); // #52: one-shot, frame loop не крутится
        using var host = HostBuilder.Build(args);
        return await TaskRunRunner.RunAsync(Console.Out, Console.Error, host.Services, args.Skip(1).ToArray())
            .ConfigureAwait(false);
    }
}
