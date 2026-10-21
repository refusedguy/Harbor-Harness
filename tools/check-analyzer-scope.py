#!/usr/bin/env python3
"""Fail when an analyzer package's SCOPE says anything but what the tree says.

Stdlib only — no pip, no network, no dotnet, no restore. Wired into
`.github/workflows/ci.yml` as its own job (issue #1801), next to the other
gates in `tools/`:

  check-abstractions-contract.py  the dependency surface of four src/ projects.
  check-test-packages.py          the package set of the test/sample projects.
  check-no-test-sdk.py            that the VSTest test host is not among them.
  THIS ONE                       where each analyzer package is allowed to run.

WHY THIS FILE EXISTS (issue #1801)

  Nine analyzer packages were referenced by EVERY project in the tree, from
  the repository-root `Directory.Build.props`. The cost of that is not the
  number of findings — it is the number of ANALYZER CLASSES a package
  registers times the number of COMPILATIONS the package is referenced by.
  Roslyn runs an analyzer's diagnostics concurrently and reports the elapsed
  time per analyzer assembly, so the cost of a package is visible in the
  build as CPU time that buys nothing.

  A severity table cannot switch that off: `dotnet_diagnostic.X.severity =
  none` silences a RULE; the analyzer that implements it still runs over
  every compilation. The only lever that removes the cost is the SCOPE —
  which projects reference the package at all — and a scope that is not
  written down drifts back to solution-wide within a few months. Nothing in
  the build would complain: the build stays green, the findings stay the
  same, and only the wall time grows.

  So the scope is written down — in `tools/analyzer-scope.json` — and this
  file is what keeps the tree and that table in step.

WHAT THIS FILE IS NOT

  It is not a list of packages a project may reference. It is a list of
  analyzer packages and the TREES each of them may run in. A non-analyzer
  package is none of its business.

  It is also not a second copy of the table. The table is the source of
  truth; this file reads it and checks the tree against it. Changing the
  scope means changing the table AND the props files, and this file is what
  tells you when the two disagree.

RULES
  SCOPE-DRIFT         the trees a family actually runs in differ from the
                      trees the table declares.
  DECLARATION-SITE    a family is declared in a file the table does not
                      name (or is missing from a file the table names).
  NO-CONDITION        a family is declared WITHOUT the switch that turns it
                      off. Without the switch the scope cannot be expressed
                      and the measurement harness cannot address one family.
  UNDECLARED-ANALYZER a package that looks like an analyzer is referenced
                      and is not in the table. A new analyzer arrives with a
                      scope decision, not by accident.
  MISSING-PIN         a family in the table has no `<PackageVersion>` in
                      Directory.Packages.props.
  ORPHAN-SWITCH       a scope file switches a property that no family owns.
  STALE-ROW           a family in the table is not declared anywhere.

NON-VACUITY
  `--self-test` builds throwaway trees and asserts the failure path on every
  shape the rules have: a family declared in the wrong file, a family whose
  scope drifted, a family without the switch, an unknown analyzer package, a
  missing pin, an orphan switch, a stale table row — and the
  false-positive controls: a NON-analyzer package that looks like one, a
  comment that names an analyzer package, and a clean tree stays clean.

  The self-test is wired into CI as the first step of the job, before the
  real check, so a gate whose matcher stopped matching cannot pass quietly.

  The scan floor (`--min-files`) is a ratchet: the gate reports how many
  project files it looked at, and a scan that silently narrowed — because
  someone changed the extension filter, or moved the tree — goes red
  instead of green.
"""

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_ROOT = os.path.dirname(HERE)
DEFAULT_TABLE = os.path.join(HERE, "analyzer-scope.json")

# The project files in scope: every tracked .csproj / .props / .targets.
SCAN_SUFFIXES = (".csproj", ".props", ".targets")

# The props file that holds the DEFAULT scope, and the tree it stands for.
ROOT_PROPS = "Directory.Build.props"

# The scope file of each tree, and the tree it stands for. The contrib
# copies mirror the tree they live in — contrib/ is not built by CI, but
# its props files ARE tracked, and an analyzer wired there is still a
# decision somebody has to make.
SCOPE_FILES = {
    "src/Directory.Build.props": "src",
    "apps/Directory.Build.props": "apps",
    "contrib/apps/Directory.Build.props": "apps",
    "tests/Directory.Build.props": "tests",
    "contrib/tests/Directory.Build.props": "tests",
}

# The vendored third-party tree (ADR-019) is a git submodule: its files are
# not tracked here, and the exclusion is belt and braces.
EXCLUDED_PREFIXES = ("contrib/external/",)

# A package id that LOOKS like an analyzer. The heuristic is deliberately
# broad — a false positive costs a table row, a false negative lets a new
# analyzer arrive without a scope decision.
_ANALYZER_WORDS = ("analyzer", "analysers", "analyzers")


def looks_like_analyzer(package_id):
    """True when a package id is shaped like an analyzer package."""
    lowered = package_id.lower()
    if any(word in lowered for word in _ANALYZER_WORDS):
        return True
    # Two packages in this repository break the shape rule, and both are
    # named in the table, so the table is what decides them.
    return package_id in ("AsyncFixer", "ReferenceTrimmer")


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

# The whole element, so that the attributes on it (the switch, in
# particular) can be read, and the id out of it.
_ELEMENT = re.compile(r"<PackageReference\b(?P<attrs>[^>]*)>", re.S | re.I)

_INCLUDE = re.compile(
    r"\bInclude\s*=\s*(?P<quote>[\"'])(?P<id>[^\"']+)(?P=quote)", re.I
)

_PROPERTY = re.compile(
    r"<(?P<name>Harbor[A-Za-z]+)\b[^>]*>\s*(?P<value>[^<]*?)\s*</(?P=name)\s*>"
)

_VERSION = re.compile(
    r"<PackageVersion\b[^>]*?\bInclude\s*=\s*(?P<quote>[\"'])(?P<id>[^\"']+)(?P=quote)",
    re.S | re.I,
)


def mask_comments(text):
    """Replace every XML comment with spaces, keeping the line count."""
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
            out.append(" " * (length - start))
            break
        body = text[start : end + 3]
        out.append("".join("\n" if ch == "\n" else " " for ch in body))
        index = end + 3
    return "".join(out)


def tree_of(path):
    """The tree a project file belongs to."""
    if path == ROOT_PROPS:
        return "root"
    if path in SCOPE_FILES:
        return SCOPE_FILES[path]
    top = path.split("/")[0]
    if top in ("src", "apps", "tests"):
        return top
    if top == "contrib":
        parts = path.split("/")
        if len(parts) > 1 and parts[1] in ("apps", "tests"):
            return parts[1]
    return "root"


def package_references(text):
    """Every PackageReference element in one file: (id, element, line)."""
    masked = mask_comments(text)
    found = []
    for match in _ELEMENT.finditer(masked):
        include = _INCLUDE.search(match.group("attrs"))
        if not include:
            continue
        line = masked.count("\n", 0, match.start()) + 1
        found.append((include.group("id").strip(), match.group(0), line))
    return found


def properties(text):
    """Every Harbor* property assignment in one file: (name, value, line)."""
    masked = mask_comments(text)
    found = []
    for match in _PROPERTY.finditer(masked):
        line = masked.count("\n", 0, match.start()) + 1
        found.append((match.group("name"), match.group("value").strip(), line))
    return found


# ---------------------------------------------------------------------------
# The analysis
# ---------------------------------------------------------------------------


class Finding:
    """One breach of the scope table, with the evidence to act on."""

    def __init__(self, rule, path, line, detail):
        self.rule = rule
        self.path = path
        self.line = line
        self.detail = detail

    def __str__(self):
        where = f"{self.path}:{self.line}" if self.line else self.path
        return f"{self.rule}  {where}  {self.detail}"


def load_table(path):
    with open(path, encoding="utf-8") as handle:
        return json.load(handle)


def analyse(root, table):
    """(census, findings). Both are derived from the tree; neither is declared."""
    files = scan_files(root)
    findings = []

    families = {entry["id"]: entry for entry in table["families"]}
    known_properties = {entry["property"] for entry in table["families"]}
    not_analyzers = {entry["id"] for entry in table.get("not_analyzers", [])}

    # What the tree says: where each family is declared, what each scope file
    # switches, and which packages are referenced that the table does not know.
    declarations = {fid: {} for fid in families}
    switches = {}
    referenced = {}
    pins = set()

    for relative in files:
        path = os.path.join(root, relative)
        try:
            with open(path, encoding="utf-8", errors="replace") as handle:
                text = handle.read()
        except OSError as error:
            findings.append(
                Finding("SCAN-BLIND", relative, 0, f"the file could not be read ({error})")
            )
            continue

        for package_id, element, line in package_references(text):
            referenced.setdefault(package_id, []).append((relative, line))
            if package_id in families:
                declarations[package_id][relative] = (element, line)
            elif package_id in not_analyzers:
                pass
            elif looks_like_analyzer(package_id):
                findings.append(
                    Finding(
                        "UNDECLARED-ANALYZER",
                        relative,
                        line,
                        f"{package_id} is referenced and is not in tools/analyzer-scope.json — "
                        "an analyzer package arrives with a scope decision, not by accident",
                    )
                )

        if relative == "Directory.Packages.props":
            for match in _VERSION.finditer(mask_comments(text)):
                pins.add(match.group("id").strip())

        if relative == ROOT_PROPS or relative in SCOPE_FILES:
            for name, value, line in properties(text):
                if not name.startswith("HarborAnalyzer"):
                    continue
                switches.setdefault(relative, {})[name] = (value, line)
                if name not in known_properties:
                    findings.append(
                        Finding(
                            "ORPHAN-SWITCH",
                            relative,
                            line,
                            f"{name} switches a family that tools/analyzer-scope.json does not declare",
                        )
                    )

    # ── per family ──────────────────────────────────────────────────────────
    for package_id, entry in families.items():
        declared_in = sorted(entry["declared_in"])
        actual_in = sorted(declarations[package_id])

        if not actual_in:
            findings.append(
                Finding(
                    "STALE-ROW",
                    "tools/analyzer-scope.json",
                    0,
                    f"{package_id} is declared in the table but is not referenced anywhere in the tree",
                )
            )
            continue

        if actual_in != declared_in:
            findings.append(
                Finding(
                    "DECLARATION-SITE",
                    ", ".join(actual_in),
                    0,
                    f"{package_id} is declared in {actual_in} but the table says {declared_in}",
                )
            )

        for relative in actual_in:
            element, line = declarations[package_id][relative]
            if entry["mode"] == "job":
                if "Condition=" not in element:
                    findings.append(
                        Finding(
                            "NO-CONDITION",
                            relative,
                            line,
                            f"{package_id} carries no switch of its own; a job-scoped analyzer "
                            "must say when it runs",
                        )
                    )
                continue
            expected = "Condition=\"'$(%s)' != 'off'\"" % entry["property"]
            if expected not in element:
                findings.append(
                    Finding(
                        "NO-CONDITION",
                        relative,
                        line,
                        f"{package_id} must carry {expected} — without the switch the scope "
                        "cannot be expressed and the measurement harness cannot address one family",
                    )
                )

        if package_id not in pins:
            findings.append(
                Finding(
                    "MISSING-PIN",
                    "Directory.Packages.props",
                    0,
                    f"{package_id} has no <PackageVersion> pin",
                )
            )

        if entry["mode"] != "scope":
            continue

        derived = derive_scopes(entry, declarations[package_id], switches, findings)
        declared_scopes = sorted(entry["scopes"])
        if sorted(derived) != declared_scopes:
            findings.append(
                Finding(
                    "SCOPE-DRIFT",
                    "tools/analyzer-scope.json",
                    0,
                    f"{package_id} runs in {sorted(derived)} but the table says {declared_scopes}",
                )
            )

    census = {
        "files": len(files),
        "families": len(families),
        "scopes": len(table["trees"]),
    }
    return census, findings


def derive_scopes(entry, declaration_sites, switches, findings):
    """The trees a family actually runs in, read out of the tree."""
    sites = sorted(declaration_sites)
    table_trees = ("root", "src", "apps", "tests")

    # A family declared in a .csproj runs in that project's tree.
    if any(not site.endswith(".props") for site in sites):
        return {tree_of(site) for site in sites}

    # A family declared in a scope file runs in that scope's tree, and
    # nowhere else: it is not referenced by the root file at all.
    if ROOT_PROPS not in sites:
        return {tree_of(site) for site in sites}

    # A family declared in the root file runs in a tree unless that tree's
    # scope file switches it off. The root's own value is the default for
    # the trees that have no scope file of their own.
    root_value = switches.get(ROOT_PROPS, {}).get(entry["property"], ("on", 0))[0]
    enabled = set()
    for tree in sorted(table_trees):
        stated = []
        for path, name in SCOPE_FILES.items():
            if name != tree:
                continue
            if entry["property"] in switches.get(path, {}):
                stated.append((path, switches[path][entry["property"]]))
        values = {value for _, (value, _) in stated}
        if len(values) > 1:
            findings.append(
                Finding(
                    "SCOPE-DRIFT",
                    ", ".join(path for path, _ in stated),
                    0,
                    f"{entry['property']} is switched differently in the copies of the "
                    f"{tree} tree: {sorted(values)}",
                )
            )
        value = stated[0][1][0] if stated else root_value
        if value == "on":
            enabled.add(tree)
    return enabled


# ---------------------------------------------------------------------------
# Self-test — the failure path, on every run of the gate in CI.
# ---------------------------------------------------------------------------

CSPROJ = """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
{refs}  </ItemGroup>
</Project>
"""

PROPS = """<Project>
  <PropertyGroup>
{props}  </PropertyGroup>
  <ItemGroup>
{refs}  </ItemGroup>
</Project>
"""

ANALYZER = "Roslynator.Analyzers"
SWITCH = "HarborAnalyzerRoslynator"


def fixture(files):
    """A throwaway tree. `files` maps repo-relative path -> contents."""
    root = tempfile.mkdtemp(prefix="analyzer-scope-gate-")
    for relative, body in files.items():
        path = os.path.join(root, relative)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as handle:
            handle.write(body)
    return root


def table_for(families):
    return {
        "version": 1,
        "trees": {"root": "r", "src": "s", "apps": "a", "tests": "t"},
        "families": families,
    }


def family(identifier=ANALYZER, prop=SWITCH, scopes=None, declared_in=None, mode="scope"):
    return {
        "id": identifier,
        "property": prop,
        "declared_in": declared_in or ["Directory.Build.props"],
        "scopes": scopes if scopes is not None else ["root", "src", "apps", "tests"],
        "mode": mode,
        "reason": "fixture",
    }


def clean_tree():
    """A tree that agrees with the table: one family, everywhere."""
    return {
        "Directory.Build.props": PROPS.format(
            props=f"    <{SWITCH}>on</{SWITCH}>",
            refs=f'    <PackageReference Include="{ANALYZER}" Condition="\'$({SWITCH})\' != \'off\'" />',
        ),
        "src/Directory.Build.props": PROPS.format(
            props=f"    <{SWITCH}>on</{SWITCH}>",
            refs="",
        ),
        "apps/Directory.Build.props": PROPS.format(
            props=f"    <{SWITCH}>on</{SWITCH}>",
            refs="",
        ),
        "tests/Directory.Build.props": PROPS.format(
            props=f"    <{SWITCH}>on</{SWITCH}>",
            refs="",
        ),
        "Directory.Packages.props": PROPS.format(
            props="",
            refs=f'    <PackageVersion Include="{ANALYZER}" Version="5.0.1" />',
        ),
    }


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

    def rules(found):
        return {f.rule for f in found}

    # 0. The control: a tree that agrees with the table is clean.
    root = fixture(clean_tree())
    _, found = analyse(root, table_for([family()]))
    check("a clean tree stays green", not found, shown(found))
    shutil.rmtree(root)

    # 1. SCOPE-DRIFT: the scope file switches the family off, the table
    #    still says it runs everywhere.
    files = clean_tree()
    files["tests/Directory.Build.props"] = PROPS.format(
        props=f"    <{SWITCH}>off</{SWITCH}>", refs=""
    )
    root = fixture(files)
    _, found = analyse(root, table_for([family()]))
    check(
        "a family switched off in a scope file is reported",
        "SCOPE-DRIFT" in rules(found),
        shown(found),
    )
    shutil.rmtree(root)

    # 2. DECLARATION-SITE: the family is declared in a file the table does
    #    not name.
    files = clean_tree()
    files["src/Directory.Build.props"] = PROPS.format(
        props=f"    <{SWITCH}>on</{SWITCH}>",
        refs=f'    <PackageReference Include="{ANALYZER}" Condition="\'$({SWITCH})\' != \'off\'" />',
    )
    root = fixture(files)
    _, found = analyse(root, table_for([family()]))
    check(
        "a family declared in an unnamed file is reported",
        "DECLARATION-SITE" in rules(found),
        shown(found),
    )
    shutil.rmtree(root)

    # 3. NO-CONDITION: the family is declared without the switch.
    files = clean_tree()
    files["Directory.Build.props"] = PROPS.format(
        props=f"    <{SWITCH}>on</{SWITCH}>",
        refs=f'    <PackageReference Include="{ANALYZER}" />',
    )
    root = fixture(files)
    _, found = analyse(root, table_for([family()]))
    check(
        "a family without the switch is reported",
        "NO-CONDITION" in rules(found),
        shown(found),
    )
    shutil.rmtree(root)

    # 4. UNDECLARED-ANALYZER: a package that looks like an analyzer is
    #    referenced and is not in the table.
    files = clean_tree()
    files["src/Directory.Build.props"] = PROPS.format(
        props=f"    <{SWITCH}>on</{SWITCH}>",
        refs='    <PackageReference Include="Brand.New.Analyzers" />',
    )
    root = fixture(files)
    _, found = analyse(root, table_for([family()]))
    check(
        "an analyzer package that is not in the table is reported",
        "UNDECLARED-ANALYZER" in rules(found),
        shown(found),
    )
    shutil.rmtree(root)

    # 5. MISSING-PIN: the family has no PackageVersion.
    files = clean_tree()
    files["Directory.Packages.props"] = PROPS.format(props="", refs="")
    root = fixture(files)
    _, found = analyse(root, table_for([family()]))
    check(
        "a family without a version pin is reported",
        "MISSING-PIN" in rules(found),
        shown(found),
    )
    shutil.rmtree(root)

    # 6. ORPHAN-SWITCH: a scope file switches a property no family owns.
    files = clean_tree()
    files["src/Directory.Build.props"] = PROPS.format(
        props="    <HarborAnalyzerNobody>off</HarborAnalyzerNobody>", refs=""
    )
    root = fixture(files)
    _, found = analyse(root, table_for([family()]))
    check(
        "a switch that no family owns is reported",
        "ORPHAN-SWITCH" in rules(found),
        shown(found),
    )
    shutil.rmtree(root)

    # 7. STALE-ROW: the table names a family that is not in the tree.
    files = clean_tree()
    root = fixture(files)
    _, found = analyse(
        root,
        table_for([family(), family(identifier="Absent.Analyzers", prop="HarborAnalyzerAbsent")]),
    )
    check(
        "a table row with no declaration is reported",
        "STALE-ROW" in rules(found),
        shown(found),
    )
    shutil.rmtree(root)

    # 8. THE FALSE-POSITIVE CONTROL: a NON-analyzer package that mentions an
    #    analyzer in its id is not an analyzer.
    files = clean_tree()
    files["src/Directory.Build.props"] = PROPS.format(
        props=f"    <{SWITCH}>on</{SWITCH}>",
        refs='    <PackageReference Include="Newtonsoft.Json" />',
    )
    root = fixture(files)
    _, found = analyse(root, table_for([family()]))
    check(
        "a package that is not an analyzer is not reported",
        "UNDECLARED-ANALYZER" not in rules(found),
        shown(found),
    )
    shutil.rmtree(root)

    # 9. THE FALSE-POSITIVE CONTROL: a comment that NAMES an analyzer
    #    package is not a declaration.
    files = clean_tree()
    files["src/Directory.Build.props"] = PROPS.format(
        props=f"    <{SWITCH}>on</{SWITCH}>",
        refs=(
            "    <!-- Roslynator.Analyzers is switched off here on purpose. -->\n"
        ),
    )
    root = fixture(files)
    _, found = analyse(root, table_for([family()]))
    check(
        "an analyzer package named in a comment is not reported",
        "UNDECLARED-ANALYZER" not in rules(found),
        shown(found),
    )
    shutil.rmtree(root)

    # 10. The finding carries the line number of the FILE, not of the
    #     comment-masked copy.
    body = (
        "    <!-- Roslynator.Analyzers -->\n"
        '    <PackageReference Include="Brand.New.Analyzers" />\n'
    )
    files = clean_tree()
    files["src/Directory.Build.props"] = PROPS.format(
        props=f"    <{SWITCH}>on</{SWITCH}>", refs=body
    )
    root = fixture(files)
    expected_line = PROPS.format(props="", refs=body).split("\n").index(
        '    <PackageReference Include="Brand.New.Analyzers" />'
    ) + 1
    _, found = analyse(root, table_for([family()]))
    check(
        "the finding carries the file's line number",
        any(f.rule == "UNDECLARED-ANALYZER" and f.line == expected_line for f in found),
        shown(found) + f" (expected line {expected_line})",
    )
    shutil.rmtree(root)

    # 11. The gate must not be able to count itself: this file lives under
    #     tools/, and the scope is the project files.
    here = os.path.realpath(__file__)
    check(
        "the gate's own source is outside the scope",
        not here.endswith(".csproj"),
        here,
    )

    if failures:
        print(
            f"\nself-test FAILED: {len(failures)} of {len(ran)} fixture(s): "
            + ", ".join(failures)
        )
        return 1
    print(f"\nself-test passed: {len(ran)} fixtures")
    return 0


# ---------------------------------------------------------------------------


def main():
    parser = argparse.ArgumentParser(
        description=__doc__.split("\n")[0],
        formatter_class=argparse.RawDescriptionHelpFormatter,
    )
    parser.add_argument("--root", default=DEFAULT_ROOT, help="repository root")
    parser.add_argument(
        "--table", default=DEFAULT_TABLE, help="the scope table (analyzer-scope.json)"
    )
    parser.add_argument(
        "--report", action="store_true", help="print the derived census and exit 0"
    )
    parser.add_argument(
        "--min-files",
        type=int,
        default=0,
        help="scan floor: fail unless at least this many project files were scanned",
    )
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()

    if args.self_test:
        return selftest()

    root = os.path.abspath(args.root)
    table = load_table(args.table)
    census, findings = analyse(root, table)

    if args.report:
        print(f"project files scanned: {census['files']}")
        print(f"families in the table: {census['families']}")
        print(f"trees in the table: {census['scopes']}")
        if findings:
            print("\nfindings:")
            for finding in findings:
                print(f"  {finding}")
        return 0

    breached = []
    if args.min_files and census["files"] < args.min_files:
        breached.append(("project files", args.min_files, census["files"]))

    if findings:
        print("Analyzer-scope gate — findings:\n")
        for finding in findings:
            print(f"  {finding}")
        print()
        print(
            "  Every analyzer package runs inside the compiler of every project that\n"
            "  references it, and a severity table cannot switch that off: a rule at\n"
            "  'severity = none' is silenced while its analyzer keeps running. The\n"
            "  scope is the only lever that removes the cost.\n"
            "\n"
            "  Fix: change tools/analyzer-scope.json AND the props file that declares\n"
            "  the family, so the two agree. A family that should not run in a tree\n"
            "  is switched off there:\n"
            "\n"
            "    <PropertyGroup>\n"
            "      <HarborAnalyzerTheFamily>off</HarborAnalyzerTheFamily>\n"
            "    </PropertyGroup>\n"
            "\n"
            "  before the <Import Project=\"../Directory.Build.props\"/>.\n"
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
        f"{census['families']} analyzer families, every scope agrees with "
        "tools/analyzer-scope.json"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
