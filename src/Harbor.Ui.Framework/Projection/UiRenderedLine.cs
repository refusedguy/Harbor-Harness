using System.Collections.Immutable;
using Harbor.Ui.Framework.State;

namespace Harbor.Ui.Framework.Projection;

public sealed record UiRenderedLine(
    string Id,
    IReadOnlyList<StyledSpan> Spans,
    UiLineKind Kind,
    DateTime TimestampUtc);