#!/usr/bin/env python3
"""Generate the seam graph: who implements a seam, who extends it, who consumes it.

Stdlib only - no pip, no network, no dotnet. Reads the C# sources and the
.csproj files as FILES and writes `docs/SEAM_GRAPH.md`.

WHY THIS FILE EXISTS (issue #1731)
----------------------------------
The repository has exactly one machine-readable list of its extension axes:
`src/Harbor.Plugins.Abstractions/PluginAxisRegistry.cs` (the successor to
#620's frozen axis set, after #1719 lifted the freeze). It names the seven
MARKER/door pairs. What it does NOT name, and what no document in the tree
names, is the graph around them:

  * who IMPLEMENTS each marker - in the product, in the samples, in the tests;
  * who CONSUMES a seam - which projects reach for the interface;
  * who BYPASSES a seam - where the concrete type is used where the interface
    was the contract.

The per-route fate of each axis is declared in a second registry,
`src/Harbor.Plugins.Hosting/PluginRouteAxisRegistry.cs` (#1723): what a route
does with each axis, and why. This tool READS that registry for the verdict
and the reason, and then reads the route's own door body and prints what it
does NEXT TO what the product declares. The difference between the two is the
defect the architecture rule grades, and it is the reason the graph is worth
reading rather than a copy of a table.

WHAT IS EXTRACTED, AND HOW
--------------------------
  * Type declarations - `class`/`record`/`struct`/`interface`/`enum` with
    their base lists. Comments and string literals are blanked first, so a
    type named in a doc comment is not a declaration.
  * IMPLEMENTS edges - a base list entry that resolves to a known interface.
  * EXTENDS edges - a base list entry that resolves to a known class. The
    transitive closure of the class edges is what turns
    `class RecordingHost : IPluginLoadHost` into an implementation even when
    the interface is only reached through a base class.
  * CONSUMERS - files under src/, apps/, tests/ and samples/ that NAME the
    interface. A file that only puts the name in a base list is an
    implementation, not a consumer, and is counted as such. The build
    output (`bin/`, `obj/`) is not part of the tree and is not read: a
    working tree that still holds a build would otherwise count the
    GENERATED sources as sources, and the document would say something
    the tree does not say.
  * REGISTRATION - the `AddSingleton<IFoo, Foo>()` / `AddSingleton<IFoo>()`
    calls in src/ and apps/. This is the container side of the graph.
  * BYPASSES - `new Concrete(...)`, and a parameter or field typed as the
    CONCRETE type where the interface was the contract. Both are read from
    the source text, so they are facts about the tree, not judgements.
  * AXES - the MARKER/door pairs, read out of `PluginAxisRegistry.All` by
    parsing the file.
  * ROUTES - the per-axis verdict and reason, read out of
    `PluginRouteAxisRegistry.All` by parsing the file, plus what each route's
    own door body does: registers, refuses, is silent, mixes the two, or
    forwards to another host.

WHAT IS NOT EXTRACTED, AND SAID SO IN THE OUTPUT
------------------------------------------------
The document lists these in its own last section, because a graph that hides
its own blind spots is a decoration. In short: DI registrations written
without a type argument (`services.AddSingleton(sp => new Foo())`), generic
base arguments, extension-method seams, seams reached only through
reflection, and anything the C# compiler would resolve that a text scan
cannot (a type forwarded from another assembly, an alias).

Usage:
  ./tools/gen-seam-graph.py --write        # regenerate docs/SEAM_GRAPH.md
  ./tools/gen-seam-graph.py --check        # fail if the committed file is stale
  ./tools/gen-seam-graph.py --report       # print the summary to stdout
  ./tools/gen-seam-graph.py --self-test    # prove the checker can fail

  ./tools/gen-seam-graph.py --check --min-files 2400 --min-interfaces 180

The two floors are the anti-vacuity pair: a scan that stopped covering the
tree, or a scanner that stopped matching declarations, would produce a
document that agrees with itself and says nothing.

Exit codes:  0 = clean, 1 = stale graph (or a failed self-test), 2 = bad usage.
"""

from __future__ import annotations

import argparse
import bisect
import contextlib
import io
import os
import re
import sys
import tempfile
from dataclasses import dataclass, field
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_OUT = REPO_ROOT / "docs" / "SEAM_GRAPH.md"

# The trees the graph reads. `contrib/` is deliberately NOT here: it is not
# compiled by CI and the owner marked it unmaintained, so a seam that only
# exists there is not a seam of this product.
CONSUMER_TREES = ("src", "apps", "tests", "samples")

AXIS_REGISTRY = "src/Harbor.Plugins.Abstractions/PluginAxisRegistry.cs"
ROUTE_AXIS_REGISTRY = "src/Harbor.Plugins.Hosting/PluginRouteAxisRegistry.cs"
LOAD_HOST_INTERFACE = "src/Harbor.Plugins.Abstractions/IPluginLoadHost.cs"

# How many seams the census table shows. The full set is in `--report`.
TOP_SEAMS = 40

# The constructs that are blanked out before the tree is read. The order
# matters: the raw string and the verbatim string come before the ordinary
# string, so `"""` and `@"` are consumed whole.
TOKEN_RE = re.compile(
    r"//[^\n]*"
    r"|/\*.*?\*/"
    r'|@"(?:[^"]|"")*"'
    r'|"\"\"".*?"\"\"\"'
    r'|"(?:\\.|[^"\\\n])*"'
    r"|'(?:\\.|[^'\\\n])*'",
    re.DOTALL,
)

NEWLINE_RE = re.compile(r"\n")

# A type declaration. The keyword is found anywhere and the PREFIX of the
# line is checked afterwards, because the alternative - one regex that also
# matches the modifiers - backtracks on every line of the tree and the tree
# is 2 450 files.
TYPE_KEYWORD_RE = re.compile(
    r"\b(?:record\s+(?:class|struct)|class|interface|struct|enum|record)[ \t]+"
    r"(?P<name>[A-Za-z_]\w*)"
)

# The modifiers a declaration may carry before its keyword. Matched with
# `pattern.match(code, line_start, match.start())`, so the `^` is implied by
# the position argument and must not be written out: without re.M, `^` only
# matches at offset zero of the whole file.
DECL_PREFIX_RE = re.compile(
    r"[ \t]*(?:\[[^\]\n]*\][ \t]*)*"
    r"(?:(?:public|internal|protected|private|sealed|abstract|static|partial|"
    r"readonly|ref|new|unsafe|file|record)[ \t]+)*$"
)

# One pass for the marker dispatch sites and one for the door call sites,
# instead of one per axis.
DISPATCH_RE = re.compile(r"\bis\s+(?P<marker>[A-Z]\w*)\b")
DOOR_CALL_RE = re.compile(r"\.\s*(?P<door>Register\w*)\s*\(")

# The shape of an interface name in this tree: an `I` and a capital.
I_PREFIX_RE = re.compile(r"I[A-Z]\w*")

# A type name followed by a member name - the shape a parameter, a field or a
# local declaration has. ONE pattern for all of them; the set of concrete
# names decides which of them are a bypass.
TYPE_THEN_NAME_RE = re.compile(r"\b(?P<t>[A-Z]\w*)\s+(?P<p>[A-Za-z_]\w*)\b")

CONSTRUCTION_RE = re.compile(r"\bnew\s+(?P<t>[A-Z]\w*)\s*(?:<[^>]*>)?\s*\(")


# --------------------------------------------------------------------------
# 1. Source normalisation
# --------------------------------------------------------------------------


NON_NEWLINE = re.compile(r"[^\n]")


def blank_runs(text: str, start: int, end: int) -> str:
    """Replace a span with spaces, keeping every newline in it.

    Newlines are kept so that a line number computed against the blanked text
    is the line number of the original file.

    The fast path is the common one: a string literal or a line comment has
    no newline in it, and the whole span becomes spaces.
    """
    span = text[start:end]
    if "\n" not in span:
        return " " * len(span)
    return NON_NEWLINE.sub(" ", span)


def strip_comments_and_literals(text: str) -> str:
    """Blank out comments, string literals and char literals.

    The output has the same length and the same line structure as the input,
    so a match found in it has the same line number as in the source.

    XML doc comments are comments, so a type named only in a doc comment is
    not a declaration here - which is the point: the doc graph must be about
    code.

    The scanner is a single alternation, matched left to right, so a construct
    is consumed whole: a `//` inside a string literal is inside a match that
    started at the opening quote, and a `"` inside a comment is inside a match
    that started at the comment. A regex stripper that does not consume whole
    constructs eats the rest of the file on the first mismatch, and the damage
    is invisible in the output.
    """
    out: list[str] = []
    last = 0
    for match in TOKEN_RE.finditer(text):
        start, end = match.span()
        out.append(text[last:start])
        out.append(blank_runs(text[start:end], 0, end - start))
        last = end
    out.append(text[last:])
    return "".join(out)


# --------------------------------------------------------------------------
# 2. The tree
# --------------------------------------------------------------------------


@dataclass
class SourceFile:
    rel: str
    project: str
    raw: str
    code: str  # comments and literals blanked, same length
    line_starts: list[int] = field(default_factory=list)

    def line_of(self, offset: int) -> int:
        """The 1-based line an offset falls on."""
        return bisect.bisect_right(self.line_starts, offset)


@dataclass
class TypeDecl:
    name: str
    kind: str  # class | interface | struct | enum | record
    bases: list[str]
    file: SourceFile
    line: int
    namespace: str
    base_span: tuple[int, int] | None = None  # char offsets of the base list
    sites: list[tuple[str, int]] = field(default_factory=list)

    @property
    def rel(self) -> str:
        return self.file.rel

    @property
    def project(self) -> str:
        return self.file.project


def merge_partials(decls: list[TypeDecl]) -> list[TypeDecl]:
    """Merge the declarations of a `partial` type into one.

    `public sealed partial class JsonlSessionStore : ISessionStore` is
    written in three files, and two of them carry the base list. Without the
    merge the graph reports the same class twice, which reads as two
    implementations and is wrong.
    """
    merged: dict[tuple[str, str], TypeDecl] = {}
    order: list[tuple[str, str]] = []
    for decl in decls:
        key = (decl.namespace, decl.name)
        current = merged.get(key)
        if current is None:
            merged[key] = decl
            order.append(key)
            decl.sites = [(decl.rel, decl.line)]
            continue
        current.sites.append((decl.rel, decl.line))
        for base in decl.bases:
            if base not in current.bases:
                current.bases.append(base)
        if current.base_span is None and decl.base_span is not None:
            current.base_span = decl.base_span
            current.file = decl.file
            current.line = decl.line
        if current.kind == "record" and decl.kind == "class":
            current.kind = "record"
    return [merged[key] for key in order]


@dataclass
class Edge:
    src: str
    dst: str
    kind: str  # implements | extends
    file: str
    line: int


@dataclass
class Registration:
    service: str
    impl: str
    lifetime: str
    file: str
    line: int


@dataclass
class Bypass:
    concrete: str
    seam: str
    form: str  # construction | signature
    file: str
    line: int


@dataclass
class Axis:
    marker: str
    door: str
    marker_impls_product: list[TypeDecl] = field(default_factory=list)
    marker_impls_outside: list[TypeDecl] = field(default_factory=list)
    dispatch_sites: list[tuple[str, int]] = field(default_factory=list)
    door_sites: list[tuple[str, int]] = field(default_factory=list)
    declared: list["RouteAxisRow"] = field(default_factory=list)


@dataclass
class RouteAxisRow:
    """One axis on one route: what the product DECLARES, and what the code DOES.

    The declaration is read from `PluginRouteAxisRegistry` (#1723), the one
    place the product states what a route does with an axis and why. The
    observation is read from the route's own door body. The two are printed
    side by side, because the difference between them is the defect the
    architecture rule grades and the reader of the graph is here to see.
    """

    route: str
    host_type: str
    marker: str
    door: str
    declared: str  # Carried | Refused | ClosedSeam
    reason: str
    observed: str  # REGISTERS | REFUSES | SILENT | MIXED | DELEGATES | MISSING
    observed_note: str

    def agrees(self) -> bool:
        """Does the door do what the declaration says the route does?

        The rule the architecture file grades, in one predicate: a CARRIED
        axis must have a door that answers `Result.Success` and registers
        something; a REFUSED or CLOSED-SEAM axis must have a door that
        answers `Result.Failure` and never answers `Result.Success`.
        """
        if self.declared == "Carried":
            return self.observed == "REGISTERS"
        if self.declared in ("Refused", "ClosedSeam"):
            return self.observed == "REFUSES"
        return False


NAMESPACE_RE = re.compile(r"^[ \t]*namespace[ \t]+(?P<ns>[A-Za-z_][\w.]*)", re.M)

DI_RE = re.compile(
    r"\b(?P<call>TryAdd|Add)(?P<lifetime>Singleton|Scoped|Transient)(?:Keyed)?<"
    r"(?P<args>[^<>]*(?:<[^<>]*>[^<>]*)*)>"
)

AXIS_ROW_RE = re.compile(
    r"new\(\s*\"(?P<marker>[A-Za-z_]\w*)\"\s*,\s*nameof\(\s*"
    r"(?:[A-Za-z_]\w*\.)?(?P<door>[A-Za-z_]\w*)\s*\)\s*\)"
)

# The route registry (#1723): a row is
#   new(McpStdioRouteId, nameof(IPluginLoadHost.RegisterTool),
#       PluginAxisCarriage.Carried, "the reason ..."),
# and the reason is a string literal, sometimes split over several lines and
# concatenated with `+`.
ROUTE_CONST_RE = re.compile(
    r"public\s+const\s+string\s+(?P<name>[A-Za-z_]\w*)\s*=\s*\"(?P<value>[^\"]*)\"\s*;"
)

ROUTE_ROW_RE = re.compile(
    r"new\(\s*(?P<route>[A-Za-z_]\w*)\s*,\s*nameof\(\s*"
    r"(?:[A-Za-z_]\w*\s*\.\s*)*(?P<door>[A-Za-z_]\w*)\s*\)\s*,\s*"
    r"PluginAxisCarriage\s*\.\s*(?P<carriage>[A-Za-z_]\w*)\s*,\s*"
    r"(?P<reason>(?:\"(?:[^\"\\]|\\.)*\"\s*(?:\+\s*)?)+)",
    re.DOTALL,
)

STRING_LITERAL_RE = re.compile(r"\"((?:[^\"\\]|\\.)*)\"")

DOOR_DECL_RE = re.compile(
    r"public[ \t]+Result[ \t]+(?P<door>Register\w*)[ \t]*\(",
)

# A door body that registers something. The shapes are the ones the C# grader
# uses (tests/Harbor.Architecture.Tests/ExtensionAxisDoorHonestyRule.cs), so
# the two agree about what "registers" means.
MEMBER_WRITE_RE = re.compile(r"\bthis\s*\.\s*\w+\s*(?:\?\?|\|\||&&|\+|-|\*|/|%|\^|\|)?=(?!=)")
INDEXER_WRITE_RE = re.compile(r"\b\w+\s*\[[^\]]*\]\s*(?:\?\?|\|\||&&|\+|-|\*|/|%|\^|\|)?=(?!=)")
MUTATING_CALL_RE = re.compile(
    r"\.\s*(?:Add|AddOrUpdate|AddRange|Append|GetOrAdd|Insert|Put|Push|Register|Set|"
    r"SetItem|SetValue|Store|TryAdd|TryEnqueue|Enqueue)\s*\("
)
BARE_CALL_RE = re.compile(r"(?<!new )(?<![.\w])(?P<call>[A-Z]\w*)\s*\(")
FORWARD_CALL_RE = re.compile(r"\.\s*(?:Register\w*)\s*\(")
SUCCESS_RE = re.compile(r"Result\s*\.\s*Success\b")
FAILURE_RE = re.compile(r"Result\s*\.\s*Failure\b")
NON_EFFECT_CALLS = {
    "Log", "LogCritical", "LogDebug", "LogError", "LogInformation", "LogNone",
    "LogTrace", "LogWarning", "Trace", "Debug", "Info", "Information", "Warn",
    "Warning", "Error", "Fatal",
}


# The build output. A source file under `bin/` or `obj/` is not a source of
# the tree: it is what a BUILD of the tree left there, and the generator is
# supposed to describe the tree. Every other tree-scanning tool in this
# directory skips the same two names for the same reason.
BUILD_OUTPUT_DIRS = ("bin", "obj")


def is_build_output(path: Path, repo: Path) -> bool:
    """Is this path inside the build output rather than in the tree?"""
    try:
        parts = path.relative_to(repo).parts
    except ValueError:
        return False
    return any(part in BUILD_OUTPUT_DIRS for part in parts)


def find_project_roots(repo: Path) -> dict[Path, str]:
    """Map every directory that owns a .csproj to that project's name."""
    roots: dict[Path, str] = {}
    for tree in CONSUMER_TREES:
        base = repo / tree
        if not base.is_dir():
            continue
        for csproj in base.rglob("*.csproj"):
            if is_build_output(csproj, repo):
                continue
            roots[csproj.parent] = csproj.stem
    return roots


def project_of(path: Path, roots: dict[Path, str]) -> str:
    """The project a source file belongs to.

    `path` is the FILE, not its directory: the file's own directory is the
    first candidate, and passing the directory instead would look one level
    too high, which silently mislabels every file that sits directly in a
    project root.

    A directory with no .csproj of its own is a LINKED-SOURCE tree (Harbor
    compiles src/Harbor.Storage.Shared/*.cs into two other projects). The
    files are read and the graph counts them, but they are attributed to the
    tree's own name so a reader can see that the project column is not a
    project.
    """
    for parent in path.parents:
        if parent in roots:
            return roots[parent]
    return path.parent.name


def collect_sources(repo: Path, trees: tuple[str, ...]) -> list[SourceFile]:
    roots = find_project_roots(repo)
    files: list[SourceFile] = []
    for tree in trees:
        base = repo / tree
        if not base.is_dir():
            continue
        for path in sorted(base.rglob("*.cs")):
            if is_build_output(path, repo):
                continue
            try:
                raw = path.read_text(encoding="utf-8", errors="replace")
            except OSError:
                continue
            line_starts = [0] + [m.end() for m in NEWLINE_RE.finditer(raw)]
            files.append(
                SourceFile(
                    rel=str(path.relative_to(repo)).replace(os.sep, "/"),
                    project=project_of(path, roots),
                    raw=raw,
                    code=strip_comments_and_literals(raw),
                    line_starts=line_starts,
                )
            )
    return files


# --------------------------------------------------------------------------
# 3. Declarations
# --------------------------------------------------------------------------


def read_bases(code: str, start: int) -> tuple[list[str], tuple[int, int] | None]:
    """The base list of a declaration, starting at the name.

    Walks forward to the `:` that opens the base list, then to the `{` or `;`
    that closes the declaration. Generic arguments are skipped, a `where`
    clause ends the list, and a primary-constructor parameter list is
    skipped.

    The character span of the base list is returned with the names, so that a
    mention of an interface inside a base list can be told apart from a
    mention of it in code.
    """
    i = start
    n = len(code)
    depth = 0
    while i < n:
        ch = code[i]
        if ch in "<([":
            depth += 1
        elif ch in ">)]":
            if depth > 0:
                depth -= 1
        elif depth == 0 and ch == ":":
            break
        elif depth == 0 and ch in "{;":
            return ([], None)
        i += 1
    if i >= n or code[i] != ":":
        return ([], None)

    i += 1
    depth = 0
    body_start = i
    while i < n:
        ch = code[i]
        if ch in "<([":
            depth += 1
        elif ch in ">)]":
            if depth > 0:
                depth -= 1
        elif depth == 0:
            if ch in "{;":
                break
            if ch == ",":
                break
            if code.startswith("where", i) and (i == 0 or not code[i - 1].isalnum()):
                break
        i += 1
    body = code[body_start:i]
    if not body.strip():
        return ([], None)
    return (split_bases(body), (body_start, i))


def split_bases(body: str) -> list[str]:
    """Split a base list on commas that are not inside generic arguments."""
    parts: list[str] = []
    depth = 0
    current: list[str] = []
    for ch in body:
        if ch in "<([":
            depth += 1
        elif ch in ">)]":
            depth = max(0, depth - 1)
        if ch == "," and depth == 0:
            parts.append("".join(current))
            current = []
            continue
        current.append(ch)
    parts.append("".join(current))

    names: list[str] = []
    for part in parts:
        cleaned = strip_generic(part)
        cleaned = cleaned.strip()
        if not cleaned:
            continue
        # A qualified name: take the last segment.
        tail = cleaned.split(".")[-1].strip()
        tail = tail.split(" ")[0].strip()
        if tail:
            names.append(tail)
    return names


def strip_generic(text: str) -> str:
    """Remove `<...>` groups from a type expression."""
    out: list[str] = []
    depth = 0
    for ch in text:
        if ch == "<":
            depth += 1
            continue
        if ch == ">":
            depth = max(0, depth - 1)
            continue
        if depth == 0:
            out.append(ch)
    return "".join(out)


def extract_types(files: list[SourceFile]) -> list[TypeDecl]:
    decls: list[TypeDecl] = []
    for source in files:
        code = source.code
        namespaces = [(m.start(), m.group("ns")) for m in NAMESPACE_RE.finditer(code)]
        for match in TYPE_KEYWORD_RE.finditer(code):
            name = match.group("name")
            line = source.line_of(match.start())
            line_start = source.line_starts[line - 1]
            if not DECL_PREFIX_RE.match(code, line_start, match.start()):
                # Something other than modifiers sits before the keyword on
                # this line: not a declaration.
                continue
            kind = "record" if match.group(0).startswith("record") else match.group(0).split()[0]
            namespace = ""
            for pos, ns in namespaces:
                if pos < match.start():
                    namespace = ns
                else:
                    break
            bases, base_span = read_bases(code, match.end())
            decls.append(
                TypeDecl(
                    name=name,
                    kind=kind,
                    bases=bases,
                    file=source,
                    line=line,
                    namespace=namespace,
                    base_span=base_span,
                )
            )
    return merge_partials(decls)


# --------------------------------------------------------------------------
# 4. Graph
# --------------------------------------------------------------------------


def build_index(decls: list[TypeDecl]) -> dict[str, list[TypeDecl]]:
    index: dict[str, list[TypeDecl]] = {}
    for decl in decls:
        index.setdefault(decl.name, []).append(decl)
    return index


def resolve(index: dict[str, list[TypeDecl]], name: str, ns: str) -> TypeDecl | None:
    """Resolve a simple name to a declaration.

    Same namespace first, then a unique global match. An ambiguous name
    resolves to nothing rather than to a guess: a wrong edge in a graph is
    worse than a missing one, and the missing one is visible in the output.
    """
    candidates = index.get(name)
    if not candidates:
        return None
    if len(candidates) == 1:
        return candidates[0]
    same_ns = [c for c in candidates if c.namespace == ns]
    if len(same_ns) == 1:
        return same_ns[0]
    return None


class Graph:
    """The extracted graph: declarations, edges, registrations, bypasses."""

    def __init__(self, repo: Path) -> None:
        self.repo = repo
        # Read each file ONCE: the consumer set is a superset of the product
        # set, and the product files are the consumer files that live under
        # src/ or apps/.
        self.consumer_files = collect_sources(repo, CONSUMER_TREES)
        self.product_files = [
            f for f in self.consumer_files
            if f.rel.startswith(("src/", "apps/"))
        ]
        self.decls = extract_types(self.consumer_files)
        self.index = build_index(self.decls)
        self.implements: dict[str, list[TypeDecl]] = {}
        self.extends: dict[str, list[TypeDecl]] = {}
        self.edges: list[Edge] = []
        self._build_edges()
        self.registrations = self._extract_registrations()
        self._index_mentions()
        self.bypasses = self._extract_bypasses()

    # -- edges ------------------------------------------------------------

    def _build_edges(self) -> None:
        for decl in self.decls:
            for base in decl.bases:
                target = resolve(self.index, base, decl.namespace)
                if target is None:
                    continue
                if target.kind == "interface":
                    self.implements.setdefault(target.name, []).append(decl)
                    self.edges.append(
                        Edge(decl.name, target.name, "implements", decl.rel, decl.line)
                    )
                else:
                    self.extends.setdefault(target.name, []).append(decl)
                    self.edges.append(
                        Edge(decl.name, target.name, "extends", decl.rel, decl.line)
                    )
        self._close_implements()

    def _close_implements(self) -> None:
        """A class that extends a class which implements an interface implements it.

        `RecordingPluginLoadHost : IPluginLoadHost` is direct. A class that
        extends THAT class implements the interface too. The fixpoint is what
        makes "who implements this seam" answer the question a reader has.
        """
        changed = True
        while changed:
            changed = False
            for seam, impls in list(self.implements.items()):
                for impl in list(impls):
                    for base in impl.bases:
                        target = resolve(self.index, base, impl.namespace)
                        if target is None:
                            continue
                        if target.name in self.implements and not any(
                            other.name == impl.name for other in self.implements[target.name]
                        ):
                            self.implements[target.name].append(impl)
                            self.edges.append(
                                Edge(
                                    impl.name,
                                    target.name,
                                    "extends",
                                    impl.rel,
                                    impl.line,
                                )
                            )
                            changed = True

    def implementers(self, seam: str) -> list[TypeDecl]:
        return sorted(self.implements.get(seam, []), key=lambda d: (d.rel, d.line))

    def project_of(self, rel: str) -> str:
        """The project a repository-relative path belongs to."""
        for source in self.consumer_files:
            if source.rel == rel:
                return source.project
        return Path(rel).parent.name

    # -- consumers --------------------------------------------------------

    def _index_mentions(self) -> None:
        """One pass over the tree: where each interface NAME is mentioned.

        The index is built once, per file, and inverted. Asking "who consumes
        this seam" then costs a dictionary lookup instead of a scan of the
        whole tree per seam - which is the difference between a tool that runs
        in a second and one that runs in a minute.

        A mention inside a base list is an IMPLEMENTATION, not a consumer, so
        the base-list spans are recorded and excluded.
        """
        interface_names = {d.name for d in self.decls if d.kind == "interface"}
        # file -> seam -> [(line, ...)] for mentions OUTSIDE a base list.
        self.mentions: dict[str, dict[str, list[int]]] = {}

        base_spans: dict[int, list[tuple[int, int, str]]] = {}
        for decl in self.decls:
            if not decl.bases or decl.base_span is None:
                continue
            lo, hi = decl.base_span
            base_spans.setdefault(id(decl.file), []).append((lo, hi, decl.name))

        # TWO patterns, not one alternation of every interface name. An
        # alternation of 200 names is tried branch by branch at every position
        # of every file; the `I` prefix is a single character class that
        # matches 204 of the 208 interfaces, and the handful that do not carry
        # the prefix are matched by name.
        prefixed = {n for n in interface_names if I_PREFIX_RE.fullmatch(n)}
        named = sorted(interface_names - prefixed, key=lambda n: (-len(n), n))
        patterns: list[re.Pattern[str]] = []
        if prefixed:
            patterns.append(re.compile(r"\bI[A-Z]\w*\b"))
        if named:
            patterns.append(
                re.compile(r"\b(" + "|".join(re.escape(n) for n in named) + r")\b")
            )

        for source in self.consumer_files:
            code = source.code
            spans = base_spans.get(id(source), [])
            per_file: dict[str, list[int]] = {}
            for pattern in patterns:
                for match in pattern.finditer(code):
                    name = match.group(0)
                    if name not in interface_names:
                        continue
                    if any(lo <= match.start() <= hi for lo, hi, _ in spans):
                        continue
                    line = source.line_of(match.start())
                    per_file.setdefault(name, []).append(line)
            if per_file:
                self.mentions[source.rel] = per_file

    def consumers(self, seam: str) -> dict[str, list[tuple[str, int]]]:
        """Files that NAME the seam, per file, with the line of each mention."""
        result: dict[str, list[tuple[str, int]]] = {}
        for rel, per_file in self.mentions.items():
            lines = per_file.get(seam)
            if lines:
                result[rel] = [(rel, line) for line in lines]
        return result

    # -- registration -----------------------------------------------------

    def _extract_registrations(self) -> list[Registration]:
        out: list[Registration] = []
        for source in self.product_files:
            for match in DI_RE.finditer(source.code):
                args = [a.strip() for a in match.group("args").split(",")]
                args = [a for a in args if a]
                if not args:
                    continue
                service = strip_generic(args[0]).strip()
                impl = strip_generic(args[1]).strip() if len(args) > 1 else ""
                if not service:
                    continue
                out.append(
                    Registration(
                        service=service,
                        impl=impl,
                        lifetime=match.group("lifetime"),
                        file=source.rel,
                        line=source.line_of(match.start()),
                    )
                )
        return out

    def registrations_for(self, service: str) -> list[Registration]:
        return [r for r in self.registrations if r.service == service]

    # -- bypasses ---------------------------------------------------------

    def _extract_bypasses(self) -> list[Bypass]:
        """Where the CONCRETE type is used where the seam was the contract.

        Two forms, both read from the text:
          * `new Concrete(...)` - the seam is bypassed at construction;
          * a parameter or a field typed as `Concrete` - the seam is bypassed
            in a signature.

        The second form is a heuristic: a type name in a parameter or field
        position, matched by shape and not by a parsed type. It is reported
        as a heuristic in the output.

        The scan is ONE pass per file over a generic pattern, not one pass per
        concrete type: the map from a concrete name to the seams it implements
        is consulted, not the other way round.
        """
        concrete_to_seams: dict[str, set[str]] = {}
        for seam, impls in self.implements.items():
            for impl in impls:
                if impl.kind == "interface":
                    continue
                concrete_to_seams.setdefault(impl.name, set()).add(seam)

        if not concrete_to_seams:
            return []

        construction_re = CONSTRUCTION_RE
        # A type name followed by a member name - the shape a parameter, a
        # field or a local declaration has. The set of concrete names decides
        # which of them are a bypass, so ONE pattern serves all of them.
        type_then_name_re = TYPE_THEN_NAME_RE

        out: list[Bypass] = []
        for source in self.product_files:
            code = source.code
            for match in construction_re.finditer(code):
                concrete = match.group("t")
                seams = concrete_to_seams.get(concrete)
                if not seams:
                    continue
                line = source.line_of(match.start())
                for seam in sorted(seams):
                    out.append(
                        Bypass(
                            concrete=concrete,
                            seam=seam,
                            form="construction",
                            file=source.rel,
                            line=line,
                        )
                    )
            for match in type_then_name_re.finditer(code):
                concrete = match.group("t")
                seams = concrete_to_seams.get(concrete)
                if not seams:
                    continue
                # A declaration, not a mention: the type name is followed by
                # the member's name.
                line = source.line_of(match.start())
                for seam in sorted(seams):
                    out.append(
                        Bypass(
                            concrete=concrete,
                            seam=seam,
                            form="signature",
                            file=source.rel,
                            line=line,
                        )
                    )
        # One row per (concrete, seam, file, line, form).
        seen: set[tuple[str, str, str, int, str]] = set()
        unique: list[Bypass] = []
        for bypass in sorted(out, key=lambda b: (b.file, b.line, b.concrete, b.form)):
            key = (bypass.concrete, bypass.seam, bypass.file, bypass.line, bypass.form)
            if key in seen:
                continue
            seen.add(key)
            unique.append(bypass)
        return unique

    # -- axes -------------------------------------------------------------

    def axes(self) -> list[Axis]:
        """The extension axes, and what each route does with them.

        Two sources, and they answer different questions:

          * `PluginAxisRegistry` says WHICH AXES EXIST - the marker/door
            pairs. That is the axis list.
          * `PluginRouteAxisRegistry` says what each ROUTE does with each
            axis, and why (#1723). That is the declaration, and it is the one
            the architecture rule grades the code against.

        The graph reads both, and then reads the route's OWN door body and
        prints what it does next to what the product declares. The difference
        between the two is the defect the rule is about, and it is the reason
        the graph is worth reading.
        """
        registry_path = self.repo / AXIS_REGISTRY
        host_path = self.repo / LOAD_HOST_INTERFACE
        if not registry_path.exists() or not host_path.exists():
            # A tree without the plugin registry has no axes to report. The
            # tool says so rather than failing, so that a fixture and a
            # partial tree are both readable.
            self.declared_doors = []
            self.route_rows = []
            self.other_hosts = []
            return []

        registry = registry_path.read_text(encoding="utf-8")
        pairs = [
            (m.group("marker"), m.group("door"))
            for m in AXIS_ROW_RE.finditer(registry)
        ]
        host_iface = host_path.read_text(encoding="utf-8")
        declared_doors = [m.group("door") for m in DOOR_DECL_RE.finditer(host_iface)]

        marker_of = {door: marker for marker, door in pairs}
        route_rows = self._route_rows(marker_of)

        dispatch_sites = self._find_sites_by_name(
            DISPATCH_RE, "marker", {marker for marker, _ in pairs}
        )
        door_sites = self._find_sites_by_name(
            DOOR_CALL_RE, "door", {door for _, door in pairs}
        )

        by_door: dict[str, list[RouteAxisRow]] = {}
        for row in route_rows:
            by_door.setdefault(row.door, []).append(row)

        out: list[Axis] = []
        for marker, door in pairs:
            axis = Axis(marker=marker, door=door)
            for impl in self.implementers(marker):
                if impl.rel.startswith(("src/", "apps/")):
                    axis.marker_impls_product.append(impl)
                else:
                    axis.marker_impls_outside.append(impl)
            axis.dispatch_sites = dispatch_sites.get(marker, [])
            axis.door_sites = door_sites.get(door, [])
            axis.declared = by_door.get(door, [])
            out.append(axis)

        self.declared_doors = declared_doors
        self.route_rows = route_rows
        self.other_hosts = self._hosts_the_registry_does_not_name(route_rows)
        return out

    def _route_rows(self, marker_of: dict[str, str]) -> list[RouteAxisRow]:
        """The rows of the route registry, with what the route's door does.

        The registry is parsed as TEXT: the route id and the host type name
        are constants in it, and the rows name the door, the verdict and the
        reason. Nothing is hardcoded here - a new route, a new verdict or a
        new reason is read, not assumed.
        """
        path = self.repo / ROUTE_AXIS_REGISTRY
        if not path.exists():
            return []
        text = path.read_text(encoding="utf-8")

        constants = {
            m.group("name"): m.group("value")
            for m in ROUTE_CONST_RE.finditer(text)
        }
        # The route id and the host type name share a prefix:
        # `McpStdioRouteId` and `McpStdioHostType`. The pairing is a NAME
        # CONVENTION, not declared data, and the document says so.
        host_of_route: dict[str, str] = {}
        for name, value in constants.items():
            if not name.endswith("RouteId"):
                continue
            prefix = name[: -len("RouteId")]
            host_name = f"{prefix}HostType"
            if host_name in constants:
                host_of_route[value] = constants[host_name]

        rows: list[RouteAxisRow] = []
        for match in ROUTE_ROW_RE.finditer(text):
            route = constants.get(match.group("route"), match.group("route"))
            door = match.group("door")
            declared = match.group("carriage")
            reason = "".join(
                m.group(1) for m in STRING_LITERAL_RE.finditer(match.group("reason"))
            )
            host_type = host_of_route.get(route, "")
            host_decl = self._host_by_name(host_type)
            if host_decl is None:
                observed, note = "MISSING", "the product has no such load host"
            else:
                observed, note = self._grade_door(host_decl, door)
            rows.append(
                RouteAxisRow(
                    route=route,
                    host_type=host_type,
                    marker=marker_of.get(door, door),
                    door=door,
                    declared=declared,
                    reason=reason,
                    observed=observed,
                    observed_note=note,
                )
            )
        return rows

    def _host_by_name(self, name: str) -> TypeDecl | None:
        """The product's load host with this name, if it has one."""
        if not name:
            return None
        for decl in self.implementers("IPluginLoadHost"):
            if decl.name == name and decl.rel.startswith(("src/", "apps/")):
                return decl
        return None

    def _hosts_the_registry_does_not_name(
        self, route_rows: list[RouteAxisRow]
    ) -> list[tuple[TypeDecl, str, str]]:
        """The load hosts the route registry does not name, and what they do.

        A decorator around a route is not a route: it forwards every door to
        the host it wraps. The registry does not declare it, and the graph
        does not pretend it is one - but it IS an implementation of the seam,
        so a reader looking for "who answers this door" has to see it.
        """
        named = {row.host_type for row in route_rows if row.host_type}
        out: list[tuple[TypeDecl, str, str]] = []
        for decl in self.implementers("IPluginLoadHost"):
            if not decl.rel.startswith(("src/", "apps/")):
                continue
            if decl.name in named:
                continue
            verdicts = []
            for door in self.declared_doors:
                verdict, note = self._grade_door(decl, door)
                verdicts.append((verdict, note))
            kinds = sorted({v for v, _ in verdicts})
            out.append(
                (
                    decl,
                    ", ".join(kinds) if kinds else "no door",
                    "; ".join(sorted({n for _, n in verdicts if n})),
                )
            )
        return out

    def _grade_door(self, host: TypeDecl, door: str) -> tuple[str, str]:
        """What one host actually does with one door.

        The verdicts, in the order they are decided:

          DELEGATES  the door forwards to another host, which decides;
          REGISTERS  the door answers `Result.Success` and registers;
          SILENT     the door answers `Result.Success` and registers nothing -
                     the lie #1758 is about;
          REFUSES    the door answers `Result.Failure` and says why;
          MIXED      the door answers both;
          UNKNOWN    the door answers neither.

        The EFFECT is checked before the ANSWER, and that order is the point:
        a door written as a conditional expression - `_tools.TryAdd(...) ?
        Result.Success() : Result.Failure(...)` - mentions BOTH answers, and
        a grader that reads the answer first calls it a refusal when it is in
        fact the one door that registers.
        """
        body = self._method_body(host, door)
        if body is None:
            return ("MISSING", "the host does not declare the door")
        if FORWARD_CALL_RE.search(body):
            return ("DELEGATES", "the door forwards the contribution to another host")
        claims_success = bool(SUCCESS_RE.search(body))
        claims_failure = bool(FAILURE_RE.search(body))
        if claims_success and self._has_effect(body):
            return ("REGISTERS", "the door answers Result.Success and registers")
        if claims_failure and not claims_success:
            return ("REFUSES", "the door answers Result.Failure")
        if claims_success and claims_failure:
            return ("MIXED", "the door answers Result.Success and Result.Failure")
        if claims_success:
            return ("SILENT", "the door answers Result.Success and registers nothing")
        return ("UNKNOWN", "the door answers neither Result.Success nor Result.Failure")

    def _has_effect(self, body: str) -> bool:
        if MEMBER_WRITE_RE.search(body) or INDEXER_WRITE_RE.search(body):
            return True
        if MUTATING_CALL_RE.search(body):
            return True
        if FORWARD_CALL_RE.search(body):
            return True
        for match in BARE_CALL_RE.finditer(body):
            if match.group("call") not in NON_EFFECT_CALLS:
                return True
        return False

    def _method_body(self, host: TypeDecl, door: str) -> str | None:
        """The body of one method of one type, from the blanked source.

        The search is scoped to the TYPE's own body, not to the file: two
        routes can live in one file, and a file-wide search would read the
        first route's door as the second route's answer. The type's body is
        found by locating its declaration and matching the braces that open
        it: the brace is often on the next line, which is why the scan skips
        whitespace rather than assuming it is on the declaration line.
        """
        pattern = re.compile(rf"\b{re.escape(door)}\s*\(")
        for source in self._files_of(host):
            code = source.code
            span = self._type_body_span(code, host.name)
            if span is None:
                continue
            lo, hi = span
            for match in pattern.finditer(code, lo, hi):
                body = self._body_after_signature(code, match.end() - 1, hi)
                if body is not None:
                    return body
        return None

    def _files_of(self, decl: TypeDecl) -> list[SourceFile]:
        """The files a (possibly partial) type is declared in."""
        wanted = {rel for rel, _ in decl.sites} or {decl.rel}
        return [s for s in self.consumer_files if s.rel in wanted]

    @staticmethod
    def _type_body_span(code: str, name: str) -> tuple[int, int] | None:
        """The character span of a type's body, from its declaration."""
        declaration = re.search(
            rf"\b(?:class|record|struct|interface)\s+{re.escape(name)}\b", code
        )
        if declaration is None:
            return None
        i = declaration.end()
        depth = 0
        n = len(code)
        while i < n:
            ch = code[i]
            if ch in "<([":
                depth += 1
            elif ch in ">)]":
                if depth > 0:
                    depth -= 1
            elif depth == 0:
                if ch == "{":
                    close = match_balanced(code, i, "{", "}")
                    return (i, close + 1) if close >= 0 else None
                if ch == ";":
                    # An interface or an abstract declaration: no body.
                    return None
            i += 1
        return None

    @staticmethod
    def _body_after_signature(code: str, open_paren: int, limit: int) -> str | None:
        """The body of a method, given the `(` of its parameter list."""
        close_paren = match_balanced(code, open_paren, "(", ")")
        if close_paren < 0 or close_paren > limit:
            return None
        i = close_paren + 1
        while i < len(code) and code[i] in " \t\r\n":
            i += 1
        if i >= len(code):
            return None
        if code[i] == "{":
            close = match_balanced(code, i, "{", "}")
            return code[i : close + 1] if close >= 0 else None
        if code.startswith("=>", i):
            end = code.find(";", i)
            return code[i : end + 1] if end >= 0 else code[i:]
        return None

    def _find_sites_by_name(
        self, regex: re.Pattern[str], group: str, wanted: set[str]
    ) -> dict[str, list[tuple[str, int]]]:
        """One pass over the tree, bucketed by the name the match carries.

        One pass per axis would be fourteen passes over 1 226 files; one pass
        with a bucket is one.
        """
        found: dict[str, list[tuple[str, int]]] = {name: [] for name in wanted}
        for source in self.product_files:
            for match in regex.finditer(source.code):
                name = match.group(group)
                if name in found:
                    found[name].append((source.rel, source.line_of(match.start())))
        return found


def match_balanced(text: str, start: int, open_ch: str, close_ch: str) -> int:
    """The index of the closer that matches the opener at `start`, or -1."""
    depth = 0
    for i in range(start, len(text)):
        if text[i] == open_ch:
            depth += 1
        elif text[i] == close_ch:
            depth -= 1
            if depth == 0:
                return i
    return -1


# --------------------------------------------------------------------------
# 5. Rendering
# --------------------------------------------------------------------------


def cell(values: list[str], limit: int = 6) -> str:
    """A table cell: the values, comma separated, truncated with a count."""
    if not values:
        return "none"
    if len(values) <= limit:
        return ", ".join(values)
    return ", ".join(values[:limit]) + f" (+{len(values) - limit} more)"


def link(rel: str, text: str | None = None) -> str:
    """A markdown link to a repository file, relative to docs/."""
    label = text or rel
    return f"[`{label}`](../{rel})"


class Labels:
    """Display names for types, disambiguated where the simple name is not unique.

    A simple name that two namespaces share is ambiguous, and printing it
    twice in one cell reads as one type listed twice. The disambiguator is
    the project when the project settles it, and the file when it does not.
    """

    def __init__(self, decls: list[TypeDecl]) -> None:
        self.by_name: dict[str, list[TypeDecl]] = {}
        for decl in decls:
            self.by_name.setdefault(decl.name, []).append(decl)
        self.by_name_project: dict[tuple[str, str], int] = {}
        for name, group in self.by_name.items():
            for decl in group:
                key = (name, decl.project)
                self.by_name_project[key] = self.by_name_project.get(key, 0) + 1

    def label(self, decl: TypeDecl) -> str:
        group = self.by_name.get(decl.name, [])
        if len(group) <= 1:
            return decl.name
        if self.by_name_project.get((decl.name, decl.project), 0) == 1:
            return f"{decl.name} ({decl.project})"
        return f"{decl.name} ({Path(decl.rel).name})"

    def ambiguous(self, decl: TypeDecl) -> bool:
        return len(self.by_name.get(decl.name, [])) > 1


def render(graph: Graph) -> str:
    axes = graph.axes()
    labels = Labels(graph.decls)
    out: list[str] = []
    w = out.append

    w("# Seam graph")
    w("")
    w(
        "Generated by [`tools/gen-seam-graph.py`](../tools/gen-seam-graph.py). "
        "Do not edit by hand. Regenerate with:"
    )
    w("")
    w("```")
    w("./tools/gen-seam-graph.py --write")
    w("```")
    w("")
    w(
        "Every row below is read out of the C# sources at the moment of "
        "generation. Nothing here is a hand-maintained claim, which is the "
        "point: the graph cannot drift away from the code, and the CI job "
        "`seamgraph` fails the build when it does."
    )
    w("")
    w(
        "The graph answers four questions about a seam: **who implements it**, "
        "**who extends what implements it**, **who consumes it**, and **who is "
        "registered in the container**. It also reports the question nobody "
        "asks, which is the one that finds bugs: **where the seam is "
        "bypassed**, the places that name the concrete type where the "
        "interface was the contract."
    )
    w("")

    # ---- 1. axes -------------------------------------------------------
    w("## 1. Extension axes")
    w("")
    w(
        "The axes of plugin extension, read out of "
        f"{link(AXIS_REGISTRY)}, the single registry the architecture rule "
        "`ExtensionAxisRegistrationRule` grades the code against. Each axis has "
        "two halves: a **marker** a plugin implements, and a **door** on "
        f"{link(LOAD_HOST_INTERFACE)} that receives the contribution."
    )
    w("")
    w(
        "**In-tree implementers** are the classes under `src/` and `apps/` that "
        "implement the marker. **Out-of-tree implementers** are the ones under "
        "`tests/` and `samples/`, the doubles and the sample plugins."
    )
    w("")
    w(
        "| Axis | Marker | Door | In-tree implementers | Out-of-tree implementers "
        "| Marker dispatched at | Door called at |"
    )
    w("|---|---|---|---|---|---|---|")
    for axis in axes:
        product = [f"`{labels.label(d)}`" for d in axis.marker_impls_product]
        outside = [f"`{labels.label(d)}`" for d in axis.marker_impls_outside]
        dispatch = cell([f"{rel}:{line}" for rel, line in axis.dispatch_sites], limit=2)
        called = cell([f"{rel}:{line}" for rel, line in axis.door_sites], limit=2)
        w(
            f"| {axis.marker} | `{axis.marker}` | `{axis.door}` | {cell(product)} | "
            f"{cell(outside)} | {dispatch} | {called} |"
        )
    w("")
    in_tree_markers = sum(len(a.marker_impls_product) for a in axes)
    uncalled_doors = [a.door for a in axes if not a.door_sites]
    facts: list[str] = []
    if in_tree_markers == 0:
        facts.append(
            "**no in-tree class implements any axis marker**. The markers are "
            "extension points, so the only implementers are out-of-tree "
            "plugins, the samples and the test doubles"
        )
    else:
        facts.append(
            f"**{in_tree_markers} in-tree class(es) implement an axis marker**"
        )
    if uncalled_doors:
        facts.append(
            "**these doors are never called on a receiver**: "
            + ", ".join(f"`{d}`" for d in uncalled_doors)
        )
    else:
        facts.append(
            "**every door is called on a receiver** inside the product, which "
            "is the half the architecture rule grades"
        )
    w(
        "Two facts a reader would otherwise have to dig for. First, "
        + facts[0]
        + ". Second, "
        + facts[1]
        + "."
    )
    w("")

    # ---- 2. route matrix -----------------------------------------------
    if graph.route_rows:
        w("### 1.1 Which route carries which axis")
        w("")
        w(
            "A **route** is the place a plugin's contribution lands: an "
            "implementation of `IPluginLoadHost` the product declares a route "
            "for. What the route does with each axis is DECLARED in "
            f"{link(ROUTE_AXIS_REGISTRY)} since #1723, and the table reads that "
            "file for the verdict and the reason."
        )
        w("")
        w(
            "The **observed** column is the other half: what the route's own "
            "door body does, read from the source. The two columns are printed "
            "side by side because the difference between them is the defect "
            "the architecture rule grades, and a reader of the graph is here "
            "to see it."
        )
        w("")
        w(
            "| Route | Axis | Door | Declared | Observed | Reason |"
        )
        w("|---|---|---|---|---|---|")
        for row in graph.route_rows:
            declared = {"Carried": "carried", "Refused": "refused",
                        "ClosedSeam": "closed seam"}.get(row.declared, row.declared)
            w(
                f"| `{row.route}` | `{row.marker}` | `{row.door}` | "
                f"{declared} | {row.observed.lower()} | {row.reason} |"
            )
        w("")
        for route in sorted({row.route for row in graph.route_rows}):
            rows = [r for r in graph.route_rows if r.route == route]
            carried = sum(1 for r in rows if r.declared == "Carried")
            refused = sum(1 for r in rows if r.declared == "Refused")
            closed = sum(1 for r in rows if r.declared == "ClosedSeam")
            w(
                f"- `{route}` carries {carried} of the {len(rows)} axes, "
                f"refuses {refused} and closes {closed}."
            )
        w("")
        mismatches = [
            row
            for row in graph.route_rows
            if not row.agrees()
        ]
        if mismatches:
            w(
                "The declared verdict and the observed behaviour disagree on "
                f"{len(mismatches)} row(s):"
            )
            w("")
            for row in mismatches:
                w(
                    f"- `{row.route}` / `{row.door}`: declared "
                    f"{row.declared.lower()}, observed {row.observed.lower()} "
                    f"({row.observed_note})"
                )
            w("")
        if graph.other_hosts:
            w(
                "The other implementations of `IPluginLoadHost` in the product "
                "are not routes: the registry does not name them, and the "
                "graph does not pretend otherwise. They are still answers to "
                "the same doors, so a reader looking for who answers a given "
                "door has to see them."
            )
            w("")
            w("| Implementation | What its doors do | Where |")
            w("|---|---|---|")
            for decl, kinds, note in graph.other_hosts:
                w(
                    f"| `{labels.label(decl)}` | {kinds.lower()} | {link(decl.rel)}"
                    + (f" ({note})" if note else "")
                    + " |"
                )
            w("")

    # ---- 3. mermaid ----------------------------------------------------
    w("### 1.2 The axes as a graph")
    w("")
    w("```mermaid")
    w("graph LR")
    w('  subgraph plugin["A plugin implements"]')
    for axis in axes:
        w(f'    {axis.marker}["{axis.marker}"]')
    w("  end")
    w('  subgraph registrar["PluginRegistrar dispatches"]')
    for axis in axes:
        w(f'    d_{axis.marker}["{axis.door}"]')
    w("  end")
    for axis in axes:
        w(f"  {axis.marker} --> d_{axis.marker}")
    w("```")
    w("")

    # ---- 4. core seams -------------------------------------------------
    interfaces = [
        d for d in graph.decls
        if d.kind == "interface" and d.rel.startswith("src/")
    ]
    seam_rows: list[dict[str, object]] = []
    for decl in interfaces:
        impls = graph.implementers(decl.name)
        consumers = graph.consumers(decl.name)
        regs = graph.registrations_for(decl.name)
        bypasses = [b for b in graph.bypasses if b.seam == decl.name]
        product_impls = [
            i for i in impls if i.rel.startswith(("src/", "apps/"))
        ]
        seam_rows.append(
            {
                "decl": decl,
                "impls": impls,
                "product_impls": product_impls,
                "consumers": consumers,
                "regs": regs,
                "bypasses": bypasses,
            }
        )
    seam_rows.sort(
        key=lambda row: (
            -(len(row["product_impls"]) + len(row["consumers"])),
            row["decl"].name,
        )
    )

    w("## 2. Core seams")
    w("")
    w(
        "Every interface declared under `src/`, ranked by how much of the "
        "product touches it. **Implementations** counts the classes that "
        "implement it, directly or through a base class; the in-tree ones come "
        "first and the out-of-tree ones (doubles, samples) after a `/`. "
        "**Consumers** counts the files that name it, excluding the files that "
        "only put it in a base list. **Registered** is the container "
        "registration, if the seam is resolved from the DI graph. **Bypasses** "
        "counts the places that name the concrete type where the interface was "
        "the contract."
    )
    w("")
    w(
        f"The table shows the {TOP_SEAMS} seams with the most in-tree "
        f"implementations and consumers, out of {len(interfaces)} interfaces "
        "declared under `src/`. The rest are in `--report`."
    )
    w("")
    w(
        "| Seam | Declared in | Implementations | Consumer files | Registered as "
        "| Bypasses |"
    )
    w("|---|---|---|---|---|---|")
    for row in seam_rows[:TOP_SEAMS]:
        decl = row["decl"]
        impls: list[TypeDecl] = row["impls"]
        consumers: dict[str, list[tuple[str, int]]] = row["consumers"]
        regs: list[Registration] = row["regs"]
        bypasses: list[Bypass] = row["bypasses"]
        product_impls: list[TypeDecl] = row["product_impls"]
        product_names = [f"`{labels.label(i)}`" for i in product_impls]
        other_names = [
            f"`{labels.label(i)}`"
            for i in impls
            if not i.rel.startswith(("src/", "apps/"))
        ]
        impl_cell = cell(product_names, limit=5)
        if other_names:
            impl_cell += " / " + cell(other_names, limit=3)
        reg_text = cell(
            [
                f"`{r.impl}`" if r.impl else "a factory"
                for r in regs
            ],
            limit=2,
        )
        w(
            f"| `{labels.label(decl)}` | {link(decl.rel)} | {impl_cell} | "
            f"{len(consumers)} | {reg_text} | {len(bypasses)} |"
        )
    w("")

    # ---- 5. detail -----------------------------------------------------
    w("## 3. Three seams in detail")
    w("")
    w(
        "The table above is a census. These are the seams a reader most often "
        "has to reason about, and the graph says things about them that are "
        "not visible by reading one file."
    )
    w("")
    for seam in ("IToolRegistry", "ILlmClient", "IPluginHost"):
        decl = next((d for d in interfaces if d.name == seam), None)
        if decl is None:
            continue
        impls = graph.implementers(seam)
        consumers = graph.consumers(seam)
        regs = graph.registrations_for(seam)
        bypasses = [b for b in graph.bypasses if b.seam == seam]
        product_impls = [
            i for i in impls if i.rel.startswith(("src/", "apps/"))
        ]
        other_impls = [
            i for i in impls if not i.rel.startswith(("src/", "apps/"))
        ]
        w(f"### `{seam}`")
        w("")
        w(f"Declared in {link(decl.rel)}.")
        w("")
        w(f"- **In-tree implementations** ({len(product_impls)}):")
        if product_impls:
            for impl in product_impls:
                w(f"  - `{labels.label(impl)}`, {link(impl.rel)}")
        else:
            w("  - **none**")
        w(f"- **Out-of-tree implementations** ({len(other_impls)}):")
        if other_impls:
            for impl in other_impls:
                w(f"  - `{labels.label(impl)}`, {link(impl.rel)}")
        else:
            w("  - **none**")
        projects = sorted({graph.project_of(rel) for rel in consumers})
        w(
            f"- **Named by** {len(consumers)} file(s) in "
            f"{len(projects)} project(s): " + ", ".join(projects[:10])
        )
        if regs:
            w(
                "- **Registered in the container**: "
                + "; ".join(
                    f"`{r.service}` → "
                    + (f"`{r.impl}`" if r.impl else "a factory")
                    + f" ({r.lifetime}, {link(r.file)})"
                    for r in regs
                )
            )
        else:
            w("- **Registered in the container**: **no DI registration**")
        constructions = [b for b in bypasses if b.form == "construction"]
        signatures = [b for b in bypasses if b.form == "signature"]
        w(
            f"- **Bypassed**: {len(constructions)} direct construction(s) of the "
            f"concrete type, {len(signatures)} signature(s) naming the concrete "
            "type where the interface was the contract:"
        )
        shown = (constructions + signatures)[:12]
        if shown:
            for bypass in shown:
                w(
                    f"  - `{bypass.concrete}` instead of `{seam}`, "
                    f"{bypass.form} at {link(bypass.file)}:{bypass.line}"
                )
            if len(constructions) + len(signatures) > len(shown):
                w(
                    f"  - … and "
                    f"{len(constructions) + len(signatures) - len(shown)} more"
                )
        else:
            w("  - **none**")
        w("")

    # ---- 6. holes ------------------------------------------------------
    w("## 4. Holes")
    w("")
    w(
        "What the graph finds that reading the code does not: interfaces with "
        "no implementation, seams nobody resolves from the container, and "
        "implementations nobody consumes."
    )
    w("")
    empty = [
        d for d in interfaces
        if not graph.implementers(d.name) and graph.consumers(d.name)
    ]
    if empty:
        w(
            f"- **{len(empty)} interface(s) declared under `src/` are named "
            "somewhere but have no implementation**: "
            + ", ".join(f"`{d.name}`" for d in empty)
        )
    else:
        w(
            "- No interface under `src/` is named without being implemented."
        )
    unregistered = [
        d for d in interfaces
        if graph.implementers(d.name)
        and not graph.registrations_for(d.name)
    ]
    w(
        f"- **{len(unregistered)} seam(s) with an implementation have no "
        "container registration**: "
        + ", ".join(f"`{d.name}`" for d in unregistered[:20])
        + (" …" if len(unregistered) > 20 else "")
    )
    w(
        "  That is a census of the `AddSingleton<T>` calls, not a verdict on "
        "reachability: a seam can also be resolved through a registry rather "
        "than the container. `ILlmClient` is reached through "
        "`IProviderRegistry`, not through DI."
    )
    silent = [
        (row.route, row.door)
        for row in graph.route_rows
        if row.observed == "SILENT"
    ]
    if silent:
        w(
            f"- **{len(silent)} door/route pair(s) answer `Result.Success` "
            "without registering anything**: "
            + ", ".join(f"`{door}` via `{route}`" for route, door in silent)
        )
    else:
        w(
            "- No door of a declared route answers `Result.Success` without "
            "registering anything (the #1758 shape)."
        )
    mismatches = [row for row in graph.route_rows if not row.agrees()]
    if mismatches:
        w(
            f"- **{len(mismatches)} declared route verdict(s) disagree with what "
            "the door does**: "
            + ", ".join(
                f"`{row.route}` / `{row.door}` ({row.declared.lower()} vs "
                f"{row.observed.lower()})"
                for row in mismatches
            )
        )
    else:
        w(
            "- Every declared route verdict agrees with what the route's door "
            "body does."
        )
    w("")

    # ---- 7. limits -----------------------------------------------------
    w("## 5. What this graph does not see")
    w("")
    w(
        "A graph that hides its own blind spots is a decoration. These are the "
        "gaps, and they are gaps of a TEXT SCAN, not of the code:"
    )
    w("")
    w(
        "- **DI registrations written without a type argument** "
        "(`services.AddSingleton(sp => new Foo())`) are invisible: the service "
        "type is inferred by the compiler, and the scan has no compiler."
    )
    w(
        "- **Bypasses in a signature** are a heuristic: a type name followed by "
        "a member name is matched by shape, not by a parsed type. The count is "
        "an upper bound."
    )
    w(
        "- **`contrib/` is not scanned.** It is not compiled by CI and the owner "
        "marked it unmaintained, so a seam that only exists there is not a seam "
        "of this product."
    )
    w(
        "- **Extension methods are not classified.** A static class with `this` "
        "in its first parameter is a seam to read, and the scan does not say so."
    )
    w(
        "- **Reflection-reached seams are not counted as consumed.** A type "
        "resolved by name at run time has no textual mention to find."
    )
    w(
        "- **A simple name that two namespaces share is disambiguated in the "
        "labels, but the EDGES are resolved per namespace.** An ambiguous name "
        "used in a base list resolves to nothing rather than to a guess."
    )
    w(
        "- **The route registry pairs a route with its host by NAME.** The "
        "registry declares `McpStdioRouteId` and `McpStdioHostType` as two "
        "constants, and the graph pairs them because the names share a "
        "prefix. A route whose id and host type do not share one is read as "
        "a route with no host, and the table says so."
    )
    w(
        "- **The type graph is one level of inheritance deep for the "
        "`extends` edges, and closed transitively for the `implements` "
        "edges.** A base class reached only through an alias is not seen."
    )
    w("")
    w("## 6. How to regenerate and to check")
    w("")
    w("```")
    w("./tools/gen-seam-graph.py --write     # regenerate this file")
    w("./tools/gen-seam-graph.py --check     # fail if this file is stale")
    w("./tools/gen-seam-graph.py --report    # the numbers, on stdout")
    w("./tools/gen-seam-graph.py --self-test # prove the checker can fail")
    w("```")
    w("")
    return "\n".join(out)


# --------------------------------------------------------------------------
# 6. Commands
# --------------------------------------------------------------------------


def render_document(repo: Path) -> str:
    """The document, exactly as it is committed.

    No commit stamp: a stamp would make the file differ from its own
    regeneration on every commit, and the check would then fail on the
    commit that regenerated it. The provenance is the file's own history.
    """
    graph = Graph(repo)
    return render(graph)


def write_output(repo: Path, out: Path) -> int:
    text = render_document(repo)
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(text, encoding="utf-8")
    print(f"wrote {out.relative_to(repo) if out.is_relative_to(repo) else out}")
    return 0


def check_output(repo: Path, out: Path, min_files: int = 0, min_interfaces: int = 0) -> int:
    if not out.exists():
        print(f"FAIL: {out} does not exist; run --write")
        return 1
    graph = Graph(repo)
    interfaces = [
        d for d in graph.decls
        if d.kind == "interface" and d.rel.startswith("src/")
    ]
    if len(graph.consumer_files) < min_files:
        print(
            f"FAIL: only {len(graph.consumer_files)} source files were scanned, "
            f"floor is {min_files}. A scan that stopped covering the tree "
            "would produce a document that agrees with itself and says "
            "nothing, the #509 shape."
        )
        return 1
    if len(interfaces) < min_interfaces:
        print(
            f"FAIL: only {len(interfaces)} interfaces were found under src/, "
            f"floor is {min_interfaces}. A scanner that stopped matching "
            "declarations would produce a document that agrees with itself "
            "and says nothing."
        )
        return 1
    expected = render(graph)
    actual = out.read_text(encoding="utf-8")
    if expected == actual:
        print(
            f"OK: {out.name} matches the tree "
            f"({len(graph.consumer_files)} files, {len(interfaces)} seams)"
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
        print(f"  line count differs: committed {len(act_lines)}, generated {len(exp_lines)}")
    print("Run ./tools/gen-seam-graph.py --write and commit the result.")
    return 1


def report(repo: Path) -> int:
    graph = Graph(repo)
    axes = graph.axes()
    interfaces = [
        d for d in graph.decls
        if d.kind == "interface" and d.rel.startswith("src/")
    ]
    print(f"files scanned      : {len(graph.consumer_files)}")
    print(f"types declared     : {len(graph.decls)}")
    print(f"interfaces in src/ : {len(interfaces)}")
    print(f"implements edges   : {sum(1 for e in graph.edges if e.kind == 'implements')}")
    print(f"extends edges      : {sum(1 for e in graph.edges if e.kind == 'extends')}")
    print(f"DI registrations   : {len(graph.registrations)}")
    print(f"bypass sites       : {len(graph.bypasses)}")
    print(f"axes               : {len(axes)}")
    for axis in axes:
        print(f"  {axis.marker:<22} {axis.door:<22}")
    print("")
    print("declared route carriage (the product's declaration vs. the door body):")
    for row in graph.route_rows:
        mark = "ok" if row.agrees() else "MISMATCH"
        print(
            f"  {row.route:<12} {row.door:<24} declared={row.declared:<11} "
            f"observed={row.observed:<10} {mark}"
        )
    print("")
    print("every seam in src/, with its in-tree implementations and consumers:")
    rows = []
    for decl in interfaces:
        impls = graph.implementers(decl.name)
        consumers = graph.consumers(decl.name)
        product = [i for i in impls if i.rel.startswith(("src/", "apps/"))]
        rows.append((len(product) + len(consumers), decl.name, len(product), len(consumers)))
    rows.sort(key=lambda r: (-r[0], r[1]))
    for _, name, product, consumers in rows:
        print(f"  {name:<40} impls={product:<4} consumers={consumers}")
    return 0


def self_test(repo: Path) -> int:
    """Prove the checker can fail.

    Two fixtures: a tree whose graph differs from the committed file must be
    reported stale, and a tree whose graph matches must be reported clean. A
    checker that cannot say no is a checker that enforces nothing.
    """
    with tempfile.TemporaryDirectory() as tmp:
        fixture = Path(tmp) / "fixture"
        (fixture / "src" / "Demo").mkdir(parents=True)
        (fixture / "src" / "Demo" / "Demo.csproj").write_text(
            "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n", encoding="utf-8"
        )
        (fixture / "src" / "Demo" / "ISeam.cs").write_text(
            "namespace Demo;\n\npublic interface ISeam\n{\n"
            "    string Name { get; }\n}\n",
            encoding="utf-8",
        )
        (fixture / "src" / "Demo" / "Seam.cs").write_text(
            "namespace Demo;\n\npublic sealed class Seam : ISeam\n{\n"
            "    public string Name => \"seam\";\n}\n",
            encoding="utf-8",
        )
        (fixture / "src" / "Demo" / "Consumer.cs").write_text(
            "namespace Demo;\n\npublic sealed class Consumer\n{\n"
            "    public string Describe(ISeam seam) => seam.Name;\n}\n",
            encoding="utf-8",
        )
        # The axis half of the fixture: a marker, a door, a route that
        # registers it, and a route that refuses it.
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
        (abstractions / "IPluginLoadHost.cs").write_text(
            "namespace Harbor.Plugins.Abstractions;\n\n"
            "public interface IPluginLoadHost\n{\n"
            "    public Result RegisterDemo(string id);\n}\n",
            encoding="utf-8",
        )
        host = fixture / "src" / "Demo" / "Hosts.cs"
        host.write_text(
            "namespace Demo;\n\n"
            "public sealed class CarryingHost : Harbor.Plugins.Abstractions."
            "IPluginLoadHost\n{\n"
            "    private readonly Dictionary<string, string> _items = new();\n"
            "    public Result RegisterDemo(string id)\n"
            "    {\n"
            "        _items[id] = id;\n"
            "        return Result.Success();\n"
            "    }\n}\n"
            "\n"
            "public sealed class LyingHost : Harbor.Plugins.Abstractions."
            "IPluginLoadHost\n{\n"
            "    public Result RegisterDemo(string id) => Result.Failure(\"no\");\n}\n",
            encoding="utf-8",
        )
        # The route half of the fixture: the registry declares the route, the
        # host it names and the verdict per axis.
        hosting = fixture / "src" / "Harbor.Plugins.Hosting"
        hosting.mkdir(parents=True)
        (hosting / "PluginRouteAxisRegistry.cs").write_text(
            "namespace Harbor.Plugins.Hosting;\n\n"
            "public enum PluginAxisCarriage\n{\n"
            "    Carried,\n"
            "    Refused,\n"
            "    ClosedSeam,\n}\n"
            "\n"
            "public static class PluginRouteAxisRegistry\n{\n"
            "    public const string DemoRouteId = \"demo\";\n"
            "    public const string DemoHostType = \"CarryingHost\";\n"
            "\n"
            "    public const string LyingRouteId = \"lying\";\n"
            "    public const string LyingHostType = \"LyingHost\";\n"
            "\n"
            "    public static readonly object[] All =\n"
            "    [\n"
            "        new(\n"
            "            DemoRouteId,\n"
            "            nameof(Harbor.Plugins.Abstractions.IPluginLoadHost.RegisterDemo),\n"
            "            PluginAxisCarriage.Carried,\n"
            "            \"the demo route carries the demo axis\"),\n"
            "        new(\n"
            "            LyingRouteId,\n"
            "            nameof(Harbor.Plugins.Abstractions.IPluginLoadHost.RegisterDemo),\n"
            "            PluginAxisCarriage.Carried,\n"
            "            \"this row claims the axis and the door refuses it\"),\n"
            "    ];\n}\n",
            encoding="utf-8",
        )
        (fixture / "AXIS.md").write_text("stale\n", encoding="utf-8")

        graph = Graph(fixture)
        names = sorted(d.name for d in graph.decls)
        problems: list[str] = []
        if "ISeam" not in names:
            problems.append(f"the fixture's interface was not declared: {names}")
        if [d.name for d in graph.implementers("ISeam")] != ["Seam"]:
            problems.append(
                "the fixture's implementation was not linked: "
                f"{[d.name for d in graph.implementers('ISeam')]}"
            )
        consumers = graph.consumers("ISeam")
        if not any("Consumer.cs" in rel for rel in consumers):
            problems.append(f"the fixture's consumer was not found: {sorted(consumers)}")
        if graph.registrations_for("ISeam"):
            problems.append("the fixture registered nothing, but a registration was found")

        axes = graph.axes()
        if len(axes) != 1 or axes[0].marker != "IDemoPlugin":
            problems.append(
                f"the fixture's axis was not read: {[(a.marker, a.door) for a in axes]}"
            )
        else:
            axis = axes[0]
            rows = {row.route: row for row in graph.route_rows}
            if set(rows) != {"demo", "lying"}:
                problems.append(
                    f"the fixture's route rows were not read: {sorted(rows)}"
                )
            else:
                row = rows["demo"]
                if row.host_type != "CarryingHost":
                    problems.append(
                        f"the fixture's host type was not read: {row.host_type}"
                    )
                if row.declared != "Carried":
                    problems.append(
                        f"the fixture's declared verdict was not read: {row.declared}"
                    )
                if row.reason != "the demo route carries the demo axis":
                    problems.append(
                        f"the fixture's reason was not read: {row.reason!r}"
                    )
                if row.observed != "REGISTERS":
                    problems.append(
                        "the fixture's carrying route was not observed registering: "
                        f"{row.observed}"
                    )
                if not row.agrees():
                    problems.append(
                        "the fixture's declared verdict and its door disagree"
                    )
                # The load-bearing fixture: a route that CLAIMS the axis and
                # whose door refuses it. The cross-check has to say so.
                lying = rows["lying"]
                if lying.observed != "REFUSES":
                    problems.append(
                        "the fixture's refusing route was not observed refusing: "
                        f"{lying.observed}"
                    )
                if lying.agrees():
                    problems.append(
                        "the cross-check accepted a route that declares Carried "
                        "and whose door refuses the axis"
                    )

        # The stale-file half: the committed file must NOT match the tree.
        with contextlib.redirect_stdout(io.StringIO()) as captured:
            stale = check_output(fixture, fixture / "AXIS.md")
        if stale != 1:
            problems.append(f"the stale check did not fail: exit {stale}")
        if "stale" not in captured.getvalue():
            problems.append(
                "the stale check did not say WHAT was stale: "
                f"{captured.getvalue()!r}"
            )

        # The clean half: a file the generator produced must pass.
        fresh = fixture / "FRESH.md"
        fresh.write_text(render_document(fixture), encoding="utf-8")
        with contextlib.redirect_stdout(io.StringIO()):
            clean = check_output(fixture, fresh)
        if clean != 0:
            problems.append(f"the fresh check did not pass: exit {clean}")

        if problems:
            for problem in problems:
                print(f"FAIL: {problem}")
            return 1
        print(
            "OK: the self-test passed: the scanner reads the fixture, the "
            "route grader reads the declaration and observes the door, the "
            "cross-check catches a route that lies about its axis, and the "
            "checker fails on a stale file"
        )
        return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--repo", default=str(REPO_ROOT), help="repository root")
    ap.add_argument("--out", default=str(DEFAULT_OUT), help="output document")
    ap.add_argument("--write", action="store_true", help="regenerate the document")
    ap.add_argument("--check", action="store_true", help="fail if the document is stale")
    ap.add_argument("--report", action="store_true", help="print the numbers")
    ap.add_argument("--self-test", action="store_true", help="prove the checker can fail")
    ap.add_argument(
        "--min-files",
        type=int,
        default=0,
        help="fail unless at least this many C# files were scanned (0 = off)",
    )
    ap.add_argument(
        "--min-interfaces",
        type=int,
        default=0,
        help="fail unless at least this many interfaces were found under src/ (0 = off)",
    )
    args = ap.parse_args()

    repo = Path(args.repo).resolve()
    out = Path(args.out).resolve()

    if args.self_test:
        return self_test(repo)
    if args.write:
        return write_output(repo, out)
    if args.check:
        return check_output(repo, out, args.min_files, args.min_interfaces)
    if args.report:
        return report(repo)
    ap.print_help()
    return 2


if __name__ == "__main__":
    sys.exit(main())
