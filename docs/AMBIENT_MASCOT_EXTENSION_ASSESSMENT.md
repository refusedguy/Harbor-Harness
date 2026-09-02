# Harbor AmbientMascot Extension Assessment

**Date:** 2025-08-28  
**Scope:** Can `AmbientMascot` be extended into a more complex animated mascot system?  
**Repo:** `/mnt/projects/Harbor-Harness`  

---

## 1. Current Implementation Analysis

### 1.1 Architecture

`AmbientMascot` is a **static pure-function renderer** (`AmbientMascot.cs:25-56`).  
It has four mood-based frame banks (`string[]`), each containing single-width ASCII frames:

| Mood | Frames | Example | Width |
|------|--------|---------|-------|
| `Idle` | 4 | `( ^..^ )` / `( -..- )` | 8 |
| `Working` | 4 | `( ^..^)/` / `( >..^ )` | 8 |
| `Awaiting` | 2 | `( O..O )` | 8 |
| `Sleeping` | 4 | `( -.-  )` / `( -.- z)` | 8 |

Frame selection is a **deterministic function of `(monotonicTick, mood)`** — no timers, no allocations, no mutable state.

### 1.2 Wiring Points

**StatusPanel** (`ChatScreenLayout.cs:78-205`) is the sole consumer:
- `MascotEnabled` is read once from `HARBOR_MASCOT` env var (`ChatScreenLayout.cs:92-93`).
- On each `Paint()`, tick increments, mood is derived from `Vm.Mode` + idle timeout (`ChatScreenLayout.cs:112-137`).
- Frame is rendered via **single `SetText` call** at the trailing edge of the 1-row status bar (`ChatScreenLayout.cs:168-171`):
  ```csharp
  buffer.SetText(Rect.Right - mascot.Length, Rect.Y, mascot, ChatPalette.Dim);
  ```
- Mascot only appears when `Rect.Width >= MascotMinWidth (100)` (`ChatScreenLayout.cs:83`).

### 1.3 Hard Constraints (Explicit in Code)

| Constraint | Evidence | Location |
|------------|----------|----------|
| **Zero allocations** | `"zero allocations, deterministic like SpinnerStrip"` | `AmbientMascot.cs:22-23` |
| **Deterministic** | `Frame()` is a pure function; tests assert periodicity | `AmbientMascotTests.cs:8-16` |
| **Single-width ASCII** | All chars `< 128`; `Width()` returns `frame.Length` | `AmbientMascotTests.cs:56-68`, `AmbientMascot.cs:51-52` |
| **Constant frame width** | Every mood's frames share identical width | `AmbientMascotTests.cs:21-39` |
| **No per-frame allocations** | `Frame()` returns `string` from static arrays | `AmbientMascot.cs:43-49` |
| **1-row footprint** | Rendered in `StatusPanel` which is `minHeight: 1` | `ChatScreenLayout.cs:271` |
| **Disabled by env var** | `HARBOR_MASCOT=off` short-circuits rendering | `ChatScreenLayout.cs:92-93` |

---

## 2. Existing Animation Infrastructure (Reusable)

The codebase already has **mature, zero-alloc animation primitives** that a mascot system can leverage without new allocations.

### 2.1 PanelFx (`PanelFx.cs`)

| Primitive | Signature | Purpose |
|-----------|-----------|---------|
| `Progress(start, now, duration)` | `double` | Eased [0..1] progress (ease-out cubic) |
| `EaseOut(t)` / `EaseIn(t)` | `double` | Cubic easing curves |
| `BlendRegion(buffer, rect, alpha)` | `void` | Alpha-blends an entire region toward panel surface |
| `AccentRamp(flipTick, nowTick)` | `double` | Status-bar mode crossfade ramp |
| `Lerp(from, to, t)` | `PackedColor` | Linear RGB channel interpolation |
| `WithAlpha(style, alpha)` | `CellStyle` | Returns alpha-blended style (passes through at α≥1) |

**Key invariant:** All `PanelFx` helpers are pure functions of `(tick, startTick)` — zero allocations, no timers.

### 2.2 Tick-Driven Rendering

- `ChatTimelinePanel` increments `Timeline.CurrentTick` each paint (`VirtualizedChatTimeline.cs:370-371`).
- `StatusPanel` increments its own `Tick` each paint (`ChatScreenLayout.cs:114`).
- `VirtualizedChatTimeline` uses `CurrentTick` for **entrance fades/slides** and **smooth scroll easing** (`VirtualizedChatTimeline.cs:314-353`).

### 2.3 Diff Optimization

`DiffEngine` supports `FrameHint(in Rect damage)` (`DiffEngine.cs:38`) — a **bounded partial-scan** optimization. If a mascot only changes a small region, the hint skips silent rows (O(1) per silent row vs O(cols) full scan). **Currently unused in production** (only in tests), but the API is ready.

### 2.4 ScreenBuffer Capabilities

- `SetText(x, y, ReadOnlySpan<char>, style)` — writes a text run (`ScreenBuffer.cs:219-244`).
- `SetRune(x, y, Rune, style)` — writes one rune with wide-char handling (`ScreenBuffer.cs:175-216`).
- `At(x, y)` — returns `ref Cell` for direct mutation (`ScreenBuffer.cs:47`).
- `Fill(rect, cell)` — fills a rectangle (`ScreenBuffer.cs:131-167`).

This means a mascot can paint **arbitrary 2D cell grids**, not just single text rows.

---

## 3. Technical Feasibility Assessment

### 3.1 What Is Already Possible Within Constraints

| Extension | Feasibility | How |
|-----------|-------------|-----|
| **More frames per mood** | ✅ Trivial | Add entries to `string[]`; `IndexOf()` handles any length |
| **Variable-speed animations** | ✅ Trivial | Divide tick by period before indexing, like `Sleeping` does (`AmbientMascot.cs:47`) |
| **Color cycling / mood-dependent palette** | ✅ Easy | Use existing `PanelFx.Lerp` + `PackedColor` in the paint site (`StatusPanel`) |
| **Smooth fade between moods** | ✅ Easy | `PanelFx.AccentRamp` already crossfades the status row on mode flip (`ChatScreenLayout.cs:176-185`) |
| **Entrance animation** | ✅ Easy | `PanelFx.BlendRegion` or slide offset, driven by `CurrentTick` |
| **Multi-row mascot (e.g. 2-3 rows)** | ✅ Feasible | Add a dedicated `MascotPanel` leaf to `LayoutTree`; paint a `Rect` instead of a string |

### 3.2 What Breaks the Zero-Alloc Contract

| Anti-pattern | Why It Breaks | Mitigation |
|--------------|---------------|------------|
| `string.Concat` / interpolation per frame | Allocates new string every tick | Pre-compute all frames as `static readonly string[]` or `char[][]` |
| `StringBuilder` in `Paint()` | Heap allocation | Avoid; use `ScreenBuffer.SetRune` per cell |
| `List<T>` / array init in hot path | Gen-0 pressure | Pre-allocate; reuse spans |
| `DateTime.Now` / `Environment.TickCount64` in frame selector | Non-deterministic, but **not an allocation** | Acceptable for mood derivation (already used in `MascotFor`) |
| Timer / `async` loop for animation | Violates tick-driven contract | Keep animation purely as `f(tick, mood) → frame` |
| New `string` per cell from `Rune` conversion | Allocates | Use `SetRune` with pre-decoded `Rune` structs, or `SetText` with static spans |

### 3.3 What Breaks the Single-Width / Deterministic Contract

| Anti-pattern | Why It Breaks | Mitigation |
|--------------|---------------|------------|
| Wide runes (CJK, emoji) | Vary in display width; `Width()` is no longer `Length` | Restrict to ASCII or pre-measure and cache widths |
| Random / non-deterministic frame selection | Tests fail; CI flakiness | Pure tick modulus; seed any randomness at startup |
| Mutable static state in `Frame()` | Breaks referential transparency | Keep `Frame()` side-effect-free |

---

## 4. Concrete Extension Paths

### Path A: In-Place Frame Enrichment (Low Risk)

Keep the mascot in `StatusPanel`, but enrich what a "frame" means.

**Approach:**
1. Change `AmbientMascot.Frame()` to return a **struct** or overload that includes style metadata:
   ```csharp
   public readonly record struct MascotFrame(string Text, CellStyle Style, Rect? DamageHint);
   ```
2. `StatusPanel` uses `DamageHint` to call `Engine.FrameHint(...)` for partial diffs.
3. Add a `MascotMood.Transition` state with cross-faded frames computed by `PanelFx.Lerp`.

**Zero-alloc guarantee:** Struct is stack-allocated; `string` and `CellStyle` are value/ref types with no new heap traffic.

**Code references:**
- `StatusPanel.Paint()` at `ChatScreenLayout.cs:159-171` is the render site.
- `PanelFx.BlendRegion` at `PanelFx.cs:138-148` already alpha-blends regions.

### Path B: Dedicated Multi-Row Mascot Panel (Medium Risk)

Add a `MascotPanel : Panel` to `LayoutTree`, replacing or augmenting the trailing-edge text.

**Approach:**
1. Define a new panel with `minHeight: 2` or `3`, placed as a sibling of `StatusPanel` or overlaid via a new split.
2. Store frames as `char[][]` or pre-rendered `Cell[,]` grids indexed by tick.
3. In `MascotPanel.Paint(buffer)`, iterate the grid and call `buffer.At(x, y) = cell` or `buffer.SetRune(...)`.
4. Use `PanelFx.Progress` for **entrance slide-up** and **blink/squint alpha modulation**.

**Example frame storage (zero-alloc):**
```csharp
// 3 rows × 9 cols, precomputed at startup
private static readonly Cell[,] WorkingFrames = ...;
private static readonly int FrameStride = 9;
```

**Layout integration** (`ChatScreenLayout.cs:253-287`):
```csharp
var mascotPanel = new MascotPanel("chat.mascot", minWidth: 9, minHeight: 3, priority: 5);
tree.Split(StatusId, SplitDir.Horizontal, 0.9f, mascotPanel, gap: 1);
```

**Risk:** `MascotMinWidth` (100) was chosen so the mascot never competes with status segments. A multi-row panel needs a new minimum-width policy or it will collapse the sidebar/timeline.

### Path C: Block-Level Mascot (In-Timeline) (Higher Risk, Higher Reward)

Render mascot frames as an `IChatBlock` inside `VirtualizedChatTimeline`.

**Approach:**
1. Create `MascotBlock : IChatBlock` that measures height based on desired rows.
2. In `Paint(ctx)`, draw the frame grid into `ctx.Buffer` using the same cell-grid technique as Path B.
3. Insert/remove the block via `Timeline.Append/Replace` based on agent state.
4. Benefit: mascot can **scroll with the timeline**, appear between messages, or sit pinned at top/bottom via anchor.

**Code references:**
- `IChatBlock` at `ChatBlock.cs:49-75`.
- `VirtualizedChatTimeline.Append/Replace` at `VirtualizedChatTimeline.cs:73-134`.
- `BlockPaintContext` at `ChatBlock.cs:25-41`.

**Risk:** Mascot becomes part of scroll history — may feel less "ambient". Also, `BudgetBytes` eviction could garbage-collect the mascot unexpectedly.

### Path D: Sub-Cell Morphing (Novel, Low Risk)

Keep single-row ASCII, but animate **internal state** (eyes, tail) by compositing multiple layers.

**Approach:**
1. Split each frame into layers: base cat, eyes-state, tail-state, accessory.
2. On each tick, composite layers into a `Span<char>` buffer and call `SetText` once.
3. Use `PanelFx.Progress` to interpolate eye openness (e.g., `^` → `-` → `·`) by pre-computing intermediate glyphs.

**Zero-alloc:** Composite into a stack-allocated `Span<char>` (max 8 chars) and pass as `ReadOnlySpan<char>` to `SetText`.

**Code reference:** `ScreenBuffer.SetText` already accepts `ReadOnlySpan<char>` (`ScreenBuffer.cs:219`).

---

## 5. Recommended Architecture

For a **minimal-risk, maximum-feature** extension, combine **Path A + Path D**:

```
┌─────────────────────────────────────────────────────────────┐
│  AmbientMascot (static, pure)                                │
│    Frame(tick, mood) → MascotFrame                           │
│      .Text   = ReadOnlySpan<char> (composited)               │
│      .Style  = CellStyle (mood-dependent palette)            │
│      .Damage = Rect? (for DiffEngine hints)                  │
│                                                             │
│  Compositor: ticks through sub-layers (eyes, tail, breath)  │
│    All layer arrays are static readonly char[][]             │
│    Composition writes into stackalloc Span<char>[8]          │
└─────────────────────────────────────────────────────────────┘
            ↓ consumed by
┌─────────────────────────────────────────────────────────────┐
│  StatusPanel (unchanged contract)                            │
│    - Calls AmbientMascot.Frame()                             │
│    - Uses Frame.Damage to call Engine.FrameHint()            │
│    - Uses Frame.Style for ChatPalette lookup                 │
│    - Alpha-blends on mood transition via PanelFx             │
└─────────────────────────────────────────────────────────────┘
```

**Why this wins:**
- **Zero new panels / layout risk** — mascot stays in the status footer.
- **Zero allocations** — `Span<char>` composition and struct return.
- **Deterministic** — still `f(tick, mood)`.
- **Diff-friendly** — `FrameHint` narrows the scan to the mascot cell range.
- **Testable** — `AmbientMascotTests` can assert on `MascotFrame.Text` and `.Style` independently.

---

## 6. Code References Summary

| File | Lines | Relevance |
|------|-------|-----------|
| `AmbientMascot.cs` | 1-56 | Current implementation; frame banks, `Frame()`, `Width()` |
| `ChatScreenLayout.cs` | 78-205 | `StatusPanel` — sole render site, `MascotFor()`, env-var gate |
| `ChatScreenLayout.cs` | 253-287 | `ChatScreen.Build()` — layout composition; where a new panel would split |
| `PanelFx.cs` | 16-168 | All animation primitives: `Progress`, `EaseOut`, `BlendRegion`, `Lerp` |
| `VirtualizedChatTimeline.cs` | 314-353 | Entrance fade/slide pattern using `PanelFx` + `CurrentTick` |
| `ChatBlock.cs` | 25-75 | `BlockPaintContext`, `IChatBlock` — for Path C |
| `DiffEngine.cs` | 37-38, 60-81 | `FrameHint` + `Flush` — partial-scan optimization |
| `ScreenBuffer.cs` | 175-244 | `SetRune`, `SetText`, `At` — cell-level paint API |
| `LayoutTree.cs` | 117-128 | `Split()` — how to insert a new panel leaf |
| `AmbientMascotTests.cs` | 1-69 | Determinism, width, ASCII constraints |
| `StatusPanelMascotTests.cs` | 1-69 | Wiring tests; wide/narrow row behavior |

---

## 7. Verdict

**Yes, Harbor's AmbientMascot can be extended into a more complex animated mascot system** without breaking the zero-alloc / deterministic / single-width contract. The codebase already contains the necessary building blocks (`PanelFx`, `DiffEngine.FrameHint`, `ScreenBuffer` cell API, tick-driven pipeline). The lowest-risk path is to keep the mascot in the status footer (Path A + D), enrich the frame return type, and add sub-layer compositing — all within the existing 1-row, 8-char ASCII footprint. A multi-row panel (Path B) or timeline block (Path C) are feasible but introduce layout complexity and new minimum-width constraints.
