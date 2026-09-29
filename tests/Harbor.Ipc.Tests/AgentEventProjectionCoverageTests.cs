// AgentEventProjectionCoverageTests.cs — #495 guard.
//
// The AgentEvent → HarborEvent projection used to be one switch duplicated in
// two IPC hosts, each ending in `_ => null`. Adding a case in one assembly and
// forgetting the other compiled cleanly and produced a host that silently never
// delivered that event — no error, no log, no failing test. The switch is now a
// single shared table (AgentEventProjector), and THESE tests are what stop it
// from drifting again:
//
//   * Every concrete AgentEvent subtype must be classified — in Handlers or in
//     NoWireCaseReasons, never in neither. Enumerated by REFLECTION over the
//     declared union, so a new event type fails the build the moment it lands.
//   * Every LlmEvent subtype must be classified as delta-carrier or delta-free.
//   * Every HarborEvent case, HarborEventKind member and wire DTO stay 1:1.
//   * The two host assemblies must not construct HarborEvent values themselves.
//
// The sample tables below only supply INSTANCES to project; the classification
// being asserted is discovered by reflection, never read from these tables.

using Harbor.Abstractions.Events;
using Harbor.Abstractions.Models;
using Harbor.Ipc.Protocol;
using TUnit.Assertions;

namespace Harbor.Ipc.Tests;

/// <summary>
///     Exhaustiveness guards for the shared <see cref="AgentEventProjector" />,
///     plus a structural guard that the projection is not re-duplicated into a
///     host again (#495).
/// </summary>
public class AgentEventProjectionCoverageTests
{
    private static readonly AssistantMessage Sample =
        AssistantMessage.Empty("s1", "stub-model");

    /// <summary>One instance per concrete <see cref="AgentEvent" /> subtype.</summary>
    private static readonly Dictionary<Type, AgentEvent> AgentEventSamples = new()
    {
        [typeof(AgentStartEvent)] = new AgentStartEvent("s1", []),
        [typeof(TurnStartEvent)] = new TurnStartEvent(1, "s1"),
        [typeof(MessageStartEvent)] = new MessageStartEvent(Sample),
        [typeof(MessageUpdateEvent)] = new MessageUpdateEvent(new TextDeltaEvent("m1", "hi"), Sample),
        [typeof(MessageEndEvent)] = new MessageEndEvent(Sample),
        [typeof(ToolExecutionStartEvent)] = ToolExecutionStartEvent.Create("tc1", "read", default),
        [typeof(ToolExecutionUpdateEvent)] = new ToolExecutionUpdateEvent("tc1", "partial"),
        [typeof(ToolExecutionEndEvent)] = new ToolExecutionEndEvent("tc1", ToolResult.Success("ok"), false),
        [typeof(TurnEndEvent)] = new TurnEndEvent(Sample, [], "s1"),
        [typeof(AgentEndEvent)] = new AgentEndEvent([]),
        [typeof(AgentErrorEvent)] = new AgentErrorEvent("boom"),
        [typeof(PluginBlockedEvent)] = new PluginBlockedEvent("my-plugin", "timeout", "30s exceeded"),
        [typeof(CompactionStartedEvent)] = new CompactionStartedEvent("s1"),
        [typeof(CompactionCompletedEvent)] = new CompactionCompletedEvent("s1", "summary", 2, 100, TimeSpan.FromSeconds(1)),
        [typeof(CompactionFailedEvent)] = new CompactionFailedEvent("s1", "tokenizer blew up"),
        [typeof(SessionStatsEvent)] = new SessionStatsEvent("s1", SessionMetadata.Empty),
        [typeof(SessionChangedEvent)] = new SessionChangedEvent("s2")
    };

    /// <summary>One instance per concrete <see cref="LlmEvent" /> subtype.</summary>
    private static readonly Dictionary<Type, LlmEvent> LlmEventSamples = new()
    {
        [typeof(TextStartEvent)] = new TextStartEvent("t1"),
        [typeof(TextDeltaEvent)] = new TextDeltaEvent("t1", "tok"),
        [typeof(TextEndEvent)] = new TextEndEvent("t1", "final"),
        [typeof(ThinkingStartEvent)] = new ThinkingStartEvent("h1"),
        [typeof(ThinkingDeltaEvent)] = new ThinkingDeltaEvent("h1", "think"),
        [typeof(ThinkingEndEvent)] = new ThinkingEndEvent("h1", "thought"),
        [typeof(ToolCallStartEvent)] = new ToolCallStartEvent("c1", "read"),
        [typeof(ToolCallDeltaEvent)] = new ToolCallDeltaEvent("c1", "{\"pa"),
        [typeof(ToolCallEndEvent)] = ToolCallEndEvent.Create("c1", "read", default),
        [typeof(StepStartEvent)] = new StepStartEvent(0),
        [typeof(StepFinishEvent)] = new StepFinishEvent(0, "tool_use", null),
        [typeof(FinishEvent)] = new FinishEvent(),
        [typeof(ErrorEvent)] = new ErrorEvent("stream broke")
    };

    /// <summary>
    ///     THE guard: every concrete <see cref="AgentEvent" /> subtype must appear in
    ///     exactly one of the two projector tables. A new event type with no case
    ///     would previously have been swallowed by a <c>_ =&gt; null</c> arm; here it
    ///     fails the test suite instead.
    /// </summary>
    [Test]
    public async Task EveryConcreteAgentEvent_IsClassifiedByTheSharedProjector()
    {
        HashSet<Type> classified = AgentEventProjector.Handlers.Keys
            .Concat(AgentEventProjector.NoWireCaseReasons.Keys)
            .ToHashSet();

        string[] unclassified = ConcreteSubtypesOf(typeof(AgentEvent))
            .Where(type => !classified.Contains(type))
            .Select(type => type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        await Assert.That(unclassified).IsEmpty()
            .Because(
                $"every AgentEvent subtype must be in AgentEventProjector.Handlers " +
                $"(reaches the wire) or .NoWireCaseReasons (deliberately does not, with a reason). " +
                $"Unclassified: {string.Join(", ", unclassified)}");
    }

    /// <summary>A type cannot both reach the wire and be declared as having no wire case.</summary>
    [Test]
    public async Task NoAgentEvent_IsBothEmittedAndSkipped()
    {
        string[] contradictions = AgentEventProjector.Handlers.Keys
            .Intersect(AgentEventProjector.NoWireCaseReasons.Keys)
            .Select(type => type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        await Assert.That(contradictions).IsEmpty();
    }

    /// <summary>Every classification carries a written reason — "no wire case" is a decision, not a shrug.</summary>
    [Test]
    public async Task EveryNoWireCase_DocumentsWhyItIsSkipped()
    {
        string[] unexplained = AgentEventProjector.NoWireCaseReasons
            .Where(entry => string.IsNullOrWhiteSpace(entry.Value))
            .Select(entry => entry.Key.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        await Assert.That(unexplained).IsEmpty();
    }

    /// <summary>
    ///     Every concrete <see cref="AgentEvent" /> has a sample instance here, so a
    ///     newly added subtype cannot slip past the other assertions by simply
    ///     missing from the table above.
    /// </summary>
    [Test]
    public async Task EveryConcreteAgentEvent_HasASampleInstance()
    {
        string[] missing = ConcreteSubtypesOf(typeof(AgentEvent))
            .Where(type => !AgentEventSamples.ContainsKey(type))
            .Select(type => type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        await Assert.That(missing).IsEmpty()
            .Because($"add a sample to AgentEventSamples so the type is actually projected in a test. Missing: {string.Join(", ", missing)}");
    }

    /// <summary>
    ///     Projecting any known event type never reports
    ///     <see cref="EventProjectionOutcome.Unmapped" />, and an emitted one always
    ///     yields a non-null wire event carrying a declared
    ///     <see cref="HarborEventKind" />.
    /// </summary>
    [Test]
    public async Task EveryKnownAgentEvent_ProjectsToAKnownOutcome()
    {
        var problems = new List<string>();

        foreach ((Type type, AgentEvent sample) in AgentEventSamples.OrderBy(e => e.Key.Name, StringComparer.Ordinal))
        {
            var projection = AgentEventProjector.Project(sample, new ProjectionState());

            if (projection.Outcome == EventProjectionOutcome.Unmapped)
            {
                problems.Add($"{type.Name}: Unmapped");
                continue;
            }

            if (projection.Outcome != EventProjectionOutcome.Emitted)
            {
                continue;
            }

            HarborEvent? emitted = projection.Event;
            if (emitted is null)
            {
                problems.Add($"{type.Name}: Emitted with a null event");
            }
            else if (!Enum.IsDefined(emitted.Kind))
            {
                problems.Add($"{type.Name}: unknown HarborEventKind {emitted.Kind}");
            }
        }

        await Assert.That(problems).IsEmpty().Because(string.Join("; ", problems));
    }

    /// <summary>
    ///     Every event the projector can emit has a declared
    ///     <see cref="HarborEventKind" /> member and a MessagePack DTO to travel in.
    ///     A new case added to the projector without a DTO breaks the wire contract
    ///     for every non-.NET client, so it fails here rather than on a remote host.
    /// </summary>
    [Test]
    public async Task EveryProjectedEvent_HasAKindMemberAndAWireDto()
    {
        HashSet<Type> dataDtos = ConcreteSubtypesOf(typeof(HarborEventData)).ToHashSet();
        var problems = new List<string>();

        foreach ((Type type, AgentEvent sample) in AgentEventSamples.OrderBy(e => e.Key.Name, StringComparer.Ordinal))
        {
            var projection = AgentEventProjector.Project(sample, new ProjectionState());
            if (projection.Event is not { } emitted)
            {
                continue;
            }

            if (!Enum.IsDefined(emitted.Kind))
            {
                problems.Add($"{type.Name}: unknown HarborEventKind {emitted.Kind}");
                continue;
            }

            Type? expectedDto = typeof(HarborEventData).Assembly
                .GetType($"Harbor.Ipc.Protocol.HarborEvent{emitted.Kind}", throwOnError: false);

            bool hasDto = expectedDto is not null && dataDtos.Contains(expectedDto);
            if (!hasDto)
            {
                problems.Add($"{type.Name}: {emitted.Kind} has no HarborEventData DTO");
            }
        }

        await Assert.That(problems).IsEmpty().Because(string.Join("; ", problems));
    }

    /// <summary>
    ///     The same exhaustion guarantee one level down: a new
    ///     <see cref="LlmEvent" /> subtype must be declared as a delta carrier or as
    ///     delta-free, so <see cref="AgentEventProjector.ExtractDelta" /> never hits
    ///     its residual empty-string branch by accident.
    /// </summary>
    [Test]
    public async Task EveryConcreteLlmEvent_IsClassifiedAsDeltaCarrierOrDeltaFree()
    {
        HashSet<Type> classified = AgentEventProjector.DeltaExtractors.Keys
            .Concat(AgentEventProjector.DeltaFreeLlmEvents)
            .ToHashSet();

        string[] unclassified = ConcreteSubtypesOf(typeof(LlmEvent))
            .Where(type => !classified.Contains(type))
            .Select(type => type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        await Assert.That(unclassified).IsEmpty()
            .Because(
                $"every LlmEvent subtype must be in AgentEventProjector.DeltaExtractors " +
                $"or .DeltaFreeLlmEvents. Unclassified: {string.Join(", ", unclassified)}");
    }

    /// <summary>Delta extraction is exhaustive: carriers yield their text, delta-free types yield empty.</summary>
    [Test]
    public async Task ExtractDelta_ReturnsTextForCarriersAndEmptyForTheRest()
    {
        var problems = new List<string>();

        foreach ((Type type, LlmEvent sample) in LlmEventSamples.OrderBy(e => e.Key.Name, StringComparer.Ordinal))
        {
            string delta = AgentEventProjector.ExtractDelta(sample);
            bool carries = AgentEventProjector.DeltaExtractors.ContainsKey(type);
            bool free = AgentEventProjector.DeltaFreeLlmEvents.Contains(type);

            if (carries == free)
            {
                problems.Add($"{type.Name}: classified as both carrier and delta-free, or neither");
            }
            else if (carries && string.IsNullOrEmpty(delta))
            {
                problems.Add($"{type.Name}: delta carrier produced an empty delta");
            }
            else if (free && !string.IsNullOrEmpty(delta))
            {
                problems.Add($"{type.Name}: delta-free type produced '{delta}'");
            }
        }

        await Assert.That(problems).IsEmpty().Because(string.Join("; ", problems));
    }

    /// <summary>
    ///     The wire union stays closed three ways: a <see cref="HarborEvent" /> case
    ///     per <see cref="HarborEventKind" /> member per MessagePack DTO. A new case
    ///     without a DTO (or the reverse) breaks the wire contract for non-.NET
    ///     clients, so it fails here.
    /// </summary>
    [Test]
    public async Task WireUnion_Cases_KindMembers_AndDataDtos_AreOneToOne()
    {
        string[] caseNames = ConcreteSubtypesOf(typeof(HarborEvent))
            .Select(type => type.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        string[] kindNames = Enum.GetNames<HarborEventKind>()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        string[] dtoNames = ConcreteSubtypesOf(typeof(HarborEventData))
            .Select(type => type.Name)
            .Select(name => name.StartsWith("HarborEvent", StringComparison.Ordinal)
                ? name["HarborEvent".Length..]
                : name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        await Assert.That(caseNames).IsEquivalentTo(kindNames);
        await Assert.That(dtoNames).IsEquivalentTo(kindNames);
    }

    /// <summary>
    ///     Projection side effects live in <see cref="ProjectionState" />, not in a
    ///     host: a run start makes the session active, and a turn start/end pair
    ///     round-trips the index the event carried. This is the state the old
    ///     duplicated copies disagreed about.
    /// </summary>
    [Test]
    public async Task SharedProjection_TracksActiveSessionAndTurnIndex()
    {
        var state = new ProjectionState();

        AgentEventProjector.Project(new AgentStartEvent("run-a", []), state);
        await Assert.That(state.ActiveSessionId).IsEqualTo("run-a");

        AgentEventProjector.Project(new TurnStartEvent(4), state);
        var turnEnd = AgentEventProjector.Project(new TurnEndEvent(Sample, []), state);

        // TurnStart carried no session id → resolved against the active run.
        await Assert.That(turnEnd.IsEmitted).IsTrue();
        await Assert.That(turnEnd.Event!.GetType()).IsEqualTo(typeof(HarborEvent.TurnEnd));
        await Assert.That(((HarborEvent.TurnEnd)turnEnd.Event!).Turn).IsEqualTo(4);

        // A second run in the same session restarts the counter.
        AgentEventProjector.Project(new AgentStartEvent("run-a", []), state);
        var restarted = AgentEventProjector.Project(new TurnEndEvent(Sample, [], "run-a"), state);
        await Assert.That(((HarborEvent.TurnEnd)restarted.Event!).Turn).IsEqualTo(0);
    }

    /// <summary>
    ///     The duplication itself is the bug, so it gets a structural guard: the two
    ///     host assemblies must not build <see cref="HarborEvent" /> values. Any
    ///     projection re-added there has to construct one, which fails this test.
    /// </summary>
    [Test]
    public async Task IpcHosts_DoNotConstructHarborEventsThemselves()
    {
        string? root = FindRepoRoot();
        if (root is null)
        {
            // Published / trimmed test host: the sources are not on disk next to the
            // binaries, so there is nothing to scan. The reflection guards above
            // still hold.
            return;
        }

        string[] hosts = ["Harbor.Ipc.Server", "Harbor.Ipc.InProcess"];
        string separator = Path.DirectorySeparatorChar.ToString();

        string[] offenders = hosts
            .SelectMany(project => Directory.EnumerateFiles(
                Path.Combine(root, "src", project), "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{separator}obj{separator}", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{separator}bin{separator}", StringComparison.Ordinal))
            .Where(path => File.ReadAllText(path).Contains("new HarborEvent.", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        await Assert.That(offenders).IsEmpty()
            .Because(
                "the AgentEvent -> HarborEvent projection must exist only in " +
                "src/Harbor.Ipc.Abstractions/AgentEventProjector.cs (#495). Offenders: " +
                string.Join(", ", offenders));
    }

    private static List<Type> ConcreteSubtypesOf(Type baseType) =>
        baseType.Assembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false })
            .Where(baseType.IsAssignableFrom)
            .ToList();

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Harbor.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
