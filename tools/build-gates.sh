#!/usr/bin/env bash
# build-gates.sh -- build the compiled gate binary and commit its bytes.
#
# The gates of this repository used to be python scripts. They are now one
# multi-call binary (harbor-gates <gate> [flags]), and the binary is
# committed to the repository so that a gate can run where there is no SDK,
# no build of Harbor, and no python. The price of a binary in git is that
# nobody can read it in review, and the answer is a machine check: this
# script rebuilds the binary from the sources in the same tree, and the
# gates-binary workflow compares the bytes with the committed ones.
#
# THE BUILD
#
#   CGO_ENABLED=0 go build -trimpath -ldflags="-buildid= -s -w"
#
#   CGO_ENABLED=0   a static binary, no libc, no dynamic loader: it runs on
#                   any linux of the same architecture.
#   -trimpath       no absolute paths in the binary, so the same source
#                   builds the same bytes in any checkout directory.
#   -buildid=       no build id, which is otherwise a hash of the build
#                   environment and changes on every machine.
#   -s -w           no symbol table, no DWARF: smaller, and fewer bytes that
#                   could differ between toolchain patch versions.
#
# The toolchain is pinned by tools/gates/go.mod (the `go` directive) and the
# workflow reads it from there, so a toolchain bump is an explicit,
# reviewable change.
#
# THE ARTIFACTS
#
#   tools/bin/harbor-gates-linux-x64
#   tools/bin/harbor-gates-linux-arm64
#   tools/bin/SHA256SUMS
#
# usage:
#   tools/build-gates.sh              build both, write the manifest
#   tools/build-gates.sh --check      build both, compare with the committed
#                                     bytes, fail on a difference (no writes)
#   tools/build-gates.sh --verify     build both TWICE and compare the two
#                                     builds with each other (reproducibility)
#   tools/build-gates.sh --arch amd64 build one architecture
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/.." && pwd)"
SRC="$HERE/gates"
BIN="$HERE/bin"
GO_VERSION="$(sed -n 's/^go //p' "$SRC/go.mod" | head -1)"

MODE="build"
ARCHES="amd64 arm64"

while [ $# -gt 0 ]; do
    case "$1" in
        --check) MODE="check" ;;
        --verify) MODE="verify" ;;
        --arch) shift; ARCHES="$1" ;;
        -h|--help)
            sed -n '2,40p' "$0"
            exit 0
            ;;
        *)
            echo "build-gates.sh: unknown argument: $1" >&2
            exit 2
            ;;
    esac
    shift
done

command -v go >/dev/null 2>&1 || {
    echo "build-gates.sh: go is not on PATH; the pinned toolchain is $GO_VERSION" >&2
    exit 2
}

mkdir -p "$BIN"

# build <arch> <output> -- one reproducible build.
build() {
    local arch="$1" out="$2"
    ( cd "$SRC" && \
      CGO_ENABLED=0 GOOS=linux GOARCH="$arch" GOFLAGS=-mod=mod \
      go build -trimpath -ldflags="-buildid= -s -w" -o "$out" . )
}

# The name a platform's binary has in the repository.
artifact_for() {
    case "$1" in
        amd64) echo "harbor-gates-linux-x64" ;;
        arm64) echo "harbor-gates-linux-arm64" ;;
        *) echo "unknown architecture: $1" >&2; return 2 ;;
    esac
}

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

echo "build-gates.sh: toolchain $(go version)"
echo "build-gates.sh: pinned by tools/gates/go.mod (go $GO_VERSION)"

for arch in $ARCHES; do
    name="$(artifact_for "$arch")"
    built="$TMP/$name"
    build "$arch" "$built"

    if [ "$MODE" = "verify" ]; then
        # Build twice, in two different directories, and compare the bytes.
        second_dir="$TMP/second"
        mkdir -p "$second_dir"
        ( cd "$SRC" && \
          CGO_ENABLED=0 GOOS=linux GOARCH="$arch" GOFLAGS=-mod=mod \
          go build -trimpath -ldflags="-buildid= -s -w" -o "$second_dir/$name" . )
        a="$(sha256sum "$built" | cut -d' ' -f1)"
        b="$(sha256sum "$second_dir/$name" | cut -d' ' -f1)"
        if [ "$a" != "$b" ]; then
            echo "FAIL: the build is not reproducible: $name $a != $b" >&2
            exit 1
        fi
        echo "build-gates.sh: $name is byte-reproducible ($a)"
    fi

    if [ "$MODE" = "check" ]; then
        committed="$BIN/$name"
        if [ ! -f "$committed" ]; then
            echo "FAIL: $committed is not in the tree; run tools/build-gates.sh" >&2
            exit 1
        fi
        a="$(sha256sum "$built" | cut -d' ' -f1)"
        b="$(sha256sum "$committed" | cut -d' ' -f1)"
        if [ "$a" != "$b" ]; then
            echo "FAIL: the committed $name does not match its source." >&2
            echo "  gate     : harbor-gates" >&2
            echo "  platform : linux-${arch}" >&2
            echo "  expected : $b" >&2
            echo "  rebuilt  : $a" >&2
            echo "  fix      : tools/build-gates.sh" >&2
            exit 1
        fi
        echo "build-gates.sh: $name matches the committed binary ($a)"
    else
        if [ "$MODE" = "build" ]; then
            cp "$built" "$BIN/$name"
            chmod +x "$BIN/$name"
            echo "build-gates.sh: wrote $BIN/$name ($(stat -c %s "$BIN/$name") bytes)"
        fi
    fi
done

if [ "$MODE" = "build" ]; then
    ( cd "$BIN" && sha256sum harbor-gates-linux-x64 harbor-gates-linux-arm64 > SHA256SUMS )
    echo "build-gates.sh: wrote $BIN/SHA256SUMS"
    cat "$BIN/SHA256SUMS"
fi
