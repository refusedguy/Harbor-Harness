extern alias CompatWire;

using System.Collections.Generic;
using System.Linq;
using Harbor.Abstractions.Events;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using CompatChunkState = CompatWire::Harbor.Providers.Internal.ChunkStreamState;
using CompatEmitter = CompatWire::Harbor.Providers.Internal.ProviderToolCallEmitter;

namespace Harbor.Providers.Tests;

/// <summary>
///     #3762: the shared emitter is the single place a parsed tool call becomes
///     an event pair. These tests pin the observable events, their order and
///     the id-resolution policy that <c>ChunkStreamState</c> owns, so a
///     provider that stops routing through the primitive fails here.
/// </summary>
public class ProviderToolCallEmitterTests
{
    [Test]
    public async Task Emit_WireId_EmitsStartThenDeltaWithSameId()
    {
        var state = new CompatChunkState();
        var events = new List<LlmEvent>();

        CompatEmitter.Emit(events, state, 0, "call_X", "read", "{\"path\":");

        await Assert.That(events.Count).IsEqualTo(2);
        await Assert.That(events[0] is ToolCallStartEvent).IsTrue();
        await Assert.That(events[1] is ToolCallDeltaEvent).IsTrue();

        var start = (ToolCallStartEvent)events[0];
        var delta = (ToolCallDeltaEvent)events[1];
        await Assert.That(start.Id).IsEqualTo("call_X");
        await Assert.That(delta.Id).IsEqualTo("call_X");
        await Assert.That(start.ToolName).IsEqualTo("read");
        await Assert.That(delta.ArgsDelta).IsEqualTo("{\"path\":");
    }

    [Test]
    public async Task Emit_MissingWireId_ReusesRememberedIdWithoutAnotherRemap()
    {
        var state = new CompatChunkState();
        var events = new List<LlmEvent>();

        CompatEmitter.Emit(events, state, 2, null, "ls", "{}");
        CompatEmitter.Emit(events, state, 2, null, null, "{\"a\":");

        await Assert.That(state.RemappedToolCalls).IsEqualTo(1);
        await Assert.That(events.Count).IsEqualTo(3);
        var ids = events.OfType<ToolCallStartEvent>().Select(e => e.Id)
            .Concat(events.OfType<ToolCallDeltaEvent>().Select(e => e.Id))
            .Distinct().ToList();
        await Assert.That(ids.Count).IsEqualTo(1);
        await Assert.That(ids[0]).IsEqualTo("tc2");
    }

    [Test]
    public async Task Emit_EmptyNameOrArgs_ContributesNoEvent()
    {
        var state = new CompatChunkState();
        var events = new List<LlmEvent>();

        CompatEmitter.Emit(events, state, 0, "c1", null, null);

        await Assert.That(events.Count).IsEqualTo(0);
    }
}
