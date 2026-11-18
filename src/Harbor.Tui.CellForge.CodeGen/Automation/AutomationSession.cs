using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Harbor.Tui.CellForge.CodeGen.Automation;

/// <summary>
///     The versioned method table of the headless automation protocol (issue
///     #2438). One session owns the live tree snapshot and a virtual clock,
///     validates every request, and returns an id-correlated response with a
///     stable error code. No renderer, host or terminal is started.
/// </summary>
public sealed class AutomationSession
{
    /// <summary>Protocol version reported by the capabilities handshake.</summary>
    public const string ProtocolVersion = "1.0";

    /// <summary>The versioned method table: one handler per public method.</summary>
    private static readonly Dictionary<string, Func<AutomationSession, AutomationRequest, AutomationResponse>> MethodTable =
        new(StringComparer.Ordinal)
        {
            ["capabilities"] = static (session, request) => session.Capabilities(request),
            ["getTree"] = static (session, request) => session.GetTree(request),
            ["testId"] = static (session, request) => session.FindTestId(request),
            ["clock"] = static (session, request) => session.ClockMethod(request)
        };

    private static readonly string[] SupportedMethods = [.. MethodTable.Keys];

    /// <summary>Clock actions. Fast-forward advances time in one step; now is a read.</summary>
    private static readonly Dictionary<string, Action<AutomationSession, long>> ClockActions =
        new(StringComparer.Ordinal)
        {
            ["pause"] = static (session, _) => session.Clock.Pause(),
            ["resume"] = static (session, _) => session.Clock.Resume(),
            ["fastForward"] = static (session, ms) => session.Clock.FastForward(ms),
            ["now"] = static (_, _) => { }
        };

    private readonly AutomationNode? _root;

    /// <summary>Creates a session over one headless tree snapshot.</summary>
    public AutomationSession(AutomationNode? root) => _root = root;

    /// <summary>Virtual clock controlled by the clock method.</summary>
    public AutomationClock Clock { get; } = new();

    /// <summary>
    ///     Decodes one JSON request, dispatches it and returns the complete
    ///     response JSON. Malformed input still gets a correlated Protocol
    ///     error, so a transport never has to invent an error payload.
    /// </summary>
    public string ExecuteJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return WriteResponse(Failure(string.Empty, AutomationErrorCode.Protocol, "empty request"));

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return WriteResponse(Failure(string.Empty, AutomationErrorCode.Protocol, "invalid JSON: " + ex.Message));
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return WriteResponse(Failure(string.Empty, AutomationErrorCode.Protocol, "request must be an object"));

            string id = string.Empty;
            if (root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String)
                id = idElement.GetString() ?? string.Empty;
            if (id.Length == 0)
                return WriteResponse(Failure(string.Empty, AutomationErrorCode.Protocol, "'id' must be a non-empty string"));

            if (!root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
                return WriteResponse(Failure(id, AutomationErrorCode.Protocol, "'method' must be a string"));

            var method = methodElement.GetString() ?? string.Empty;
            var parameters = root.TryGetProperty("params", out var paramsElement) ? paramsElement : default;
            if (parameters.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined))
                return WriteResponse(Failure(id, AutomationErrorCode.Protocol, "'params' must be an object"));

            var response = Execute(new AutomationRequest(id, method, parameters));
            return WriteResponse(response);
        }
    }

    /// <summary>Dispatches a validated request through the method table.</summary>
    public AutomationResponse Execute(AutomationRequest request)
    {
        if (!MethodTable.TryGetValue(request.Method, out var handler))
            return Failure(request.Id, AutomationErrorCode.Protocol,
                "unknown method '" + request.Method + "'; supported: " + string.Join(",", SupportedMethods));
        return handler(this, request);
    }

    private AutomationResponse Capabilities(AutomationRequest request) =>
        Success(request.Id, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("protocol", ProtocolVersion);
            writer.WritePropertyName("methods");
            writer.WriteStartArray();
            foreach (var method in SupportedMethods)
                writer.WriteStringValue(method);
            writer.WriteEndArray();
            writer.WriteEndObject();
        });

    private AutomationResponse GetTree(AutomationRequest request)
    {
        var error = ReadString(request.Parameters, "testId", isRequired: false, out var testId);
        if (error is not null)
            return Failure(request.Id, AutomationErrorCode.Protocol, error);

        AutomationNode? node;
        if (testId is null)
        {
            node = _root;
            if (node is null)
                return Failure(request.Id, AutomationErrorCode.NotFound, "no live tree");
        }
        else
        {
            var matches = Match(testId);
            if (matches.Count == 0)
                return Failure(request.Id, AutomationErrorCode.NotFound, "no node with testId '" + testId + "'");
            if (matches.Count > 1)
                return Failure(request.Id, AutomationErrorCode.Ambiguous,
                    testId + " matches " + matches.Count + " nodes");
            node = matches[0];
        }

        return Success(request.Id, writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("tree");
            WriteNode(writer, node);
            writer.WriteEndObject();
        });
    }

    private AutomationResponse FindTestId(AutomationRequest request)
    {
        var error = ReadString(request.Parameters, "value", isRequired: true, out var value);
        if (error is not null)
            return Failure(request.Id, AutomationErrorCode.Protocol, error);

        error = ReadNonNegativeInt64(request.Parameters, "timeoutMs", isRequired: false, out var timeoutMs);
        if (error is not null)
            return Failure(request.Id, AutomationErrorCode.Protocol, error);

        var matches = Match(value!);
        if (matches.Count == 0 && timeoutMs > 0)
        {
            // Headless time only moves when asked: advance to the deadline in
            // one deterministic step, then resolve against the same tree.
            Clock.FastForward(timeoutMs);
            matches = Match(value!);
        }

        if (matches.Count == 0)
            return Failure(request.Id, timeoutMs > 0 ? AutomationErrorCode.Timeout : AutomationErrorCode.NotFound,
                "no node with testId '" + value + "'");
        if (matches.Count > 1)
            return Failure(request.Id, AutomationErrorCode.Ambiguous,
                value + " matches " + matches.Count + " nodes");

        return Success(request.Id, writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("node");
            WriteNode(writer, matches[0]);
            writer.WriteEndObject();
        });
    }

    private AutomationResponse ClockMethod(AutomationRequest request)
    {
        var error = ReadString(request.Parameters, "action", isRequired: true, out var action);
        if (error is not null)
            return Failure(request.Id, AutomationErrorCode.Protocol, error);

        long ms = 0;
        if (string.Equals(action, "fastForward", StringComparison.Ordinal))
        {
            error = ReadNonNegativeInt64(request.Parameters, "ms", isRequired: true, out ms);
            if (error is not null)
                return Failure(request.Id, AutomationErrorCode.Protocol, error);
        }

        if (!ClockActions.TryGetValue(action!, out var apply))
            return Failure(request.Id, AutomationErrorCode.Protocol,
                "unknown clock action '" + action + "'");
        apply(this, ms);

        return Success(request.Id, writer =>
        {
            writer.WriteStartObject();
            writer.WritePropertyName("clock");
            WriteClock(writer);
            writer.WriteEndObject();
        });
    }

    private IReadOnlyList<AutomationNode> Match(string testId)
    {
        var matches = new List<AutomationNode>();
        if (_root is null)
            return matches;
        foreach (var node in HeadlessTree.Flatten(_root))
        {
            if (string.Equals(node.TestId, testId, StringComparison.Ordinal))
                matches.Add(node);
        }

        return matches;
    }

    private void WriteClock(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteNumber("nowMs", Clock.NowMilliseconds);
        writer.WriteBoolean("paused", Clock.IsPaused);
        writer.WriteEndObject();
    }

    private static void WriteNode(Utf8JsonWriter writer, AutomationNode node)
    {
        writer.WriteStartObject();
        writer.WriteNumber("id", node.Id);
        writer.WriteString("type", node.Type);
        if (node.TestId is null)
            writer.WriteNull("testId");
        else
            writer.WriteString("testId", node.TestId);

        writer.WritePropertyName("bounds");
        writer.WriteStartObject();
        writer.WriteNumber("x", node.Bounds.X);
        writer.WriteNumber("y", node.Bounds.Y);
        writer.WriteNumber("width", node.Bounds.Width);
        writer.WriteNumber("height", node.Bounds.Height);
        writer.WriteEndObject();

        writer.WritePropertyName("children");
        writer.WriteStartArray();
        foreach (var child in node.Children)
            WriteNode(writer, child);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static AutomationResponse Success(string id, Action<Utf8JsonWriter> write) =>
        new(id, BuildResult(write), null);

    private static AutomationResponse Failure(string id, AutomationErrorCode code, string message) =>
        new(id, null, new AutomationError(code, message));

    private static JsonElement BuildResult(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            write(writer);
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    /// <summary>Serializes a response with the request id at the top level.</summary>
    public static string WriteResponse(AutomationResponse response)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("id", response.Id);
            if (response.Error is { } error)
            {
                writer.WritePropertyName("error");
                writer.WriteStartObject();
                writer.WriteString("code", error.Code.ToString());
                writer.WriteString("message", error.Message);
                writer.WriteEndObject();
            }
            else
            {
                writer.WritePropertyName("result");
                if (response.Result is { } result)
                    result.WriteTo(writer);
                else
                    writer.WriteNullValue();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string? ReadString(JsonElement parameters, string name, bool isRequired, out string? value)
    {
        value = null;
        if (parameters.ValueKind == JsonValueKind.Undefined)
            return isRequired ? "'" + name + "' is required" : null;
        if (!parameters.TryGetProperty(name, out var element))
            return isRequired ? "'" + name + "' is required" : null;
        if (element.ValueKind != JsonValueKind.String)
            return "'" + name + "' must be a string";

        value = element.GetString();
        if (string.IsNullOrEmpty(value))
            return isRequired ? "'" + name + "' must not be empty" : null;
        return null;
    }

    private static string? ReadNonNegativeInt64(JsonElement parameters, string name, bool isRequired, out long value)
    {
        value = 0;
        if (parameters.ValueKind == JsonValueKind.Undefined)
            return isRequired ? "'" + name + "' is required" : null;
        if (!parameters.TryGetProperty(name, out var element))
            return isRequired ? "'" + name + "' is required" : null;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out value) || value < 0)
            return "'" + name + "' must be a non-negative integer";
        return null;
    }
}
