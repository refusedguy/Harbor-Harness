#!/usr/bin/env bash
# Non-vacuity proof for the COMPILE-TIME public-API gate (#1845).
#
# WHY THIS FILE EXISTS
#
# The public-API freeze of #428 is now a compiler gate:
# Microsoft.CodeAnalysis.PublicApiAnalyzers reports RS0016 (a public symbol
# that is not in the declared API) and RS0017 (a declared API symbol that is
# not in the code) while the two frozen assemblies compile. A gate that
# stops firing is indistinguishable from a surface nobody changed, and the
# python gate it replaced proved it was alive with its own --self-test. This
# script is that proof for the compiler gate, and it runs in CI on every PR.
#
# WHAT IT DOES
#
# It builds a THROWAWAY project three times against the real analyzer and
# the real .editorconfig:
#
#   1. one public member, an empty baseline  -> the build MUST fail with
#      `error RS0016`. This is the unreviewed change.
#   2. the same project with the row the analyzer asked for -> the build
#      MUST pass. This is the re-signed change.
#   3. the member deleted, the baseline left alone -> the build MUST fail
#      with `error RS0017`. This is the removed member.
#
# The throwaway project lives OUTSIDE the repository, so the repo's
# Directory.Build.props does NOT apply to it: TreatWarningsAsErrors is off
# there, and the failure in step 1 is caused by the explicit
# `dotnet_diagnostic.RS0016.severity = error` line in the repo's
# .editorconfig — which is copied in — and by nothing else. That is the
# point of the exercise: the gate is an error because the severity was
# decided, not because a global warnings-as-errors policy happened to
# promote it.
#
# The rows for step 2 are not written by hand: they are read out of the
# analyzer's own RS0016 message, which carries the exact line the analyzer
# wants. So the proof also demonstrates the regeneration path the migration
# used.
#
# Exit codes: 0 the gate is alive, 1 it is not.

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

version="$(sed -n 's/.*PackageVersion Include="Microsoft\.CodeAnalysis\.PublicApiAnalyzers" Version="\([^"]*\)".*/\1/p' \
  "$repo_root/Directory.Packages.props")"
if [ -z "$version" ]; then
  echo "::error::the PublicApiAnalyzers pin was not found in Directory.Packages.props" >&2
  exit 1
fi
echo "using Microsoft.CodeAnalysis.PublicApiAnalyzers $version"

cat > "$work/gate.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.PublicApiAnalyzers" Version="$version">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
  </ItemGroup>
</Project>
EOF

# The severities the repository actually ships. Copied, not invented: the
# proof is about THIS configuration.
cp "$repo_root/.editorconfig" "$work/.editorconfig"

printf '#nullable enable\n' > "$work/PublicAPI.Shipped.txt"
printf '#nullable enable\n' > "$work/PublicAPI.Unshipped.txt"

cat > "$work/Widget.cs" <<'EOF'
namespace Gate;

public sealed class Widget
{
    public int Size { get; }
}
EOF

cd "$work"

build() {
  dotnet build gate.csproj -c Release --tl:off -v minimal > "$1" 2>&1
}

echo "== step 1: an unreviewed public member must fail the build"
set +e
build build1.log
rc1=$?
set -e
if [ "$rc1" -eq 0 ]; then
  echo "::error::the planted violation did NOT fail the build — the gate is not firing" >&2
  cat build1.log >&2
  exit 1
fi
if ! grep -q "error RS0016" build1.log; then
  echo "::error::the build failed, but not with RS0016 — the gate is not the thing that failed" >&2
  cat build1.log >&2
  exit 1
fi
echo "   failed as required"

echo "== step 2: the re-signed member must build"
sed -n "s/.*error RS0016: Symbol '\(.*\)' is not part of the declared public API.*/\1/p" build1.log \
  | sort -u > rows.txt
if [ ! -s rows.txt ]; then
  echo "::error::no RS0016 rows were extracted from the build log" >&2
  cat build1.log >&2
  exit 1
fi
{
  echo "#nullable enable"
  cat rows.txt
} > "$work/PublicAPI.Unshipped.txt"
echo "   re-signed with:"
sed 's/^/     /' rows.txt

set +e
build build2.log
rc2=$?
set -e
if [ "$rc2" -ne 0 ]; then
  echo "::error::the re-signed project did NOT build" >&2
  cat build2.log >&2
  exit 1
fi
echo "   built as required"

echo "== step 3: the removed member must fail the build"
cat > "$work/Widget.cs" <<'EOF'
namespace Gate;

public sealed class Widget
{
}
EOF
set +e
build build3.log
rc3=$?
set -e
if [ "$rc3" -eq 0 ]; then
  echo "::error::the removed member did NOT fail the build — the gate is not firing" >&2
  cat build3.log >&2
  exit 1
fi
if ! grep -q "error RS0017" build3.log; then
  echo "::error::the build failed, but not with RS0017 — the gate is not the thing that failed" >&2
  cat build3.log >&2
  exit 1
fi
echo "   failed as required"

echo "OK: the public-API gate fires on an unreviewed change, passes on a re-signed one, and fires on a removal"
