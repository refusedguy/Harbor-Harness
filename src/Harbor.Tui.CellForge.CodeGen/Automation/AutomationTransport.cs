namespace Harbor.Tui.CellForge.CodeGen.Automation;

/// <summary>
///     Server-sent-event transport for the automation protocol (issue #2438).
///     Only <c>data:</c> lines are commands: any other line (a log, a banner,
///     a terminal escape) is ignored so it cannot leak into the message
///     stream. Requests and responses correlate by the id in the JSON body.
/// </summary>
public sealed class AutomationTransport
{
    private const string DataPrefix = "data:";

    private readonly AutomationSession _session;
    private readonly TextReader _input;
    private readonly TextWriter _output;

    /// <summary>Creates a transport over a request source and a response sink.</summary>
    public AutomationTransport(AutomationSession session, TextReader input, TextWriter output)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    /// <summary>Number of lines rejected because they were not <c>data:</c> lines.</summary>
    public int IgnoredLines { get; private set; }

    /// <summary>Number of commands that produced a correlated response.</summary>
    public int ProcessedLines { get; private set; }

    /// <summary>
    ///     Handles one input line. Returns false when the line was not an SSE
    ///     command and therefore produced no response.
    /// </summary>
    public bool ProcessLine(string? line)
    {
        if (line is null)
            return false;
        if (!line.StartsWith(DataPrefix, StringComparison.Ordinal))
        {
            IgnoredLines++;
            return false;
        }

        var payload = line[DataPrefix.Length..].TrimStart();
        string response = _session.ExecuteJson(payload);
        _output.Write(DataPrefix);
        _output.Write(' ');
        _output.Write(response);
        _output.Write('\n');
        _output.Flush();
        ProcessedLines++;
        return true;
    }

    /// <summary>Reads commands until end of input or cancellation.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line = await _input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
                return;
            ProcessLine(line);
        }
    }
}
