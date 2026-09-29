using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Harbor.Ui.Framework.State;

namespace Harbor.Ui.Framework.ViewModels;

/// <summary>
///     Projects <see cref="ChatDomainState.ToolCalls" /> onto a view-model's
///     <see cref="ToolCallViewModel" /> collection — the single reconciler both the
///     desktop chat view-model and the Avalonia render engine use (#680).
/// </summary>
/// <remarks>
///     <para>
///         There were two copies of this reconciler, one per rendering path, and
///         they disagreed: each parsed the rendered transcript line back into a tool
///         call and each carried its own glyph table. Both are gone. This one READS
///         the structure the reducer published.
///     </para>
///     <para>
///         Cards are reused by id, so a card that the user expanded stays expanded
///         and a reconcile during streaming mutates the tail instead of rebuilding
///         every row. The collection is updated in place when the count matches and
///         rebuilt only when it does not, which is what keeps the re-render cheap.
///     </para>
/// </remarks>
public static class ToolCallProjection
{
    /// <summary>
    ///     Bring <paramref name="target" /> in line with the state's tool calls.
    /// </summary>
    /// <param name="state">The state snapshot to project.</param>
    /// <param name="target">The observable card collection, updated in place.</param>
    public static void Reconcile(UiState state, ObservableCollection<ToolCallViewModel> target)
    {
        ImmutableArray<ToolCallSnapshot> calls = state.Chat.ToolCalls;

        var existingById = new Dictionary<string, ToolCallViewModel>(StringComparer.Ordinal);
        for (int i = 0; i < target.Count; i++)
        {
            existingById[target[i].Id] = target[i];
        }

        var ordered = new List<ToolCallViewModel>(calls.Length);
        for (int i = 0; i < calls.Length; i++)
        {
            ToolCallSnapshot snapshot = calls[i];

            if (!existingById.TryGetValue(snapshot.Id, out ToolCallViewModel? card))
            {
                card = new ToolCallViewModel { Id = snapshot.Id };
            }

            card.ToolName = snapshot.ToolName;
            card.IconText = snapshot.Glyph;
            card.ArgsPreview = snapshot.ArgsPreview;

            // Guarded, not unconditional: the store dispatches once per streamed
            // token, and Complete raises three dependent notifications. Calling it
            // on every reconcile would storm the binding layer for a card whose
            // status has not moved.
            if (card.Status != snapshot.Status || card.ResultPreview != snapshot.ResultPreview)
            {
                card.Complete(snapshot.Status, snapshot.ResultPreview, TimeSpan.Zero);
            }

            // Assigned unconditionally, not under `if (IsDiffTool)`. The snapshot is the
            // whole truth about this call, and a card reused across the two-phase
            // lifecycle (named → executing) must not keep a value the state no
            // longer holds — a stale diff preview is worse than none, because the
            // card would show one file's diff while claiming another.
            card.IsDiffTool = snapshot.IsDiffTool;
            card.DiffFilePath = snapshot.DiffFilePath;
            card.DiffPreview = snapshot.DiffPreview;
            card.DiffFull = snapshot.DiffFull;

            ordered.Add(card);
        }

        if (target.Count != ordered.Count)
        {
            target.Clear();
            foreach (ToolCallViewModel card in ordered)
            {
                target.Add(card);
            }

            return;
        }

        for (int i = 0; i < target.Count; i++)
        {
            if (!ReferenceEquals(target[i], ordered[i]))
            {
                target[i] = ordered[i];
            }
        }
    }
}
