using System.Collections.Immutable;

namespace Harbor.Ui.Framework.State;

/// <summary>
///     One PR review comment, as an already-loaded input contract (#1488).
/// </summary>
/// <remarks>
///     This is data only: no network, no provider, no fetch. A host that already
///     has review comments (from a session snapshot or a file context) dispatches
///     them with <see cref="AppMsg.SetPrComments" />; the cards panel in CellForge
///     draws exactly what arrived here. Keeping the contract immutable and
///     renderer-agnostic is what lets the panel stay a pure reader of
///     <see cref="TerminalUiState.PrComments" />, the same shape the file-tree
///     uses for its listing (#667).
/// </remarks>
/// <param name="Author">Reviewer display name.</param>
/// <param name="FilePath">Path of the file the comment is anchored to.</param>
/// <param name="Line">1-based line the comment is anchored to.</param>
/// <param name="Body">Comment body (plain text or markdown).</param>
/// <param name="CodeContext">Source lines around <paramref name="Line" />.</param>
/// <param name="Language">Optional language id used by the existing tokenizer.</param>
public sealed record PrCommentCard(
    string Author,
    string FilePath,
    int Line,
    string Body,
    ImmutableArray<string> CodeContext,
    string? Language = null);
