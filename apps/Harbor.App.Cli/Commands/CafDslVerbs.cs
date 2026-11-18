#if HARBOR_WITH_DSL_DEV
using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Threading;
using ConsoleAppFramework;
using Harbor.Abstractions.Dsl;
using Harbor.Tui.CellForge.CodeGen;
using Harbor.Tui.CellForge.CodeGen.DevReload;

namespace Harbor.App.Cli.Commands;

/// <summary>
///     #2432: developer-only IR inspection verbs. They read a
///     <c>.harbor-ui</c> file with the one shared parser and render the IR
///     with the deterministic tree dump. No renderer, session or host is
///     started, so the output depends on the file alone.
/// </summary>
/// <remarks>
///     The whole file is behind <c>HARBOR_WITH_DSL_DEV</c>. That symbol is
///     defined only when the CLI is built for development; an AOT publish
///     switches it off together with the ProjectReference to the parser
///     assembly, so none of this reaches the AOT binary.
/// </remarks>
internal static class DslDevCommands
{
    /// <summary>Parses one markup file and reports its syntax diagnostics.</summary>
    /// <param name="file">Path to a .harbor-ui file.</param>
    /// <param name="printIr">Print the parsed IR as a debug tree.</param>
    public static int Check([Argument] string file, bool printIr = false)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            Console.Error.WriteLine("dsl check: a .harbor-ui file is required.");
            return 1;
        }

        if (!File.Exists(file))
        {
            Console.Error.WriteLine($"dsl check: file not found: {file}");
            return 1;
        }

        string text = File.ReadAllText(file);
        var result = MarkupParser.Parse(text);

        foreach (var diagnostic in result.Diagnostics)
            Console.Error.WriteLine($"{file}({diagnostic.Line},{diagnostic.Column}): {diagnostic.Message}");

        if (printIr)
            Console.Out.Write(result.Arena.DebugTree());

        return result.IsValid ? 0 : 1;
    }

    /// <summary>
    ///     #2433: re-interprets markup in dev mode and reports the template
    ///     diff decision. With one file it only builds and reports the initial
    ///     template; with two, the second is the edited version and the
    ///     interpreter reports patch, rebuild or restart.
    /// </summary>
    /// <param name="files">One or two .harbor-ui file paths (before, after).</param>
    public static int Reload([Argument] params string[] files)
    {
        if (files.Length is < 1 or > 2)
        {
            Console.Error.WriteLine("dsl reload: expected a .harbor-ui file and an optional edited file.");
            return 1;
        }

        foreach (var file in files)
        {
            if (!File.Exists(file))
            {
                Console.Error.WriteLine($"dsl reload: file not found: {file}");
                return 1;
            }
        }

        // #2437: the reload decision also goes to the dev-only channel, which
        // no renderer subscribes to. The console lines below belong to this
        // standalone command; the channel is the input for `dsl devlog`.
        var channel = new DevLogChannel(DevLogChannel.DefaultPath);
        if (!DevReloadInterpreter.TryCreate(File.ReadAllText(files[0]), out var interpreter, out var error))
        {
            channel.Write("interpreter", "initial parse rejected: " + error);
            Console.Error.WriteLine("dsl reload: " + error);
            return 1;
        }

        interpreter!.AttachLog(channel);

        Console.Out.WriteLine(
            $"initial: template={interpreter!.TemplateSignature} root={interpreter.LiveRootId} revision={interpreter.Revision}");
        if (files.Length == 1)
            return 0;

        var result = interpreter.Reload(File.ReadAllText(files[1]));
        Console.Out.WriteLine($"{result.Outcome}: {result.Reason}");
        Console.Out.WriteLine(
            $"live: root={result.LiveRootId} revision={result.Revision} preserved=[{string.Join(",", result.PreservedElementIds)}]");
        return result.Outcome == ReloadOutcome.Rejected ? 1 : 0;
    }

    /// <summary>
    ///     #2437: reads the dev-only diagnostic channel in a separate command.
    ///     No CellForge host, event bus or renderer is started, so a live cell
    ///     grid cannot be overwritten by these lines.
    /// </summary>
    /// <param name="devlog">Channel file; defaults to the per-user Harbor dev path.</param>
    /// <param name="count">How many recent records to print.</param>
    public static int Devlog(string? devlog = null, int count = 50)
    {
        string path = string.IsNullOrWhiteSpace(devlog) ? DevLogChannel.DefaultPath : devlog!;
        var entries = DevLogChannel.ReadFile(path, count);
        if (entries.Count == 0)
        {
            Console.Out.WriteLine($"devlog: no records in {path}");
            return 0;
        }

        foreach (var entry in entries)
            Console.Out.WriteLine($"{entry.At:O} [{entry.Source}] {entry.Message}");
        return 0;
    }

    /// <summary>
    ///     #2437: polls a markup file and sends every observation to the dev
    ///     channel only. There is deliberately no console or renderer output:
    ///     `dsl devlog` is the reader, in its own process.
    /// </summary>
    /// <param name="file">Path to a .harbor-ui file.</param>
    /// <param name="devlog">Channel file; defaults to the per-user Harbor dev path.</param>
    public static async Task<int> Watch([Argument] string file, string? devlog = null)
    {
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
        {
            Console.Error.WriteLine("dsl watch: a .harbor-ui file is required.");
            return 1;
        }

        string path = string.IsNullOrWhiteSpace(devlog) ? DevLogChannel.DefaultPath : devlog!;
        var channel = new DevLogChannel(path);
        if (!DevReloadInterpreter.TryCreate(File.ReadAllText(file), out var interpreter, out var error))
        {
            channel.Write("watcher", "initial parse rejected: " + error);
            Console.Error.WriteLine("dsl watch: " + error);
            return 1;
        }

        interpreter!.AttachLog(channel);
        channel.Write("watcher", $"watching {file}");

        using var watcher = new DslDevFileWatcher(file, interpreter, channel);
        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, args) =>
        {
            args.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            await watcher.RunUntilCancelledAsync(cts.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }

        return 0;
    }

    /// <summary>Prints the component table as a JSON schema or C# enum.</summary>
    /// <param name="format">Output format: json (default) or csharp.</param>
    public static int Types(string format = "json")
    {
        var kinds = Enum.GetValues<SpecKind>();
        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            Console.Out.Write(JsonSchema(kinds));
            return 0;
        }

        if (string.Equals(format, "csharp", StringComparison.OrdinalIgnoreCase))
        {
            Console.Out.Write(CSharpEnum(kinds));
            return 0;
        }

        Console.Error.WriteLine($"dsl types: unknown format '{format}'. Use json or csharp.");
        return 1;
    }

    /// <summary>
    ///     Builds the canonical markup name from the same enum the parser
    ///     resolves, so the emitted schema cannot drift from the grammar.
    /// </summary>
    private static string MarkupName(SpecKind kind)
    {
        string name = kind.ToString();
        var output = new StringBuilder(name.Length + 4);
        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];
            if (index > 0 && char.IsUpper(character) && !char.IsUpper(name[index - 1]))
                output.Append('_');
            output.Append(char.ToLowerInvariant(character));
        }

        return output.ToString();
    }

    private static string JsonSchema(IReadOnlyList<SpecKind> kinds)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("$schema", "https://json-schema.org/draft/2020-12/schema");
            writer.WriteString("title", "Harbor UI component kinds");
            writer.WriteString("description", "Components accepted by the harbor-ui markup grammar. Generated from SpecKind.");
            writer.WriteStartArray("enum");
            foreach (var kind in kinds)
                writer.WriteStringValue(MarkupName(kind));
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n";
    }

    private static string CSharpEnum(IReadOnlyList<SpecKind> kinds)
    {
        var output = new StringBuilder();
        output.AppendLine("// Generated by harbor dsl types --format csharp.");
        output.AppendLine("// Source of truth: Harbor.Abstractions.Dsl.SpecKind.");
        output.AppendLine("namespace Harbor.Dsl;");
        output.AppendLine();
        output.AppendLine("public enum DslComponentKind");
        output.AppendLine("{");
        foreach (var kind in kinds)
            output.Append("    ").Append(kind).AppendLine(",");
        output.AppendLine("}");
        return output.ToString();
    }
}
#endif
