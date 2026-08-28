# SPRINT: Mascot as Brand — State-Reactive ASCII Character

## Context
Harbor's ambient mascot is functional but lacks depth vs Claude Code's /buddy. This sprint makes the mascot memorable and state-reactive without breaking zero-alloc/CI constraints. Goal: a character, not a Tamagotchi clone.

## Tasks
1. State-reactive frame selection
   - Map agent states to distinct moods: idle, thinking, tool-call, approval-pending, error, success
   - Smooth crossfade between moods via PanelFx.AccentRamp
   - Acceptance: mascot changes frame within 1 tick of state change; no frame drop under load

2. Multi-row panel mode
   - Optional 2-3 row mascot panel via LayoutTree.Split()
   - Toggle with HARBOR_MASCOT_MODE=footer|panel|off
   - Acceptance: panel mode shows 3-row animated cat; footer mode stays 1-row; both zero-alloc

3. Event-driven reactions
   - React to specific events: error blink, success bounce, approval wiggle
   - Per-event frame overlay via PanelFx.BlendRegion
   - Acceptance: error event triggers 3-frame blink sequence; success triggers bounce; no allocations during animation

## Hard Rules
- Do NOT add RPG stats, speech bubbles, or Tamagotchi mechanics.
- Do NOT break existing HARBOR_MASCOT=off opt-out used by CI/golden tests.
- Do NOT allocate in Frame() or render path; use stack-only spans and static arrays.

## Deliverables
- 3 atomic commits with focused diffs
- Updated ASCII frames for 6 moods + 3 events
- Test summary per commit (including golden test with mascot off)
