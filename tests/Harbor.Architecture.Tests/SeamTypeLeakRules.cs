// SeamTypeLeakRules.cs — GUARD for issue #494: two seams named a CONCRETE
// implementation type where the consumer therefore learned the implementation
// instead of the contract.
//
// THE TWO DEFECTS, AND WHY THEY ARE NOT THE SAME DEFECT
// -----------------------------------------------------
// The issue filed them as one finding ("put an interface at the seam"). They
// have OPPOSITE diagnoses, and applying the same remedy to both would have made
// one of them worse. The file keeps them apart on purpose.
//
//   SEAM 1 — `Harbor.Ipc.Protocol.ReconnectableRpcClient` named
//   `MessagePackRpcClient` in six signatures. THE CONTRACT COULD NOT EXPRESS
//   WHAT WAS NEEDED. `IHarborClient` is a DOMAIN contract: its
//   `SubscribeToEventsAsync` yields `IAsyncEnumerable<HarborEvent>`, and
//   `HarborEvent` has no sequence. The whole reconnect protocol IS a sequence
//   protocol — `lastSeen`, `SubscribeToEventsRequest(lastSeen)`, the dedup at
//   `frame.Sequence <= lastSeen`, the `SubscriptionAck.ServerSequence` baseline.
//   Its methods also return `Result<…>`, which erases precisely the
//   `OkResponse`/`ErrorResponse` split the subscribe path branches on. And it has
//   no edge-triggered "the connection just died" signal at all — `IsConnected`
//   is a level, and the pump needs an edge. So the decorator could not have
//   named `IHarborClient` even in principle: the state machine it exists to
//   hold would have had nowhere to live. This is a MISSING contract, not an ISP
//   violation, and the fix is to write the contract (#494), not to shrink one.
//
//   SEAM 2 — `CellForgeRenderContext.Writer` returned `AnsiWriter`, a 28-member
//   concrete class. THE CONTRACT DID NOT FAIL TO EXPRESS ANYTHING; the reader
//   set is EMPTY. The property's own doc claimed it existed "for diagnostics and
//   golden-frame tests", and no diagnostic and no golden-frame test read it:
//   every one of them substitutes one layer DOWN, at `ITerminalBackend` /
//   `ISyncTerminalBackend`, which is already an interface and already has a
//   recording fake in the tree (`RecordingBackend`). Nothing was prevented from
//   substituting anything, because nothing used the property. Adding
//   `IAnsiTextSink`/`IAnsiFrameWriter` here would have created two interfaces
//   with one implementation each and zero consumers — speculative generality,
//   the exact shape #565, #594 and #558 delete rather than add. The honest fix
//   is to DELETE the dead property, which is what this file pins.
//
// So: rule 1 below exists because the contract was missing, and rule 2 exists
// because a contract that was already there made the property unnecessary.
//
// WHY THE SCAN IS OVER IL, AND NOT OVER SOURCE TEXT
// -------------------------------------------------
// The question is "what TYPE does this member have", which is a metadata
// question, and a per-file grep cannot answer it without becoming a
// project-wide grep that then passes anything the project names. The same
// reasoning is recorded in RendererEventSeamRule.cs. A name ban would also be
// defeated by a rename, which is the failure mode SlashCommandRouterShapeRule.cs
// was written to close: these are TYPE-IDENTITY rules (`banned == member.FieldType`),
// so renaming a member satisfies nothing and renaming the concrete class moves
// the ban to the new identity rather than deleting it.
//
// CONSTRUCTOR PARAMETERS ARE *NOT* A LEAK, AND THAT IS A DECISION
// ---------------------------------------------------------------
// Rule 1 counts a constructor parameter; rule 2 does not. The distinction is not
// a convenience, it is the whole point:
//
//   * A constructor parameter names an implementation THE COMPOSITION ROOT
//     CHOOSES. `new ScreenSession(AnsiWriter, …)` and `new DiffEngine().Flush(buf,
//     AnsiWriter)` are how a subsystem is wired, and forcing them behind an
//     interface would only move the concrete name, not remove it.
//   * A property or method RETURN type PUBLISHES the implementation. Every
//     consumer can reach all 28 members through it, and a change to any of them
//     breaks a consumer in another assembly — far from the cause.
//
// `NonVacuity_RuleTwo_AllowsCompositionButNotPublication` pins that rule 2 does
// discriminate, so it cannot quietly widen into "no mention of AnsiWriter
// anywhere", which would be a name ban wearing a shape rule's clothes.
//
// WHAT IS DELIBERATELY NOT BANNED
// -------------------------------
// `ReconnectableRpcClient` is not required to implement `IHarborClient`, which
// is what issue #494 originally proposed. It would have cost fourteen members,
// eleven of which are pass-through delegation to domain methods the decorator
// has no business knowing about, and `SubscribeToEventsAsync` could not have
// carried the sequence number — so the fourteen-member surface would have been
// both wider than the contract needs (the ISP half of the issue's own title)
// and lossy about the one thing the decorator is for. No ban here pushes code
// towards that; the file only pins the two seams the issue actually names.
//
// NON-VACUITY
// -----------
// A ban that matches nothing is indistinguishable from a ban that is satisfied.
// Four things close that here:
//
//   1. `NonVacuity_TheConcreteTypesAreFoundWhereTheLeaksWere` names both
//      concrete types in the assemblies that declared them. A namespace move, a
//      failed assembly load or a typo'd prefix is red here rather than
//      vacuously green.
//   2. `NonVacuity_RuleOne_FiresOnAPlantedConcreteMember` — the POSITIVE
//      CONTROL: a private class declared in THIS file holding a
//      `MessagePackRpcClient` field under a different name, so tripping it
//      proves the rule reads member types and not the word "Reconnect".
//   3. `NonVacuity_RuleTwo_AllowsCompositionButNotPublication` — a private
//      class whose constructor takes the writer (must be ALLOWED) beside one
//      whose PROPERTY returns it (must be BANNED).
//   4. `NonVacuity_TheGovernedSetExcludesOnlyThisAssembly` — this project
//      holds the positive controls above, so it must be excluded from the
//      governed set or the gate is red forever. The exclusion is by assembly
//      IDENTITY (a reference comparison), not by a name that could be typo'd.
//
// PERIMETER, STATED RATHER THAN HIDDEN
// ------------------------------------
// The governed set is every loaded `Harbor*` assembly except this one. Today
// that is `src/` only: this project references no other test project, and it
// deliberately takes no reference edge to the composition roots under `apps/`
// (UiConfigDefaultsRule.cs and AbstractionsNamespaceOwnershipRules.cs both say
// why). So a NEW consumer added under `apps/` would not be seen by rules 1–3.
// That gap is stated here rather than papered over with a source regex that
// would fire on comments and give false confidence in exchange for it. The
// integration tests under `tests/Harbor.Ipc.Tests` construct the concrete
// client on purpose and are likewise not in the governed set today; if this
// project ever takes that reference edge the gate goes RED, which is the
// intended direction — the exemption then has to be written down with its
// reason, instead of a prefix list being added here in advance for a
// hypothetical.
//
// System.Reflection is a global using in this project (GlobalUsings.cs), so
// Assembly / Type / MemberInfo need no using here — and adding one would only
// duplicate the global. The namespace usings below are NOT redundant: this
// project has no global using for product namespaces, and naming the concrete
// types by their real identity is the point — a `typeof` on the actual type is
// what makes these rules type-identity rules instead of string matches.

using Harbor.Ipc;
using Harbor.Ipc.Protocol;
using Harbor.Tui.CellForge.Rendering;

namespace Harbor.Architecture.Tests;

/// <summary>One offending member, with enough context to locate it.</summary>
/// <param name="TypeFullName">The declaring type.</param>
/// <param name="Detail">Which member named the concrete type, and how.</param>
internal readonly record struct SeamLeak(string TypeFullName, string Detail);

/// <summary>
///     The classifier both rule sets go through, so neither can be satisfied by
///     weakening the other's half. Internal rather than private because the
///     positive controls must call the SAME code the rules call.
/// </summary>
internal static class SeamLeakProbe
{
    /// <summary>
    ///     The reconnect decorator — the wrapper whose signatures rule 1
    ///     governs, together with everything nested inside it.
    /// </summary>
    internal const string DecoratedTypeName = "ReconnectableRpcClient";

    /// <summary>
    ///     Whether a type is the reconnect decorator or is nested inside it.
    ///     Walking <see cref="Type.DeclaringType" /> matters: the half of the
    ///     leak a rule looking only at the outer type would miss is
    ///     <c>LostSubscription</c>, a private nested class holding both the
    ///     field and the <c>ConnectionLost</c> subscription.
    /// </summary>
    internal static bool IsPartOfTheReconnectDecorator(Type type)
    {
        for (Type? cursor = type; cursor is not null; cursor = cursor.DeclaringType)
        {
            if (string.Equals(cursor.Name, DecoratedTypeName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Flags covering every declared member of a type: public and non-public,
    ///     instance and static. <see cref="BindingFlags.DeclaredOnly" /> matters —
    ///     without it a subclass inherits its base's members and gets blamed for
    ///     them.
    /// </summary>
    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic
        | BindingFlags.Instance | BindingFlags.Static
        | BindingFlags.DeclaredOnly;

    /// <summary>
    ///     Every slot in which <paramref name="owner" />'s own declarations name
    ///     <paramref name="banned" />: fields, properties, events, method return
    ///     types, method parameters, and constructor parameters.
    /// </summary>
    /// <remarks>
    ///     Constructor parameters are INCLUDED here and EXCLUDED in
    ///     <see cref="PublishesConcrete" />. That difference is the rule, not an
    ///     oversight — see the header.
    /// </remarks>
    internal static List<SeamLeak> NamesConcrete(Type owner, Type banned)
    {
        var hits = new List<SeamLeak>();

        foreach (FieldInfo field in owner.GetFields(Declared))
        {
            if (Contains(field.FieldType, banned))
            {
                hits.Add(new SeamLeak(Name(owner), $"field '{field.Name}'"));
            }
        }

        foreach (PropertyInfo property in owner.GetProperties(Declared))
        {
            if (Contains(property.PropertyType, banned))
            {
                hits.Add(new SeamLeak(Name(owner), $"property '{property.Name}'"));
            }
        }

        foreach (EventInfo declaredEvent in owner.GetEvents(Declared))
        {
            if (Contains(declaredEvent.EventHandlerType, banned))
            {
                hits.Add(new SeamLeak(Name(owner), $"event '{declaredEvent.Name}'"));
            }
        }

        foreach (MethodInfo method in owner.GetMethods(Declared))
        {
            if (Contains(method.ReturnType, banned))
            {
                hits.Add(new SeamLeak(Name(owner), $"method '{method.Name}' return type"));
            }

            AddParameterHits(owner, method, banned, hits);
        }

        foreach (ConstructorInfo constructor in owner.GetConstructors(Declared))
        {
            AddParameterHits(owner, constructor, banned, hits);
        }

        return hits;
    }

    /// <summary>
    ///     Only the PUBLISHING slots: a property type or a method return type.
    ///     Fields, constructor parameters, method parameters and events are not
    ///     publication — they are wiring.
    /// </summary>
    internal static List<SeamLeak> PublishesConcrete(Type owner, Type banned)
    {
        var hits = new List<SeamLeak>();

        foreach (PropertyInfo property in owner.GetProperties(Declared))
        {
            if (Contains(property.PropertyType, banned))
            {
                hits.Add(new SeamLeak(Name(owner), $"property '{property.Name}'"));
            }
        }

        foreach (MethodInfo method in owner.GetMethods(Declared))
        {
            if (Contains(method.ReturnType, banned))
            {
                hits.Add(new SeamLeak(Name(owner), $"method '{method.Name}' return type"));
            }
        }

        return hits;
    }

    /// <summary>
    ///     Whether <paramref name="type" /> IS <paramref name="banned" />, or
    ///     has it anywhere inside: a generic argument, an array element, a
    ///     by-ref or pointer target.
    /// </summary>
    /// <remarks>
    ///     Without the descent a rule is defeated by a single layer of
    ///     wrapping, and every member on this seam is wrapped:
    ///     <c>Task&lt;MessagePackRpcClient&gt;</c> from <c>ConnectAsync</c> and
    ///     <c>Task&lt;(MessagePackRpcClient, …)&gt;</c> from <c>DialAsync</c>
    ///     are the two that leaked, and an exact-type comparison would have seen
    ///     neither. Recursion rather than a shallow "generic or not" test, so a
    ///     tuple inside a task inside a list is still the same dependency.
    /// </remarks>
    internal static bool Contains(Type? type, Type banned)
    {
        if (type is null)
        {
            return false;
        }

        if (banned == type)
        {
            return true;
        }

        if (type.IsArray || type.IsByRef || type.IsPointer)
        {
            return Contains(type.GetElementType(), banned);
        }

        if (type.IsGenericType)
        {
            foreach (Type argument in type.GetGenericArguments())
            {
                if (Contains(argument, banned))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    ///     Every public member this type DECLARES that
    ///     <paramref name="contract" /> does not — constructors excluded, and
    ///     property accessors excluded because the property itself is the member.
    /// </summary>
    /// <remarks>
    ///     Matched by name PLUS parameter type list, not by name alone: an
    ///     implementation of <c>Write(string)</c> must not be excused merely
    ///     because the contract also has a <c>WriteColored(string, TuiColor,
    ///     TuiColor?)</c>. The key is built from the parameter types so
    ///     <c>WriteLine(string)</c> and <c>WriteLine()</c> cannot stand in for
    ///     each other either.
    /// </remarks>
    internal static List<string> SurfaceBeyondContract(Type owner, Type contract)
    {
        var contractMembers = new HashSet<string>(MemberKeys(contract), StringComparer.Ordinal);

        return [.. MemberKeys(owner).Where(key => !contractMembers.Contains(key))];
    }

    /// <summary>
    ///     <c>name(paramTypeFullName,…)</c> for methods and properties,
    ///     <c>prop:Name</c> for properties, <c>field:Name</c> for fields.
    /// </summary>
    private static IEnumerable<string> MemberKeys(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        foreach (PropertyInfo property in type.GetProperties(flags))
        {
            yield return $"prop:{property.Name}";
        }

        foreach (MethodInfo method in type.GetMethods(flags))
        {
            // get_/set_/add_/remove_ are the accessors OF the property/event
            // already yielded above; counting them again would report every
            // implemented interface member as an extra.
            if (method.IsSpecialName)
            {
                continue;
            }

            yield return $"{method.Name}({string.Join(",", method.GetParameters().Select(p => Full(p.ParameterType)))})";
        }
    }

    private static void AddParameterHits(Type owner, MethodBase method, Type banned, List<SeamLeak> hits)
    {
        foreach (ParameterInfo parameter in method.GetParameters())
        {
            if (Contains(parameter.ParameterType, banned))
            {
                hits.Add(new SeamLeak(Name(owner), $"'{method.Name}' parameter '{parameter.Name}'"));
            }
        }
    }

    private static string Full(Type type) => type.FullName ?? type.Name;

    private static string Name(Type type) => type.FullName ?? type.Name;

    /// <summary>
    ///     Types of an assembly, tolerating the ones that failed to load. The
    ///     liveness test is what stops "returned nothing" from reading as
    ///     "found no offending type".
    /// </summary>
    internal static IReadOnlyList<Type> SafeGetTypes(Assembly asm)
    {
        try
        {
            return asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return [.. ex.Types.Where(static t => t is not null).Select(static t => t!)];
        }
        catch (FileNotFoundException)
        {
            return [];
        }
    }
}

/// <summary>
///     Issue #494: the reconnect decorator and the CellForge render context may
///     not hand a consumer a concrete implementation type, and the render
///     context may not carry a surface beyond the contract it implements.
/// </summary>
public sealed class SeamTypeLeakRules
{
    /// <summary>
    ///     Every type in every loaded Harbor assembly, EXCEPT this project's own.
    ///     The exclusion is by assembly IDENTITY — a reference comparison, so
    ///     there is no name to typo — because <c>LoadHarborAssemblies()</c>
    ///     accepts anything starting with <c>Harbor</c>, and this assembly is
    ///     <c>Harbor.Architecture.Tests</c>, which holds the positive controls.
    /// </summary>
    private static readonly Lazy<IReadOnlyList<Type>> Governed = new(CollectTypes);

    /// <summary>The concrete MessagePack client — the type seam 1 named.</summary>
    private static readonly Lazy<Type> ConcreteClient = new(
        () => typeof(MessagePackRpcClient));

    /// <summary>The concrete ANSI writer — the type seam 2 published.</summary>
    private static readonly Lazy<Type> ConcreteWriter = new(
        () => typeof(AnsiWriter));

    // =====================================================================
    // 1. The rules.
    // =====================================================================

    /// <summary>
    ///     RULE 1 — the reconnect seam, in IL. The reconnect DECORATOR — the type
    ///     named by <see cref="SeamLeakProbe.DecoratedTypeName" /> and everything
    ///     nested inside it — may not declare a field, property, event, method
    ///     return, method parameter or constructor parameter typed as the
    ///     concrete MessagePack RPC client.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Scoped to the decorator ON PURPOSE, and the scoping is the
    ///         diagnosis rather than a convenience. A decorator's whole value is
    ///         that it is interchangeable with the thing it wraps, so every type
    ///         in its signature is a decision about what the wrapper gives back.
    ///         Naming the concrete class there makes "use the reconnecting client
    ///         instead of the plain one" a construction-site rewrite rather than a
    ///         type-level swap, and makes the backoff / subscribe-before-snapshot
    ///         / sequence-fencing state machine reachable only over a real
    ///         MessagePack pipe.
    ///     </para>
    ///     <para>
    ///         The rule does NOT extend to <c>IpcHarborClient</c> and
    ///         <c>EventSubscription</c>, both of which hold the concrete client
    ///         in a private field and both of which are correct to: they are the
    ///         IMPLEMENTATION, and a field naming the implementation a
    ///         composition root handed them is wiring, not leakage. Only the
    ///         wrapper is held to the narrower rule. Widening it to the whole
    ///         assembly would flag the code that is right and get switched off,
    ///         which is the fate of every ban in this repository that grew past
    ///         its defect.
    ///     </para>
    /// </remarks>
    [Test]
    public async Task TheReconnectDecorator_NeverNamesTheConcreteRpcClient()
    {
        Type banned = ConcreteClient.Value;
        List<string> offenders = [];

        foreach (Type type in Governed.Value.Where(SeamLeakProbe.IsPartOfTheReconnectDecorator))
        {
            foreach (SeamLeak hit in SeamLeakProbe.NamesConcrete(type, banned))
            {
                offenders.Add($"{hit.TypeFullName}: {hit.Detail}");
            }
        }

        offenders.Sort(StringComparer.Ordinal);

        await Assert.That(offenders).IsEmpty()
            .Because(
                "issue #494: ReconnectableRpcClient named MessagePackRpcClient in six signatures — its "
                + "_current field, ConnectAsync's Task<MessagePackRpcClient> return, DialAsync's "
                + "Task<(MessagePackRpcClient, …)> tuple, RegisterLost, and the nested LostSubscription — so "
                + "the decorator could not be substituted for the client it wraps, and exercising its state "
                + "machine required a real MessagePack pipe. IHarborClient cannot be the contract here: "
                + "SubscribeToEventsAsync yields HarborEvent, which has no sequence, and the whole reconnect "
                + "protocol is a sequence protocol. Name the RPC-client contract (IRpcClient) instead, and "
                + "inject the implementation through a factory. Offenders: " + string.Join(", ", offenders));
    }

    /// <summary>
    ///     RULE 2 — the writer seam, in IL. No type may PUBLISH the concrete
    ///     ANSI writer through a property type or a method return type.
    ///     Constructor and method parameters are allowed; see the header.
    /// </summary>
    [Test]
    public async Task NoType_PublishesTheConcreteAnsiWriter()
    {
        Type banned = ConcreteWriter.Value;
        List<string> offenders = [];

        foreach (Type type in Governed.Value)
        {
            foreach (SeamLeak hit in SeamLeakProbe.PublishesConcrete(type, banned))
            {
                offenders.Add($"{hit.TypeFullName}: {hit.Detail}");
            }
        }

        offenders.Sort(StringComparer.Ordinal);

        await Assert.That(offenders).IsEmpty()
            .Because(
                "issue #494: a property returning AnsiWriter hands every consumer all 28 members of the "
                + "implementation and makes a change to any of them break an assembly that has no business "
                + "knowing it exists. CellForgeRenderContext.Writer advertised itself as 'exposed for "
                + "diagnostics and golden-frame tests' and had no readers at all — those tests substitute at "
                + "ITerminalBackend, one layer down, where an interface already exists. Do not reach for a "
                + "new IAnsiTextSink here: two interfaces with one implementation and no consumers is the "
                + "shape #565 and #594 delete. Offenders: " + string.Join(", ", offenders));
    }

    /// <summary>
    ///     RULE 3 — the render context adds nothing. Every public member
    ///     <c>CellForgeRenderContext</c> declares must already exist on
    ///     <c>ITuiRenderContext</c>.
    /// </summary>
    /// <remarks>
    ///     This is the rule that makes the deletion of <c>Writer</c> permanent.
    ///     Rules 1 and 2 forbid the CONCRETE type; this one forbids the extra
    ///     surface itself, so the substitution that replaces it has to be a
    ///     member of the contract — the same reason <c>CaptureRenderContext</c>
    ///     implements <c>ITuiRenderContext</c> and nothing more.
    /// </remarks>
    [Test]
    public async Task CellForgeRenderContext_DeclaresNothingBeyondItsContract()
    {
        Type context = typeof(Harbor.Tui.CellForge.CellForgeRenderContext);
        Type contract = typeof(Harbor.Terminal.Abstractions.Renderers.ITuiRenderContext);

        List<string> extra = SeamLeakProbe.SurfaceBeyondContract(context, contract);

        await Assert.That(extra).IsEmpty()
            .Because(
                "CellForgeRenderContext implements ITuiRenderContext, and every consumer — including "
                + "CaptureRenderContext, the recording fake used everywhere else in the tree — is typed to "
                + "that interface. A public member beyond it is a SECOND contract with no second "
                + "implementation and, in the one case that existed, no readers at all (issue #494: "
                + "'exposed for diagnostics and golden-frame tests'). Extra members: "
                + string.Join(", ", extra));
    }

    /// <summary>
    ///     RULE 4 — the substitute seam is real and already occupied. The
    ///     render context must keep taking an INTERFACE terminal backend, and
    ///     the type doing so must live OUTSIDE the engine that owns the writer.
    /// </summary>
    /// <remarks>
    ///     This rule is green today and is here to keep rule 2 honest. Without
    ///     it, "just delete the property" is indistinguishable from "delete the
    ///     property and quietly wire the concrete writer into the adapter",
    ///     which would make the writer untestable at the only level where it is
    ///     ever tested.
    /// </remarks>
    [Test]
    public async Task TheWriterSubstitute_IsAnInterfaceAndIsActuallyUsed()
    {
        await Assert.That(typeof(ISyncTerminalBackend).IsInterface).IsTrue()
            .Because(
                "ISyncTerminalBackend is the seam every golden-frame test substitutes at — "
                + "RecordingBackend, CountingBackend and AsyncOnlyBackend all implement it, and "
                + "SyncBackendCapabilityTests drives the capability flag through it. If it stopped being an "
                + "interface, issue #494's deletion of CellForgeRenderContext.Writer would have removed the "
                + "only substitutable handle on the writer instead of a redundant one");

        Type[] renderContextCtors = typeof(Harbor.Tui.CellForge.CellForgeRenderContext).GetConstructors();

        bool takesInterface = renderContextCtors.Any(
            static c => c.GetParameters().Any(
                static p => typeof(ISyncTerminalBackend).IsAssignableFrom(p.ParameterType)));

        await Assert.That(takesInterface).IsTrue()
            .Because(
                "CellForgeRenderContext must keep a constructor that takes an ISyncTerminalBackend. That "
                + "parameter is the whole reason the golden-frame tests can assert on bytes: the render "
                + "context is wired over a fake backend, and the concrete AnsiWriter never has to be "
                + "replaced to observe a frame");

        string engineAssembly = typeof(AnsiWriter).Assembly.GetName().Name!;
        string contextAssembly = typeof(Harbor.Tui.CellForge.CellForgeRenderContext).Assembly.GetName().Name!;

        await Assert.That(contextAssembly).IsNotEqualTo(engineAssembly)
            .Because(
                "CellForgeRenderContext lives in Harbor.Tui.CellForge and AnsiWriter in "
                + "Harbor.Tui.CellForge.Engine. That crossing is the seam: the adapter consumes the engine's "
                + "output device through an interface while the writer stays an implementation detail on the "
                + "other side. If the two assemblies were ever merged, rule 2's perimeter would be gone and "
                + "this file's rule 2 would be enforcing nothing");
    }

    // =====================================================================
    // 2. Non-vacuity.
    // =====================================================================

    /// <summary>
    ///     The scan really reaches the two concrete types, in the assemblies
    ///     that declare them. This is the direct answer to the NetArchTest
    ///     trap: a scan that cannot load an assembly finds no offending type and
    ///     reports success.
    /// </summary>
    [Test]
    public async Task NonVacuity_TheConcreteTypesAreFoundWhereTheLeaksWere()
    {
        IReadOnlyDictionary<string, Assembly> loaded = ArchitectureTestHelpers.LoadHarborAssemblies();

        await Assert.That(loaded.ContainsKey("Harbor.Ipc.Client")).IsTrue()
            .Because(
                "Harbor.Ipc.Client declares MessagePackRpcClient — the type ReconnectableRpcClient named in "
                + "six signatures. If it is not in the loaded inventory, rule 1 is satisfied by an empty scan");

        await Assert.That(loaded.ContainsKey("Harbor.Tui.CellForge.Engine")).IsTrue()
            .Because(
                "Harbor.Tui.CellForge.Engine declares AnsiWriter — the type CellForgeRenderContext published. "
                + "If it is not loaded, rule 2 is satisfied by an empty scan");

        await Assert.That(loaded.ContainsKey("Harbor.Tui.CellForge")).IsTrue()
            .Because(
                "Harbor.Tui.CellForge declares CellForgeRenderContext and ScreenSession — the two "
                + "CellForge-side consumers rule 2 exists for. Without it, nothing on that side of the seam "
                + "is being read at all");

        var clientNames = SeamLeakProbe.SafeGetTypes(loaded["Harbor.Ipc.Client"])
            .Select(static t => t.FullName ?? t.Name)
            .ToHashSet(StringComparer.Ordinal);

        // MessagePackRpcClient and ReconnectableRpcClient are declared in
        // src/Harbor.Ipc.Client/Protocol/ — MessagePackRpcClient.cs and
        // ReconnectableRpcClient.cs respectively.
        await Assert.That(clientNames.Contains("Harbor.Ipc.Protocol.MessagePackRpcClient")).IsTrue()
            .Because(
                "the banned client type must still be FOUND by name in Harbor.Ipc.Client; if the scan "
                + "cannot see it, rule 1 enforces nothing");

        await Assert.That(clientNames.Contains("Harbor.Ipc.Protocol.ReconnectableRpcClient")).IsTrue()
            .Because(
                "ReconnectableRpcClient is the decorator whose signatures rule 1 governs. If it is gone, the "
                + "reconnect state machine was deleted rather than de-leaked, and rule 1 is defending an "
                + "empty seat");

        var writerNames = SeamLeakProbe.SafeGetTypes(loaded["Harbor.Tui.CellForge.Engine"])
            .Select(static t => t.FullName ?? t.Name)
            .ToHashSet(StringComparer.Ordinal);

        await Assert.That(writerNames.Contains("Harbor.Tui.CellForge.Rendering.AnsiWriter")).IsTrue()
            .Because(
                "the banned writer type must still be FOUND by name in Harbor.Tui.CellForge.Engine; "
                + "AnsiWriter stays — it is a legitimate implementation of an already-substitutable seam, and "
                + "what #494 removed is the PUBLIC HAND it was handed, not the writer itself");

        await Assert.That(writerNames.Contains("Harbor.Tui.CellForge.Rendering.ITerminalBackend")).IsTrue()
            .Because(
                "ITerminalBackend is the interface the writer implements and the seam every golden-frame "
                + "test substitutes at. Rule 4 asserts it is still an interface; this asserts it is still "
                + "THERE, so rule 4 cannot be satisfied by the interface having been deleted");
    }

    /// <summary>
    ///     THE POSITIVE CONTROL for rule 1. Its nested
    ///     <c>ReconnectableRpcClient</c> carries the exact shapes the real
    ///     decorator had — a <c>MessagePackRpcClient</c> field, a
    ///     <c>Task&lt;MessagePackRpcClient&gt;</c> return and a constructor
    ///     parameter — under a DIFFERENT enclosing type, so tripping it proves
    ///     the rule reads member TYPES and descends into wrappers, rather than
    ///     matching the product assembly or the type's simple name.
    /// </summary>
    [Test]
    public async Task NonVacuity_RuleOne_FiresOnAPlantedConcreteMember()
    {
        IReadOnlyList<Type> selfTypes = SeamLeakProbe.SafeGetTypes(typeof(SeamTypeLeakRules).Assembly);
        Type banned = ConcreteClient.Value;
        Type planted = typeof(DecoratorProbeHost.ReconnectableRpcClient);

        await Assert.That(selfTypes).Contains(planted)
            .Because("the planted decorator is declared in this file; if reflection cannot see it, the "
                   + "positive control below is testing nothing");

        await Assert.That(SeamLeakProbe.IsPartOfTheReconnectDecorator(planted)).IsTrue()
            .Because(
                "the planted type is NESTED, and rule 1 must reach nested types — the real leak's second "
                + "half was LostSubscription, a private nested class holding both the field and the "
                + "ConnectionLost subscription. A rule that only looked at top-level types would see one "
                + "site of six");

        await Assert.That(planted.Assembly.GetName().Name!)
            .IsNotEqualTo(typeof(MessagePackRpcClient).Assembly.GetName().Name!)
            .Because(
                "the planted decorator lives in Harbor.Architecture.Tests while the real one lives in "
                + "Harbor.Ipc.Client. If rule 1 keyed on the assembly, this control would pass while the "
                + "rule enforced nothing");

        List<SeamLeak> hits = SeamLeakProbe.NamesConcrete(planted, banned);
        string[] details = [.. hits.Select(static h => h.Detail)];

        await Assert.That(details).Contains("field '_current'")
            .Because(
                "the planted decorator's _current field is typed MessagePackRpcClient — the exact shape "
                + "ReconnectableRpcClient._current had. If the classifier misses it, rule 1 enforces "
                + "nothing and the ban is a comment. Found: " + string.Join(", ", details));

        await Assert.That(details).Contains("method 'ConnectAsync' return type")
            .Because(
                "ConnectAsync returns Task<MessagePackRpcClient> — the wrapped type behind a one-layer "
                + "generic wrapper, which is the second of the two leaks an exact-type comparison would "
                + "have missed. Found: " + string.Join(", ", details));

        await Assert.That(details).Contains("'.ctor' parameter 'inner'")
            .Because(
                "the nested LostSubscription takes MessagePackRpcClient as a constructor parameter — the "
                + "rule counts constructor parameters, which is the difference between it and rule 2. "
                + "Found: " + string.Join(", ", details));

        // -- negative control: an interface-typed member is the shape we want --
        List<SeamLeak> clean = SeamLeakProbe.NamesConcrete(typeof(ContractMemberProbe), banned);

        await Assert.That(clean).IsEmpty()
            .Because(
                "ContractMemberProbe declares the same members typed as an INTERFACE. A gate that flagged "
                + "this would be flagging the fix, and a gate that gets switched off for that reason "
                + "enforces nothing. Offenders: " + string.Join(", ", clean.Select(static h => h.Detail)));

        // -- negative control: the scope is the decorator, not the assembly ---
        List<SeamLeak> implementation = SeamLeakProbe.NamesConcrete(typeof(ImplementationProbe), banned);

        await Assert.That(SeamLeakProbe.IsPartOfTheReconnectDecorator(typeof(ImplementationProbe))).IsFalse()
            .Because(
                "ImplementationProbe is deliberately NOT the decorator and not nested in it. "
                + "IpcHarborClient and EventSubscription both hold the concrete client in a private field "
                + "and both are correct to — they are the implementation, and a field naming what the "
                + "composition root handed them is wiring, not leakage. Rule 1 must not reach them");

        await Assert.That(implementation.Count).IsGreaterThan(0)
            .Because(
                "ImplementationProbe holds a MessagePackRpcClient field on purpose. If the probe found "
                + "nothing there, the negative control above would pass for the wrong reason — because the "
                + "scan was broken rather than because the scope is right. Found: "
                + string.Join(", ", implementation.Select(static h => h.Detail)));
    }

    /// <summary>
    ///     THE POSITIVE CONTROL for rule 2, and the proof that it discriminates
    ///     publication from composition: a private class whose PROPERTY returns
    ///     the writer must be banned, and one whose CONSTRUCTOR takes it must be
    ///     allowed.
    /// </summary>
    [Test]
    public async Task NonVacuity_RuleTwo_AllowsCompositionButNotPublication()
    {
        Type banned = ConcreteWriter.Value;

        List<SeamLeak> published = SeamLeakProbe.PublishesConcrete(typeof(PublishedWriterProbe), banned);
        List<string> publishedDetails = [.. published.Select(static h => h.Detail)];

        await Assert.That(publishedDetails).Contains("property 'Writer'")
            .Because(
                "PublishedWriterProbe.WRITER-PROPERTY is typed AnsiWriter — the exact shape "
                + "CellForgeRenderContext.Writer had, which is what issue #494 removed. If the classifier "
                + "misses it, rule 2 enforces nothing");

        List<SeamLeak> composed = SeamLeakProbe.PublishesConcrete(typeof(WiredWriterProbe), banned);

        await Assert.That(composed).IsEmpty()
            .Because(
                "WiredWriterProbe takes the writer as a CONSTRUCTOR parameter and stores it in a private "
                + "field — which is how ScreenSession, InlineSession and DiffEngine.Flush all consume it, "
                + "and is not a leak: a constructor parameter names an implementation the composition root "
                + "chooses, while a property or return type PUBLISHES it to every consumer. If rule 2 flagged "
                + "this, it would be a name ban in a shape rule's clothes. Offenders: "
                + string.Join(", ", composed.Select(static h => h.Detail)));

        // And the same shape IS caught by rule 1's constructor-inclusive probe,
        // which is the difference between the two rules stated in one place.
        List<SeamLeak> asName = SeamLeakProbe.NamesConcrete(typeof(WiredWriterProbe), banned);
        List<string> asNameDetails = [.. asName.Select(static h => h.Detail)];

        await Assert.That(asNameDetails).Contains("'ctor' parameter 'writer'")
            .Because(
                "rule 1 counts constructor parameters and rule 2 does not. That asymmetry is deliberate and "
                + "this assertion pins it: WiredWriterProbe is caught by one and not the other, so neither "
                + "rule can be quietly widened until they are the same rule");
    }

    /// <summary>
    ///     The governed set really does exclude this assembly, and really does
    ///     include the product assemblies the rules are about.
    /// </summary>
    [Test]
    public async Task NonVacuity_TheGovernedSetExcludesOnlyThisAssembly()
    {
        var governedAssemblies = Governed.Value
            .Select(static t => t.Assembly.GetName().Name!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static n => n, StringComparer.Ordinal)
            .ToList();

        string selfName = typeof(SeamTypeLeakRules).Assembly.GetName().Name!;

        await Assert.That(governedAssemblies.Contains(selfName)).IsFalse()
            .Because(
                "this assembly holds the positive controls DecoratorProbeHost, PublishedWriterProbe and "
                + "WiredWriterProbe, which deliberately carry the banned shapes. It must be excluded from the "
                + "governed set or rules 1 and 2 flag this file's own fixtures and the gate can never pass. "
                + "Excluded by assembly identity, not by name. Governed: "
                + string.Join(", ", governedAssemblies));

        await Assert.That(governedAssemblies.Contains("Harbor.Ipc.Client")).IsTrue()
            .Because(
                "Harbor.Ipc.Client must be governed — it is where the reconnect decorator and the concrete "
                + "client live, i.e. the assembly both rules are for");

        await Assert.That(governedAssemblies.Contains("Harbor.Tui.CellForge")).IsTrue()
            .Because(
                "Harbor.Tui.CellForge must be governed — it declared CellForgeRenderContext, the type that "
                + "published the writer");
    }

    // =====================================================================
    // 3. Plumbing.
    // =====================================================================

    /// <summary>Every type in every loaded Harbor assembly but this one.</summary>
    private static IReadOnlyList<Type> CollectTypes()
    {
        Assembly self = typeof(SeamTypeLeakRules).Assembly;
        var found = new List<Type>();

        foreach ((string _, Assembly asm) in ArchitectureTestHelpers.LoadHarborAssemblies())
        {
            // Reference identity, not a name: this project's own assembly must be
            // excluded and there is nothing here a rename could break.
            if (ReferenceEquals(asm, self))
            {
                continue;
            }

            found.AddRange(SeamLeakProbe.SafeGetTypes(asm));
        }

        return found;
    }

    // =====================================================================
    // 4. The positive controls. Declared here so their IL is in this assembly.
    //    Never implemented, never registered, never referenced by product code.
    // =====================================================================

    /// <summary>
    ///     HOST for the rule 1 positive control. The enclosing type is named
    ///     something else on purpose, so the rule cannot be satisfied by matching
    ///     "the type is called ReconnectableRpcClient" without also walking in.
    /// </summary>
    private sealed class DecoratorProbeHost
    {
        /// <summary>
        ///     The planted decorator, carrying all three of the shapes that
        ///     leaked: a field, a <c>Task&lt;T&gt;</c> return, and — one level
        ///     down, in a nested type — a constructor parameter.
        /// </summary>
        private sealed class ReconnectableRpcClient
        {
            /// <summary>The banned shape: the cached inner client.</summary>
            private readonly MessagePackRpcClient _current = null!;

            /// <summary>The banned shape, behind a one-layer generic wrapper.</summary>
            public Task<MessagePackRpcClient> ConnectAsync() => Task.FromResult(_current);

            /// <summary>The banned shape, behind a tuple inside a task.</summary>
            public Task<(MessagePackRpcClient Inner, IIpcClientTransport Transport)> DialAsync()
                => Task.FromResult((_current, (IIpcClientTransport)null!));

            /// <summary>Never called; keeps the nested type from being trimmed.</summary>
            public MessagePackRpcClient Current => _current;

            /// <summary>
            ///     The nested half of the leak — a private nested subscription
            ///     class, exactly like <c>LostSubscription</c>.
            /// </summary>
            private sealed class LostSubscription
            {
                /// <summary>The banned shape: a constructor parameter.</summary>
                public LostSubscription(MessagePackRpcClient inner) => _inner = inner;

                private readonly MessagePackRpcClient _inner;

                /// <summary>Never called; keeps the field from being trimmed.</summary>
                public bool IsSubscribed => _inner is not null;
            }
        }
    }

    /// <summary>
    ///     NEGATIVE control for rule 1's SCOPE. It holds the concrete client in
    ///     a private field — the shape <c>IpcHarborClient</c> and
    ///     <c>EventSubscription</c> both have, and the shape rule 1 must leave
    ///     alone because those types are the implementation, not a wrapper.
    /// </summary>
    private sealed class ImplementationProbe
    {
        /// <summary>The implementation's own reference to what it was built with.</summary>
        private readonly MessagePackRpcClient _rpc = null!;

        /// <summary>Never called; keeps the field from being trimmed.</summary>
        public MessagePackRpcClient Rpc => _rpc;
    }

    /// <summary>
    ///     NEGATIVE control for rule 1: the same member, typed as a CONTRACT.
    ///     This is the shape #494 steers code towards.
    /// </summary>
    private sealed class ContractMemberProbe
    {
        /// <summary>Stands in for the RPC-client contract added by #494.</summary>
        private readonly IProbeRpcContract _inner = null!;

        /// <summary>Never called; keeps the field from being trimmed.</summary>
        public bool IsConnected => _inner is not null;
    }

    /// <summary>
    ///     Stands in for the RPC-client contract. Declared locally so this file
    ///     compiles and reads the same whether or not that contract exists yet,
    ///     and so the negative control is not accidentally coupled to it.
    /// </summary>
    private interface IProbeRpcContract : IAsyncDisposable
    {
        /// <summary>Connect the underlying channel.</summary>
        Task ConnectAsync(CancellationToken ct = default);

        /// <summary>Send a request and await its response.</summary>
        Task<HarborResponse> SendAsync(HarborRequest request, CancellationToken ct = default);
    }

    /// <summary>
    ///     POSITIVE CONTROL for rule 2: publishes the concrete writer through a
    ///     property — the shape CellForgeRenderContext.Writer had.
    /// </summary>
    private sealed class PublishedWriterProbe
    {
        /// <summary>The banned shape: a property whose TYPE is the concrete writer.</summary>
        public AnsiWriter Writer { get; } = null!;
    }

    /// <summary>
    ///     NEGATIVE control for rule 2: CONSUMES the concrete writer by
    ///     composition — the shape ScreenSession and DiffEngine.Flush both use,
    ///     and which rule 2 must leave alone.
    /// </summary>
    private sealed class WiredWriterProbe
    {
        /// <summary>The composition-root's choice of implementation.</summary>
        private readonly AnsiWriter _writer;

        /// <summary>Wire a writer in, exactly as ScreenSession is wired.</summary>
        public WiredWriterProbe(AnsiWriter writer) => _writer = writer;

        /// <summary>Never called; keeps the field from being trimmed.</summary>
        public int WriterCount => _writer.TrackedX;
    }
}