using System.Reflection;
using Harbor.Abstractions.Events;
using Harbor.TestKit;
using Harbor.Tui.CellForge.Streaming;

namespace Harbor.Tui.CellForge.Tests;

/// <summary>
///     #1485 — the part of the render-path Visitor a compiler cannot check:
///     an <see cref="AgentEvent"/> union member with no arm. The bridge used to
///     dispatch with a typeof-switch, so a new kind was silently dropped. Now
///     <see cref="ChatScreenBridge.AgentEventVisitor"/> names every member, and
///     these tests reflect over the union so an unarmed subtype fails a test
///     instead of disappearing from the render path.
/// </summary>
/// <remarks>
///     The nested LlmEvent dispatch is owned by <c>LlmEventVisitor</c> (#1494,
///     PR #2281) and is deliberately out of scope here.
/// </remarks>
public class AgentEventVisitorCensusTests
{
    [Test]
    public async Task Every_AgentEvent_Subtype_Has_A_Visitor_Arm()
    {
        var gaps = new List<string>();
        Census(typeof(AgentEvent), typeof(ChatScreenBridge.AgentEventVisitor), gaps);

        await Assert.That(gaps.Count).IsEqualTo(0).Because(string.Join("\n", gaps));
    }

    /// <summary>Non-vacuity: a kind outside the closed union is refused, not dropped.</summary>
    [Test]
    public async Task Accept_Refuses_An_Unknown_Kind()
    {
        var refused = Assert.Throws<NotSupportedException>(
            () => new AgentProbe().Route(new RogueEvent()));

        await Assert.That(refused.Message.Contains("RogueEvent", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    ///     #2704 D02. Reflection proves every arm exists; routing a generated
    ///     transcript proves the visitor accepts the kinds a real run carries.
    /// </summary>
    [Test]
    public async Task GeneratedTranscripts_Route_WithoutRejection()
    {
        var probe = new AgentProbe();
        var mismatches = new List<string>();
        int routed = 0;

        foreach (int seed in new[] { 1, 2, 3, 5, 8 })
        {
            foreach (AgentEvent item in Arbitrary.EventStream(new Random(seed), 6))
            {
                string kind = probe.Route(item);
                if (!string.Equals(kind, item.GetType().Name, StringComparison.Ordinal))
                {
                    mismatches.Add($"seed {seed}: {item.GetType().Name} routed as {kind}");
                }

                routed++;
            }
        }

        await Assert.That(mismatches).IsEmpty().Because(string.Join("\n", mismatches));
        await Assert.That(routed).IsGreaterThan(0)
            .Because("the census must not pass on an empty generated transcript");
    }

    private static void Census(Type union, Type visitor, List<string> gaps)
    {
        Type[] kinds = [.. union.Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && union.IsAssignableFrom(t))];
        if (kinds.Length == 0)
        {
            gaps.Add($"{union.Name}: empty census, the check would pass for nothing.");
        }

        foreach (Type kind in kinds)
        {
            bool armed = Array.Exists(
                visitor.GetMethods(BindingFlags.Public | BindingFlags.Instance),
                m => m.Name == "Visit" && m.GetParameters() is [{ ParameterType: var p }] && p == kind);
            if (!armed)
            {
                gaps.Add($"{visitor.Name} has no Visit({kind.Name}) arm.");
                continue;
            }

            try
            {
                if (!string.Equals(new AgentProbe().Route((AgentEvent)Make(kind)), kind.Name, StringComparison.Ordinal))
                {
                    gaps.Add($"Accept misroutes {kind.Name}.");
                }
            }
            catch (NotSupportedException ex)
            {
                gaps.Add($"Accept refuses {kind.Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    ///     Inert placeholder for each arm: strings get a marker, value types
    ///     their default, everything else null. Visit bodies here only record
    ///     which arm was reached; they touch no bridge state.
    /// </summary>
    private static object Make(Type kind)
    {
        ConstructorInfo ctor = kind.GetConstructors()
            .OrderBy(c => c.GetParameters().Length)
            .First();
        object?[] values = [.. ctor.GetParameters().Select(p =>
            p.ParameterType == typeof(string) ? "s"
            : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)];
        return ctor.Invoke(values);
    }

    private sealed class AgentProbe : ChatScreenBridge.AgentEventVisitor
    {
        private string? _seen;
        public string Route(AgentEvent e) { base.Accept(e); return _seen ?? "<none>"; }
        private string Mark(string n) => _seen = n;

        public override void Visit(AgentStartEvent e) => Mark(nameof(AgentStartEvent));
        public override void Visit(TurnStartEvent e) => Mark(nameof(TurnStartEvent));
        public override void Visit(MessageStartEvent e) => Mark(nameof(MessageStartEvent));
        public override void Visit(MessageUpdateEvent e) => Mark(nameof(MessageUpdateEvent));
        public override void Visit(MessageEndEvent e) => Mark(nameof(MessageEndEvent));
        public override void Visit(ToolExecutionStartEvent e) => Mark(nameof(ToolExecutionStartEvent));
        public override void Visit(ToolExecutionUpdateEvent e) => Mark(nameof(ToolExecutionUpdateEvent));
        public override void Visit(ToolExecutionEndEvent e) => Mark(nameof(ToolExecutionEndEvent));
        public override void Visit(TurnEndEvent e) => Mark(nameof(TurnEndEvent));
        public override void Visit(AgentEndEvent e) => Mark(nameof(AgentEndEvent));
        public override void Visit(AgentErrorEvent e) => Mark(nameof(AgentErrorEvent));
        public override void Visit(PluginBlockedEvent e) => Mark(nameof(PluginBlockedEvent));
        public override void Visit(CompactionStartedEvent e) => Mark(nameof(CompactionStartedEvent));
        public override void Visit(CompactionCompletedEvent e) => Mark(nameof(CompactionCompletedEvent));
        public override void Visit(CompactionFailedEvent e) => Mark(nameof(CompactionFailedEvent));
        public override void Visit(SessionStatsEvent e) => Mark(nameof(SessionStatsEvent));
        public override void Visit(SessionChangedEvent e) => Mark(nameof(SessionChangedEvent));
    }

    private sealed record RogueEvent : AgentEvent;
}
