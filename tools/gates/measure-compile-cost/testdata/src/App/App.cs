namespace Fixture.App;

// The fixture's top: it references Mid, so the cost of a change in Core
// reaches it through two hops.
public sealed record AppValue(int Number, string Text);
