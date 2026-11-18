using Harbor.Abstractions.Env;
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
        Environment.SetEnvironmentVariable(HarborEnv.NoApprover, "1");

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

    /// <summary>
    ///     #2675 item 022: pull the one-shot <c>--timings</c> flag out of the
    ///     argument list. The flag has no value; unlike <c>--script</c> it
    ///     never reaches the host builder, only the runner's stderr report.
    /// </summary>
    internal static bool ExtractTimingsArg(string[] args, out string[] remaining)
    {
        bool timings = false;
        var remainingList = new List<string>(args.Length);
        foreach (string a in args)
        {
            if (a.Equals("--timings", StringComparison.OrdinalIgnoreCase))
            {
                timings = true;
                continue;
            }
            remainingList.Add(a);
        }
        remaining = remainingList.ToArray();
        return timings;
    }

    /// <summary>
    ///     F14: resolve a shareable prompt deep link into its prompt text. A
    ///     link carries the prompt in its <c>prompt</c> query parameter, e.g.
    ///     <c>https://example.com/new?prompt=Explain%20this+diff</c>. Parsing is
    ///     deliberately scheme/host agnostic: the link is only a carrier for
    ///     the query value, nothing in it is fetched or contacted.
    /// </summary>
    /// <returns>
    ///     The decoded prompt, or <see langword="null" /> when <paramref name="url" />
    ///     is empty or carries no non-blank <c>prompt</c> parameter.
    /// </returns>
    internal static string? ParsePromptLink(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        int question = url.IndexOf('?');
        if (question < 0 || question == url.Length - 1)
            return null;

        int fragment = url.IndexOf('#', question + 1);
        string query = fragment >= 0 ? url[(question + 1)..fragment] : url[(question + 1)..];

        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            if (equals < 0)
                continue;
            string key = Uri.UnescapeDataString(pair[..equals].Replace('+', ' '));
            if (!key.Equals("prompt", StringComparison.OrdinalIgnoreCase))
                continue;
            string value = Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        return null;
    }
}
