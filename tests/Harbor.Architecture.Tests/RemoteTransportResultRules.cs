// RemoteTransportResultRules.cs — the guard for issue #587.
//
// WHY THIS FILE EXISTS
// --------------------
// `IMcpRemoteTransport` used to declare exactly one member:
//
//     Task<JsonDocument?> RoundTripAsync(...)
//
// A nullable document has two meanings and no way to tell them apart: "the
// server answered 202 with no body" and "the endpoint returned 503 three times
// in 12.4 s". The lossless `Result<Maybe<JsonDocument>> TryRoundTripAsync`
// already existed on both concrete transports (#201 C4) — but only on the
// concrete classes, so the interface could not reach it, and the single
// production caller went through a `Match(static doc => ..., _ => null)`
// compat wrapper that threw the error away. `McpRegistry` then turned the
// resulting `null` into the fixed string "MCP server 'x' returned no response.",
// so the endpoint, the HTTP status, the attempt count and the latency reached
// the log file and nothing else. A user filing a bug could not see any of it.
//
// The fix is a type change on the seam, and a type change alone is exactly the
// kind of thing that rots: a future transport drops in, someone adds a
// `Task<JsonDocument?>` overload "just for compatibility", and the whole
// diagnostic is gone again with no test noticing. `BannedSymbols.txt` cannot
// express this rule — "this method must not return a nullable" is a missing
// shape, not a named symbol. Hence this file.
//
// TWO RULES, TWO DIRECTIONS
// -------------------------
//   1. SEAM (reflection, the interface): every member of `IMcpRemoteTransport`
//      must return `Task<Result<…>>`. This makes the lossless overload the only
//      thing a caller can reach, and goes red the moment a nullable-returning
//      member is re-added.
//   2. IMPLEMENTORS (reflection, the assembly): no concrete transport may
//      re-introduce a nullable-returning round-trip member either. Rule 1 alone
//      would not catch it — the two wrappers this issue deletes were `public`
//      on `public` sealed classes, which is exactly how they stayed reachable
//      after the interface stopped naming them. The implementor set is
//      discovered from the interface, so a third transport is covered the day
//      it is written rather than the day somebody remembers to edit this file.
//
// NON-VACUITY
// -----------
// A reflection guard over an interface that was emptied, or an implementor scan
// that matches nothing, passes forever and reads as a green light. The last
// test below closes that: it requires the discovery step to find a non-trivial
// number of implementors and members.

using System.Reflection;
using CSharpFunctionalExtensions;
using Harbor.Tools.Mcp;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Fails the build if the remote-MCP transport seam stops being a
///     <c>Result</c> railway. See the file header for the rationale.
/// </summary>
public class RemoteTransportResultRules
{
    /// <summary>
    ///     Every declared member of <see cref="IMcpRemoteTransport" /> must return
    ///     <c>Task&lt;Result&lt;…&gt;&gt;</c>.
    /// </summary>
    [Test]
    public async Task Assert_EveryTransportSeamMemberReturnsAResult()
    {
        var violations = new List<string>();

        foreach (MethodInfo method in typeof(IMcpRemoteTransport)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (IsResultRoundTrip(method.ReturnType))
            {
                continue;
            }

            violations.Add(
                $"{typeof(IMcpRemoteTransport).Name}.{method.Name} returns {Describe(method.ReturnType)}");
        }

        await Assert.That(violations).IsEmpty().Because(
            $"every member of IMcpRemoteTransport must return Task<Result<…>>. A nullable or bare return cannot tell "
            + $"\"the server answered with no body\" apart from \"the endpoint returned 503 three times\", and the second "
            + $"case is what used to reach the user as a bare \"returned no response\". Declare "
            + $"Task<Result<Maybe<JsonDocument>>> TryRoundTripAsync(…) and keep the diagnostic. Offenders:"
            + $"{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    /// <summary>
    ///     No concrete <see cref="IMcpRemoteTransport" /> may expose a member that
    ///     hands back a nullable document — the shape of the two compat wrappers
    ///     this issue removed. Reflection over discovered implementors, not a
    ///     hard-coded pair of type names.
    /// </summary>
    [Test]
    public async Task Assert_NoConcreteTransportExposesANullableRoundTrip()
    {
        var violations = new List<string>();

        foreach (Type transport in ConcreteImplementors())
        {
            foreach (MethodInfo method in transport
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (!method.Name.Contains("RoundTrip", StringComparison.Ordinal))
                {
                    continue;
                }

                if (IsResultRoundTrip(method.ReturnType))
                {
                    continue;
                }

                violations.Add($"{transport.Name}.{method.Name} returns {Describe(method.ReturnType)}");
            }
        }

        await Assert.That(violations).IsEmpty().Because(
            $"a transport member returning anything other than Task<Result<…>> is the flattening this issue removed: "
            + $"TryRoundTripAsync(RoundTripAsync(…)) hides the failure behind a null, and the next caller writes "
            + $"\"no response\" for a 503. Keep only the Result-returning member. Offenders:"
            + $"{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    /// <summary>
    ///     Non-vacuity: both discovery steps above must find something. Without
    ///     this, an emptied interface or a scan that silently finds no implementors
    ///     would pass forever and be believed.
    /// </summary>
    [Test]
    public async Task Assert_TheGuardActuallyFindsTheSeamAndItsImplementors()
    {
        int members = typeof(IMcpRemoteTransport)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Length;
        List<Type> implementors = ConcreteImplementors();

        await Assert.That(members).IsGreaterThan(0)
            .Because("IMcpRemoteTransport declared no members — the seam rule above is vacuous");
        await Assert.That(implementors.Count).IsGreaterThanOrEqualTo(2)
            .Because(
                $"expected both builtin remote transports (streamable HTTP + legacy SSE) to be discoverable, found {implementors.Count}");
        await Assert.That(implementors).DoesNotContain(typeof(IMcpRemoteTransport))
            .Because("the interface itself must not count as a concrete implementor");
    }

    /// <summary>Every non-abstract class in the assembly that implements the seam.</summary>
    private static List<Type> ConcreteImplementors()
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
            if (type is { IsInterface: false, IsAbstract: false }
                && typeof(IMcpRemoteTransport).IsAssignableFrom(type))
            {
                result.Add(type);
            }
        }

        return result;
    }

    /// <summary>
    ///     True for <c>Task&lt;Result&lt;…&gt;&gt;</c> — the one shape the seam may
    ///     return. The inner <c>Result</c>'s payload is deliberately not inspected:
    ///     <c>Result&lt;Maybe&lt;JsonDocument&gt;&gt;</c> is today's answer, but the
    ///     rule being enforced is "a Result crosses the seam", not which payload it
    ///     carries.
    /// </summary>
    private static bool IsResultRoundTrip(Type returnType)
    {
        if (returnType is not { IsGenericType: true })
        {
            return false;
        }

        if (returnType.GetGenericTypeDefinition() != typeof(Task<>))
        {
            return false;
        }

        Type payload = returnType.GetGenericArguments()[0];
        return payload is { IsGenericType: true }
            && payload.GetGenericTypeDefinition() == typeof(Result<>);
    }

    /// <summary>Readable rendering for a failure message, generic arity folded in.</summary>
    private static string Describe(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.Name;
        }

        string name = type.Name;
        int tick = name.IndexOf('`', StringComparison.Ordinal);
        if (tick >= 0)
        {
            name = name[..tick];
        }

        return $"{name}<{string.Join(", ", Array.ConvertAll(type.GetGenericArguments(), Describe))}>";
    }
}
