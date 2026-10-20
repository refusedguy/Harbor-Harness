using System.Text;
using Ensure = Harbor.Abstractions.Extensions.Ensure;
using Harbor.Tui.CellForge.Input;
using Harbor.Tui.CellForge.Rendering;
// #436: GlowRegion is engine-typed; its accent/rect convert exactly below.
using EngineCells = Harbor.Tui.CellForge.Rendering;
// #436: the wheel payload comes from the engine input vocabulary rather
// than its grid vocabulary. The shared alias below pins the other twin,
// so this file names the engine side through a dedicated alias.
using EngineInput = Harbor.Tui.CellForge.Input;
using Harbor.Ui.Framework.Rendering.Input;
using Harbor.Ui.Framework.State;
using Harbor.Abstractions.Models;

namespace Harbor.Tui.CellForge.Widgets;

public sealed partial class VirtualizedChatTimeline
{
    // ── Eviction caps, in-place replacement and entrance tracking ──────────
    // #95 slice (#1511): the resident-history budget (byte cap + max_lines
    // window), the two replacement paths that swap a block in place, the
    // damage mark they share, and the entrance-motion bookkeeping. Moved
    // out of VirtualizedChatTimeline.cs verbatim — same partial, same
    // public surface, zero logic change. The Paint window and the scroll
    // surface stay in the main file (next slice).

    /// <summary>Amortized eviction: triggered only when the running total
    /// crosses the budget, then evicts down to the 75 % low-water mark in one
    /// pass — streaming pays the pass once per ~¼ budget of new bytes instead
    /// of rescanning and evicting on every append (O(n²) during token storms).</summary>
    private void EvictOverBudget()
    {
        long budget = BudgetBytes;
        if (budget <= 0 || _budgetUsed <= budget)
        {
            return;
        }

        long lowWater = budget - (budget >> 2);
        while (_cache.Count > 1 && _budgetUsed > lowWater)
        {
            EvictOldest();
        }
    }

    /// <summary>Sliding-window cap (#1190, textual max_lines + _start_line shift):
    /// drops oldest blocks while over <see cref="MaxLines"/>, newest always
    /// survives (a cap below 1 still admits the live tail). Runs after
    /// <see cref="EvictOverBudget"/> so both caps hold simultaneously.</summary>
    private void EvictOverMaxLines()
    {
        int cap = MaxLines < 1 ? 1 : MaxLines;
        while (_cache.Count > cap)
        {
            EvictOldest();
        }
    }

    /// <summary>Single shared evict-one step: drops the oldest block, keeps the
    /// O(1) budget ledger exact, and flags geometry/damage like every other
    /// structural change.</summary>
    private void EvictOldest()
    {
        var evicted = _cache.BlockAt(0);
        _budgetUsed -= Math.Min(_budgetUsed, Math.Max(0, evicted.BudgetBytes));
        _ = _cache.EvictFirst();
        _ = _entranceStarts.Remove(evicted);
        _dirtyGeometry = true;
        _broadDamage = true;
    }

    /// <summary>Swaps the live-stream placeholder for its committed form.</summary>
    public void ReplaceLast(IChatBlock block)
    {
        if (_cache.Count == 0)
        {
            Append(block);
            return;
        }

        var old = _cache.BlockAt(_cache.Count - 1);
        _budgetUsed += Math.Max(0, block.BudgetBytes) - Math.Max(0, old.BudgetBytes);
        _cache.Replace(_cache.Count - 1, block);
        _ = _entranceStarts.Remove(old);
        _dirtyGeometry = true;
        _broadDamage = true;
        MarkLastDirty();
    }

    /// <summary>
    /// Replaces a specific block instance in place (the stream slot may sit
    /// below newer tool cards). No-op when the block is gone.
    /// </summary>
    public void Replace(IChatBlock existing, IChatBlock replacement)
    {
        Ensure.NotNull(existing);

        for (int i = 0; i < _cache.Count; i++)
        {
            if (ReferenceEquals(_cache.BlockAt(i), existing))
            {
                _budgetUsed += Math.Max(0, replacement.BudgetBytes) - Math.Max(0, existing.BudgetBytes);
                _cache.Replace(i, replacement);
                _ = _entranceStarts.Remove(existing);
                _dirtyGeometry = true;
                _broadDamage = true;
                _cache.MarkHeightsDirty(i);
                return;
            }
        }
    }

    /// <summary>Unknown card mutated in place — treat the whole frame as
    /// viewport-wide (partial-scan contract). This is the hook mutating
    /// components use when the changed block is NOT the streaming tail:
    /// approval gates and tool cards settle below newer blocks, so their
    /// rects are not the tail's suffix and only a full scan is safe.
    ///
    /// The streaming path must not use this — it marked every stream frame
    /// viewport-wide and cost a full scan per frame (#465). Use
    /// <see cref="MarkDirty"/> with the block that actually changed.</summary>
    public void MarkLastDirty()
    {
        _cache.MarkHeightsDirty(Math.Max(0, _cache.Count - 1));
        MarkViewportWide();
    }

    // ── Entrance tracking (HDS v1 motion bookkeeping) ──────────────────────

    /// <summary>
    /// Registers an entrance start for eligible blocks appended after the
    /// first painted frame. Pre-first-frame appends (initial populate) and
    /// stream continuations render settled — no motion on cold screens.
    /// </summary>
    private void MarkEntrance(IChatBlock block)
    {
        if (!_entranceFx || !_hasPaintedFrame || block.IsStreamContinuation)
        {
            return;
        }

        _entranceStarts[block] = CurrentTick;
        if (block is ApprovalGateView gate && gate.IsPending)
        {
            gate.BeginWarnPulse(CurrentTick);
        }
    }
}
