#!/usr/bin/env python3
"""Fail if `Microsoft.NET.Test.Sdk` is declared anywhere in this repository.

Stdlib only — no pip, no network, no dotnet, no restore. Wired into
`.github/workflows/ci.yml` as its own job (issue #1893), next to the other
gates in `tools/`:

  check-abstractions-contract.py  the dependency surface of four src/ projects.
  check-test-packages.py          the package set of the test/sample projects.
  THIS ONE                       that the VSTest test host is not among them.

WHY THIS FILE EXISTS (issue #1893)

  `Microsoft.NET.Test.Sdk` was declared in 16 `.csproj` files (11 under
  `tests/`, 5 under `contrib/tests/`) and pinned once in
  `Directory.Packages.props`. TUnit's own installation documentation says the
  package must not be used with TUnit:

    "If you're used to other testing frameworks, you're probably used to the
     package Microsoft.NET.Test.Sdk. This should NOT be used with TUnit. It'll
     stop test discovery from working properly."
    — https://tunit.dev/docs/getting-started/installation

  The package dragged three assemblies into the output directory of every
  test project (`Microsoft.TestPlatform.TestHost`,
  `Microsoft.TestPlatform.ObjectModel`, `Microsoft.CodeCoverage`) and brought
  the VSTest host with it — the host this repository does not use: it runs on
  Microsoft.Testing.Platform (`global.json` selects the MTP runner) and every
  CI job executes a test project as a plain executable
  (`dotnet run --project tests/<Name> -c Release --no-build -- ...`).

  Removal was the whole fix, and it needed no replacement package:
  `Microsoft.Testing.Platform.MSBuild` (the entry-point generator) and
  `Microsoft.Testing.Extensions.CodeCoverage` / `.TrxReport` (the `--coverage`
  and `--report-trx` extensions CI uses) already arrive TRANSITIVELY through
  `TUnit` → `TUnit.Engine`. Measured on the restored graph of
  `tests/Harbor.Core.Tests`: `Microsoft.Testing.Platform.MSBuild/2.3.2` and
  `Microsoft.Testing.Platform/2.3.2` are present with and without the VSTest
  host, and nothing else in the repository used what the host provided.

  So this file exists for one reason: to make the removal IRREVERSIBLE
  without a reviewer's memory. Nothing in the tree would have complained if
  the package came back — the build would still be green, the tests would
  still run, and the only symptom would be the documented TUnit discovery
  conflict re-appearing on the next TUnit upgrade.

WHAT THIS FILE IS NOT

  It is not a list of packages a project may reference. It names exactly one
  forbidden id, and it forbids it for a documented reason. `tools/
  check-test-packages.py` owns the general question (which packages may a
  test project reference, read out of `BannedSymbols.txt`); this file owns
  the one id whose removal was a deliberate, dated decision, and it owns it
  because that decision is easy to undo by accident and hard to notice.

RULES

  VSTEST-SDK-REFERENCE  a `<PackageReference Include="Microsoft.NET.Test.Sdk" />`
                       (or `<PackageDownload>`) element in any tracked project
                       file.
  VSTEST-SDK-PIN       a `<PackageVersion Include="Microsoft.NET.Test.Sdk" ... />`
                       element in any tracked props file. The pin alone is
                       harmless — it declares no dependency — but it is the
                       first half of the re-introduction: a pin with no
                       reference is one `<PackageReference>` away from being
                       live, and the reference is what the pin was written for.

  The match is on the ELEMENT, not on the text. A comment that names the
  package — and several do, to explain why it is absent — is not a finding.
  XML comments are masked out before the match runs, so the line numbers the
  findings carry are the line numbers of the file.

SCOPE, AND WHAT IS DELIBERATELY OUTSIDE IT

  Every tracked `*.csproj`, `*.props` and `*.targets` in the repository,
  with ONE exclusion: `contrib/external/`, the vendored third-party tree
  (ADR-019). That tree is a git SUBMODULE, so its files are not tracked
  here at all, and the vendored projects keep their own test stack on their
  own terms — `Microsoft.NET.Test.Sdk` 17.11.1 / 18.6.0 there is not a
  Harbor decision.

  The file list comes from `git ls-files`, not from a glob: the same reason
  `tools/check-test-packages.py` uses it. An untracked scratch project must
  not be able to change what the gate sees, and a tracked project must not
  be able to hide from it. A directory walk is the fallback for a throwaway
  fixture tree, which is not a git repository.

NON-VACUITY

  `--self-test` builds throwaway trees and asserts the failure path on every
  shape the rule has: a `PackageReference` in a `.csproj`, a `PackageVersion`
  pin in a props file, a reference with a `Version` attribute of its own, a
  reference whose `Include` is spelled in single quotes or with different
  casing, a reference with a body rather than a self-closing tag, and a
  `PackageDownload`. It also asserts the false-positive controls: a project
  file that NAMES the package in a comment is clean, a project file that
  references a different package whose id merely CONTAINS the forbidden id
  is clean, and a clean tree stays clean.

  The self-test is wired into CI as the first step of the job, before the
  real check, so a gate whose matcher stopped matching cannot pass quietly.

  The scan floor (`--min-files`) is a ratchet: the gate reports how many
  project files it looked at, and a scan that silently narrowed — because
  someone changed the extension filter, or moved the tree — goes red
  instead of green.
"""
import argparse
import os
import re
import shutil
import subprocess
import sys
import tempfile

# The one forbidden id. NuGet ids are case-insensitive, so the match is too.
FORBIDDEN_ID = "Microsoft.NET.Test.Sdk"

# The elements that can introduce a package into a project's graph.
# `PackageReference` declares a dependency, `PackageVersion` pins one in the
# central catalogue, `PackageDownload` fetches one without referencing it.
ELEMENTS = ("PackageReference", "PackageVersion", "PackageDownload")

# The project files in scope: every tracked .csproj / .props / .targets.
SCAN_SUFFIXES = (".csproj", ".props", ".targets")

# The vendored third-party tree. It is a git submodule (ADR-019), so its
# files are not tracked in this repository and the exclusion is belt and
# braces; it is stated rather than implied so that a reader can see that
# "the whole repository" means "the part Harbor owns".
EXCLUDED_PREFIXES = ("contrib/external/",)

# An element with an `Include` attribute. The attribute may sit on any line
# of the element, may use either quote style, and the element may be
# self-closing or carry a body.
_INCLUDE = re.compile(
    r"<(?P<element>" + "|".join(ELEMENTS) + r")\b[^>]*?"
    r"\bInclude\s*=\s*(?P<quote>[\"'])(?P<id>[^\"']+)(?P=quote)",
    re.S | re.I,
)


class Finding:
    """One forbidden declaration, with the evidence to act on it."""

    def __init__(self, rule, path, line, element, snippet):
        self.rule = rule
        self.path = path
        self.line = line
        self.element = element
        self.snippet = snippet

    def __str__(self):
        return f"{self.rule}  {self.path}:{self.line}  <{self.element}>"


# ---------------------------------------------------------------------------
# The scope
# ---------------------------------------------------------------------------


def scan_files(root):
    """Every project file in scope, repository-relative.

    `git ls-files` rather than a glob, for the reason the module docstring
    states: an untracked scratch project must not change what the gate sees,
    and a tracked project must not be able to hide from it.
    """
    try:
        proc = subprocess.run(
            ["git", "ls-files", "--", "*.csproj", "*.props", "*.targets"],
            cwd=root,
            capture_output=True,
            text=True,
            check=True,
        )
        tracked = [
            line
            for line in proc.stdout.splitlines()
            if line.endswith(SCAN_SUFFIXES)
        ]
        if tracked:
            return sorted(
                line for line in tracked if not line.startswith(EXCLUDED_PREFIXES)
            )
    except (OSError, subprocess.CalledProcessError):
        pass

    found = []
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [
            d for d in dirnames if d not in ("bin", "obj", ".git")
        ]
        for name in filenames:
            if not name.endswith(SCAN_SUFFIXES):
                continue
            full = os.path.join(dirpath, name)
            relative = os.path.relpath(full, root).replace(os.sep, "/")
            if relative.startswith(EXCLUDED_PREFIXES):
                continue
            found.append(relative)
    return sorted(found)


# ---------------------------------------------------------------------------
# The match
# ---------------------------------------------------------------------------


def mask_comments(text):
    """Replace every XML comment with spaces, keeping the line count.

    The line numbers a finding carries have to be the line numbers of the
    FILE, and a comment that mentions the forbidden id must not be read as
    a declaration. Blanking the comment's characters (but not its
    newlines) does both at once.
    """
    out = []
    index = 0
    length = len(text)
    while index < length:
        start = text.find("<!--", index)
        if start < 0:
            out.append(text[index:])
            break
        out.append(text[index:start])
        end = text.find("-->", start + 4)
        if end < 0:
            # An unterminated comment: treat the rest of the file as a
            # comment, so a broken file cannot manufacture a finding.
            out.append(" " * (length - start))
            break
        body = text[start : end + 3]
        out.append("".join("\n" if ch == "\n" else " " for ch in body))
        index = end + 3
    return "".join(out)


def find_declarations(relative, text):
    """Every forbidden element in one file."""
    masked = mask_comments(text)
    findings = []
    for match in _INCLUDE.finditer(masked):
        if match.group("id").strip().lower() != FORBIDDEN_ID.lower():
            continue
        element = match.group("element")
        rule = (
            "VSTEST-SDK-PIN"
            if element.lower() == "packageversion"
            else "VSTEST-SDK-REFERENCE"
        )
        line = masked.count("\n", 0, match.start()) + 1
        snippet = " ".join(match.group(0).split())
        findings.append(Finding(rule, relative, line, element, snippet))
    return findings


def analyse(root):
    """(census, findings). Both are derived from the tree; neither is declared."""
    files = scan_files(root)
    findings = []
    for relative in files:
        path = os.path.join(root, relative)
        try:
            with open(path, encoding="utf-8", errors="replace") as handle:
                text = handle.read()
        except OSError as error:
            findings.append(
                Finding(
                    "SCAN-BLIND",
                    relative,
                    0,
                    "Project",
                    f"the file could not be read ({error})",
                )
            )
            continue
        findings.extend(find_declarations(relative, text))

    census = {
        "files": len(files),
        "by_suffix": {
            suffix: sum(1 for f in files if f.endswith(suffix))
            for suffix in SCAN_SUFFIXES
        },
        "excluded": list(EXCLUDED_PREFIXES),
    }
    return census, findings


# ---------------------------------------------------------------------------
# Self-test — the failure path, on every run of the gate in CI.
# ---------------------------------------------------------------------------

CSPROJ = """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
{refs}  </ItemGroup>
</Project>
"""

PACKAGE_REF = '    <PackageReference Include="{pkg}"/>'
PACKAGE_REF_VERSION = '    <PackageReference Include="{pkg}" Version="18.8.1"/>'
PACKAGE_REF_SINGLE = "    <PackageReference Include='{pkg}'/>"
PACKAGE_REF_BODY = (
    '    <PackageReference Include="{pkg}">\n'
    "      <PrivateAssets>all</PrivateAssets>\n"
    "    </PackageReference>"
)
PACKAGE_DOWNLOAD = '    <PackageDownload Include="{pkg}"/>'

PROPS = """<Project>
  <ItemGroup>
{refs}  </ItemGroup>
</Project>
"""

PACKAGE_VERSION = '    <PackageVersion Include="{pkg}" Version="18.8.1" />'


def fixture(files):
    """A throwaway tree. `files` maps repo-relative path -> contents."""
    root = tempfile.mkdtemp(prefix="no-test-sdk-gate-")
    for relative, body in files.items():
        path = os.path.join(root, relative)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as handle:
            handle.write(body)
    return root


def csproj(refs):
    return CSPROJ.format(refs="".join(f"{ref}\n" for ref in refs))


def props(refs):
    return PROPS.format(refs="".join(f"{ref}\n" for ref in refs))


def selftest():
    failures = []
    ran = []

    def check(name, condition, detail=""):
        ran.append(name)
        if condition:
            print(f"  ok    {name}")
        else:
            print(f"  FAIL  {name} {detail}")
            failures.append(name)

    def shown(found):
        return " | ".join(str(f) for f in found)

    # 1. The shape the issue is about: a PackageReference in a .csproj.
    root = fixture(
        {
            "tests/Harbor.Core.Tests/Harbor.Core.Tests.csproj": csproj(
                [PACKAGE_REF.format(pkg=FORBIDDEN_ID)]
            ),
        }
    )
    _, found = analyse(root)
    check(
        "a PackageReference in a .csproj is reported",
        any(f.rule == "VSTEST-SDK-REFERENCE" for f in found),
        shown(found),
    )
    shutil_rmtree(root)

    # 2. The central pin. A pin declares no dependency, but it is the first
    #    half of the re-introduction, so it is reported on its own.
    root = fixture(
        {
            "Directory.Packages.props": props(
                [PACKAGE_VERSION.format(pkg=FORBIDDEN_ID)]
            ),
        }
    )
    _, found = analyse(root)
    check(
        "a PackageVersion pin in a props file is reported",
        any(f.rule == "VSTEST-SDK-PIN" for f in found),
        shown(found),
    )
    shutil_rmtree(root)

    # 3. The same reference with a version of its own (a project that does
    #    not use central package management).
    root = fixture(
        {
            "contrib/tests/Harbor.E2E.Cli/Harbor.E2E.Cli.csproj": csproj(
                [PACKAGE_REF_VERSION.format(pkg=FORBIDDEN_ID)]
            ),
        }
    )
    _, found = analyse(root)
    check(
        "a reference carrying its own Version is reported",
        any(f.rule == "VSTEST-SDK-REFERENCE" for f in found),
        shown(found),
    )
    shutil_rmtree(root)

    # 4. The same reference in single quotes.
    root = fixture(
        {
            "tests/Harbor.Ipc.Tests/Harbor.Ipc.Tests.csproj": csproj(
                [PACKAGE_REF_SINGLE.format(pkg=FORBIDDEN_ID)]
            ),
        }
    )
    _, found = analyse(root)
    check(
        "a reference in single quotes is reported",
        any(f.rule == "VSTEST-SDK-REFERENCE" for f in found),
        shown(found),
    )
    shutil_rmtree(root)

    # 5. The same reference with a body instead of a self-closing tag.
    root = fixture(
        {
            "tests/Harbor.LoadTests/Harbor.LoadTests.csproj": csproj(
                [PACKAGE_REF_BODY.format(pkg=FORBIDDEN_ID)]
            ),
        }
    )
    _, found = analyse(root)
    check(
        "a reference with a body is reported",
        any(f.rule == "VSTEST-SDK-REFERENCE" for f in found),
        shown(found),
    )
    shutil_rmtree(root)

    # 6. A PackageDownload fetches the package without referencing it.
    root = fixture(
        {
            "tests/Harbor.Ui.Framework.Tests/Harbor.Ui.Framework.Tests.csproj": csproj(
                [PACKAGE_DOWNLOAD.format(pkg=FORBIDDEN_ID)]
            ),
        }
    )
    _, found = analyse(root)
    check(
        "a PackageDownload is reported",
        any(f.rule == "VSTEST-SDK-REFERENCE" for f in found),
        shown(found),
    )
    shutil_rmtree(root)

    # 7. The casing. NuGet ids are case-insensitive.
    root = fixture(
        {
            "tests/Harbor.Tui.CellForge.Tests/Harbor.Tui.CellForge.Tests.csproj": csproj(
                [PACKAGE_REF.format(pkg="microsoft.net.test.sdk")]
            ),
        }
    )
    _, found = analyse(root)
    check(
        "a reference in a different casing is reported",
        any(f.rule == "VSTEST-SDK-REFERENCE" for f in found),
        shown(found),
    )
    shutil_rmtree(root)

    # 8. THE FALSE-POSITIVE CONTROL. A project file that NAMES the package
    #    in a comment — which several do, to explain why it is absent — is
    #    clean. A gate that flagged its own explanation would be switched
    #    off within a week.
    root = fixture(
        {
            "tests/Harbor.Core.Tests/Harbor.Core.Tests.csproj": CSPROJ.format(
                refs=(
                    "    <!-- Microsoft.NET.Test.Sdk is deliberately absent:\n"
                    "         TUnit documents it as breaking discovery. -->\n"
                    '    <PackageReference Include="TUnit"/>\n'
                )
            ),
        }
    )
    _, found = analyse(root)
    check("a package named in a comment is not reported", not found, shown(found))
    shutil_rmtree(root)

    # 9. A package whose id merely CONTAINS the forbidden id is a different
    #    package. The match is exact, as NuGet ids are.
    root = fixture(
        {
            "tests/Harbor.Core.Tests/Harbor.Core.Tests.csproj": csproj(
                [PACKAGE_REF.format(pkg="Microsoft.NET.Test.Sdk.Extensions")]
            ),
        }
    )
    _, found = analyse(root)
    check(
        "a package whose id merely contains the forbidden id is not reported",
        not found,
        shown(found),
    )
    shutil_rmtree(root)

    # 10. A clean tree stays clean.
    root = fixture(
        {
            "tests/Harbor.Core.Tests/Harbor.Core.Tests.csproj": csproj(
                [PACKAGE_REF.format(pkg="TUnit")]
            ),
            "Directory.Packages.props": props(
                [PACKAGE_VERSION.format(pkg="TUnit")]
            ),
        }
    )
    _, found = analyse(root)
    check("a clean tree stays green", not found, shown(found))
    shutil_rmtree(root)

    # 11. The finding carries the line number of the FILE, not of the
    #     comment-masked copy. A comment above the element must not shift it.
    root = fixture(
        {
            "tests/Harbor.Core.Tests/Harbor.Core.Tests.csproj": CSPROJ.format(
                refs=(
                    "    <!-- Microsoft.NET.Test.Sdk -->\n"
                    '    <PackageReference Include="Microsoft.NET.Test.Sdk"/>\n'
                )
            ),
        }
    )
    _, found = analyse(root)
    check(
        "the finding carries the file's line number",
        any(f.rule == "VSTEST-SDK-REFERENCE" and f.line == 4 for f in found),
        shown(found),
    )
    shutil_rmtree(root)

    # 12. The gate must not be able to count itself: this file lives under
    #     tools/, and the scope is the project files, so the two cannot
    #     overlap.
    here = os.path.realpath(__file__)
    check(
        "the gate's own source is outside the scope",
        not here.endswith(".csproj"),
        here,
    )

    if failures:
        print(
            f"\nself-test FAILED: {len(failures)} of {len(ran)} fixture(s): "
            f", ".join(failures)
        )
        return 1
    print(f"\nself-test passed: {len(ran)} fixtures")
    return 0


def shutil_rmtree(root):
    """Remove a fixture tree."""
    shutil.rmtree(root, ignore_errors=True)


# ---------------------------------------------------------------------------


def main():
    parser = argparse.ArgumentParser(
        description=__doc__.split("\n")[0],
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument(
        "--root",
        default=os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
        help="repository root (default: the root of this checkout)",
    )
    parser.add_argument(
        "--report",
        action="store_true",
        help="print the derived census and exit 0 regardless",
    )
    parser.add_argument(
        "--min-files",
        type=int,
        default=0,
        help="scan floor: fail unless at least this many project files were "
        "scanned (0 = off)",
    )
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()

    if args.self_test:
        return selftest()

    root = os.path.abspath(args.root)
    census, findings = analyse(root)

    if args.report:
        print(f"project files scanned: {census['files']}")
        for suffix, count in census["by_suffix"].items():
            print(f"  {suffix}: {count}")
        print(f"excluded prefixes: {', '.join(census['excluded']) or '-'}")
        if findings:
            print("\nfindings:")
            for finding in findings:
                print(f"  {finding}")
        return 0

    breached = []
    if args.min_files and census["files"] < args.min_files:
        breached.append(
            (
                "project files",
                args.min_files,
                census["files"],
            )
        )

    if findings:
        print("No-VSTest-SDK gate — findings:\n")
        for finding in findings:
            print(f"  {finding}")
            print(f"      {finding.snippet}")
        print()
        print(
            f"  {FORBIDDEN_ID} must not be declared in this repository. TUnit's\n"
            "  installation docs call it out by name:\n"
            "    \"If you're used to other testing frameworks, you're probably\n"
            "     used to the package Microsoft.NET.Test.Sdk. This should NOT be\n"
            "     used with TUnit. It'll stop test discovery from working\n"
            "     properly.\" — https://tunit.dev/docs/getting-started/installation\n"
            "\n"
            "  Fix: delete the element. Do NOT replace it with another VSTest\n"
            "  host package. This repository runs on Microsoft.Testing.Platform,\n"
            "  and the pieces it needs — Microsoft.Testing.Platform.MSBuild,\n"
            "  Microsoft.Testing.Extensions.CodeCoverage,\n"
            "  Microsoft.Testing.Extensions.TrxReport — arrive transitively\n"
            "  through TUnit, so there is nothing to add.\n"
        )

    if breached:
        for name, floor, actual in breached:
            print(
                f"  SCAN-BLIND  scan floor: {name} compared {actual}, floor is "
                f"{floor}. The gate saw less than the tree holds, so it cannot "
                "report that the tree is clean."
            )
        print()

    if findings or breached:
        return 1

    print(
        f"OK: {census['files']} project file(s) scanned, "
        f"{FORBIDDEN_ID} is declared in none of them"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
