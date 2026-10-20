using Harbor.Abstractions.Events;

namespace Harbor.Providers.Tests;

/// <summary>
///     Drains an LLM event stream into a list.
/// </summary>
/// <remarks>
///     <para>
///         Two provider suites assert on the events a client emitted, and both
///         wrote the same drain loop.
///     </para>
/// </remarks>
internal static class StreamCollector
{
    internal static async Task<List<LlmEvent>> CollectAsync(IAsyncEnumerable<LlmEvent> stream)
    {
        var events = new List<LlmEvent>();
        await foreach (var evt in stream)
        {
            events.Add(evt);
        }
        return events;
    }
}
