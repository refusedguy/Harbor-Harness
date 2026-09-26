using System.Reflection;
using CSharpFunctionalExtensions;
using Harbor.Abstractions.Agents;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Models.Identifiers;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Sessions;
using Harbor.Application.Agents;
using Harbor.Application.Tests.Fakes;
using Harbor.TestKit;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Assertions;

namespace Harbor.Application.Tests;

/// <summary>
///     #79: the live abort <see cref="CancellationTokenSource" /> must never
///     escape the runner. Callers observe <see cref="IAgentRunner.AbortToken" />
///     and abort through <see cref="IAgentRunner.RequestAbort" />, so no external
///     code can bypass the coordinated cancel funnel (approval coordinator sweeps
///     pending gates first, then aborts here) or dispose the source out from
///     under an in-flight run.
/// </summary>
public class AbortSurfaceGuardTests
{
    private static string[] CtsLeaks(Type type, BindingFlags flags) =>
        type.GetMembers(flags)
            .Where(m => m switch
            {
                PropertyInfo p => p.PropertyType == typeof(CancellationTokenSource),
                MethodInfo mi => mi.ReturnType == typeof(CancellationTokenSource),
                FieldInfo f => f.FieldType == typeof(CancellationTokenSource),
                _ => false,
            })
            .Select(m => m.Name)
            .ToArray();

    [Test]
    public async Task IAgentRunner_ExposesNoLiveCts()
    {
        // The runner interface is the only surface external callers see: a CTS
        // here would let anyone cancel/dispose around the funnel.
        await Assert.That(CtsLeaks(typeof(IAgentRunner), BindingFlags.Public | BindingFlags.Instance)).IsEmpty();
    }

    [Test]
    public async Task IAgent_ExposesNoLiveCts()
    {
        // IAgent extends IAgentRunner; assert the full contract too, so a future
        // CTS added directly on IAgent is caught.
        await Assert.That(CtsLeaks(typeof(IAgent), BindingFlags.Public | BindingFlags.Instance)).IsEmpty();
    }

    [Test]
    public async Task DefaultAgent_ExposesNoPublicLiveCts()
    {
        // The backing source stays a private field: external code cannot grab
        // and dispose it, and reflection cannot reach it via public members.
        await Assert.That(CtsLeaks(
            typeof(DefaultAgent),
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)).IsEmpty();
    }

    [Test]
    public async Task RequestAbort_IsOnlyIngress_CancelsAbortToken()
    {
        // Behavioral half: the coordinated ingress actually drives the observed token.
        var session = Session.Create("/tmp/harbor-abort-surface-guard-tests", "code", "test", "test-model");
        var agent = new DefaultAgent(
            new FakeSessionStore(session),
            new NoopLoop(),
            new FakeEventBus(),
            NullLogger<DefaultAgent>.Instance);
        agent.Initialize(session, new AgentDefinition(
            AgentName.Create("code"),
            "Code",
            "abort surface guard",
            "test-model",
            "test",
            new PermissionRuleset(new PermissionRule[] { new("*", "*", PermissionAction.Allow) })));
        try
        {
            await Assert.That(agent.AbortToken.IsCancellationRequested).IsFalse();
            agent.RequestAbort();
            await Assert.That(agent.AbortToken.IsCancellationRequested).IsTrue();
        }
        finally
        {
            agent.Dispose();
        }
    }

    private sealed class NoopLoop : IAgentLoop
    {
        public Task<Result> RunAsync(ISessionContext session, AgentDefinition agent, CancellationToken ct = default) =>
            Task.FromResult(Result.Success());
    }
}
