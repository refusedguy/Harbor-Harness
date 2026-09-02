# OSC Protocol Compliance Matrix

Harbor's terminal-protocol stack (osc-expansion sprint). Reference: the sprint in
`.kilo-docs/sprints/osc-expansion/prompt.md`; encoders live in
`src/Harbor.Tui.CellForge/Rendering/`, detection in `src/Harbor.Tui.CellForge/Capabilities/`,
wiring in `apps/Harbor.App.Cli/Repl/CellForgeReplRunner.cs`.

## Sprint protocols

| Protocol | Direction | Harbor component | Emission format | Capability decision |
|---|---|---|---|---|
| OSC 1337 (iTerm2 inline images) | out | `Osc1337Image` | `ESC ] 1337 ; File=name=N;size=N;inline=1;preserveAspectRatio=1:<base64> BEL` | `InlineImageProbe.Detect` (env; `HARBOR_INLINE_IMAGE` override) |
| kitty graphics (APC) | out | `Graphics.KittyPngInline` | `ESC _ G f=100,a=T,m={0,1};<base64> ESC \` (chunked, 4096 chars) | kitty family env (KITTY_WINDOW_ID / KITTY_PID / TERM) |
| OSC 99 (kitty notifications) | out+probe | `Osc99Notify` + `TerminalQueries.Osc99NotifyProbe` | notify: `ESC ] 99 ; ; <title>\n<body> ESC \`; probe: `ESC ] 99 ; i=harbor : p=? BEL` | **Wire probe** — parser intercepts the answer (`Osc99NotifyReport`) |
| OSC 777 (urxvt notify) | out | `Osc777Notify` | `ESC ] 777 ; notify ; <title> ; <body> BEL` | env only (no probe exists in this family); `HARBOR_OSC777` override |
| Bracketed paste 2004 (anti-injection) | in+out | `PasteSanitizer`, parser §paste | enable/disable `CSI ?2004 h/l`; payload between `CSI 200~ / 201~` | DECRQM 2004 probe (`TerminalCapabilities.BracketedPasteConfirmed`) |

### Sprint-title errata (facts, so nobody "fixes" these back)

- The sprint calls OSC 1337 the "kitty graphics protocol". **kitty does not speak
  OSC 1337** — its graphics protocol is APC (`ESC _ G`). Harbor therefore routes
  kitty → APC (PNG native) and OSC 1337 → iTerm2/WezTerm/Konsole/mintty. kitty+JPEG
  falls back to the text card by design (APC `f=100` is PNG-only).
- The sprint calls OSC 777 "kitty notifications". **kitty's notification protocol is
  OSC 99** and — unlike 777 — it has a real capability probe. Harbor probes 99 and
  uses OSC 777 as the urxvt-family fallback, so the sprint's "suppressed if the
  terminal doesn't support" is enforced on the wire where possible.

## Per-terminal compliance (kitty / iTerm2 / Windows Terminal / xterm)

| Feature | kitty | iTerm2 | Windows Terminal | xterm (patch ≥ 388) |
|---|---|---|---|---|
| OSC 11 auto-theme (query/report) | ✅ | ✅ | ✅ | ✅ |
| OSC 52 copy-on-select | ✅ (tmux-wrapped) | ✅ | ✅ (partial clipboard) | ✅ (allowWindowOps) |
| Inline images — kitty APC | ✅ PNG (JPEG → text card) | ❌ ignored | ❌ ignored | ❌ ignored |
| Inline images — OSC 1337 | ❌ ignored | ✅ PNG+JPEG | ❌ ignored | ❌ ignored |
| Notifications — OSC 99 probe+notify | ✅ answers probe | ❌ silent | ❌ silent | ❌ silent |
| Notifications — OSC 777 | ❌ ignored (harmless) | ❌ | ❌ | ❌ |
| Notifications — OSC 777 (urxvt family) | n/a | n/a | n/a | n/a (✅ urxvt itself) |
| Bracketed paste + sanitize | ✅ | ✅ | ✅ | ✅ |
| DECRQM 2004 probe | ✅ | ✅ | ✅ | ✅ |

Harmless-ignorance notes: unsupported outbound OSC sequences are ignored silently by
every terminal in the matrix (xterm's `allowWindowOps` governs OSC 52 only), so a
miss-detected capability never corrupts the display — it only loses the feature.

## Detection rules

| Probe / env | Result |
|---|---|
| `HARBOR_INLINE_IMAGE=off\|osc1337\|kitty` | explicit inline-image override (wins over everything) |
| `TMUX` / `STY` set | inline images **off** (passthrough out of scope, mirrors kitty-keyboard guardrail) |
| `KITTY_WINDOW_ID`, `KITTY_PID`, `TERM` contains `kitty` | kitty APC |
| `ITERM_SESSION_ID`, `TERM_PROGRAM=iTerm.app`, `WEZTERM_EXECUTABLE`, `TERM_PROGRAM=WezTerm`, `KONSOLE_VERSION`, `TERM_PROGRAM=mintty` | OSC 1337 |
| OSC 99 probe answered (`p=<payload types>`) | kitty notifications (Osc99) |
| `HARBOR_OSC777=1\|true` / `0\|false` | force / suppress 777 family |
| `TERM` contains `rxvt`/`urxvt` | OSC 777 family |
| DECRQM `CSI ?2004$p` answer value 1/2 | bracketed paste confirmed |

## Paste sanitization guarantees (`PasteSanitizer`)

- Clean payload → **original string reference returned, zero allocation**
  (scan is `SearchValues<char>` + `ReadOnlySpan<char>`, stack-only).
- Dirty payload → exactly one allocation (two-pass `string.Create`: count, then fill).
- Stripped: CSI (incl. 200~/201~ markers inside the payload), OSC (BEL/ST),
  DCS/SOS/PM/APC, Fe/Fp/nF escapes, lone/trailing ESC, C0 except `\n`/`\t`,
  DEL, full C1 (incl. 8-bit CSI 0x9B). `\r\n`→`\n`, lone `\r`→`\n`.
- Preserved: `\n`, `\t`, all printable text incl. full Unicode/surrogates.
- Injection contract: newlines inside a paste can never synthesize Enter
  (parser-level, `PasteEvent`); the sanitized buffer is what the composer shows
  and what submit routes to the agent — the preview is the actual content.
- No new permission surface: paste is trusted input after sanitization.

## Test matrix (where the compliance is proven)

| Area | Suite / file |
|---|---|
| OSC 1337 envelope + sanitization + caps | `tests/Harbor.Tui.CellForge.Tests/Osc1337ImageTests.cs` |
| Inline-image detection matrix | `tests/Harbor.Tui.CellForge.Tests/InlineImageProbeTests.cs` |
| kitty APC + Sixel encoders | `tests/Harbor.Tui.CellForge.Tests/GraphicsTests.cs` |
| OSC 777/99 encoders + detection + parser probe interception | `tests/Harbor.Tui.CellForge.Tests/Osc777NotifyTests.cs` |
| OSC 11 report interception (regression guard) | `tests/Harbor.Tui.CellForge.Tests/Osc11ReportTests.cs` |
| OSC 52 clipboard (regression guard) | `tests/Harbor.Tui.CellForge.Tests/Osc52ClipboardTests.cs` |
| Bracketed-paste golden bytes (regression guard) | `tests/Harbor.Tui.CellForge.Tests/BracketedPasteTests.cs` |
| Paste sanitizer golden vectors | `tests/Harbor.Tui.CellForge.Tests/PasteSanitizerTests.cs` |
| Inline-image bridge hand-off | `tests/Harbor.Tui.CellForge.Tests/ChatScreenBridgeTests.cs` |
| PTY: paste injection «/danger», multiline verbatim, chunked paste | `tests/Harbor.Tui.CellForge.PtyTests/PasteInjectionScenarioTests.cs`, `PasteEdgeScenarioTests.cs` |
| Capability probe ladder (DECRQM/kitty) | `tests/Harbor.Tui.CellForge.Tests/CapabilityProbeTests.cs` |
