#!/usr/bin/env bash
# demo/install-toolchain.sh — install the *pinned* VHS recording toolchain.
#
#   demo/install-toolchain.sh            # install + print the resolved versions
#   DEMO_TOOLCHAIN_DIR=… demo/install-toolchain.sh
#   eval "$(demo/install-toolchain.sh --print-path)"   # just the PATH export
#
# Every download is sha256-verified against demo/toolchain.lock, and an already
# verified install is reused (so the script is safe to run on every CI job).
# The pinned monospace font is installed to ~/.local/share/fonts + fc-cache, and
# the script asserts that `fc-match "JetBrains Mono"` really resolves to it —
# a silent fallback to DejaVu would reflow every tape (issue #426).
set -euo pipefail

REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
LOCK="$REPO_ROOT/demo/toolchain.lock"
INSTALL_DIR="${DEMO_TOOLCHAIN_DIR:-${XDG_CACHE_HOME:-$HOME/.cache}/harbor-demo-toolchain}"
FONT_DIR="${DEMO_FONT_DIR:-$HOME/.local/share/fonts}"

die() { echo "install-toolchain: $*" >&2; exit 1; }

[[ -f "$LOCK" ]] || die "missing $LOCK"
# shellcheck disable=SC1090
set -a; . "$LOCK"; set +a

for required in VHS_URL VHS_SHA256 VHS_VERSION TTYD_URL TTYD_SHA256 TTYD_VERSION \
                FONT_URL FONT_SHA256 FONT_VERSION FONT_FAMILY; do
  [[ -n "${!required:-}" ]] || die "$required is not pinned in demo/toolchain.lock"
done

if [[ "${1:-}" == "--print-path" ]]; then
  printf 'export PATH=%q:$PATH\n' "$INSTALL_DIR/bin"
  exit 0
fi

command -v curl >/dev/null || die "curl is required to install the pinned toolchain"
command -v sha256sum >/dev/null || die "sha256sum is required to verify the pinned toolchain"

mkdir -p "$INSTALL_DIR/bin" "$INSTALL_DIR/downloads"

verify() { # verify <file> <expected-sha256>
  local actual
  actual="$(sha256sum "$1" | cut -d' ' -f1)"
  [[ "$actual" == "$2" ]] || die "sha256 mismatch for $(basename "$1"): expected $2, got $actual"
}

fetch() { # fetch <url> <expected-sha256> → path on stdout
  local url="$1"
  local want="$2"
  local name dest
  name="$(basename "$url")"
  dest="$INSTALL_DIR/downloads/$name"
  if [[ -f "$dest" ]] && verify "$dest" "$want" 2>/dev/null; then
    printf '%s\n' "$dest"
    return 0
  fi
  echo "install-toolchain: downloading $url" >&2
  curl --fail --location --silent --show-error --retry 3 -o "$dest" "$url" \
    || die "download failed: $url"
  verify "$dest" "$want"
  printf '%s\n' "$dest"
}

# --- vhs ---------------------------------------------------------------------
if [[ -x "$INSTALL_DIR/bin/vhs" ]]; then
  vhs_installed="$("$INSTALL_DIR/bin/vhs" --version 2>&1 | head -1)"
  case "$vhs_installed" in
    *"$VHS_VERSION"*) ;;
    *) echo "install-toolchain: replacing vhs $vhs_installed (lock wants $VHS_VERSION)" >&2
       rm -f "$INSTALL_DIR/bin/vhs" ;;
  esac
fi
if [[ ! -x "$INSTALL_DIR/bin/vhs" ]]; then
  vhs_tar="$(fetch "$VHS_URL" "$VHS_SHA256")"
  tar -xzf "$vhs_tar" -C "$INSTALL_DIR/downloads"
  install -m 0755 "$INSTALL_DIR/downloads/vhs_${VHS_VERSION}_Linux_x86_64/vhs" "$INSTALL_DIR/bin/vhs"
  rm -rf "$INSTALL_DIR/downloads/vhs_${VHS_VERSION}_Linux_x86_64"
fi

# --- ttyd --------------------------------------------------------------------
if [[ -x "$INSTALL_DIR/bin/ttyd" ]]; then
  ttyd_installed="$("$INSTALL_DIR/bin/ttyd" --version 2>&1 | head -1)"
  case "$ttyd_installed" in
    *"$TTYD_VERSION"*) ;;
    *) echo "install-toolchain: replacing ttyd $ttyd_installed (lock wants $TTYD_VERSION)" >&2
       rm -f "$INSTALL_DIR/bin/ttyd" ;;
  esac
fi
if [[ ! -x "$INSTALL_DIR/bin/ttyd" ]]; then
  install -m 0755 "$(fetch "$TTYD_URL" "$TTYD_SHA256")" "$INSTALL_DIR/bin/ttyd"
fi

# --- font --------------------------------------------------------------------
# Only the one family the tapes ask for. The recorder must not pick up a
# fallback: a different glyph set means a different line-wrap and a different
# GIF, which is exactly the "size drift that is not a regression" we are killing.
mkdir -p "$FONT_DIR"
if ! fc-list 2>/dev/null | grep -qi "$FONT_FAMILY"; then
  font_zip="$(fetch "$FONT_URL" "$FONT_SHA256")"
  font_tmp="$(mktemp -d)"
  trap 'rm -rf "$font_tmp"' EXIT
  unzip -q -o "$font_zip" -d "$font_tmp"
  install -m 0644 "$font_tmp"/fonts/ttf/*.ttf "$FONT_DIR"/ 2>/dev/null \
    || install -m 0644 "$font_tmp"/*.ttf "$FONT_DIR"/
  fc-cache -f >/dev/null 2>&1 || true
  rm -rf "$font_tmp"
  trap - EXIT
fi

resolved_font="$(fc-match -f '%{family}' "$FONT_FAMILY" 2>/dev/null || true)"
case "$resolved_font" in
  *"$FONT_FAMILY"*) ;;
  *) die "fc-match resolved '$FONT_FAMILY' to '${resolved_font:-<nothing>}' — the pinned font is missing, the recording would reflow" ;;
esac

# --- provenance --------------------------------------------------------------
export PATH="$INSTALL_DIR/bin:$PATH"
echo "install-toolchain: vhs  $($INSTALL_DIR/bin/vhs --version 2>&1 | head -1)  (pinned $VHS_VERSION)"
echo "install-toolchain: ttyd $($INSTALL_DIR/bin/ttyd --version 2>&1 | head -1)  (pinned $TTYD_VERSION)"
echo "install-toolchain: font $resolved_font $FONT_VERSION ($FONT_DIR)"
if command -v ffmpeg >/dev/null; then
  echo "install-toolchain: ffmpeg $(ffmpeg -version 2>&1 | head -1 | cut -d' ' -f1-3)  (runner-provided, recorded in baseline.json)"
else
  die "ffmpeg is required by the recorder and is not on PATH"
fi
echo "install-toolchain: export PATH=\"$INSTALL_DIR/bin:\$PATH\""
