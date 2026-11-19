using Harbor.Abstractions.Extensions;

namespace Harbor.App.Cli.Repl.Commands;

/// <summary>
///     <c>/cost-alert &lt;amount|off&gt;</c> (issue #3109). Parses the argument
///     through the shared <see cref="CostAlertMonitor" /> and reports the new
///     threshold in the chat timeline. The frame loop then warns when a
///     <c>SessionStatsEvent</c> pushes spend past it.
/// </summary>
internal sealed class CostAlertCommand(CostAlertMonitor monitor) : IReplCommand
{
    public string Id => "cost-alert";
    public IReadOnlyList<string> Aliases => [];
    public string Title => "Cost Alert";
    public string Description => "warn when session spend exceeds a threshold";
    public string Group => "Config";

    public Task ExecuteAsync(ReplCommandContext ctx, CancellationToken ct)
    {
        Ensure.NotNull(ctx);
        var host = ctx.Host;
        host.Bridge.AppendSystemLine(monitor.Apply(ExtractArgument(ctx.RawInput)));
        host.WakeUp();
        return Task.CompletedTask;
    }

    /// <summary>The argument of the typed slash line, or null for a bare commit.</summary>
    private static string? ExtractArgument(string? rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput) || rawInput[0] != '/')
        {
            return null;
        }

        int space = rawInput.IndexOf(' ');
        return space < 0 ? null : rawInput[(space + 1)..].Trim();
    }
}
