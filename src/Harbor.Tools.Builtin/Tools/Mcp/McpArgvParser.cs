using System.Text;

namespace Harbor.Tools.Mcp;

/// <summary>
///     Tokenizes a legacy MCP stdio command line into argv using shell-like quoting
///     rules (shlex-style). Pure static function — no process, no filesystem access.
/// </summary>
/// <remarks>
///     <para>
///         <b>Grammar:</b> tokens are separated by whitespace outside quotes.
///         Double-quoted segments honor <c>\"</c> → <c>"</c> and <c>\\</c> → <c>\</c>
///         escapes (any other backslash pair is kept literally). Single-quoted segments
///         are fully literal — no escapes inside. A backslash outside quotes escapes the
///         next character (a trailing lone backslash is kept literally). Quoted empty
///         strings (<c>""</c>) produce an empty argument, POSIX-style.
///     </para>
///     <para>
///         Unterminated quotes are a hard failure: the command would be launched with a
///         mangled argv, so <see cref="ParseCommand" /> returns <c>Result.Failure</c>
///         instead of guessing.
///     </para>
/// </remarks>
public static class McpArgvParser
{
    /// <summary>
    ///     Split a command line into argv tokens. The first token is the program,
    ///     the rest are its arguments. Empty/whitespace-only input or an unterminated
    ///     quote yields <c>Result.Failure</c>.
    /// </summary>
    public static Result<string[]> ParseCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return Result.Failure<string[]>("stdioCommand cannot be empty.");

        var tokens = new List<string>();
        var token = new StringBuilder(command.Length);
        int i = 0;
        int len = command.Length;

        while (i < len)
        {
            while (i < len && char.IsWhiteSpace(command[i])) i++;
            if (i >= len) break;

            token.Clear();
            char quote = '\0';
            int quoteStart = -1;

            while (i < len)
            {
                char c = command[i];

                if (quote == '\0')
                {
                    if (char.IsWhiteSpace(c)) break;
                    if (c is '"' or '\'')
                    {
                        quote = c;
                        quoteStart = i;
                        i++;
                        continue;
                    }
                    if (c == '\\' && i + 1 < len)
                    {
                        token.Append(command[i + 1]);
                        i += 2;
                        continue;
                    }
                    token.Append(c);
                    i++;
                    continue;
                }

                if (c == quote)
                {
                    quote = '\0';
                    i++;
                    continue;
                }

                if (quote == '"' && c == '\\' && i + 1 < len &&
                    command[i + 1] is '"' or '\\')
                {
                    token.Append(command[i + 1]);
                    i += 2;
                    continue;
                }

                token.Append(c);
                i++;
            }

            if (quote != '\0')
            {
                string kind = quote == '"' ? "double" : "single";
                return Result.Failure<string[]>($"Unterminated {kind} quote starting at offset {quoteStart}.");
            }

            tokens.Add(token.ToString());
        }

        return Result.Success(tokens.ToArray());
    }
}
