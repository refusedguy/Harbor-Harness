using CSharpFunctionalExtensions;
using Harbor.Abstractions.Permissions;
using Harbor.Terminal.Abstractions;
using Harbor.Tui.CellForge.Streaming;

namespace Harbor.App.Cli.Repl;

/// <summary>
///     Mini one-line permission prompt ([steal/opencode] #1182, epic #1155)
///     for renderers without the CellForge frame loop
///     (<c>HARBOR_TUI=plain</c>, narrow terminals): the gate card needs key
///     routing plus a render-thread drain, neither of which runs outside the
///     fullscreen loop — an approval posted there would wait forever. This
///     asker prints the same machine's footer density
///     (<see cref="ApprovalGateRouter.DescribeMiniLine"/>, mono) through the
///     active renderer and reads one <c>y/a/n</c> line instead.
///     Fail-closed like the card path: EOF, an empty line, or anything but a
///     leading <c>y</c>/<c>a</c> denies; cancellation propagates to the
///     caller (which maps it to Deny) instead of collapsing here.
/// </summary>
internal sealed class PlainPermissionAsker(ITuiRenderer renderer)
{
    public async Task<PermissionResponse> AskAsync(PermissionRequest request, CancellationToken ct)
    {
        string line = ApprovalGateRouter.DescribeMiniLine(
            request.Permission,
            CellForgePermissionAsker.Describe(request),
            ProbeWidth(),
            mono: true);
        await renderer.WriteLineAsync(line).ConfigureAwait(false);

        var answer = await renderer.ReadLineAsync("[y] allow  [a] always  [n] deny: ", ct).ConfigureAwait(false);
        string reply = answer.GetValueOrDefault(string.Empty).Trim();
        if (reply.StartsWith("a", StringComparison.OrdinalIgnoreCase))
        {
            return new PermissionResponse(PermissionAction.Allow, PersistDecision: true);
        }

        if (reply.StartsWith("y", StringComparison.OrdinalIgnoreCase))
        {
            return new PermissionResponse(PermissionAction.Allow, PersistDecision: false);
        }

        return new PermissionResponse(PermissionAction.Deny, PersistDecision: false);
    }

    private static int ProbeWidth()
    {
        int width = PermissionMiniLine.DefaultMaxWidth;
        try
        {
            int probe = Console.WindowWidth;
            if (probe >= 40)
            {
                width = Math.Min(probe, 240);
            }
        }
        catch (IOException)
        {
            width = PermissionMiniLine.DefaultMaxWidth;
        }
        catch (UnauthorizedAccessException)
        {
            width = PermissionMiniLine.DefaultMaxWidth;
        }

        return width;
    }
}
