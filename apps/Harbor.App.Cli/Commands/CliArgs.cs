using Harbor.App.Cli.Logging;
using Microsoft.Extensions.Logging;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     Shared CLI argument helpers, extracted from <c>Program</c> (#176).
///     Pure functions over <c>string[]</c> — no host, no I/O.
/// </summary>
internal static class CliArgs
{
    /// <summary>
    ///     Помечает процесс как approver-less (#52): one-shot verbs
    ///     (<c>ask</c>, <c>run task</c>) никогда не крутят ChatScreen frame
    ///     loop, поэтому approval-карточка CellForge заведомо не может быть
    ///     отвечена. <see cref="Harbor.App.Cli.Hosting.CellForgeModule" /> по этому маркеру
    ///     не подменяет fail-closed <c>PermissionService</c> интерактивным
    ///     asker'ом — Ask-вызовы получают быстрый Deny вместо вечного ожидания.
    /// </summary>
    internal static void MarkApproverless() =>
        Environment.SetEnvironmentVariable("HARBOR_NO_APPROVER", "1");

    // Delegates to HarborLogManager.ResolveConsoleLevel so the default level
    // (Debug under debugger, Information otherwise) and the --log-level /
    // --loglevel / -ll / HARBOR_LOGLEVEL forms stay in one place. Kept for
    // backward compat with any internal caller that still hits Program.ResolveLogLevel.
    internal static LogLevel ResolveLogLevel(string[] args) => HarborLogManager.ResolveConsoleLevel(args);

    internal static string[] StripLogArgs(string[] args)
    {
        var result = new List<string>(args.Length);
        int i = 0;
        while (i < args.Length)
        {
            if (args[i].Equals("--loglevel", StringComparison.OrdinalIgnoreCase) ||
                args[i].Equals("--log-level", StringComparison.OrdinalIgnoreCase) ||
                args[i].Equals("-ll", StringComparison.OrdinalIgnoreCase))
            {
                i += 2;
                continue;
            }
            // Also strip the --loglevel=Info / --log-level=Info inline form.
            if (args[i].StartsWith("--loglevel=", StringComparison.OrdinalIgnoreCase) ||
                args[i].StartsWith("--log-level=", StringComparison.OrdinalIgnoreCase))
            {
                i += 1;
                continue;
            }
            result.Add(args[i]);
            i++;
        }
        return result.ToArray();
    }

    /// <summary>
    ///     Extract <c>--script &lt;path&gt;</c> (or <c>--script=&lt;path&gt;</c>) from the
    ///     argument list. Returns the script path (or <see langword="null" /> if not
    ///     present) and the remaining args with the flag stripped.
    /// </summary>
    internal static string? ExtractScriptArg(string[] args, out string[] remaining)
    {
        var remainingList = new List<string>(args.Length);
        string? scriptPath = null;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a.Equals("--script", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length)
                {
                    scriptPath = args[i + 1];
#pragma warning disable S127 // Intentional: consume the value token alongside the flag.
                    i++;
#pragma warning restore S127
                }
                continue;
            }
            if (a.StartsWith("--script=", StringComparison.OrdinalIgnoreCase))
            {
                scriptPath = a["--script=".Length..];
                continue;
            }
            remainingList.Add(a);
        }
        remaining = remainingList.ToArray();
        return scriptPath;
    }
}
