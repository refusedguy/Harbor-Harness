using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Harbor.Providers.Ollama;

/// <summary>
///     A tool call recovered from a model that has no native tool-call support.
///     The name identifies the Harbor tool; the JSON is a complete argument
///     object ready for that tool's validator.
/// </summary>
internal sealed record EmulatedToolCall(string ToolName, string ArgumentsJson);

/// <summary>
///     Reads the two conventions small local models are trained to fall back to
///     when the provider advertises no tools: a line beginning with <c>$ </c>
///     (shell command) and a fenced block whose info string is
///     <c>execute_javascript</c> or <c>list_functions</c>.
/// </summary>
/// <remarks>
///     This lives behind the Ollama provider boundary (#2678 item 92). It is a
///     pure text transform: it never touches the tool registry, the permission
///     ruleset, or the event bus. The caller decides whether a recovered call is
///     allowed to execute. No reflection and no per-line <c>JsonDocument</c>, so
///     the path stays NativeAOT-safe.
/// </remarks>
internal interface ILegacyToolCallParser
{
    IReadOnlyList<EmulatedToolCall> Parse(string? text);
}

/// <inheritdoc cref="ILegacyToolCallParser" />
internal sealed class LegacyToolCallParser : ILegacyToolCallParser
{
    private const string CommandFence = "```";
    private const string CommandPrefix = "$ ";

    /// <summary>
    ///     Extract emulated tool calls in the order they appear. An unterminated
    ///     fenced block is withheld: a partial body is not a tool call, and the
    ///     streaming layer may still append the closing fence.
    /// </summary>
    public IReadOnlyList<EmulatedToolCall> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<EmulatedToolCall>();

        var calls = new List<EmulatedToolCall>();
        string? fenceLanguage = null;
        var fenceBody = new StringBuilder();

        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            string trimmed = line.Trim();

            if (fenceLanguage is not null)
            {
                if (trimmed.StartsWith(CommandFence, StringComparison.Ordinal))
                {
                    calls.Add(new EmulatedToolCall(fenceLanguage, Arguments("code", fenceBody.ToString().TrimEnd())));
                    fenceLanguage = null;
                    fenceBody.Clear();
                }
                else
                {
                    fenceBody.Append(line).Append('\n');
                }

                continue;
            }

            if (trimmed.StartsWith(CommandFence, StringComparison.Ordinal))
            {
                string info = trimmed[CommandFence.Length..].Trim();
                if (info is "execute_javascript" or "list_functions") fenceLanguage = info;
                continue;
            }

            if (trimmed.StartsWith(CommandPrefix, StringComparison.Ordinal) && trimmed.Length > CommandPrefix.Length)
            {
                calls.Add(new EmulatedToolCall("bash", Arguments("command", trimmed[CommandPrefix.Length..].Trim())));
            }
        }

        return calls;
    }

    /// <summary>Build one flat JSON object with a Utf8JsonWriter, without reflection.</summary>
    private static string Arguments(string key, string value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(key, value);
            writer.WriteEndObject();
            writer.Flush();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
