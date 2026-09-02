using System.Reflection;
using System.Text.Json.Serialization;
using Harbor.Abstractions.Events;
using TUnit.Assertions;

namespace Harbor.Abstractions.Tests;

/// <summary>
///     P.5 guard (F21, deep2-core): every concrete AgentEvent record MUST carry
///     a [JsonDerivedType] registration on the AgentEvent base. The compiler
///     cannot protect this contract — a new event without registration
///     compiles fine and then throws SerializationException only when a future
///     wire/log export serializes events. This test is that missing protection.
/// </summary>
public class AgentEventPolymorphismGuardTests
{
    [Test]
    public async Task EveryConcreteAgentEvent_IsRegisteredAsJsonDerivedType()
    {
        Type baseType = typeof(AgentEvent);

        HashSet<Type> registered = baseType
            .GetCustomAttributes<JsonDerivedTypeAttribute>()
            .Select(attribute => attribute.DerivedType)
            .ToHashSet();

        List<Type> concreteEvents = baseType.Assembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false })
            .Where(baseType.IsAssignableFrom)
            .ToList();

        await Assert.That(concreteEvents).IsNotEmpty();

        var unregistered = concreteEvents
            .Where(type => !registered.Contains(type))
            .Select(type => type.Name)
            .OrderBy(name => name)
            .ToArray();

        await Assert.That(unregistered).IsEmpty();
    }

    [Test]
    public async Task EveryRegisteredDiscriminator_IsUniqueAndNonEmpty()
    {
        IEnumerable<JsonDerivedTypeAttribute> attributes =
            typeof(AgentEvent).GetCustomAttributes<JsonDerivedTypeAttribute>();

        string[] discriminators = attributes
            .Select(a => (string)a.TypeDiscriminator!)
            .ToArray();

        await Assert.That(discriminators.Any(string.IsNullOrWhiteSpace)).IsFalse();
        await Assert.That(discriminators.GroupBy(d => d).Any(g => g.Count() > 1)).IsFalse();
    }
}
