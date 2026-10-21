using System.Text;
using Harbor.Abstractions.Extensions;

namespace Harbor.Application.Agents;

/// <summary>
///     Counts CONSECUTIVE identical tool calls — the same tool, with the same
///     arguments — and, once the chain reaches the configured threshold, tells
///     the MODEL that it is repeating itself (#1756).
/// </summary>
/// <remarks>
/// <para>
///     <b>It never blocks.</b> The guard counts and advises; the call is
///     executed exactly as it would be without the guard. A stuck agent gets
///     the chance to change its mind on its own — that is the whole point of
///     an advisory guard, and the reason it does not add a second limit axis
///     next to <see cref="RunBudgetTracker" /> (#404) or the step budget.
/// </para>
/// <para>
///     <b>Where the counter lives.</b> One instance per RUN, created by the
///     loop and handed down to the turn runner — never a static, never a
///     container singleton. A process-wide counter would attribute one run's
///     chain to the next, and two sessions running side by side would count
///     each other's calls.
/// </para>
/// <para>
///     <b>What counts as "the same call".</b> The tool name plus the
///     CANONICALIZED arguments: the argument object's keys are sorted, so
///     <c>{"a":1,"b":2}</c> and <c>{"b":2,"a":1}</c> are ONE call. A call
///     with different arguments starts a NEW chain — the model changed its
///     mind, which is the opposite of a loop.
/// </para>
/// <para>
///     <b>A user interjection ends the chain.</b> Repetition ACROSS a user
///     message is not the same loop: the user may have asked for exactly that
///     call again. <see cref="SyncUserContext" /> is where that is noticed.
/// </para>
/// </remarks>
public sealed class RepeatToolGuard
{
    /// <summary>
    ///     The threshold the shipped agents run with: the third consecutive
    ///     identical call is the first one the model is told about.
    /// </summary>
    public const int DefaultThreshold = 3;

    /// <summary>
    ///     The threshold that turns the guard OFF. Zero consecutive identical
    ///     calls can never be reached, so the chain is counted and nothing is
    ///     ever said about it.
    /// </summary>
    public const int DisabledThreshold = 0;

    /// <summary>
    ///     How many characters of the canonical arguments the reminder quotes.
    ///     The cap bounds the REMINDER, never the detection: the chain is
    ///     always keyed on the full canonical string.
    /// </summary>
    public const int ArgumentsPreviewChars = 500;

    private readonly int _threshold;
    private string? _chainToolName;
    private string? _chainArguments;
    private int _chainCount;
    private int _seenUserMessages = -1;

    /// <summary>
    ///     Create a guard that speaks up at <paramref name="threshold" />
    ///     consecutive identical calls.
    /// </summary>
    /// <param name="threshold">
    ///     The count at which the model is told. <see cref="DisabledThreshold" />
    ///     turns the guard off; a negative value is a programming error.
    /// </param>
    public RepeatToolGuard(int threshold = DefaultThreshold)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(threshold);
        _threshold = threshold;
    }

    /// <summary>The count at which the model is told. Zero means the guard is off.</summary>
    public int Threshold => _threshold;

    /// <summary>
    ///     How many identical calls the current chain holds. Zero when there is
    ///     no chain.
    /// </summary>
    public int ConsecutiveCount => _chainCount;

    /// <summary>The tool the current chain repeats, or <c>null</c> when there is no chain.</summary>
    public string? ChainToolName => _chainToolName;

    /// <summary>
    ///     Count one tool call and say what the model needs to hear about it.
    /// </summary>
    /// <param name="toolName">The tool the model called.</param>
    /// <param name="arguments">The call's arguments, as the model wrote them.</param>
    /// <returns>
    ///     The reminder to hand the model, or <c>None</c> while the chain is
    ///     below the threshold. Absence is a value, not a null to guess at
    ///     (#1557).
    /// </returns>
    public Maybe<RepeatReminder> Observe(string toolName, JsonElement arguments)
    {
        string canonical = Canonicalize(arguments);

        if (string.Equals(_chainToolName, toolName, StringComparison.Ordinal)
            && string.Equals(_chainArguments, canonical, StringComparison.Ordinal))
        {
            _chainCount++;
        }
        else
        {
            // A different call starts a new chain: the count belongs to THIS
            // call, not to the run.
            _chainToolName = toolName;
            _chainArguments = canonical;
            _chainCount = 1;
        }

        if (_threshold == DisabledThreshold || _chainCount < _threshold)
        {
            return Maybe<RepeatReminder>.None;
        }

        // The FIRST crossing of the threshold is the gentle one; every repeat
        // past it is the detailed one. The wording escalates with the chain,
        // so a model that ignored the first nudge gets a firmer one.
        return Maybe<RepeatReminder>.From(
            new RepeatReminder(toolName, _chainCount, canonical, _chainCount > _threshold));
    }

    /// <summary>
    ///     Forget the current chain. The next call starts a fresh one.
    /// </summary>
    public void Reset()
    {
        _chainToolName = null;
        _chainArguments = null;
        _chainCount = 0;
    }

    /// <summary>
    ///     Tell the guard what the history holds. A user message that joined
    ///     the history since the last call ends the chain: repetition ACROSS a
    ///     user interjection is not the same loop.
    /// </summary>
    /// <param name="history">The run's message history, oldest first.</param>
    /// <remarks>
    ///     The first call only records what the history held — the run's own
    ///     prompt is already in there, and treating it as an interjection would
    ///     reset the chain on every turn.
    /// </remarks>
    public void SyncUserContext(IReadOnlyList<AgentMessage> history)
    {
        int users = 0;
        for (int i = 0; i < history.Count; i++)
        {
            if (history[i] is UserMessage)
            {
                users++;
            }
        }

        if (_seenUserMessages < 0)
        {
            _seenUserMessages = users;
            return;
        }

        if (users == _seenUserMessages)
        {
            return;
        }

        _seenUserMessages = users;
        Reset();
    }

    /// <summary>
    ///     The canonical form of a call's arguments: the same JSON with every
    ///     object's keys sorted, so two calls that differ only in the order
    ///     their arguments were written in are ONE call.
    /// </summary>
    /// <param name="arguments">The arguments as the model wrote them.</param>
    /// <returns>The canonical text the chain is keyed on.</returns>
    /// <remarks>
    ///     Scalars keep the JSON text they arrived with, so the canonical form
    ///     is a function of the bytes the provider sent — the same call twice
    ///     produces the same string, and a different call does not.
    /// </remarks>
    public static string Canonicalize(JsonElement arguments)
    {
        using var pooled = StringBuilderPool.Rent(128);
        WriteCanonical(pooled.Builder, arguments);
        return pooled.Builder.ToString();
    }

    /// <summary>
    ///     Write one element in canonical form: objects with sorted keys,
    ///     arrays in the order they were written (order is meaning there),
    ///     scalars as they arrived.
    /// </summary>
    private static void WriteCanonical(StringBuilder builder, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                List<JsonProperty> properties = SortedProperties(element);
                builder.Append('{');
                for (int i = 0; i < properties.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    WriteName(builder, properties[i].Name);
                    builder.Append(':');
                    WriteCanonical(builder, properties[i].Value);
                }

                builder.Append('}');
                break;
            }

            case JsonValueKind.Array:
            {
                builder.Append('[');
                int index = 0;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (index++ > 0)
                    {
                        builder.Append(',');
                    }

                    WriteCanonical(builder, item);
                }

                builder.Append(']');
                break;
            }

            case JsonValueKind.Undefined:
                // GetRawText() throws on Undefined; a call with no arguments at
                // all is still a call, and it must have a canonical form.
                builder.Append("null");
                break;

            default:
                builder.Append(element.GetRawText());
                break;
        }
    }

    /// <summary>
    ///     The object's properties, ordered by name. The order is the WHOLE
    ///     point: two writes of the same object in a different order are the
    ///     same call.
    /// </summary>
    private static List<JsonProperty> SortedProperties(JsonElement element)
    {
        var properties = new List<JsonProperty>();
        foreach (JsonProperty property in element.EnumerateObject())
        {
            properties.Add(property);
        }

        properties.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));
        return properties;
    }

    /// <summary>
    ///     Write a property name as a quoted, escaped JSON string. Only the
    ///     quote and the backslash need escaping for the encoding to be
    ///     unambiguous, and unambiguous is all the canonical form has to be.
    /// </summary>
    private static void WriteName(StringBuilder builder, string name)
    {
        builder.Append('"');
        foreach (char c in name)
        {
            if (c is '"' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        builder.Append('"');
    }
}

/// <summary>
///     What the guard wants the model to know about a repeated call: the tool
///     that repeated, how many times in a row, and the arguments it repeated
///     with.
/// </summary>
/// <param name="ToolName">The tool that repeated.</param>
/// <param name="Count">How many identical calls the chain holds.</param>
/// <param name="CanonicalArguments">The canonical form of the repeated arguments.</param>
/// <param name="Escalated">
///     Whether the chain is already PAST the threshold — the first crossing
///     gets the gentle wording, every repeat after it the detailed one.
/// </param>
public sealed record RepeatReminder(
    string ToolName,
    int Count,
    string CanonicalArguments,
    bool Escalated)
{
    /// <summary>
    ///     The text to hand the model. It rides at the HEAD of the repeated
    ///     call's own result, so it is where the model is already looking —
    ///     and where the model-context trim keeps it (that trim keeps the
    ///     head of a long output and cuts the tail).
    /// </summary>
    public string Text => Escalated ? DetailedText() : GentleText();

    /// <summary>
    ///     The first crossing of the threshold: a nudge, without the arguments
    ///     spelled out again.
    /// </summary>
    private string GentleText() =>
        $"[harbor: repeated tool call — '{ToolName}' was called {Count} times in a row with exactly the same arguments]\n"
        + "The previous result cannot tell you anything new. Read it once more, then either change the arguments "
        + "or take a different approach.";

    /// <summary>
    ///     Every repeat past the threshold: the arguments are quoted back, so
    ///     the model can see exactly which call is being repeated.
    /// </summary>
    private string DetailedText() =>
        $"[harbor: repeated tool call — '{ToolName}' was called {Count} times in a row with exactly the same arguments: {Preview()}]\n"
        + "Repeating the same call cannot produce a different result. Use what the previous calls already told you, "
        + "change the arguments, or state what is still missing.";

    /// <summary>
    ///     The quoted arguments, capped. The cap bounds the reminder, never the
    ///     detection — the chain was keyed on the full canonical string.
    /// </summary>
    private string Preview()
    {
        if (CanonicalArguments.Length <= RepeatToolGuard.ArgumentsPreviewChars)
        {
            return CanonicalArguments;
        }

        int hidden = CanonicalArguments.Length - RepeatToolGuard.ArgumentsPreviewChars;
        return string.Concat(
            CanonicalArguments.AsSpan(0, RepeatToolGuard.ArgumentsPreviewChars),
            $" (+{hidden} more chars)");
    }
}
