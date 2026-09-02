using Harbor.Ipc.Protocol;
using MessagePack;

namespace Harbor.Ipc.Tests;

/// <summary>
///     Tests that every <see cref="HarborRequest" /> and
///     <see cref="HarborResponse" /> subtype round-trips through
///     MessagePack serialization without losing data. This catches
///     <c>[Key]</c> ordering bugs and missing <c>[Union]</c> tags early
///     — long before they reach a non-.NET client.
/// </summary>
[NotInParallel]
    public class ProtocolSerializationTests
{
    /// <summary>
    ///     Every concrete HarborRequest subtype must round-trip through
    ///     MessagePack serialize → deserialize as the abstract base type.
    /// </summary>
    [Test]
    [Arguments(typeof(StartAgentRequest))]
    [Arguments(typeof(AbortAgentRequest))]
    [Arguments(typeof(SendPromptRequest))]
    [Arguments(typeof(CreateSessionRequest))]
    [Arguments(typeof(ListSessionsRequest))]
    [Arguments(typeof(GetSessionRequest))]
    [Arguments(typeof(DeleteSessionRequest))]
    [Arguments(typeof(GetMessagesRequest))]
    [Arguments(typeof(ListProvidersRequest))]
    [Arguments(typeof(ListModelsRequest))]
    [Arguments(typeof(ListToolsRequest))]
    [Arguments(typeof(SubscribeToEventsRequest))]
    [Arguments(typeof(ConnectRequest))]
    [Arguments(typeof(DisconnectRequest))]
    [Arguments(typeof(PskAuthRequest))]
    public async Task Request_Subtype_RoundTrips_Through_MessagePack(Type requestType)
    {
        // Construct via parameterless or default-arg constructor.
        var request = (HarborRequest?)Activator.CreateInstance(requestType, true)
            ?? throw new InvalidOperationException($"Could not construct {requestType.Name}");

        byte[] bytes = MessagePackSerializer.Serialize((HarborRequest)request);
        HarborRequest deserialized = MessagePackSerializer.Deserialize<HarborRequest>(bytes);

        await Assert.That(deserialized).IsNotNull();
        await Assert.That(deserialized.GetType()).IsEqualTo(requestType);
        await Assert.That(deserialized.RequestId).IsEqualTo(request.RequestId);
    }

    /// <summary>
    ///     Sprint 6 A1/A3: envelope sequence + target addressing survive the
    ///     wire; SubscribeToEventsRequest carries the replay bookkeeping.
    /// </summary>
    [Test]
    public async Task EventEnvelope_Sequence_And_TargetClientId_RoundTrip()
    {
        var envelope = new EventEnvelope
        {
            EventBytes = [1, 2, 3],
            Sequence = 987654321UL,
            TargetClientId = "client-42"
        };

        byte[] bytes = MessagePackSerializer.Serialize((HarborResponse)envelope);
        var back = (EventEnvelope)MessagePackSerializer.Deserialize<HarborResponse>(bytes);

        await Assert.That(back.Sequence).IsEqualTo(987654321UL);
        await Assert.That(back.TargetClientId).IsEqualTo("client-42");
        await Assert.That(back.EventBytes).IsEquivalentTo(new byte[] { 1, 2, 3 });
    }

    [Test]
    public async Task SubscribeToEvents_LastSequence_RoundTrips()
    {
        HarborRequest request = new SubscribeToEventsRequest(12345UL);
        byte[] bytes = MessagePackSerializer.Serialize(request);
        var back = (SubscribeToEventsRequest)MessagePackSerializer.Deserialize<HarborRequest>(bytes);

        await Assert.That(back.LastSequence).IsEqualTo(12345UL);

        byte[] legacyBytes = MessagePackSerializer.Serialize((HarborRequest)new SubscribeToEventsRequest());
        var legacy = (SubscribeToEventsRequest)MessagePackSerializer.Deserialize<HarborRequest>(legacyBytes);
        await Assert.That(legacy.LastSequence).IsNull();
    }

    /// <summary>
    ///     OkResponse with a null payload must round-trip.
    /// </summary>
    [Test]
    public async Task OkResponse_With_Null_Payload_RoundTrips()
    {
        var resp = new OkResponse { RequestId = Guid.NewGuid() };
        byte[] bytes = MessagePackSerializer.Serialize((HarborResponse)resp);
        var deserialized = (OkResponse)MessagePackSerializer.Deserialize<HarborResponse>(bytes);

        await Assert.That(deserialized).IsNotNull();
        await Assert.That(deserialized.RequestId).IsEqualTo(resp.RequestId);
        await Assert.That(deserialized.Payload).IsNull();
    }

    /// <summary>
    ///     ErrorResponse must round-trip with the error message preserved.
    /// </summary>
    [Test]
    public async Task ErrorResponse_RoundTrips_With_Message()
    {
        var resp = new ErrorResponse { RequestId = Guid.NewGuid(), Message = "test error" };
        byte[] bytes = MessagePackSerializer.Serialize((HarborResponse)resp);
        var deserialized = (ErrorResponse)MessagePackSerializer.Deserialize<HarborResponse>(bytes);

        await Assert.That(deserialized).IsNotNull();
        await Assert.That(deserialized.Message).IsEqualTo("test error");
    }

    /// <summary>
    ///     EventEnvelope must round-trip with the event bytes preserved.
    /// </summary>
    [Test]
    public async Task EventEnvelope_RoundTrips_With_EventBytes()
    {
        var evt = new HarborEventAgentStarted("test-session-id");
        byte[] evtBytes = MessagePackSerializer.Serialize((HarborEventData)evt);
        var envelope = new EventEnvelope { EventBytes = evtBytes };
        byte[] bytes = MessagePackSerializer.Serialize((HarborResponse)envelope);
        var deserialized = (EventEnvelope)MessagePackSerializer.Deserialize<HarborResponse>(bytes);

        await Assert.That(deserialized).IsNotNull();
        await Assert.That(deserialized.EventBytes).IsNotNull();
        var evt2 = (HarborEventAgentStarted)MessagePackSerializer.Deserialize<HarborEventData>(deserialized.EventBytes!);
        await Assert.That(evt2.SessionId).IsEqualTo("test-session-id");
    }

    /// <summary>
    ///     Every concrete HarborEventData subtype must round-trip through
    ///     MessagePack serialize → deserialize as the abstract base type.
    /// </summary>
    [Test]
    public async Task EventData_All_Subtypes_RoundTrip()
    {
        HarborEventData[] samples =
        [
            new HarborEventAgentStarted("s1"),
            new HarborEventMessageUpdate(null, "delta"),
            new HarborEventMessageEnd(null),
            new HarborEventToolStart("tc1", "read"),
            new HarborEventToolEnd("tc1", null),
            new HarborEventTurnStart(1),
            new HarborEventTurnEnd(1),
            new HarborEventAgentEnded("s1"),
            new HarborEventAgentError("boom"),
            new HarborEventCompactionStarted("s1"),
            new HarborEventCompactionCompleted("s1", 5, 1000)
        ];

        foreach (var sample in samples)
        {
            byte[] bytes = MessagePackSerializer.Serialize((HarborEventData)sample);
            HarborEventData deserialized = MessagePackSerializer.Deserialize<HarborEventData>(bytes);

            await Assert.That(deserialized).IsNotNull();
            await Assert.That(deserialized.GetType()).IsEqualTo(sample.GetType());
        }
    }

    /// <summary>
    ///     The <see cref="WireCodec" /> domain-type serializer/deserializer
    ///     must round-trip a Session instance.
    /// </summary>
    [Test]
    public async Task WireCodec_SerializeDomain_RoundTrips_Session()
    {
        var session = Harbor.Abstractions.Models.Session.Create(
            "/tmp/test", "code", "ollama", "qwen2.5-coder:7b");

        byte[] bytes = WireCodec.SerializeDomain(session);
        var restored = WireCodec.DeserializeDomain<Harbor.Abstractions.Models.Session>(bytes);

        await Assert.That(restored).IsNotNull();
        await Assert.That(restored!.Id).IsEqualTo(session.Id);
        await Assert.That(restored.Agent).IsEqualTo("code");
    }

    /// <summary>
    ///     The <see cref="WireCodec" /> domain-type serializer/deserializer
    ///     must round-trip a list of sessions.
    /// </summary>
    [Test]
    public async Task WireCodec_SerializeDomain_RoundTrips_List_Of_Session()
    {
        var sessions = new[]
        {
            Harbor.Abstractions.Models.Session.Create("/tmp/a", "code", "ollama", "m1"),
            Harbor.Abstractions.Models.Session.Create("/tmp/b", "plan", "anthropic", "m2")
        };

        byte[] bytes = WireCodec.SerializeDomain<IReadOnlyList<Harbor.Abstractions.Models.Session>>(sessions);
        var restored = WireCodec.DeserializeDomain<IReadOnlyList<Harbor.Abstractions.Models.Session>>(bytes);

        await Assert.That(restored).IsNotNull();
        await Assert.That(restored!.Count).IsEqualTo(2);
        await Assert.That(restored[0].Id).IsEqualTo(sessions[0].Id);
    }

}
