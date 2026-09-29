using System.Reflection;
using Harbor.Abstractions.Permissions;
using Harbor.Abstractions.Tools;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Harbor.Abstractions.Tests;

/// <summary>
///     #470 — regression guards for the two service locators that used to sit in
///     hot contracts.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="ToolContext" /> declared an <c>IServiceProvider Services</c>
///         that <c>ToolDispatcher</c> and <c>McpStdioServer</c> both passed as
///         <c>null!</c>: the signature promised a container that never existed, the
///         compiler stayed quiet, and seven tools hung a resolution off it. The
///         field is gone; these tests fail if it ever comes back in either shape.
///     </para>
/// </remarks>
public class ServiceLocatorContractTests
{
    /// <summary>
    ///     No constructor parameter and no property of <see cref="ToolContext" />
    ///     may be a service locator — neither typed as <see cref="IServiceProvider" />
    ///     nor assignable from one.
    /// </summary>
    [Test]
    public async Task ToolContext_ExposesNoServiceProvider()
    {
        Type type = typeof(ToolContext);
        bool IsLocator(Type candidate) =>
            candidate == typeof(IServiceProvider) || typeof(IServiceProvider).IsAssignableFrom(candidate);

        var locatorParams = type
            .GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Where(p => IsLocator(p.ParameterType))
            .Select(p => p.Name ?? p.ParameterType.Name)
            .ToArray();

        var locatorProperties = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(p => IsLocator(p.PropertyType))
            .Select(p => p.Name)
            .ToArray();

        await Assert.That(locatorParams).IsEmpty();
        await Assert.That(locatorProperties).IsEmpty();
    }

    /// <summary>
    ///     A <see cref="ToolContext" /> is constructible with exactly the eight
    ///     honest members — no hidden trailing provider that callers must pad with
    ///     a placeholder argument.
    /// </summary>
    [Test]
    public async Task ToolContext_HasExactlyEightDeclaredMembers()
    {
        ParameterInfo[] parameters = typeof(ToolContext)
            .GetConstructors().Single().GetParameters();

        await Assert.That(parameters.Length).IsEqualTo(8);
        await Assert.That(parameters.Select(p => p.Name).ToArray()).IsEquivalentTo(new[]
        {
            "SessionId", "MessageId", "CallId", "Agent", "Abort", "Messages", "ReportProgress", "Ask",
        });
    }

    /// <summary>
    ///     The <c>PermissionResponse</c> / <c>PermissionRequest</c> round trip that
    ///     used to sit next to the provider still behaves — a smoke test that the
    ///     record was reshaped, not broken.
    /// </summary>
    [Test]
    public async Task ToolContext_RoundTripsItsCoreMembers()
    {
        var context = new ToolContext(
            "s1",
            "m1",
            "c1",
            "code",
            CancellationToken.None,
            Array.Empty<Harbor.Abstractions.Models.AgentMessage>(),
            (_, _) => Task.CompletedTask,
            (_, _) => Task.FromResult(new PermissionResponse(PermissionAction.Allow, false)));

        await Assert.That(context.SessionId).IsEqualTo("s1");
        await Assert.That(context.Agent).IsEqualTo("code");
        var answer = await context.Ask(
            PermissionRequest.Create("read", "file", default, Array.Empty<string>()),
            CancellationToken.None);
        await Assert.That(answer.Action).IsEqualTo(PermissionAction.Allow);
    }
}
