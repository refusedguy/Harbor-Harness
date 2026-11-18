using Harbor.Abstractions.Dsl;

namespace Harbor.Abstractions.Tests;

/// <summary>
///     #2432: the tree dump used by IR snapshots is a pure function of the
///     contracts assembly. This project references no renderer and starts no
///     CellForge instance, so the assertion proves the dump is available
///     without one.
/// </summary>
public class DslDebugTreeTests
{
    [Test]
    public async Task DebugTree_ContractsOnly_IsDeterministicAndNeedsNoRenderer()
    {
        var arena = new SpecArena();
        var root = arena.CreateNode(SpecKind.Panel, 1);
        root.AddOperation(new SpecOp(
            "new",
            [new SpecValue(SpecValueKind.String, "main")],
            1,
            1));
        arena.AddRoot(root);

        const string expected = "node Panel@1:1 claimed=false\n  op new(\"main\")@1:1\n";

        await Assert.That(arena.DebugTree()).IsEqualTo(expected);
        await Assert.That(arena.DebugTree()).IsEqualTo(expected);
    }
}
