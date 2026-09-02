using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Harbor.Abstractions.Tools;

/// <summary>
///     Unified tool-boundary error classifier (ROP-A П.13): ten tools carried
///     hand-copied catch(OCE)→"&lt;tool&gt; cancelled" blocks with drifting
///     timeout texts. The cancellation-to-string conversion is the tools'
///     established boundary contract — but the policy lives in exactly one
///     place now. Mid-pipeline Try* code uses
///     <see cref="ResultErrors.Message" /> (OCE rethrow) instead.
/// </summary>
public static class ToolErrors
{
    /// <summary>
    ///     Build the canonical handler for <paramref name="tool" />:
    ///     "&lt;tool&gt; cancelled" when the caller's token fired,
    ///     "&lt;tool&gt; timed out after Ns." for a timeout-shaped OCE,
    ///     otherwise <paramref name="failurePrefix" /> + exception message.
    /// </summary>
    public static Func<Exception, string> Handler(
        string tool, CancellationToken ct, TimeSpan? timeout = null, string? failurePrefix = null) =>
        ex => ex switch
        {
            OperationCanceledException when ct.IsCancellationRequested => $"{tool} cancelled",
            OperationCanceledException when timeout.HasValue => $"{tool} timed out after {timeout.Value.TotalSeconds:N0}s.",
            OperationCanceledException => $"{tool} cancelled",
            _ when failurePrefix is not null => $"{failurePrefix}{ex.Message}",
            _ => ex.Message
        };

    /// <summary>Kill a process tree, swallowing and tracing the failure (ROP-A П.14).</summary>
    public static void KillQuietly(System.Diagnostics.Process process, ILogger? logger = null)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            logger?.LogTrace(ex, "Process {Pid} already gone during kill", process.Id);
        }
    }
}
