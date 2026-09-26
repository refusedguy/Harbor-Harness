using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Harbor.Abstractions.Models;
using MemoryPack;
using TUnit.Assertions;

namespace Harbor.Domain.Tests;

/// <summary>
///     #87.3: polymorphic roots that actually failed a base-typed round-trip —
///     <c>AgentMessage</c>/<c>ContentPart</c> had MemoryPack unions but no STJ
///     discriminator (manual role-switch only), <c>ToolChoice</c> had neither.
///     Hierarchies without a live serialization path are deliberately left
///     alone (no speculative roots).
/// </summary>
public class PolymorphicRoundTripTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private static readonly DateTimeOffset When = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static JsonElement OwnedArgs(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static async Task<AgentMessage> StjRoundTripAsync(AgentMessage message)
    {
        string json = JsonSerializer.Serialize<AgentMessage>(message, WebOptions);
        AgentMessage? back = JsonSerializer.Deserialize<AgentMessage>(json, WebOptions);

        await Assert.That(back).IsNotNull();
        return back!;
    }

    private static async Task<ToolChoice> StjRoundTripAsync(ToolChoice choice)
    {
        string json = JsonSerializer.Serialize<ToolChoice>(choice, WebOptions);
        ToolChoice? back = JsonSerializer.Deserialize<ToolChoice>(json, WebOptions);

        await Assert.That(back).IsNotNull();
        return back!;
    }

    private static async Task<ToolChoice> MemoryPackRoundTripAsync(ToolChoice choice)
    {
        byte[] bytes = MemoryPackSerializer.Serialize<ToolChoice>(choice);
        ToolChoice? back = MemoryPackSerializer.Deserialize<ToolChoice>(bytes);

        await Assert.That(back).IsNotNull();
        return back!;
    }

    [Test]
    public async Task UserMessage_StjRoundTrip_PreservesCase()
    {
        var original = new UserMessage("m1", "s1", When, "hi", "code", "p/m");

        var back = (UserMessage)await StjRoundTripAsync(original);

        await Assert.That(back.Id).IsEqualTo("m1");
        await Assert.That(back.SessionId).IsEqualTo("s1");
        await Assert.That(back.Content).IsEqualTo("hi");
        await Assert.That(back.Agent).IsEqualTo("code");
        await Assert.That(back.Model).IsEqualTo("p/m");
        await Assert.That(back.Role).IsEqualTo("user");
    }

    [Test]
    public async Task AssistantMessage_StjRoundTrip_PreservesAllPartKinds()
    {
        const string args = """{"path":"a.txt"}""";
        var original = new AssistantMessage(
            "a1", "s1", When,
            new ContentPart[]
            {
                new TextPart("hello"),
                new ThinkingPart("hmm"),
                ToolCallPart.Create("tc1", "read", OwnedArgs(args)),
                new FilePart("a.png", "image/png", 10),
            },
            StopReason.ToolUse,
            new Usage(10, 20),
            "p/m");

        var back = (AssistantMessage)await StjRoundTripAsync(original);

        await Assert.That(back.Role).IsEqualTo("assistant");
        await Assert.That(back.StopReason).IsEqualTo(StopReason.ToolUse);
        await Assert.That(back.Usage).IsEqualTo(new Usage(10, 20));
        await Assert.That(back.Parts.Count).IsEqualTo(4);
        await Assert.That(((TextPart)back.Parts[0]).Text).IsEqualTo("hello");
        await Assert.That(((ThinkingPart)back.Parts[1]).Text).IsEqualTo("hmm");
        var toolCall = (ToolCallPart)back.Parts[2];
        await Assert.That(toolCall.Id).IsEqualTo("tc1");
        await Assert.That(toolCall.ToolName).IsEqualTo("read");
        await Assert.That(toolCall.Args.GetRawText()).IsEqualTo(args);
        var file = (FilePart)back.Parts[3];
        await Assert.That(file.Path).IsEqualTo("a.png");
        await Assert.That(file.MimeType).IsEqualTo("image/png");
        await Assert.That(file.SizeBytes).IsEqualTo(10L);
    }

    [Test]
    public async Task ToolResultMessage_StjRoundTrip_PreservesEntries()
    {
        var original = new ToolResultMessage(
            "t1", "s1", When,
            new[] { new ToolResultEntry("tc1", "read", "ok", false) });

        var back = (ToolResultMessage)await StjRoundTripAsync(original);

        await Assert.That(back.Role).IsEqualTo("tool_result");
        await Assert.That(back.Results.Count).IsEqualTo(1);
        await Assert.That(back.Results[0].ToolCallId).IsEqualTo("tc1");
        await Assert.That(back.Results[0].ToolName).IsEqualTo("read");
        await Assert.That(back.Results[0].Output).IsEqualTo("ok");
        await Assert.That(back.Results[0].IsError).IsFalse();
    }

    [Test]
    public async Task AssistantMessage_MemoryPackRoundTrip_PreservesCaseAndArgs()
    {
        const string args = """{"path":"a.txt"}""";
        AgentMessage original = new AssistantMessage(
            "a1", "s1", When,
            new ContentPart[] { ToolCallPart.Create("tc1", "read", OwnedArgs(args)) },
            StopReason.ToolUse,
            new Usage(10, 20),
            "p/m");

        byte[] bytes = MemoryPackSerializer.Serialize(original);
        AgentMessage? back = MemoryPackSerializer.Deserialize<AgentMessage>(bytes);

        await Assert.That(back).IsNotNull();
        var assistant = (AssistantMessage)back!;
        await Assert.That(assistant.Parts.Count).IsEqualTo(1);
        var toolCall = (ToolCallPart)assistant.Parts[0];
        await Assert.That(toolCall.Args.GetRawText()).IsEqualTo(args);
    }

    [Test]
    public async Task ToolChoice_StjRoundTrip_PreservesAllCases()
    {
        await Assert.That(await StjRoundTripAsync(new ToolChoice.Auto())).IsTypeOf<ToolChoice.Auto>();
        await Assert.That(await StjRoundTripAsync(new ToolChoice.None())).IsTypeOf<ToolChoice.None>();
        await Assert.That(await StjRoundTripAsync(new ToolChoice.Required())).IsTypeOf<ToolChoice.Required>();

        var specific = (ToolChoice.Specific)await StjRoundTripAsync(new ToolChoice.Specific("read"));
        await Assert.That(specific.ToolName).IsEqualTo("read");
    }

    [Test]
    public async Task ToolChoice_MemoryPackRoundTrip_PreservesAllCases()
    {
        await Assert.That(await MemoryPackRoundTripAsync(new ToolChoice.Auto())).IsTypeOf<ToolChoice.Auto>();
        await Assert.That(await MemoryPackRoundTripAsync(new ToolChoice.None())).IsTypeOf<ToolChoice.None>();
        await Assert.That(await MemoryPackRoundTripAsync(new ToolChoice.Required())).IsTypeOf<ToolChoice.Required>();

        var specific = (ToolChoice.Specific)await MemoryPackRoundTripAsync(new ToolChoice.Specific("read"));
        await Assert.That(specific.ToolName).IsEqualTo("read");
    }

    [Test]
    public async Task EveryConcreteAgentMessage_IsRegisteredAsJsonDerivedType()
    {
        await Assert.That(Unregistered(typeof(AgentMessage))).IsEmpty();
    }

    [Test]
    public async Task EveryConcreteContentPart_IsRegisteredAsJsonDerivedType()
    {
        await Assert.That(Unregistered(typeof(ContentPart))).IsEmpty();
    }

    [Test]
    public async Task EveryConcreteToolChoice_IsRegisteredAsJsonDerivedType()
    {
        await Assert.That(Unregistered(typeof(ToolChoice))).IsEmpty();
    }

    [Test]
    public async Task AllDiscriminators_AreUniqueAndNonEmpty()
    {
        var discriminators = new[]
            {
                typeof(AgentMessage),
                typeof(ContentPart),
                typeof(ToolChoice),
            }
            .SelectMany(t => t.GetCustomAttributes<JsonDerivedTypeAttribute>())
            .Select(a => (string)a.TypeDiscriminator!)
            .ToArray();

        await Assert.That(discriminators.Any(string.IsNullOrWhiteSpace)).IsFalse();
        await Assert.That(discriminators.GroupBy(d => d).Any(g => g.Count() > 1)).IsFalse();
    }

    private static string[] Unregistered(Type baseType)
    {
        HashSet<Type> registered = baseType
            .GetCustomAttributes<JsonDerivedTypeAttribute>()
            .Select(attribute => attribute.DerivedType)
            .ToHashSet();

        return baseType.Assembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false })
            .Where(baseType.IsAssignableFrom)
            .Where(type => !registered.Contains(type))
            .Select(type => type.Name)
            .OrderBy(name => name)
            .ToArray();
    }
}
