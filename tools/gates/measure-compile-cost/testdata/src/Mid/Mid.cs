namespace Fixture.Mid;

// The fixture's middle: it references Core, so the cost of a change in Core
// reaches this project.
public sealed record MidValue(int Number, string Text);
