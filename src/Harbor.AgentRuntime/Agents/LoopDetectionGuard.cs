using System.Security.Cryptography;
using System.Text;
using Harbor.Abstractions.Extensions;

namespace Harbor.Application.Agents;

/// <summary>
///     Windowed loop detection (#2212): counts identical tool interactions in
///     the last <see cref="WindowSize" /> calls and reports a loop when one
///     signature appears more than <see cref="MaxRepeats" /> times.
/// </summary>
/// <remarks>
/// <para>
///     Unlike <see cref="RepeatToolGuard" />, this is NOT about consecutive
///     calls. A model that alternates between a repeated call and a different
///     one can still be stuck; the sliding window sees it, a consecutive chain
///     does not. The signature is the Crush shape: tool name + arguments +
///     tool output, hashed to a stable key (see
///     <c>crush/agent/loop_detection.go</c>, <c>getToolInteractionSignature</c>).
/// </para>
/// <para>
///     One instance per RUN, owned by the loop and handed to the turn runner:
///     a process-wide window would attribute one session's calls to another.
/// </para>
/// </remarks>
public sealed class LoopDetectionGuard
{
    /// <summary>How many recent tool calls the window holds (Crush's windowSize).</summary>
    public const int DefaultWindowSize = 10;

    /// <summary>
    ///     The repeat count that trips detection. Following Crush, detection
    ///     fires when a signature appears MORE than this many times, so the
    ///     sixth repeat is the first one reported.
    /// </summary>
    public const int DefaultMaxRepeats = 5;

    private readonly int _windowSize;
    private readonly int _maxRepeats;
    private readonly Queue<string> _window;
    private readonly Dictionary<string, int> _counts;

    /// <summary>Create a guard with the given window and repeat threshold.</summary>
    /// <param name="windowSize">How many recent calls to consider.</param>
    /// <param name="maxRepeats">The repeat count that trips detection.</param>
    public LoopDetectionGuard(
        int windowSize = DefaultWindowSize,
        int maxRepeats = DefaultMaxRepeats)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowSize);
        ArgumentOutOfRangeException.ThrowIfNegative(maxRepeats);
        _windowSize = windowSize;
        _maxRepeats = maxRepeats;
        _window = new Queue<string>(windowSize);
        _counts = new Dictionary<string, int>(StringComparer.Ordinal);
    }

    /// <summary>How many recent calls the window holds.</summary>
    public int WindowSize => _windowSize;

    /// <summary>The repeat count that trips detection.</summary>
    public int MaxRepeats => _maxRepeats;

    /// <summary>How many calls the window currently holds.</summary>
    public int Count => _window.Count;

    /// <summary>
    ///     Add one executed tool call to the window and say whether a loop was
    ///     found. Absence is a value, not a null to guess at (#1557).
    /// </summary>
    /// <param name="toolName">The tool the model called.</param>
    /// <param name="arguments">The call's arguments as the model wrote them.</param>
    /// <param name="output">The tool's result, before any advisory reminder is prepended.</param>
    public Maybe<LoopDetection> Observe(string toolName, JsonElement arguments, string output)
    {
        string signature = Signature(toolName, arguments, output);
        _window.Enqueue(signature);
        _counts[signature] = _counts.GetValueOrDefault(signature) + 1;

        if (_window.Count > _windowSize)
        {
            string evicted = _window.Dequeue();
            int remaining = _counts[evicted] - 1;
            if (remaining == 0)
            {
                _counts.Remove(evicted);
            }
            else
            {
                _counts[evicted] = remaining;
            }
        }

        // Crush does not look for loops until the window is full. A short run
        // has not repeated enough for a repeat count to mean anything.
        if (_window.Count < _windowSize)
        {
            return Maybe<LoopDetection>.None;
        }

        int repeats = _counts[signature];
        if (repeats <= _maxRepeats)
        {
            return Maybe<LoopDetection>.None;
        }

        return Maybe<LoopDetection>.From(
            new LoopDetection(signature, toolName, repeats, _windowSize));
    }

    /// <summary>
    ///     The stable SHA-256 signature of one tool interaction: name, canonical
    ///     arguments and result, separated by NUL so no field can bleed into the
    ///     next. Arguments are canonicalized (keys sorted) so the order the
    ///     model wrote them in does not split one call into two.
    /// </summary>
    public static string Signature(string toolName, JsonElement arguments, string output)
    {
        string canonical = string.Concat(toolName, "\0", RepeatToolGuard.Canonicalize(arguments), "\0", output, "\0");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

/// <summary>
///     A loop the window found: which signature repeated, how often, and over
///     how many calls. The text is what the hard-stop publishes to the event
///     bus so every TUI can show WHY the run ended.
/// </summary>
public sealed record LoopDetection(
    string Signature,
    string ToolName,
    int Repeats,
    int WindowSize)
{
    /// <summary>User-facing reason, published with the hard stop.</summary>
    public string Reason =>
        $"loop detected: '{ToolName}' repeated {Repeats} times within the last {WindowSize} tool calls "
        + "with the same name, arguments and output";
}
