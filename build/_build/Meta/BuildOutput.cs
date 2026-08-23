using System.Globalization;
using System.Text;
using System.Text.Json;
namespace Harbor.Build.Meta;
/// <summary>
///     Output format for the build tool. <see cref="Pretty" /> prints the
///     classic human-readable log (<c>==> Target: message</c>) to stdout.
///     <see cref="Json" /> emits JSON-lines (UTF-8, invariant culture) on
///     stdout and routes all human noise to stderr.
/// </summary>
public enum OutputFormat
{
    Pretty,
    Json
}
/// <summary>
///     Single point of emission for every message the build tool prints.
///     Replaces the former scattered <c>Console.WriteLine</c> calls in
///     <c>Targets/*</c> and <c>Components/*</c>.
/// </summary>
/// <remarks>
///     <para>
///         Channel rules:
///     </para>
///     <list type="bullet">
///         <item>
///             <see cref="OutputFormat.Pretty" /> — everything to stdout, same
///             style as before (<c>==> Compile: done</c>).
///         </item>
///         <item>
///             <see cref="OutputFormat.Json" /> — machine-readable JSON-lines
///             on <b>stdout</b>; human noise (NUKE/msbuild logs, pretty tables)
///             goes to <b>stderr</b>. Contract: parse only lines that start
///             with <c>{</c> and carry <c>"v":1</c>.
///         </item>
///     </list>
///     <para>
///         Envelope kinds: <c>target_start</c>, <c>log</c>, <c>cmd</c>,
///         <c>artifact</c>, <c>target_end</c>, <c>run_end</c>. Every line is a
///         self-contained JSON object with stable fields <c>v</c>, <c>ts</c>,
///         <c>kind</c>. Timestamps are ISO-8601 UTC; durations are integer
///         milliseconds; sizes are bytes. When <see cref="IsDryRun" /> is set,
///         no target performs side effects and <see cref="RunEnd" /> reports
///         <c>status:"planned"</c>.
///     </para>
/// </remarks>
public sealed class BuildOutput
{
    private const int SchemaVersion = 1;
    private readonly TextWriter _stdout;
    private readonly TextWriter? _file;
    private readonly List<string> _failedTargets = new();
    private BuildOutput(OutputFormat format, bool dryRun, TextWriter stdout, TextWriter? file)
    {
        Format = format;
        IsDryRun = dryRun;
        _stdout = stdout;
        _file = file;
        IsJson = format == OutputFormat.Json;
    }
    /// <summary>Creates an output bound to the given format and streams.</summary>
    /// <param name="format">Requested output format.</param>
    /// <param name="dryRun">True when the run must not execute side effects.</param>
    /// <param name="stdout">
    ///     The real process stdout captured before any redirection. In Json
    ///     mode this stream carries machine lines while <see cref="Console.Out" />
    ///     has been re-pointed to stderr by <c>Build.OnBuildInitialized</c>.
    /// </param>
    /// <param name="outFile">
    ///     Optional path passed via <c>--out</c>; every emitted line is
    ///     duplicated there (insurance against stream interleaving).
    /// </param>
    public static BuildOutput Create(OutputFormat format, bool dryRun, TextWriter stdout, string? outFile)
    {
        TextWriter? file = null;
        if (!string.IsNullOrWhiteSpace(outFile))
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(outFile));
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }
            file = new StreamWriter(outFile, append: false, Encoding.UTF8);
        }
        return new BuildOutput(format, dryRun, stdout, file);
    }
    /// <summary>Configured output format.</summary>
    public OutputFormat Format { get; }
    /// <summary>True when the format is Json.</summary>
    public bool IsJson { get; }
    /// <summary>True when targets must plan instead of executing.</summary>
    public bool IsDryRun { get; }
    /// <summary>Targets that threw during this run (for the run_end event).</summary>
    public IReadOnlyList<string> FailedTargets => _failedTargets;
    /// <summary>Records a failed target for the final run_end summary.</summary>
    public void MarkFailed(string target) => _failedTargets.Add(target);
    /// <summary>
    ///     Progress line. Pretty: <c>==> Target: message</c>. Json: a
    ///     <c>log</c> event with <c>level:"info"</c> on the machine stream.
    /// </summary>
    public void Info(string target, string message)
    {
        if (IsJson)
        {
            EmitLog("info", target, message);
        }
        else
        {
            Line(_stdout, $"==> {target}: {message}");
        }
    }
    /// <summary>Warning. Same channels as <see cref="Info" />, mirrored to stderr in Json mode.</summary>
    public void Warn(string target, string message)
    {
        if (IsJson)
        {
            EmitLog("warn", target, message);
            Console.Error.WriteLine($"==> {target}: WARNING: {message}");
        }
        else
        {
            Line(_stdout, $"==> {target}: WARNING: {message}");
        }
    }
    /// <summary>Error. Same channels as <see cref="Info" />, mirrored to stderr in Json mode.</summary>
    public void Error(string target, string message)
    {
        if (IsJson)
        {
            EmitLog("error", target, message);
            Console.Error.WriteLine($"==> {target}: ERROR: {message}");
        }
        else
        {
            Line(_stdout, $"==> {target}: ERROR: {message}");
        }
    }
    /// <summary>
    ///     Emits the equivalent command-line of an external invocation. In
    ///     Json mode a <c>cmd</c> event with <c>argv</c> + <c>cwd</c>; in
    ///     Pretty mode a human-readable <c>$ dotnet …</c> line.
    /// </summary>
    public void Cmd(string target, IReadOnlyList<string> argv, string? cwd = null)
    {
        if (IsJson)
        {
            Event("cmd", w =>
            {
                w.WriteString("target", target);
                w.WriteStartArray("argv");
                foreach (var arg in argv)
                {
                    w.WriteStringValue(arg);
                }
                w.WriteEndArray();
                w.WriteString("cwd", cwd ?? Directory.GetCurrentDirectory());
            });
        }
        else
        {
            Line(_stdout, $"==> {target}: $ {string.Join(" ", argv)}");
        }
    }
    /// <summary>
    ///     Emits an artifact event. <paramref name="bytes" /> is null when the
    ///     artifact was only planned (dry-run); <paramref name="planned" />
    ///     marks those cases explicitly without changing the kind.
    /// </summary>
    public void Artifact(string target, string path, long? bytes = null, bool planned = false)
    {
        if (IsJson)
        {
            Event("artifact", w =>
            {
                w.WriteString("target", target);
                w.WriteString("path", path);
                if (bytes.HasValue)
                {
                    w.WriteNumber("bytes", bytes.Value);
                }
                if (planned)
                {
                    w.WriteBoolean("planned", true);
                }
            });
        }
        else
        {
            var size = bytes.HasValue ? $" ({HumanSize(bytes.Value)})" : string.Empty;
            var marker = planned ? " [planned]" : string.Empty;
            Line(_stdout, $"==> {target}: artifact {path}{size}{marker}");
        }
    }
    /// <summary>Emits a target_start event (no-op in Pretty mode).</summary>
    public void TargetStart(string target)
    {
        if (IsJson)
        {
            Event("target_start", w => w.WriteString("target", target));
        }
    }
    /// <summary>Emits a target_end event with status and duration.</summary>
    public void TargetEnd(string target, string status, long durationMs)
    {
        if (IsJson)
        {
            Event("target_end", w =>
            {
                w.WriteString("target", target);
                w.WriteString("status", status);
                w.WriteNumber("durationMs", durationMs);
            });
        }
        else
        {
            Line(_stdout, $"==> {target}: done ({durationMs} ms)");
        }
    }
    /// <summary>Emits the final run_end event with overall status and exit code.</summary>
    public void RunEnd(string status, IReadOnlyList<string> failed, int exitCode)
    {
        if (IsJson)
        {
            Event("run_end", w =>
            {
                w.WriteString("status", status);
                w.WriteStartArray("failed");
                foreach (var name in failed)
                {
                    w.WriteStringValue(name);
                }
                w.WriteEndArray();
                w.WriteNumber("exitCode", exitCode);
            });
        }
        else
        {
            var failedNote = failed.Count > 0 ? $" — failed: {string.Join(", ", failed)}" : string.Empty;
            Line(_stdout, $"=> build {status}{failedNote}");
        }
        Flush();
    }
    /// <summary>
    ///     Writes a complete single-line JSON document (used by the meta
    ///     commands <c>list</c>/<c>doctor</c>/<c>what</c>). The pretty
    ///     renderer runs only in Pretty mode and must use
    ///     <see cref="Human" /> so it lands on stderr in Json mode.
    /// </summary>
    public void EmitDocument(string json, Action prettyRenderer)
    {
        if (IsJson)
        {
            Line(_stdout, json);
        }
        else
        {
            prettyRenderer();
        }
    }
    /// <summary>
    ///     Human-oriented text: stdout in Pretty mode, stderr in Json mode
    ///     (so the machine stream stays parseable).
    /// </summary>
    public void Human(string message) => Line(IsJson ? Console.Error : _stdout, message);
    private void EmitLog(string level, string target, string message)
    {
        Event("log", w =>
        {
            w.WriteString("level", level);
            w.WriteString("target", target);
            w.WriteString("message", message);
        });
    }
    private void Event(string kind, Action<Utf8JsonWriter> body)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", SchemaVersion);
            writer.WriteString("ts", Timestamp());
            writer.WriteString("kind", kind);
            body(writer);
            writer.WriteEndObject();
        }
        Line(_stdout, Encoding.UTF8.GetString(buffer.ToArray()));
    }
    private void Line(TextWriter channel, string text)
    {
        channel.WriteLine(text);
        _file?.WriteLine(text);
    }
    internal static string Timestamp() =>
        DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    private static void Flush()
    {
        try
        {
            Console.Out.Flush();
            Console.Error.Flush();
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"build-output flush failed: {ex.Message}");
        }
    }
    internal static string HumanSize(long bytes) => bytes switch
    {
        < 1024L => $"{bytes.ToString(CultureInfo.InvariantCulture)} B",
        < 1024L * 1024 => $"{(bytes / 1024.0).ToString("F1", CultureInfo.InvariantCulture)} KB",
        < 1024L * 1024 * 1024 => $"{(bytes / (1024.0 * 1024)).ToString("F1", CultureInfo.InvariantCulture)} MB",
        _ => $"{(bytes / (1024.0 * 1024 * 1024)).ToString("F2", CultureInfo.InvariantCulture)} GB"
    };
}
