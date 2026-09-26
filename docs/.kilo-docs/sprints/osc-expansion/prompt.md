# SPRINT: OSC Expansion — Terminal Protocol Fluency

## Context
Harbor already speaks OSC 11 (auto-theme) and OSC 52 (copy-on-select). This sprint expands the protocol stack to make Harbor the most terminal-fluent TUI in existence. Competitors don't even have OSC 11.

## Tasks
1. OSC 1337 kitty graphics protocol
   - Render inline PNG/JPEG in timeline for screenshots/thumbnails
   - Fallback to text description on non-kitty terminals
   - Acceptance: screenshot attachment renders inline in kitty; falls back gracefully elsewhere

2. OSC 777 kitty notifications
   - Emit desktop notifications for long-running agent tasks
   - Respect terminal's notification capabilities via OSC 777 probe
   - Acceptance: notification sent when agent starts >30s task; suppressed if terminal doesn't support

3. Bracketed paste anti-injection
   - Detect bracketed paste mode; sanitize pasted content before routing to agent
   - Strip ANSI escapes and control chars from paste buffer
   - Acceptance: pasting multi-line shell script does not execute; sanitized preview shown

## Hard Rules
- Do NOT break existing OSC 11/52 flows.
- Do NOT allocate in paste-sanitize hot path; use stack-only spans.
- Do NOT add new permissions for paste; treat paste as trusted input after sanitization.

## Deliverables
- 3 atomic commits with focused diffs
- Protocol compliance test matrix (kitty, iTerm2, Windows Terminal, xterm)
- Test summary per commit
