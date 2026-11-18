using System.Buffers;
using System.Text;
using System.Text.Json;
using Harbor.Abstractions.Tools;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     <c>harbor tools list|show [--json]</c> — the self-describing tool
///     registry for humans and scripts (issue #2675 item 030, Amp's
///     <c>amp tools list</c> / <c>amp tools show --json</c>).
/// </summary>
/// <remarks>
///     This command is a pure string-driven view over a registry snapshot
///     supplied by the caller, so the same code serves the CLI verb and the
///     unit tests. JSON is written with <see cref="Utf8JsonWriter" /> (no
///     reflection, AOT-safe). No new seam and no public API change.
/// </remarks>
internal sealed class ToolsCommand
{
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private readonly Func<IReadOnlyList<ToolDescriptor>> _tools;

    public ToolsCommand(TextWriter output, TextWriter error, Func<IReadOnlyList<ToolDescriptor>> tools)
    {
        _output = output;
        _error = error;
        _tools = tools;
    }

    /// <summary>Dispatch a subcommand; <c>list</c> is the default.</summary>
    public Task<int> ExecuteAsync(string[] args, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        bool json = Array.Exists(args, static a => a == "--json");
        string[] tail = Array.FindAll(args, static a => a != "--json");
        string sub = tail.Length > 0 ? tail[0] : "list";
        return sub switch
        {
            "list" => ListAsync(json),
            "show" => ShowAsync(tail.Skip(1).ToArray(), json),
            _ => UsageAsync($"Unknown subcommand: {sub}")
        };
    }

    private async Task<int> ListAsync(bool json)
    {
        var tools = _tools()
            .OrderBy(static t => t.Name.Value, StringComparer.Ordinal)
            .ToArray();

        if (json)
            await _output.WriteLineAsync(RenderJson(tools, full: false)).ConfigureAwait(false);
        else
            foreach (var tool in tools)
                await _output.WriteLineAsync($"{tool.Name.Value,-24}{tool.Description}").ConfigureAwait(false);

        return 0;
    }

    private async Task<int> ShowAsync(string[] args, bool json)
    {
        if (args.Length == 0)
            return await UsageAsync("Missing tool name").ConfigureAwait(false);

        string name = args[0];
        var tool = _tools().FirstOrDefault(
            t => string.Equals(t.Name.Value, name, StringComparison.OrdinalIgnoreCase));
        if (tool is null)
        {
            await _error.WriteLineAsync($"Unknown tool: {name}").ConfigureAwait(false);
            return 1;
        }

        if (json)
        {
            await _output.WriteLineAsync(RenderJson(new[] { tool }, full: true)).ConfigureAwait(false);
        }
        else
        {
            await _output.WriteLineAsync($"{tool.Name.Value} — {tool.DisplayName}").ConfigureAwait(false);
            await _output.WriteLineAsync(tool.Description).ConfigureAwait(false);
            await _output.WriteLineAsync($"Execution mode: {tool.ExecutionMode}").ConfigureAwait(false);
            await _output.WriteLineAsync("Input schema:").ConfigureAwait(false);
            await _output.WriteLineAsync(tool.Schema.RootElement.GetRawText()).ConfigureAwait(false);
        }

        return 0;
    }

    private static string RenderJson(IReadOnlyList<ToolDescriptor> tools, bool full)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("tools");
            foreach (var tool in tools)
            {
                writer.WriteStartObject();
                writer.WriteString("name", tool.Name.Value);
                writer.WriteString("displayName", tool.DisplayName);
                writer.WriteString("description", tool.Description);
                if (full)
                {
                    writer.WriteString("executionMode", tool.ExecutionMode.ToString());
                    writer.WritePropertyName("inputSchema");
                    tool.Schema.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private Task<int> UsageAsync(string? message = null)
    {
        if (message is not null)
            _error.WriteLine(message);
        _error.WriteLine("Usage: harbor tools list [--json] | tools show <name> [--json]");
        return Task.FromResult(2);
    }
}
