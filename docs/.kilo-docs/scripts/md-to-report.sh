#!/usr/bin/env bash
set -euo pipefail
set +m

# md-to-report.sh — конвертит sprint findings + status в HTML для телефона/компьютера
# Usage: md-to-report.sh [sprint-name]  (default: all sprints)

REPO="${1:-.}"
cd "$REPO"

SPRINTS_DIR=".kilo-docs/sprints"
OUT_DIR=".kilo-docs/docs"
mkdir -p "$OUT_DIR"

REPORT="$OUT_DIR/sprint-report-$(date '+%Y%m%d-%H%M').html"

# ── HTML header ──────────────────────────────────────────────────────
cat > "$REPORT" <<'EOF'
<!DOCTYPE html>
<html lang="ru">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>Harbor Sprint Report</title>
<style>
  :root {
    --bg: #0a0e14; --panel: #0d1117; --border: #1f2430;
    --text: #b3b9c5; --dim: #5c6773; --accent: #39bae6;
    --ok: #7fd962; --warn: #ffb454; --err: #ff6b6b;
  }
  * { box-sizing: border-box; }
  body {
    margin: 0; background: var(--bg); color: var(--text);
    font-family: 'JetBrains Mono', 'Fira Code', ui-monospace, monospace;
    font-size: 13px; line-height: 1.6; padding: 16px;
  }
  .wrap { max-width: 900px; margin: 0 auto; }
  h1 { font-size: 20px; margin: 0 0 8px; color: var(--accent); }
  h2 { font-size: 16px; margin: 16px 0 8px; color: var(--accent); }
  h3 { font-size: 14px; margin: 12px 0 6px; color: var(--text); }
  .sub { color: var(--dim); font-size: 12px; margin-bottom: 16px; }
  .card {
    background: var(--panel); border: 1px solid var(--border);
    border-radius: 4px; padding: 12px; margin: 8px 0;
  }
  .title {
    color: var(--dim); font-size: 11px; text-transform: uppercase;
    letter-spacing: 0.08em; margin-bottom: 8px;
  }
  table { width: 100%; border-collapse: collapse; font-size: 12px; }
  th, td {
    text-align: left; padding: 6px 8px;
    border-bottom: 1px solid var(--border);
  }
  th { color: var(--dim); font-weight: normal; }
  .ok { color: var(--ok); }
  .err { color: var(--err); }
  .warn { color: var(--warn); }
  .accent { color: var(--accent); }
  code {
    background: #131820; border: 1px solid var(--border);
    border-radius: 3px; padding: 2px 6px; font-size: 11px;
    color: #d2a6ff;
  }
  ul { margin: 8px 0; padding-left: 20px; }
  li { margin: 4px 0; }
  .finding {
    background: rgba(255,107,107,0.05);
    border-left: 2px solid var(--err);
    padding: 8px; margin: 6px 0;
  }
  .finding.high { border-left-color: var(--err); }
  .finding.medium { border-left-color: var(--warn); }
  .finding.low { border-left-color: var(--dim); }
</style>
</head>
<body>
<div class="wrap">
  <div class="card">
    <h1>Harbor Sprint Report</h1>
    <div class="sub">Generated: $(date '+%d.%m.%Y %H:%M') · Branch: $(git branch --show-current) · HEAD: $(git rev-parse --short HEAD)</div>
  </div>
EOF

# ── Sprint status table ──────────────────────────────────────────────
cat >> "$REPORT" <<'EOF'
  <div class="card">
    <div class="title">Sprint Queue</div>
    <table>
      <tr><th>Sprint</th><th>State</th><th>Commits</th><th>Last Activity</th></tr>
EOF

for status_file in "$SPRINTS_DIR"/*/status.json; do
  [[ -f "$status_file" ]] || continue
  name=$(jq -r '.name' "$status_file")
  state=$(jq -r '.state' "$status_file")
  commits=$(jq -r '.commits' "$status_file")
  last=$(jq -r '.last_activity' "$status_file")
  
  state_class=""
  case "$state" in
    done) state_class="ok" ;;
    failed|error) state_class="err" ;;
    running) state_class="warn" ;;
    *) state_class="accent" ;;
  esac
  
  cat >> "$REPORT" <<EOF
      <tr><td><span class="accent">$name</span></td><td><span class="$state_class">$state</span></td><td>$commits</td><td>$last</td></tr>
EOF
done

cat >> "$REPORT" <<'EOF'
    </table>
  </div>
EOF

# ── Per-sprint details ───────────────────────────────────────────────
for sprint_dir in "$SPRINTS_DIR"/*; do
  [[ -d "$sprint_dir" ]] || continue
  sprint_name=$(basename "$sprint_dir")
  status_file="$sprint_dir/status.json"
  prompt_file="$sprint_dir/prompt.md"
  findings_file="$sprint_dir/findings.json"
  
  [[ -f "$status_file" ]] || continue
  
  state=$(jq -r '.state' "$status_file")
  commits=$(jq -r '.commits' "$status_file")
  started=$(jq -r '.started_at' "$status_file")
  finished=$(jq -r '.finished_at' "$status_file")
  
  cat >> "$REPORT" <<EOF
  <div class="card">
    <div class="title">Sprint: $sprint_name · $state · $commits commits</div>
    <h3>Status</h3>
    <ul>
      <li>State: <span class="accent">$state</span></li>
      <li>Started: $started</li>
      <li>Finished: $finished</li>
      <li>Commits: $commits</li>
    </ul>
EOF

  # prompt preview
  if [[ -f "$prompt_file" ]]; then
    prompt_preview=$(head -5 "$prompt_file" | sed 's/</\\</g; s/>/\\>/g' | tr '\n' ' ')
    cat >> "$REPORT" <<EOF
    <h3>Prompt</h3>
    <p><code>$prompt_preview...</code></p>
EOF
  fi

  # findings
  if [[ -f "$findings_file" ]]; then
    cat >> "$REPORT" <<EOF
    <h3>Findings</h3>
EOF
    jq -r '.findings[]? | "\(.severity) \(.file // ""): \(.message)"' "$findings_file" 2>/dev/null | while read -r line; do
      severity=$(echo "$line" | cut -d' ' -f1)
      rest=$(echo "$line" | cut -d' ' -f2-)
      case "$severity" in
        high|critical) sev_class="err" ;;
        medium) sev_class="warn" ;;
        *) sev_class="dim" ;;
      esac
      cat >> "$REPORT" <<EOF
    <div class="finding $sev_class">$rest</div>
EOF
    done
  fi

  cat >> "$REPORT" <<'EOF'
  </div>
EOF
done

# ── Footer ───────────────────────────────────────────────────────────
cat >> "$REPORT" <<'EOF'
</div>
</body>
</html>
EOF

log() { echo "[md-to-report] $(date '+%d.%m %H:%M:%S') $*"; }
log "generated: $REPORT"
echo "$REPORT"
