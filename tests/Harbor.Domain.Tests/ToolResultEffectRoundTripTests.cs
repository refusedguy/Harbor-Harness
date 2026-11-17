using Harbor.Abstractions.Models;
using MemoryPack;
using TUnit.Assertions;

namespace Harbor.Domain.Tests;

/// <summary>
///     #1100: the typed effect is part of ToolResult's binary payload, the
///     same way <see cref="ToolErrorInfo"/> is. A declared file change must
///     survive a MemoryPack round-trip; otherwise a result replayed from
///     storage would silently stop appearing in the diff preview.
/// </summary>
public class ToolResultEffectRoundTripTests
{
    [Test]
    public async Task MemoryPackRoundTrip_PreservesFileChangeEffect()
    {
        var original = new ToolResult("edited file.cs", false) { Effect = ToolResultEffect.FileChange };

        byte[] bytes = MemoryPackSerializer.Serialize(original);
        ToolResult? back = MemoryPackSerializer.Deserialize<ToolResult>(bytes);

        await Assert.That(back).IsNotNull();
        await Assert.That(back!.Effect).IsEqualTo(ToolResultEffect.FileChange);
        await Assert.That(back.Output).IsEqualTo("edited file.cs");
    }
}
