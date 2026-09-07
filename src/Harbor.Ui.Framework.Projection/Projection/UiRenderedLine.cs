namespace Harbor.Ui.Framework.Projection;

public sealed record UiRenderedLine(
    string Id,
    IReadOnlyList<StyledSpan> Spans,
    UiLineKind Kind,
    DateTime TimestampUtc);
