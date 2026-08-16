#!/usr/bin/env bash
# tools/ui-hygiene.sh — Pre-build XAML/UI hygiene checks for Harbor.App.Avalonia.
#
# Gates:
#   1. No bare class selectors in XAML (Style Selector must be ElementType.ClassName).
#   2. MainWindow.axaml contains required Avalonia xmlns declarations.
#   3. Markdown rendering subscribes to theme changes so syntax colours update on theme switch.
#
# Usage:
#   ./tools/ui-hygiene.sh
#   ./tools/ui-hygiene.sh --axaml-dir apps/Harbor.App.Avalonia
#
# Exit codes:
#   0  all checks passed
#   1  one or more checks failed
set -euo pipefail

# ── colours ──────────────────────────────────────────────────────────────────
RED='\033[0;31m'
YEL='\033[1;33m'
GRN='\033[0;32m'
NC='\033[0m'

# ── paths ────────────────────────────────────────────────────────────────────
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &> /dev/null && pwd)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." &> /dev/null && pwd)"

AXAML_DIR="${1:-$REPO_ROOT/apps/Harbor.App.Avalonia}"
MAIN_WINDOW="$AXAML_DIR/Views/MainWindow.axaml"

errors=0
warnings=0

info()  { printf "${GRN}[pass]${NC} %s\n" "$*"; }
warn()  { printf "${YEL}[warn]${NC} %s\n" "$*"; warnings=$((warnings+1)); }
fail()  { printf "${RED}[fail]${NC} %s\n" "$*"; errors=$((errors+1)); }

# ── 1. Bare class selector check ─────────────────────────────────────────────
# Avalonia style selectors MUST be scoped to a control type:
#   Good:  Selector="Border.Card"
#   Bad:   Selector=".Card"
# Mono random (bare) selectors are too broad and break theme layering.

printf "\n== Check 1: XAML StyleSelector prefix rule ==\n"
bare_hits=$(grep -rn 'Selector="\.[A-Za-z]' "$AXAML_DIR" --include="*.axaml" 2>/dev/null || true)
if [ -z "$bare_hits" ]; then
    info "No bare class selectors found in XAML."
else
    fail "Bare class selectors detected (must be ElementType.ClassName):"
    echo "$bare_hits" | while IFS= read -r line; do
        printf "       %s\n" "$line"
    done
fi

# ── 2. MainWindow.axaml required Avalonia namespaces ─────────────────────────
printf "\n== Check 2: MainWindow.axaml Avalonia namespaces ==\n"
if [ ! -f "$MAIN_WINDOW" ]; then
    fail "MainWindow.axaml not found at $MAIN_WINDOW"
else
    content=$(cat "$MAIN_WINDOW")
    ok=true

    if echo "$content" | grep -q 'xmlns="https://github.com/avaloniaui"'; then
        info "Core Avalonia xmlns present."
    else
        fail "Missing core Avalonia xmlns (https://github.com/avaloniaui)."
        ok=false
    fi

    if echo "$content" | grep -q 'xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"'; then
        info "XAML xmlns present."
    else
        fail "Missing XAML xmlns (http://schemas.microsoft.com/winfx/2006/xaml)."
        ok=false
    fi

    # Warn if the file still references MAUI-specific xmlns (migration artefact).
    if echo "$content" | grep -qi 'xmlns:.*maui'; then
        warn "MainWindow.axaml still contains MAUI xmlns references — this is an Avalonia app."
    fi
fi

# ── 3. Markdown syntax highlighting + theme subscription ─────────────────────
# MarkdownRenderer must re-render (or at least notify) when the theme changes,
# otherwise syntax colours stay stale after ThemeService.ApplyHds() swaps
# the palette dictionary.

printf "\n== Check 3: MarkdownRenderer theme-change subscription ==\n"
md_renderer="$AXAML_DIR/Views/Controls/MarkdownRenderer.axaml.cs"
md_block="$AXAML_DIR/Views/Controls/Markdown/MarkdownBlockRenderer.cs"
md_inline="$AXAML_DIR/Views/Controls/Markdown/MarkdownInlineRenderer.cs"
md_resolver="$AXAML_DIR/Views/Controls/Markdown/MarkdownResourceResolver.cs"

if [ ! -f "$md_renderer" ]; then
    warn "MarkdownRenderer.axaml.cs not found — skipping theme-subscription check."
else
    theme_sub="no"

    # Look for any Avalonia theme-change event subscription in markdown files.
    # Avalonia raises RequestedThemeVariantChanged on Application; some apps also
    # listen to ResourceDictionary changes.
    for f in "$md_renderer" "$md_block" "$md_inline" "$md_resolver"; do
        if [ -f "$f" ]; then
            if grep -qE 'RequestedThemeVariantChanged|ActualThemeVariantChanged|ThemeChanged|Subscribe.*[Tt]heme' "$f"; then
                theme_sub="yes"
                break
            fi
        fi
    done

    if [ "$theme_sub" = "yes" ]; then
        info "Markdown renderer subscribes to theme changes."
    else
        warn "MarkdownRenderer does NOT subscribe to theme-change events."
        warn "  → After ThemeService.ApplyHds() the markdown syntax colours will be stale."
        warn "  → Fix: subscribe to RequestedThemeVariantChanged (or equivalent) and call Render()."
    fi
fi

# ── 4. theme.json presence check (App.Analysis) ──────────────────────────────
# The project stores theme choice in avalonia.json / common config; there is no
# standalone theme.json in the repo. Verify the canonical theme sources exist.

printf "\n== Check 4: Theme source files ==\n"
hds_dir="$AXAML_DIR/Themes/Hds"
if [ ! -d "$hds_dir" ]; then
    warn "HDS themes directory not found at $hds_dir"
else
    count=$(find "$hds_dir" -maxdepth 1 -name "*.axaml" | wc -l)
    if [ "$count" -gt 0 ]; then
        info "$count HDS theme palette(s) found in $hds_dir"
    else
        warn "No .axaml theme palettes found in $hds_dir"
    fi
fi

# ── Summary ──────────────────────────────────────────────────────────────────
printf "\n== Summary ==\n"
if [ "$errors" -gt 0 ] || [ "$warnings" -gt 0 ]; then
    if [ "$errors" -gt 0 ]; then
        printf "${RED}%d error(s), ${YEL}%d warning(s)${NC} — fix before building.\n" "$errors" "$warnings"
        exit 1
    else
        printf "${YEL}%d warning(s)${NC} — review but not blocking.\n" "$warnings"
        exit 0
    fi
else
    printf "${GRN}All hygiene checks passed.${NC}\n"
    exit 0
fi
