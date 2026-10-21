#!/usr/bin/env python3
"""Generate the plugin inventory: the machine-readable snapshot of the plugin composition.

Stdlib only - no pip, no network, no dotnet. Reads the C# sources and the
sample plugins as FILES and writes `docs/PLUGIN_INVENTORY.json`.

WHY THIS FILE EXISTS (issue #1722)
---------------------------------
The plugin composition of Harbor was spread over three places, and none of them
could be read by a program:

  * `PluginAxisRegistry` (src/Harbor.Plugins.Abstractions/) declares WHICH
    axes exist - the marker/door pairs;
  * `PluginRouteAxisRegistry` (src/Harbor.Plugins.Hosting/) declares what each
    ROUTE does with each axis, and why (#1723);
  * the live state - which plugin loaded, which is pending, which was refused
    and why - lived only in the host's log lines.

So the three questions a user actually asks ("did my plugin compile?", "did it
register?", "is its tool visible?") had three answers in three shapes, and a
machine could not ask any of them. This file is the fourth shape: ONE JSON
document that answers all three, and a gate that fails when the committed copy
no longer matches the tree.

WHAT IS IN THE DOCUMENT
-----------------------
  * `axes`   - the marker/door pairs, read out of `PluginAxisRegistry`.
  * `routes` - the per-route verdict and reason, read out of
    `PluginRouteAxisRegistry`.
  * `plugins` - the plugins the REPOSITORY ships (the samples), with the axes
    each one carries, its version, and whether it ships a manifest.

The runtime half of the same format is produced by the product
(`src/Harbor.Plugins.Hosting/PluginInventory.cs`): the live composition, with
the phase of every plugin and what it registered in the host. Same schema, two
producers - and this file checks that the two producers agree on the schema,
so a member renamed in the product is a red gate here rather than a consumer
that reads a field that is not there any more.

THE SCHEMA IS DECLARED IN THE PRODUCT, AND CHECKED HERE
-------------------------------------------------------
The member names are read out of the C# writer (`PluginInventory.cs`) and
compared with the ones this generator writes. The product owns the format;
this file mirrors it; the comparison keeps the mirror honest. A generator
that decided the format on its own would be a second declaration of it, and
the second declaration is the one that drifts.

WHAT IS NOT HERE, AND SAID SO IN THE OUTPUT
-------------------------------------------
The document says nothing about a plugin's PHASE beyond `declared`: a plugin
in the repository is not loaded, so there is no phase to report. The live
phases - `pending`, `active`, `failed` - belong to the runtime document.

Usage:
  ./tools/gen-plugin-inventory.py --write        # regenerate docs/PLUGIN_INVENTORY.json
  ./tools/gen-plugin-inventory.py --check        # fail if the committed file is stale
  ./tools/gen-plugin-inventory.py --report       # print the summary to stdout
  ./tools/gen-plugin-inventory.py --self-test    # prove the checker can fail

  ./tools/gen-plugin-inventory.py --check --min-axes 7 --min-routes 7 --min-plugins 6

The floors are the anti-vacuity pair: a scan that stopped covering the tree, or
a scanner that stopped matching declarations, would produce a document that
agrees with itself and says nothing.

Exit codes:  0 = clean, 1 = stale document (or a failed self-test), 2 = bad usage.
"""

from __future__ import annotations

import argparse
import contextlib
import io
import json
import os
import re
import sys
import tempfile
from dataclasses import dataclass, field
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_OUT = REPO_ROOT / "docs" / "PLUGIN_INVENTORY.json"

AXIS_REGISTRY = "src/Harbor.Plugins.Abstractions/PluginAxisRegistry.cs"
ROUTE_AXIS_REGISTRY = "src/Harbor.Plugins.Hosting/PluginRouteAxisRegistry.cs"
INVENTORY_WRITER = "src/Harbor.Plugins.Hosting/PluginInventory.cs"

# The trees the plugin catalogue is read from. `contrib/` is deliberately NOT
# here: it is not compiled by CI and the owner marked it unmaintained, so a
# plugin that only exists there is not a plugin of this product.
PLUGIN_TREES = ("samples/plugins-cs", "samples/plugins")

# The schema version this generator writes. It is the product's constant; the
# two must agree, and the check below is what says so.
SCHEMA_VERSION = 1

# The phase of a plugin that is in the repository and nowhere else. The live
# phases are `pending`, `active` and `failed`, and they belong to the runtime
# document.
DECLARED_PHASE = "declared"

# --------------------------------------------------------------------------
# 1. Reading the registries
# --------------------------------------------------------------------------

# The axis registry row is
#   new("IToolPlugin", nameof(IPluginLoadHost.RegisterTool)),
AXIS_ROW_RE = re.compile(
    r"new\(\s*\"(?P<marker>[A-Za-z_]\w*)\"\s*,\s*nameof\(\s*"
    r"(?:[A-Za-z_]\w*\.)?(?P<door>[A-Za-z_]\w*)\s*\)\s*\)"
)

# A public const string in the route registry.
ROUTE_CONST_RE = re.compile(
    r"public\s+const\s+string\s+(?P<name>[A-Za-z_]\w*)\s*=\s*\"(?P<value>[^\"]*)\"\s*;"
)

# The route registry row is
#   new(McpStdioRouteId, nameof(IPluginLoadHost.RegisterTool),
#       PluginAxisCarriage.Carried, "the reason ..."),
# and the reason is a string literal, sometimes split over several lines and
# concatenated with `+`.
ROUTE_ROW_RE = re.compile(
    r"new\(\s*(?P<route>[A-Za-z_]\w*)\s*,\s*nameof\(\s*"
    r"(?:[A-Za-z_]\w*\s*\.\s*)*(?P<door>[A-Za-z_]\w*)\s*\)\s*,\s*"
    r"PluginAxisCarriage\s*\.\s*(?P<carriage>[A-Za-z_]\w*)\s*,\s*"
    r"(?P<reason>(?:\"(?:[^\"\\]|\\.)*\"\s*(?:\+\s*)?)+)",
    re.DOTALL,
)

STRING_LITERAL_RE = re.compile(r"\"((?:[^\"\\]|\\.)*)\"")


@dataclass
class Axis:
    """One axis, as the product registry declares it."""

    marker: str
    door: str


@dataclass
class RouteRow:
    """One axis on one route, as the route registry declares it."""

    route: str
    door: str
    marker: str
    carriage: str
    reason: str


@dataclass
class TreePlugin:
    """One plugin the repository ships."""

    name: str
    version: str
    source: str
    axes: list[str] = field(default_factory=list)
    dependencies: list[str] = field(default_factory=list)
    manifest: bool = False


def read_axes(repo: Path) -> list[Axis]:
    """The axes the product registry declares, in the order it declares them."""
    path = repo / AXIS_REGISTRY
    if not path.exists():
        return []
    text = path.read_text(encoding="utf-8")
    return [
        Axis(marker=m.group("marker"), door=m.group("door"))
        for m in AXIS_ROW_RE.finditer(text)
    ]


def read_routes(repo: Path, marker_of_door: dict[str, str]) -> list[RouteRow]:
    """The per-route verdicts the product declares, with their reasons."""
    path = repo / ROUTE_AXIS_REGISTRY
    if not path.exists():
        return []

    text = path.read_text(encoding="utf-8")
    constants = {
        m.group("name"): m.group("value") for m in ROUTE_CONST_RE.finditer(text)
    }

    rows: list[RouteRow] = []
    for match in ROUTE_ROW_RE.finditer(text):
        route = constants.get(match.group("route"), match.group("route"))
        door = match.group("door")
        reason = "".join(
            m.group(1) for m in STRING_LITERAL_RE.finditer(match.group("reason"))
        )
        rows.append(
            RouteRow(
                route=route,
                door=door,
                marker=marker_of_door.get(door, door),
                carriage=match.group("carriage"),
                reason=reason,
            )
        )
    return rows


# --------------------------------------------------------------------------
# 2. Reading the tree's plugins
# --------------------------------------------------------------------------

CLASS_RE = re.compile(
    r"\bclass\s+(?P<name>[A-Za-z_]\w*)\s*(?::\s*(?P<bases>[^{]*?))?\s*\{",
    re.DOTALL,
)

STRING_NAME_RE = re.compile(
    r"public\s+string\s+Name\s*=>\s*\"(?P<value>[^\"]*)\"\s*;"
)

VERSION_RE = re.compile(
    r"public\s+Version\s+Version\s*=>\s*(?:new\s*\(\s*(?P<a>\d+)\s*,\s*(?P<b>\d+)\s*"
    r"(?:,\s*(?P<c>\d+)\s*)?\)|new\s+Version\s*\(\s*(?P<a2>\d+)\s*,\s*(?P<b2>\d+)\s*"
    r"(?:,\s*(?P<c2>\d+)\s*)?\))"
)

DEPENDENCIES_RE = re.compile(
    r"public\s+IReadOnlyList<string>\s+Dependencies\s*=>\s*\[(?P<body>[^\]]*)\]",
    re.DOTALL,
)


def _class_body(text: str, start: int) -> str | None:
    """The body of a class, from the opening brace at `start`.

    Braces inside string literals and comments do not count: a verbatim string
    with a `{` in it is a character, not a block. A plugin that documents a
    template with braces would otherwise swallow the rest of the file.
    """
    depth = 0
    i = start
    n = len(text)
    while i < n:
        ch = text[i]
        if ch == "/" and i + 1 < n and text[i + 1] == "/":
            j = text.find("\n", i)
            i = n if j < 0 else j
            continue
        if ch == "/" and i + 1 < n and text[i + 1] == "*":
            j = text.find("*/", i + 2)
            i = n if j < 0 else j + 2
            continue
        if text.startswith('"""', i):
            i = _skip_raw(text, i)
            continue
        if ch == "@" and i + 1 < n and text[i + 1] == '"':
            i = _skip_verbatim(text, i + 1)
            continue
        if ch == '"':
            i = _skip_string(text, i)
            continue
        if ch == "'":
            i = _skip_char(text, i)
            continue
        if ch == "{":
            depth += 1
        elif ch == "}":
            depth -= 1
            if depth == 0:
                return text[start : i + 1]
        i += 1
    return None


def _skip_string(text: str, i: int) -> int:
    """The index just past the regular string literal that starts at `i`."""
    n = len(text)
    j = i + 1
    while j < n:
        ch = text[j]
        if ch == "\\":
            j += 2
            continue
        if ch == '"':
            return j + 1
        j += 1
    return n


def _skip_verbatim(text: str, i: int) -> int:
    """The index just past the verbatim string literal that starts at `i`.

    The opening quote is at `i`; a doubled quote inside is an escaped quote,
    not the end.
    """
    n = len(text)
    j = i + 1
    while j < n:
        if text[j] == '"':
            if j + 1 < n and text[j + 1] == '"':
                j += 2
                continue
            return j + 1
        j += 1
    return n


def _skip_raw(text: str, i: int) -> int:
    """The index just past the raw string literal that starts at `i`."""
    end = text.find('"""', i + 3)
    return len(text) if end < 0 else end + 3


def _skip_char(text: str, i: int) -> int:
    """The index just past the character literal that starts at `i`."""
    n = len(text)
    j = i + 1
    while j < n:
        ch = text[j]
        if ch == "\\":
            j += 2
            continue
        if ch == "'":
            return j + 1
        j += 1
    return n


def _blank_comments(text: str) -> str:
    """Blank out the comments, keeping the length and the line structure.

    The scanner is string-aware: a `//` inside a string literal is not a
    comment, and a comment that mentions a class declaration is not a
    declaration. Blanking keeps every line in place, so a line number computed
    against the blanked text is the line number of the original file.
    """
    out: list[str] = []
    i = 0
    n = len(text)
    while i < n:
        ch = text[i]
        if ch == "/" and i + 1 < n and text[i + 1] == "/":
            j = text.find("\n", i)
            j = n if j < 0 else j
            out.append(" " * (j - i))
            i = j
            continue
        if ch == "/" and i + 1 < n and text[i + 1] == "*":
            j = text.find("*/", i + 2)
            j = n if j < 0 else j + 2
            out.append("".join(c if c == "\n" else " " for c in text[i:j]))
            i = j
            continue
        if text.startswith('"""', i):
            j = _skip_raw(text, i)
            out.append(text[i:j])
            i = j
            continue
        if ch == "@" and i + 1 < n and text[i + 1] == '"':
            j = _skip_verbatim(text, i + 1)
            out.append(text[i:j])
            i = j
            continue
        if ch == '"':
            j = _skip_string(text, i)
            out.append(text[i:j])
            i = j
            continue
        if ch == "'":
            j = _skip_char(text, i)
            out.append(text[i:j])
            i = j
            continue
        out.append(ch)
        i += 1
    return "".join(out)


def read_tree_plugins(repo: Path, axis_order: list[str]) -> list[TreePlugin]:
    """The plugins the repository ships, with the axes each one carries."""
    known = set(axis_order)
    found: list[TreePlugin] = []

    for tree in PLUGIN_TREES:
        base = repo / tree
        if not base.is_dir():
            continue
        for path in sorted(base.rglob("*.cs")):
            rel = str(path.relative_to(repo)).replace(os.sep, "/")
            try:
                raw = path.read_text(encoding="utf-8", errors="replace")
            except OSError:
                continue
            code = _blank_comments(raw)
            for match in CLASS_RE.finditer(code):
                bases = match.group("bases") or ""
                markers = [b for b in split_bases(bases) if b in known]
                if not markers:
                    continue
                body = _class_body(code, match.end() - 1)
                if body is None:
                    continue
                manifest_path = path.parent / (path.stem + ".harbor-plugin.json")
                plugin = _read_plugin(body, rel, markers, axis_order, manifest_path.exists())
                if plugin is not None:
                    found.append(plugin)

    found.sort(key=lambda p: p.source)
    return found


def split_bases(body: str) -> list[str]:
    """The names in a base list, without the generic arguments."""
    out: list[str] = []
    for part in body.split(","):
        name = part.strip().split("<")[0].strip()
        name = name.split(".")[-1].strip()
        if name:
            out.append(name)
    return out


def _read_plugin(
    body: str,
    rel: str,
    markers: list[str],
    axis_order: list[str],
    manifest: bool,
) -> TreePlugin | None:
    """One plugin out of a class body, or None when it does not declare one."""
    name_match = STRING_NAME_RE.search(body)
    if name_match is None:
        return None

    version_match = VERSION_RE.search(body)
    if version_match is None:
        return None
    version = _format_version(version_match)

    # The axes are reported in the order the registry declares them, so the
    # tree's answer and the runtime's answer are the same list.
    order = {marker: index for index, marker in enumerate(axis_order)}
    axes = sorted(markers, key=lambda m: order.get(m, len(order)))

    dependencies = _read_dependencies(body, rel)

    return TreePlugin(
        name=name_match.group("value"),
        version=version,
        source=rel,
        axes=axes,
        dependencies=dependencies,
        manifest=manifest,
    )


def _format_version(match: re.Match[str]) -> str:
    major = match.group("a") or match.group("a2")
    minor = match.group("b") or match.group("b2")
    build = match.group("c") or match.group("c2")
    if build is None:
        return f"{major}.{minor}"
    return f"{major}.{minor}.{build}"


def _read_dependencies(body: str, rel: str) -> list[str]:
    """The dependencies a plugin declares, or a loud failure when it cannot be read.

    The generator does not guess. A plugin that declares dependencies and a
    generator that cannot read them would produce a document that says "this
    plugin needs nothing" - a claim about a plugin the generator never made.
    """
    match = DEPENDENCIES_RE.search(body)
    if match is None:
        if "Dependencies" in body:
            raise ValueError(
                f"{rel}: the plugin declares 'Dependencies' in a form this generator "
                "cannot read. The generator refuses to claim that such a plugin "
                "needs nothing - teach it the form, or the document is a lie."
            )
        return []
    return [
        m.group(1) for m in STRING_LITERAL_RE.finditer(match.group("body"))
    ]


# --------------------------------------------------------------------------
# 3. The schema the product owns
# --------------------------------------------------------------------------

# A member name the C# writer writes: `writer.WriteString("name", ...)`,
# `writer.WriteStartArray("axes")`, or the helper
# `WriteStringArray(writer, "axes", ...)`.
WRITE_MEMBER_RE = re.compile(
    r"\bWrite[A-Za-z]*\s*\(\s*(?:writer\s*,\s*)?\"(?P<name>[A-Za-z_][A-Za-z0-9_]*)\""
)


def read_product_members(repo: Path) -> set[str]:
    """The member names the product's writer writes."""
    path = repo / INVENTORY_WRITER
    if not path.exists():
        return set()
    text = _blank_comments(path.read_text(encoding="utf-8"))
    return {m.group("name") for m in WRITE_MEMBER_RE.finditer(text)}


# A call that opens an array in the product's writer.
WRITE_ARRAY_RE = re.compile(r"\bWrite[A-Za-z]*\s*\(\s*\"(?P<name>[A-Za-z_][A-Za-z0-9_]*)\"")


def read_array_members(repo: Path, array: str) -> set[str]:
    """The member names the product's writer writes INSIDE one array.

    An array the tree document leaves empty carries no members of its own —
    there is nothing in it to name. The members of its ELEMENTS are still part
    of the schema, and they are read here so the schema check can tell "the
    document has no registrations" apart from "the product renamed a member".
    """
    path = repo / INVENTORY_WRITER
    if not path.exists():
        return set()
    text = _blank_comments(path.read_text(encoding="utf-8"))

    start = text.find(f'WriteStartArray("{array}")')
    if start < 0:
        return set()
    end = text.find("WriteEndArray()", start)
    if end < 0:
        return set()

    region = text[start:end]
    return {m.group("name") for m in WRITE_MEMBER_RE.finditer(region)}


def empty_arrays(document: dict) -> set[str]:
    """The names of the arrays the document leaves empty."""
    names: set[str] = set()

    def walk(node: object) -> None:
        if isinstance(node, dict):
            for key, value in node.items():
                if isinstance(value, list) and not value:
                    names.add(key)
                walk(value)
        elif isinstance(node, list):
            for item in node:
                walk(item)

    walk(document)
    return names


# --------------------------------------------------------------------------
# 4. The document
# --------------------------------------------------------------------------


def build_document(
    axes: list[Axis],
    routes: list[RouteRow],
    plugins: list[TreePlugin],
) -> dict:
    """The snapshot, as the data the JSON writer renders."""
    return {
        "schemaVersion": SCHEMA_VERSION,
        "route": None,
        "axes": [{"marker": a.marker, "door": a.door} for a in axes],
        "routes": [
            {
                "route": r.route,
                "door": r.door,
                "marker": r.marker,
                "carriage": r.carriage,
                "reason": r.reason,
            }
            for r in routes
        ],
        "plugins": [
            {
                "name": p.name,
                "version": p.version,
                "source": p.source,
                "phase": DECLARED_PHASE,
                "axes": list(p.axes),
                "dependencies": list(p.dependencies),
                "unsatisfiedDependencies": [],
                "manifest": p.manifest,
                "registrations": [],
                "failureReason": None,
            }
            for p in plugins
        ],
    }


def render_document(document: dict) -> str:
    """The document, exactly as it is committed.

    No commit stamp: a stamp would make the file differ from its own
    regeneration on every commit, and the check would then fail on the commit
    that regenerated it. The provenance is the file's own history.
    """
    return json.dumps(document, indent=2, ensure_ascii=False) + "\n"


def members_of(document: dict) -> set[str]:
    """Every member name the document carries, at any level."""
    names: set[str] = set()

    def walk(node: object) -> None:
        if isinstance(node, dict):
            for key, value in node.items():
                names.add(key)
                walk(value)
        elif isinstance(node, list):
            for item in node:
                walk(item)

    walk(document)
    return names


# --------------------------------------------------------------------------
# 5. Commands
# --------------------------------------------------------------------------


def collect(repo: Path) -> tuple[dict, set[str]]:
    """The document the tree declares, and the member names the product writes."""
    axes = read_axes(repo)
    marker_of_door = {a.door: a.marker for a in axes}
    routes = read_routes(repo, marker_of_door)
    plugins = read_tree_plugins(repo, [a.marker for a in axes])
    document = build_document(axes, routes, plugins)
    return document, read_product_members(repo)


def write_output(repo: Path, out: Path) -> int:
    document, _ = collect(repo)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(render_document(document), encoding="utf-8")
    print(f"wrote {out.relative_to(repo) if out.is_relative_to(repo) else out}")
    return 0


def check_output(
    repo: Path,
    out: Path,
    min_axes: int = 0,
    min_routes: int = 0,
    min_plugins: int = 0,
) -> int:
    if not out.exists():
        print(f"FAIL: {out} does not exist; run --write")
        return 1

    document, product_members = collect(repo)
    axes = document["axes"]
    routes = document["routes"]
    plugins = document["plugins"]

    if len(axes) < min_axes:
        print(
            f"FAIL: only {len(axes)} axes were read, floor is {min_axes}. A scanner "
            "that stopped matching the registry would produce a document that "
            "agrees with itself and says nothing."
        )
        return 1
    if len(routes) < min_routes:
        print(
            f"FAIL: only {len(routes)} route verdicts were read, floor is "
            f"{min_routes}. A scanner that stopped matching the route registry "
            "would produce a document that agrees with itself and says nothing."
        )
        return 1
    if len(plugins) < min_plugins:
        print(
            f"FAIL: only {len(plugins)} tree plugins were read, floor is "
            f"{min_plugins}. A scan that stopped covering the sample tree would "
            "produce a document that agrees with itself and says nothing."
        )
        return 1

    # The product owns the schema. This generator mirrors it, and a member the
    # product renamed is a member the consumers of this document would not
    # find any more.
    if product_members:
        document_members = members_of(document)
        missing = sorted(document_members - product_members)
        if missing:
            print(
                "FAIL: the document carries member(s) the product's writer does "
                f"not write: {', '.join(missing)}. The product owns the format; "
                "update the writer, then regenerate."
            )
            return 1

        # A member the product writes INSIDE an array the document leaves empty
        # is not a member the document is missing: there is nothing in the
        # array to name. The tree declares plugins; it does not run them.
        exempt: set[str] = set()
        for array in empty_arrays(document):
            exempt |= read_array_members(repo, array)

        extra = sorted(product_members - document_members - exempt)
        if extra:
            print(
                "FAIL: the product's writer writes member(s) this document does "
                f"not carry: {', '.join(extra)}. The two producers must agree on "
                "the schema; update this generator, then regenerate."
            )
            return 1

    expected = render_document(document)
    actual = out.read_text(encoding="utf-8")
    if expected == actual:
        print(
            f"OK: {out.name} matches the tree "
            f"({len(axes)} axes, {len(routes)} route verdicts, {len(plugins)} plugins)"
        )
        return 0

    print(f"FAIL: {out.name} is stale: the tree changed since it was generated.")
    exp_lines = expected.splitlines()
    act_lines = actual.splitlines()
    for i, (e, a) in enumerate(zip(exp_lines, act_lines), start=1):
        if e != a:
            print(f"  first difference at line {i}:")
            print(f"    committed: {a}")
            print(f"    generated: {e}")
            break
    else:
        print(
            f"  line count differs: committed {len(act_lines)}, generated {len(exp_lines)}"
        )
    print("Run ./tools/gen-plugin-inventory.py --write and commit the result.")
    return 1


def report(repo: Path) -> int:
    document, product_members = collect(repo)
    print(f"axes           : {len(document['axes'])}")
    for axis in document["axes"]:
        print(f"  {axis['marker']:<22} {axis['door']:<22}")
    print("")
    print("route verdicts (the product's declaration):")
    for route in document["routes"]:
        print(
            f"  {route['route']:<12} {route['door']:<24} "
            f"{route['carriage']:<11} {route['marker']}"
        )
    print("")
    print("plugins the tree ships:")
    for plugin in document["plugins"]:
        axes = ", ".join(plugin["axes"]) or "(none)"
        print(
            f"  {plugin['name']:<22} {plugin['version']:<10} {axes:<32} "
            f"{plugin['source']}"
        )
    print("")
    print(f"members the product writes : {len(product_members)}")
    print(f"members the document carries: {len(members_of(document))}")
    return 0


def self_test(repo: Path) -> int:
    """Prove the checker can fail.

    Two fixtures: a tree whose document differs from the committed file must be
    reported stale, and a tree whose document matches must be reported clean. A
    checker that cannot say no is a checker that enforces nothing.
    """
    with tempfile.TemporaryDirectory() as tmp:
        fixture = Path(tmp) / "fixture"
        _write_fixture(fixture)

        problems: list[str] = []

        document, product_members = collect(fixture)
        axes = document["axes"]
        routes = document["routes"]
        plugins = document["plugins"]

        if len(axes) != 1 or axes[0]["marker"] != "IDemoPlugin":
            problems.append(
                f"the fixture's axis was not read: {axes}"
            )
        if len(routes) != 1:
            problems.append(
                f"the fixture's route verdicts were not read: {routes}"
            )
        elif routes[0]["carriage"] != "Carried":
            problems.append(
                f"the fixture's verdict was not read: {routes[0]}"
            )
        elif routes[0]["reason"] != "the demo route carries the demo axis":
            problems.append(
                f"the fixture's reason was not read: {routes[0]['reason']!r}"
            )
        if len(plugins) != 1:
            problems.append(f"the fixture's plugin was not read: {plugins}")
        elif plugins[0]["name"] != "demo-plugin" or plugins[0]["axes"] != ["IDemoPlugin"]:
            problems.append(f"the fixture's plugin was misread: {plugins[0]}")
        elif plugins[0]["phase"] != DECLARED_PHASE:
            problems.append(f"the fixture's phase was misread: {plugins[0]['phase']}")

        # The schema check: a product writer that renames a member has to be
        # reported, because the document would carry a member nobody writes.
        if not product_members:
            problems.append("the fixture's writer members were not read")

        # The stale-file half: the committed file must NOT match the tree.
        stale_file = fixture / "STALE.json"
        stale_file.write_text("{}\n", encoding="utf-8")
        with contextlib.redirect_stdout(io.StringIO()) as captured:
            stale = check_output(fixture, stale_file)
        if stale != 1:
            problems.append(f"the stale check did not fail: exit {stale}")
        if "stale" not in captured.getvalue():
            problems.append(
                "the stale check did not say WHAT was stale: "
                f"{captured.getvalue()!r}"
            )

        # The clean half: a file the generator produced must pass.
        fresh = fixture / "FRESH.json"
        fresh.write_text(render_document(document), encoding="utf-8")
        with contextlib.redirect_stdout(io.StringIO()):
            clean = check_output(fixture, fresh)
        if clean != 0:
            problems.append(f"the fresh check did not pass: exit {clean}")

        # The schema half: a writer that writes a member the document does not
        # carry has to be reported.
        writer = fixture / INVENTORY_WRITER
        planted = writer.read_text(encoding="utf-8").replace(
            '        writer.WriteNull("failureReason");\n',
            '        writer.WriteNull("failureReason");\n'
            '        writer.WriteString("invented", "x");\n',
        )
        writer.write_text(planted, encoding="utf-8")
        with contextlib.redirect_stdout(io.StringIO()) as captured:
            diverged = check_output(fixture, fresh)
        if diverged != 1:
            problems.append(f"the schema check did not fail: exit {diverged}")
        if "schema" not in captured.getvalue() and "member" not in captured.getvalue():
            problems.append(
                "the schema check did not say WHAT diverged: "
                f"{captured.getvalue()!r}"
            )
        writer.write_text(
            planted.replace(
                '        writer.WriteString("invented", "x");\n', ""
            ),
            encoding="utf-8",
        )

        if problems:
            for problem in problems:
                print(f"FAIL: {problem}")
            return 1
        print(
            "OK: the self-test passed: the scanner reads the fixture's axes, "
            "route verdicts and plugins, the stale file is reported stale, the "
            "fresh file passes, and a schema divergence is reported"
        )
        return 0


def _write_fixture(fixture: Path) -> None:
    """A tree with one axis, one route and one plugin."""
    abstractions = fixture / "src" / "Harbor.Plugins.Abstractions"
    abstractions.mkdir(parents=True)
    (abstractions / "PluginAxisRegistry.cs").write_text(
        "namespace Harbor.Plugins.Abstractions;\n\n"
        "public static class PluginAxisRegistry\n{\n"
        "    public static readonly object[] All =\n"
        "    [\n"
        "        new(\"IDemoPlugin\", nameof(IPluginLoadHost.RegisterDemo)),\n"
        "    ];\n}\n",
        encoding="utf-8",
    )

    hosting = fixture / "src" / "Harbor.Plugins.Hosting"
    hosting.mkdir(parents=True)
    (hosting / "PluginRouteAxisRegistry.cs").write_text(
        "namespace Harbor.Plugins.Hosting;\n\n"
        "public enum PluginAxisCarriage\n{\n"
        "    Carried,\n"
        "    Refused,\n"
        "    ClosedSeam,\n}\n\n"
        "public static class PluginRouteAxisRegistry\n{\n"
        "    public const string DemoRouteId = \"demo\";\n"
        "    public const string DemoHostType = \"DemoHost\";\n\n"
        "    public static readonly object[] All =\n"
        "    [\n"
        "        new(\n"
        "            DemoRouteId,\n"
        "            nameof(Harbor.Plugins.Abstractions.IPluginLoadHost.RegisterDemo),\n"
        "            PluginAxisCarriage.Carried,\n"
        "            \"the demo route carries the demo axis\"),\n"
        "    ];\n}\n",
        encoding="utf-8",
    )

    writer = fixture / INVENTORY_WRITER
    writer.parent.mkdir(parents=True, exist_ok=True)
    writer.write_text(
        "namespace Harbor.Plugins.Hosting;\n\n"
        "public sealed class PluginInventory\n{\n"
        "    public string ToJson()\n"
        "    {\n"
        "        writer.WriteNumber(\"schemaVersion\", 1);\n"
        "        writer.WriteNull(\"route\");\n"
        "        writer.WriteStartArray(\"axes\");\n"
        "        writer.WriteStartArray(\"routes\");\n"
        "        writer.WriteStartArray(\"plugins\");\n"
        "        WriteStringArray(writer, \"axes\", axes);\n"
        "        WriteStringArray(writer, \"dependencies\", dependencies);\n"
        "        WriteStringArray(writer, \"unsatisfiedDependencies\", unsatisfied);\n"
        "        writer.WriteString(\"name\", name);\n"
        "        writer.WriteString(\"version\", version);\n"
        "        writer.WriteString(\"source\", source);\n"
        "        writer.WriteString(\"phase\", phase);\n"
        "        writer.WriteBoolean(\"manifest\", manifest);\n"
        "        writer.WriteStartArray(\"registrations\");\n"
        "        writer.WriteString(\"door\", door);\n"
        "        writer.WriteString(\"subject\", subject);\n"
        "        writer.WriteString(\"outcome\", outcome);\n"
        "        writer.WriteNull(\"reason\");\n"
        "        writer.WriteEndArray();\n"
        "        writer.WriteNull(\"failureReason\");\n"
        "        writer.WriteString(\"marker\", marker);\n"
        "        writer.WriteString(\"carriage\", carriage);\n"
        "        return text;\n"
        "    }\n}\n",
        encoding="utf-8",
    )

    samples = fixture / "samples" / "plugins-cs"
    samples.mkdir(parents=True)
    (samples / "DemoPlugin.cs").write_text(
        "// A demo plugin.\n"
        "public sealed class DemoPlugin : IDemoPlugin\n"
        "{\n"
        "    public string Name => \"demo-plugin\";\n"
        "    public Version Version => new(1, 2, 3);\n"
        "}\n",
        encoding="utf-8",
    )


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--repo", default=str(REPO_ROOT), help="repository root")
    ap.add_argument("--out", default=str(DEFAULT_OUT), help="output document")
    ap.add_argument("--write", action="store_true", help="regenerate the document")
    ap.add_argument("--check", action="store_true", help="fail if the document is stale")
    ap.add_argument("--report", action="store_true", help="print the numbers")
    ap.add_argument("--self-test", action="store_true", help="prove the checker can fail")
    ap.add_argument(
        "--min-axes",
        type=int,
        default=0,
        help="fail unless at least this many axes were read (0 = off)",
    )
    ap.add_argument(
        "--min-routes",
        type=int,
        default=0,
        help="fail unless at least this many route verdicts were read (0 = off)",
    )
    ap.add_argument(
        "--min-plugins",
        type=int,
        default=0,
        help="fail unless at least this many tree plugins were read (0 = off)",
    )
    args = ap.parse_args()

    repo = Path(args.repo).resolve()
    out = Path(args.out).resolve()

    if args.self_test:
        return self_test(repo)
    if args.write:
        return write_output(repo, out)
    if args.check:
        return check_output(repo, out, args.min_axes, args.min_routes, args.min_plugins)
    if args.report:
        return report(repo)
    ap.print_help()
    return 2


if __name__ == "__main__":
    sys.exit(main())
