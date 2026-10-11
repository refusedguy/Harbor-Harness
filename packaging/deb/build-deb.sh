#!/usr/bin/env bash
# build-deb.sh — build a .deb for the Harbor CLI from a release archive.
#
# The .deb is a thin repackaging of the .tar.gz produced by the NUKE
# PublishArchive/Release targets (NOT a second build): same binaries, plus
# DEBIAN/control metadata and a /usr/bin/harbor shim. For an unmanaged
# install use dist/install.sh instead.
#
# Usage:
#   packaging/deb/build-deb.sh --archive <harbor-cli-*.tar.gz> --version <x.y.z> \
#       [--arch amd64] [--out artifacts/packages] [--maintainer "Name <mail>"]
#
# Requirements: tar, dpkg-deb. The --version must be plain X.Y.Z (a leading
# 'v', as in git tag v0.7.0, is stripped automatically).
set -euo pipefail

ARCHIVE=""
VERSION=""
ARCH=""
OUT="artifacts/packages"
MAINTAINER="Harbor contributors <harbor@example.com>"

usage() {
    sed -n '2,/^set -euo pipefail$/p' "$0" | sed 's/^# \{0,1\}//'
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --archive) ARCHIVE="$2"; shift 2 ;;
        --archive=*) ARCHIVE="${1#--archive=}"; shift ;;
        --version) VERSION="$2"; shift 2 ;;
        --version=*) VERSION="${1#--version=}"; shift ;;
        --arch) ARCH="$2"; shift 2 ;;
        --arch=*) ARCH="${1#--arch=}"; shift ;;
        --out) OUT="$2"; shift 2 ;;
        --out=*) OUT="${1#--out=}"; shift ;;
        --maintainer) MAINTAINER="$2"; shift 2 ;;
        --maintainer=*) MAINTAINER="${1#--maintainer=}"; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "build-deb.sh: unknown argument '$1' (try --help)" >&2; exit 2 ;;
    esac
done

[ -n "$ARCHIVE" ] || { echo "build-deb.sh: --archive is required" >&2; exit 2; }
[ -n "$VERSION" ] || { echo "build-deb.sh: --version is required" >&2; exit 2; }
[ -f "$ARCHIVE" ] || { echo "build-deb.sh: archive not found: $ARCHIVE" >&2; exit 1; }
command -v dpkg-deb >/dev/null 2>&1 || { echo "build-deb.sh: need dpkg-deb on PATH" >&2; exit 1; }

# Strip a leading 'v' (git tags are v0.7.0, Debian versions are 0.7.0).
VERSION="${VERSION#v}"
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.+~]+)?$ ]] \
    || { echo "build-deb.sh: bad --version '$VERSION' (want X.Y.Z)" >&2; exit 2; }

if [ -z "$ARCH" ]; then
    case "$(uname -m)" in
        x86_64|amd64) ARCH="amd64" ;;
        aarch64|arm64) ARCH="arm64" ;;
        *) echo "build-deb.sh: cannot map '$(uname -m)' to a Debian arch (pass --arch)" >&2; exit 2 ;;
    esac
fi

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT INT TERM
mkdir -p "$STAGE/DEBIAN" "$STAGE/usr/lib/harbor" "$STAGE/usr/bin"

tar -xzf "$ARCHIVE" -C "$STAGE/usr/lib/harbor"
[ -e "$STAGE/usr/lib/harbor/Harbor.App.Cli.dll" ] || [ -x "$STAGE/usr/lib/harbor/Harbor.App.Cli" ] \
    || { echo "build-deb.sh: archive has no Harbor.App.Cli payload: $ARCHIVE" >&2; exit 1; }

cat > "$STAGE/DEBIAN/control" <<EOF
Package: harbor-cli
Version: $VERSION
Section: devel
Priority: optional
Architecture: $ARCH
Maintainer: $MAINTAINER
Description: Harbor — modular .NET AI coding harness (CLI)
 Command-line harness for AI-assisted coding: agent loop, LLM providers,
 builtin tools, session storage, plugin runtime.
EOF

cat > "$STAGE/usr/bin/harbor" <<'EOF'
#!/bin/sh
# Part of the harbor-cli package — do not edit.
HARBOR_LIB="/usr/lib/harbor"
if [ -x "$HARBOR_LIB/Harbor.App.Cli" ]; then
    exec "$HARBOR_LIB/Harbor.App.Cli" "$@"
else
    exec dotnet "$HARBOR_LIB/Harbor.App.Cli.dll" "$@"
fi
EOF
chmod 0755 "$STAGE/usr/bin/harbor"

mkdir -p "$OUT"
DEB="$OUT/harbor-cli_${VERSION}_${ARCH}.deb"
dpkg-deb --build "$STAGE" "$DEB" >/dev/null
echo "build-deb.sh: built $DEB"
dpkg-deb --info "$DEB" | sed -n '1,8p'
