using System.Globalization;
using System.Text;
using System.Text.Json;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     Human-readable export formats for <c>harbor sessions</c> (#2675, item 049).
/// </summary>
internal enum SessionExportFormat
{
    /// <summary>A single Markdown document.</summary>
    Markdown,

    /// <summary>A single JSON document.</summary>
    Json,
}

/// <summary>
///     Core of <c>harbor sessions export-md</c> and <c>harbor sessions export-json</c>:
///     render one session's metadata and its current message surface to a file.
/// </summary>
/// <remarks>
///     <para>
///         The export is written to a TEMP file beside the target, then moved into
///         place. A half-written document under the name the user asked for looks
///         like a finished export and is not one; the rename is atomic on the
///         same filesystem, the same contract <c>SessionExportZipRunner</c> uses.
///     </para>
///     <para>
///         JSON is written with <see cref="Utf8JsonWriter" />, not reflection-based
///         serialization, so the CLI stays AOT-safe. Both formats read the store's
///         SURFACE (<see cref="ISessionStore.GetMessagesAsync" />), never the raw
///         log: shadowed/rewound records stay out of a human-readable export.
///     </para>
/// </remarks>
internal static class SessionExportFormattedRunner
{
    /// <summary>
    ///     Write one session in the requested format.
    /// </summary>
    /// <param name="output">Where the summary line goes.</param>
    /// <param name="error">Where a diagnostic goes.</param>
    /// <param name="store">The store to read the session from.</param>
    /// <param name="sessionId">The session to export.</param>
    /// <param name="path">Where the document is written.</param>
    /// <param name="format">Markdown or JSON.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A process exit code.</returns>
    internal static async Task<int> RunAsync(
        TextWriter output,
        TextWriter error,
        ISessionStore store,
        string sessionId,
        string path,
        SessionExportFormat format,
        CancellationToken ct = default)
    {
        string temp = string.Empty;
        try
        {
            var loaded = await store.GetAsync(sessionId, ct).ConfigureAwait(false);
            if (loaded.IsSuccess)
            {
                var messages = await store.GetMessagesAsync(sessionId, ct).ConfigureAwait(false);
                if (messages.IsSuccess)
                {
                    string target = Path.GetFullPath(path);
                    string directory = Path.GetDirectoryName(target) ?? Directory.GetCurrentDirectory();
                    temp = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");

                    Directory.CreateDirectory(directory);

                    if (format == SessionExportFormat.Json)
                    {
                        await WriteJsonAsync(temp, loaded.Value, messages.Value, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await WriteMarkdownAsync(temp, loaded.Value, messages.Value, ct).ConfigureAwait(false);
                    }

                    File.Move(temp, target, overwrite: true);
                    string label = format == SessionExportFormat.Markdown ? "markdown" : "json";
                    await output.WriteLineAsync($"Exported session {sessionId} → {path} ({label})")
                        .ConfigureAwait(false);
                    return 0;
                }

                await error.WriteLineAsync($"Export failed: {messages.Error}").ConfigureAwait(false);
                return 1;
            }

            await error.WriteLineAsync($"Export failed: {loaded.Error}").ConfigureAwait(false);
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                      or ArgumentException)
        {
            await error.WriteLineAsync($"Export failed: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
        finally
        {
            try
            {
                if (temp.Length > 0 && File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (IOException)
            {
                // A leftover scratch file is noise, not a failed export.
            }
            catch (UnauthorizedAccessException)
            {
                // Same.
            }
        }
    }

    private static async Task WriteJsonAsync(
        string path,
        Session session,
        IReadOnlyList<AgentMessage> messages,
        CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("id", session.Id);
            writer.WriteString("title", session.Title);
            writer.WriteString("agent", session.Agent);
            writer.WriteString("model", session.Model);
            writer.WriteString("providerId", session.ProviderId);
            writer.WriteString("directory", session.Directory);
            writer.WriteString("createdAt", Stamp(session.CreatedAt));
            writer.WriteString("updatedAt", Stamp(session.UpdatedAt));
            writer.WriteString("status", session.Status.ToString());
            writer.WriteString("parentSessionId", session.ParentSessionId);

            writer.WriteStartArray("messages");
            foreach (var message in messages)
            {
                writer.WriteStartObject();
                writer.WriteString("id", message.Id);
                writer.WriteString("role", message.Role);
                writer.WriteString("createdAt", Stamp(message.CreatedAt));
                writer.WriteString("parentId", message.ParentId);
                switch (message)
                {
                    case UserMessage user:
                        writer.WriteString("content", user.Content);
                        break;
                    case AssistantMessage assistant:
                        writer.WriteStartArray("parts");
                        foreach (var part in assistant.Parts)
                        {
                            WritePart(writer, part);
                        }
                        writer.WriteEndArray();
                        writer.WriteString("stopReason", assistant.StopReason.ToString());
                        writer.WriteNumber("inputTokens", assistant.Usage.InputTokens);
                        writer.WriteNumber("outputTokens", assistant.Usage.OutputTokens);
                        break;
                    case ToolResultMessage toolResult:
                        writer.WriteStartArray("results");
                        foreach (var result in toolResult.Results)
                        {
                            writer.WriteStartObject();
                            writer.WriteString("toolCallId", result.ToolCallId);
                            writer.WriteString("toolName", result.ToolName);
                            writer.WriteString("output", result.Output);
                            writer.WriteBoolean("isError", result.IsError);
                            writer.WriteEndObject();
                        }
                        writer.WriteEndArray();
                        break;
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
            await writer.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    private static void WritePart(Utf8JsonWriter writer, ContentPart part)
    {
        writer.WriteStartObject();
        writer.WriteString("type", part.Type);
        switch (part)
        {
            case TextPart text:
                writer.WriteString("text", text.Text);
                break;
            case ThinkingPart thinking:
                writer.WriteString("text", thinking.Text);
                break;
            case ToolCallPart call:
                writer.WriteString("id", call.Id);
                writer.WriteString("toolName", call.ToolName);
                writer.WritePropertyName("args");
                call.Args.WriteTo(writer);
                break;
            case FilePart file:
                writer.WriteString("path", file.Path);
                writer.WriteString("mimeType", file.MimeType);
                writer.WriteNumber("sizeBytes", file.SizeBytes);
                break;
        }
        writer.WriteEndObject();
    }

    private static async Task WriteMarkdownAsync(
        string path,
        Session session,
        IReadOnlyList<AgentMessage> messages,
        CancellationToken ct)
    {
        var text = new StringBuilder();
        text.Append("# ").Append(session.Title).Append('\n').Append('\n');
        text.Append("- Session: `").Append(session.Id).Append("`\n");
        text.Append("- Agent: `").Append(session.Agent).Append("`\n");
        text.Append("- Model: `").Append(session.Model).Append("`\n");
        text.Append("- Provider: `").Append(session.ProviderId).Append("`\n");
        text.Append("- Directory: `").Append(session.Directory).Append("`\n");
        text.Append("- Created: ").Append(Stamp(session.CreatedAt)).Append('\n');
        text.Append("- Updated: ").Append(Stamp(session.UpdatedAt)).Append('\n');
        if (session.ParentSessionId is not null)
        {
            text.Append("- Parent session: `").Append(session.ParentSessionId).Append("`\n");
        }
        text.Append('\n');

        text.Append("## Messages\n\n");
        foreach (var message in messages)
        {
            text.Append("### ").Append(message.Role).Append(" — ").Append(Stamp(message.CreatedAt))
                .Append('\n').Append('\n');
            switch (message)
            {
                case UserMessage user:
                    text.Append(user.Content).Append('\n').Append('\n');
                    break;
                case AssistantMessage assistant:
                    foreach (var part in assistant.Parts)
                    {
                        switch (part)
                        {
                            case TextPart content:
                                text.Append(content.Text).Append('\n').Append('\n');
                                break;
                            case ThinkingPart thinking:
                                text.Append("> Thinking: ")
                                    .Append(thinking.Text.Replace("\n", "\n> ", StringComparison.Ordinal))
                                    .Append('\n').Append('\n');
                                break;
                            case ToolCallPart call:
                                text.Append("Tool call `").Append(call.ToolName).Append("`: ")
                                    .Append(call.Args.GetRawText()).Append('\n').Append('\n');
                                break;
                            case FilePart file:
                                text.Append("File `").Append(file.Path).Append("` (").Append(file.MimeType)
                                    .Append(")\n\n");
                                break;
                        }
                    }
                    break;
                case ToolResultMessage toolResult:
                    foreach (var result in toolResult.Results)
                    {
                        text.Append("Tool result `").Append(result.ToolName).Append("` (")
                            .Append(result.IsError ? "error" : "ok").Append("):\n\n")
                            .Append(result.Output).Append('\n').Append('\n');
                    }
                    break;
            }
        }

        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        await writer.WriteAsync(text.ToString().AsMemory(), ct).ConfigureAwait(false);
    }

    private static string Stamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
}
