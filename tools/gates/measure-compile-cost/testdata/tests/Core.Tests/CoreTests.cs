namespace Fixture.Core.Tests;

// The fixture's second consumer of Core: a project that references the leaf
// and nothing else, so its radius is itself.
public sealed class CoreTests
{
    [Test]
    public void TheFixtureHasALeaf()
    {
        await Assert.That(new CoreValue(1, "one")).IsNotNull();
    }
}
