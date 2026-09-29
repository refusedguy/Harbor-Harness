// TransportRetryOwnershipRules.cs — the guard for issue #572.
//
// WHY THIS FILE EXISTS
// --------------------
// "Should this failure be retried, and how long should we wait?" was supposed to
// have one owner: `Harbor.Application.Resilience.RetryPolicy`. Instead the two
// remote-MCP transports each carried a private copy of the classification, and
// the copies answered differently from the owner:
//
//     private static bool IsTransient(Exception ex)
//         => ex is HttpRequestException or IOException or TimeoutException;
//
// — byte-identical in `McpHttpTransport` and `McpSseTransport`, and both
// disagreed with `RetryPolicy.IsTransient`, whose classifier chain has no arm
// for `IOException` or a bare `TimeoutException`. So the app had two answers to
// "is a dropped socket retryable", and a third copy of the same two-type arm
// lived in `DefaultToolRetryDecider`, twenty lines from the owner that denied
// them. `MaxAttempts = 3` / `FirstRetryDelay = 200ms` were `const`s in both
// transport files, so tuning retry behaviour meant editing three files.
//
// WHY A DEDICATED FILE, NOT A BANNED SYMBOL
// -----------------------------------------
// `BannedSymbols.txt` cannot express this rule. The thing that must not come
// back is a *shape* — "this assembly declares its own retry-classification
// predicate", "this assembly declares its own retry budget" — neither of which
// has a name to ban. A fork re-authored as `ShouldRetry`, `Retryable`,
// `IsRetryWorthy` or a `switch` expression sails past a name list, which is
// exactly the shape the existing decider already had.
//
// WHAT IS ACTUALLY BANNED, AND WHY IT IS NOT "CALL RetryPolicy INSTEAD"
// ---------------------------------------------------------------------
// The ownership boundary is the layer matrix in `docs/ARCHITECTURE_LAYERS.md`
// §2, mechanically enforced by `FullLayerMatrixTests`:
//
//     Harbor.Tools.Builtin -> Harbor.Application   FORBIDDEN
//
// `Harbor.Tools.Builtin` is Infrastructure and may reference only
// `Harbor.Abstractions` + `Harbor.Extensions`. The transports therefore cannot
// call `RetryPolicy` — an Infrastructure → Application edge is precisely the
// dependency-inversion violation, and `RetryPolicy` sits a layer above them.
// The shared verdict belongs in the Domain facade (`Harbor.Abstractions`,
// which carries `Harbor.Abstractions.Contracts`), the direction the whole
// Infrastructure section already uses; see the `Harbor.Terminal.Pty` row in
// `FullLayerMatrixTests` for the same argument already made for a contract that
// Infrastructure owns. The commit that introduces that owner proves the
// reachability mechanically instead of asserting it in prose here.
//
// Note the two retry LOOPS themselves were never byte-identical, contrary to
// the issue text: `McpHttpTransport` retries on 5xx/408 plus transient
// exceptions, while `McpSseTransport` retries on *any* `Failure` out of its
// single round-trip — which sweeps in 401/403/404 and an OAuth failure, none of
// which a retry can fix. That divergence is a separate defect from the
// duplication this guard locks down and is deliberately out of scope here.
//
// NON-VACUITY
// -----------
// A reflection guard over a set that is empty passes forever and reads as a
// green light. `Assert_TheGuardActuallyFindsTheTransports` requires the
// discovery step to find both builtin transports, so a rename or a refactor that
// emptied the set fails loudly instead of going quiet.

using System.Reflection;
using Harbor.Tools.Mcp;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Fails the build if a remote-MCP transport declares its own retry
///     classification or its own retry budget. See the file header for the
///     rationale and the layer-matrix argument.
/// </summary>
public class TransportRetryOwnershipRules
{
    /// <summary>
    ///     Field names that make up the private retry budget #572 found
    ///     duplicated in two transport files. Matched by name because a
    ///     <c>const int</c> and a <c>static readonly TimeSpan</c> cannot be told
    ///     apart reliably by type, and the name is what both copies shared.
    /// </summary>
    private static readonly string[] BannedRetryBudgetFields = ["MaxAttempts", "FirstRetryDelay"];

    /// <summary>Public + non-public, static + instance, declared on the type itself.</summary>
    private const BindingFlags DeclaredMembers =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

    /// <summary>
    ///     No transport may declare a method shaped like a retry-classification
    ///     predicate — <c>(Exception) -&gt; bool</c>. That is precisely the
    ///     signature of the private <c>IsTransient</c> fork, whatever it is
    ///     called.
    /// </summary>
    [Test]
    public async Task Assert_NoTransportDeclaresItsOwnRetryClassifier()
    {
        var violations = new List<string>();

        foreach (Type transport in ConcreteTransports())
        {
            foreach (MethodInfo method in transport.GetMethods(DeclaredMembers))
            {
                if (!IsExceptionToBoolPredicate(method))
                {
                    continue;
                }

                violations.Add($"{transport.Name}.{method.Name}");
            }
        }

        await Assert.That(violations).IsEmpty().Because(
            "a transport declared its own (Exception) -> bool retry classifier. \"Is this failure transient?\" has one "
            + "answer, owned above Infrastructure because Harbor.Tools.Builtin may not reference Harbor.Application "
            + "(docs/ARCHITECTURE_LAYERS.md 2, enforced by FullLayerMatrixTests). The copies that kept reappearing "
            + $"answered differently from that owner. Offenders:{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    /// <summary>
    ///     No transport may declare the attempt cap or the first-retry delay.
    ///     Those numbers are the policy's to own: <c>MaxAttempts = 3</c> and
    ///     <c>FirstRetryDelay = 200ms</c> were constants in each transport file,
    ///     so a tuning change had to land in three places at once.
    /// </summary>
    [Test]
    public async Task Assert_NoTransportDeclaresItsOwnRetryBudget()
    {
        var violations = new List<string>();

        foreach (Type transport in ConcreteTransports())
        {
            foreach (FieldInfo field in transport.GetFields(DeclaredMembers))
            {
                if (BannedRetryBudgetFields.Contains(field.Name, StringComparer.Ordinal))
                {
                    violations.Add($"{transport.Name}.{field.Name}");
                }
            }
        }

        await Assert.That(violations).IsEmpty().Because(
            "a transport hard-codes the shared retry budget. The attempt cap and the first-retry delay belong to the "
            + "policy, not to each implementation — otherwise the same knob has to be edited in every copy and the "
            + "copies quietly disagree. Offenders:"
            + $"{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    /// <summary>
    ///     Non-vacuity: the discovery step must find the transports the other two
    ///     rules reason about. If it ever returns nothing they pass forever.
    /// </summary>
    [Test]
    public async Task Assert_TheGuardActuallyFindsTheTransports()
    {
        List<Type> transports = ConcreteTransports();
        string[] names = [.. transports.Select(t => t.Name)];

        await Assert.That(transports.Count).IsGreaterThanOrEqualTo(2).Because(
            $"expected both builtin remote transports (streamable HTTP + legacy SSE) to be discoverable, found {transports.Count}. "
            + "A guard over an empty set is a green light that means nothing.");

        await Assert.That(Array.IndexOf(names, "McpHttpTransport") >= 0).IsTrue().Because(
            "the two transports that carried the duplicated retry loop must be the ones under the guard — if they were "
            + "renamed or dropped from the seam, this rule has silently stopped covering the defect it was written for. "
            + $"Discovered: [{string.Join(", ", names)}]");

        await Assert.That(Array.IndexOf(names, "McpSseTransport") >= 0).IsTrue().Because(
            "same coverage requirement for the legacy SSE transport, which carried the second copy of the same fork. "
            + $"Discovered: [{string.Join(", ", names)}]");
    }

    /// <summary>Every non-abstract class in the assembly that implements the transport seam.</summary>
    private static List<Type> ConcreteTransports()
    {
        Type?[] candidates;
        try
        {
            candidates = typeof(IMcpRemoteTransport).Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            candidates = ex.Types;
        }

        var result = new List<Type>();
        foreach (Type? type in candidates)
        {
            if (type is { IsInterface: false, IsAbstract: false } && typeof(IMcpRemoteTransport).IsAssignableFrom(type))
            {
                result.Add(type);
            }
        }

        return result;
    }

    /// <summary>
    ///     True for the shape of a private retry-classification predicate: one
    ///     <see cref="Exception" /> parameter, <see cref="bool" /> return.
    /// </summary>
    private static bool IsExceptionToBoolPredicate(MethodInfo method)
    {
        if (method.IsGenericMethodDefinition || method.ReturnType != typeof(bool))
        {
            return false;
        }

        ParameterInfo[] parameters = method.GetParameters();
        return parameters.Length == 1 && parameters[0].ParameterType == typeof(Exception);
    }
}
