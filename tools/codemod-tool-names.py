#!/usr/bin/env python3
"""Replace tool-name string literals with the WellKnownTools catalog (issue #1564).

Stdlib only — no pip, no network, no dotnet.

WHAT THIS IS FOR
----------------
Issue #1564 counted the tool names written out as string literals across the
product. Measured on `origin/dev`: 259 of them in 51 files under `src/` alone
("bash" seventy times, "edit" 21, "write" 19, "patch" 15, "read" 14, "glob"
13), and 276 across `src/` and `apps/`, over 62 files. Every one of those
literals is a fact about a tool written down a second time, and a fact written
twice is two facts to keep in step. The failure mode is silent: a rule that
names "basn" matches nothing, and a rule that matches nothing looks exactly
like a rule that was never meant to fire.

`WellKnownTools` (src/Harbor.Abstractions.Contracts/Models/Identifiers/) is
the one place a builtin tool's name is written down. This script is the lever
that moves the call sites onto it: it reads the CATALOG and rewrites the
literals that mean "this tool", and it leaves every other literal alone.

HOW IT DECIDES
--------------
The catalog is READ, not duplicated: `WellKnownTools.cs` is parsed for its
`public static readonly ToolName X = ToolName.Create("y")` rows, and the
resulting name -> member map is what the rewrite uses. A tool added to the
catalog is therefore known to the script with no edit here, and a name the
catalog does not have is never rewritten — the script cannot invent a member
that does not exist, so a typo is a compile error at the call site rather than
a silent mismatch in a permission rule.

The rewrites are CONTEXT-RULELED, never a blind "replace every quoted tool
name". A rule says where a literal is a tool name (the first argument of a
`new(...)` permission rule, the tool name in a `ToolErrors.Handler(...)` call,
a comparison against a tool name), and a literal that is something else — a
JSON argument key, a CLI verb, a display label, prose in a doc comment — is
left exactly where it is. The rules are listed in RULES below; each one names
the file set it applies to, so a rule cannot fire on a file it was not written
for.

WHAT IS DELIBERATELY NOT REWRITTEN
-----------------------------------
Two places cannot read a `ToolName` and keep their literal, because C# requires
a compile-time constant there:

  * a tool's own `[ToolInfo("name", ...)]` attribute argument, and
  * a `case` label in a switch over a tool name.

Both are covered by guards that already exist (the safety-declaration
cross-check and the architecture rule's dead-row check), so the literal is
redundant rather than dangerous. `--audit` lists them with that reason.

MODES
-----
  --check   report the pending rewrites and exit 1 if there are any (default)
  --apply   write the rewrites
  --audit   list every tool-name literal that is still in the tree, with the
            reason it was left

Both --check and --apply print what they did, file by file, so a reviewer can
see the diff the script would make before it makes it.
"""

import argparse
import os
import re
import sys
from pathlib import Path

# --------------------------------------------------------------------------
# The catalog, read from the tree.
# --------------------------------------------------------------------------

CATALOG_REL = "src/Harbor.Abstractions.Contracts/Models/Identifiers/WellKnownTools.cs"

CATALOG_ROW_RE = re.compile(
    r"public\s+static\s+readonly\s+ToolName\s+(?P<member>\w+)\s*=\s*"
    r"ToolName\.Create\(\s*\"(?P<name>[a-z][a-z0-9_]*)\"\s*\)")

CATALOG_TYPE_RE = re.compile(r"public\s+static\s+class\s+WellKnownTools")

# The namespace the catalog lives in, and the using a consumer needs.
CATALOG_NAMESPACE = "Harbor.Abstractions.Models.Identifiers"
CATALOG_USING = "using Harbor.Abstractions.Models.Identifiers;"

# The catalog type's name, as a call site spells it.
CATALOG_TYPE = "WellKnownTools"

# The roots the script reads. `tests/` is deliberately NOT among them: a test
# that names a tool is a test ASSERTING a name, and rewriting it to a constant
# would make the test agree with the product by construction instead of by
# measurement.
#
# `samples/` is audited but NOT rewritten: a CS-source plugin is compiled at
# load time against a FIXED set of injected namespaces (see
# RoslynPluginCompiler.ImplicitUsingNamespaces), and the catalog's namespace is
# not among them, so a rewritten sample would fail to compile when the plugin
# loads.
REWRITE_ROOTS = ("src", "apps")
AUDIT_ROOTS = ("src", "apps", "samples")

SKIP_DIRS = {"bin", "obj", ".git", "out", "packages"}


def read_catalog(repo: Path) -> dict:
    """The name -> catalog member map, read from the catalog file."""
    path = repo / CATALOG_REL
    if not path.is_file():
        raise SystemExit("catalog not found: " + str(path))
    text = path.read_text(encoding="utf-8")
    if not CATALOG_TYPE_RE.search(text):
        raise SystemExit("no `public static class WellKnownTools` in " + str(path))
    members = {}
    for match in CATALOG_ROW_RE.finditer(text):
        members[match.group("name")] = match.group("member")
    if not members:
        raise SystemExit("the catalog declares no tool names: " + str(path))
    return members


# --------------------------------------------------------------------------
# The rules.
#
# Each rule is (id, file set, regex, replacement). The file set is a list of
# path PREFIXES relative to the repository root; a rule whose file set is
# empty applies to every product file. The regex must expose a `name` group
# holding the tool name, and the replacement is a format string that may use
# {member} (the catalog member for that name) and {name}.
# --------------------------------------------------------------------------

RULES = [
    (
        "permission-rule",
        [
            "src/Harbor.Abstractions/Agents/AgentDefinition.cs",
            "src/Harbor.Abstractions.Contracts/Permissions/PermissionRuleset.cs",
        ],
        # new("read", "*", PermissionAction.Allow) — the first argument of a
        # permission rule is the tool the rule is about.
        re.compile(r"new\(\s*\"(?P<name>[a-z][a-z0-9_]*)\"\s*,\s*"),
        "new({member}, ",
    ),
    (
        "safety-declaration",
        [
            "src/Harbor.Abstractions.Contracts/Permissions/BuiltinToolSafetyProfiles.cs",
        ],
        # new("write", ToolSafetyProfile.Path()) — the safety table's tool column.
        re.compile(r"new\(\s*\"(?P<name>[a-z][a-z0-9_]*)\"\s*,\s*ToolSafetyProfile\."),
        "new({member}, ToolSafetyProfile.",
    ),
    (
        "tool-error-handler",
        ["src/Harbor.Tools.Builtin"],
        # ToolErrors.Handler("bash", ct) — the tool whose error is being shaped.
        re.compile(r"ToolErrors\.Handler\(\s*\"(?P<name>[a-z][a-z0-9_]*)\""),
        "ToolErrors.Handler({member}",
    ),
    (
        "tool-name-comparison",
        [
            "src/Harbor.Ui.Framework.State/Diff/DiffPreview.cs",
        ],
        # toolName != "edit" — a comparison against a tool name.
        re.compile(r"\btoolName\s*(?P<op>==|!=)\s*\"(?P<name>[a-z][a-z0-9_]*)\""),
        "toolName {op} {member}",
    ),
    (
        "tool-name-set",
        [
            "src/Harbor.Ui.Framework.Projection/Projection/PanelExtractors.cs",
            "src/Harbor.Abstractions.Contracts/Permissions/IArgSafetyPolicy.cs",
        ],
        # new[] { "edit", "write" } — a set of tool names.
        re.compile(r"new\[\]\s*\{(?P<body>[^{}]*?)\}"),
        None,  # handled specially: every element must be a catalog name
    ),
    (
        "ordinal-equals",
        [
            "src/Harbor.Tui.CellForge/Chat/Widgets/ToolCallBlock.cs",
            "src/Harbor.Tui.CellForge/Chat/Widgets/ReadGroupBlock.cs",
        ],
        # string.Equals(toolName, "bash", StringComparison.Ordinal)
        re.compile(
            r"string\.Equals\((?P<lhs>[^,()]+),\s*\"(?P<name>[a-z][a-z0-9_]*)\"\s*,\s*StringComparison\."),
        "string.Equals({lhs}, {member}, StringComparison.",
    ),
    (
        "const-tool-name",
        [
            "src/Harbor.Tui.CellForge/Chat/Streaming/ToolCardTracker.cs",
            "src/Harbor.Terminal.Pty/PermissionGatedTerminalPaneLauncher.cs",
        ],
        # private const string ShellPermission = "bash";
        re.compile(
            r"(?P<mods>(?:public|internal|private|protected)\s+)const\s+string\s+"
            r"(?P<id>\w+)\s*=\s*\"(?P<name>[a-z][a-z0-9_]*)\"\s*;"),
        "{mods}static readonly ToolName {id} = {member};",
    ),
    (
        "tool-result-entry",
        ["src/Harbor.AgentRuntime"],
        # new ToolResultEntry(id, "task", output, isError)
        re.compile(
            r"new\s+ToolResultEntry\((?P<lhs>[^,()]+),\s*\"(?P<name>[a-z][a-z0-9_]*)\"\s*,"),
        "new ToolResultEntry({lhs}, {member},",
    ),
    (
        "demo-tool-call",
        ["apps/Harbor.App.Cli"],
        # DemoReply.FromToolCall("bash", """...""")
        re.compile(
            r"FromToolCall\(\s*\"(?P<name>[a-z][a-z0-9_]*)\""),
        "FromToolCall({member}",
    ),
    (
        "tool-name-create",
        ["src", "apps"],
        # ToolName.Create("tree") — the one construction site that is left
        # outside the catalog, which is what the catalog is for.
        re.compile(r"ToolName\.Create\(\s*\"(?P<name>[a-z][a-z0-9_]*)\"\s*\)"),
        "{member}",
    ),
]


# --------------------------------------------------------------------------
# Helpers.
# --------------------------------------------------------------------------

def product_files(repo: Path, roots=REWRITE_ROOTS):
    """Every .cs file under the given roots, skipping build output."""
    for root in roots:
        base = repo / root
        if not base.is_dir():
            continue
        for dirpath, dirnames, filenames in os.walk(base):
            dirnames[:] = sorted(d for d in dirnames if d not in SKIP_DIRS)
            for name in sorted(filenames):
                if name.endswith(".cs"):
                    yield Path(dirpath) / name


def relative(repo: Path, path: Path) -> str:
    return path.relative_to(repo).as_posix()


def in_file_set(rel: str, prefixes) -> bool:
    if not prefixes:
        return True
    for prefix in prefixes:
        if rel == prefix or rel.startswith(prefix.rstrip("/") + "/"):
            return True
    return False


def project_dir(path: Path) -> Path:
    """The directory holding the .csproj that compiles this file."""
    for parent in path.parents:
        if any(p.suffix == ".csproj" for p in parent.iterdir() if p.is_file()):
            return parent
    return path.parent


def has_global_using(path: Path, using_line: str) -> bool:
    """Whether the compiling project declares the using globally."""
    proj = project_dir(path)
    candidate = proj / "GlobalUsings.cs"
    if not candidate.is_file():
        return False
    return using_line in candidate.read_text(encoding="utf-8")


def namespace_of(text: str) -> str:
    match = re.search(r"^namespace\s+([\w.]+)\s*;", text, re.M)
    return match.group(1) if match else ""


def needs_using(path: Path, text: str) -> bool:
    """Whether this file has to import the catalog's namespace."""
    if namespace_of(text) == CATALOG_NAMESPACE:
        return False
    if CATALOG_USING in text:
        return False
    return not has_global_using(path, CATALOG_USING)


def _using_key(line: str):
    """The sort key of a using directive: System first, then alphabetical.

    The repository's blocks are laid out that way (the SDK's default), and an
    inserted directive that lands in the wrong group is a diff a reviewer has
    to fix by hand — which is the kind of noise that makes a real change in
    the same hunk invisible.
    """
    namespace = line[len("using "):].rstrip(";").strip()
    return (0 if namespace == "System" or namespace.startswith("System.") else 1, namespace)


def insert_using(text: str) -> str:
    """Add the catalog's using to a file's using block, in sorted position."""
    lines = text.split("\n")
    plain = [i for i, line in enumerate(lines)
             if line.startswith("using ") and " = " not in line]
    if not plain:
        # No using block: put one above the namespace declaration.
        for i, line in enumerate(lines):
            if line.startswith("namespace "):
                return "\n".join([CATALOG_USING, ""] + lines[i:])
        return CATALOG_USING + "\n" + text

    key = _using_key(CATALOG_USING)
    insert_at = plain[-1] + 1
    for i in plain:
        if _using_key(lines[i]) > key:
            insert_at = i
            break
    lines.insert(insert_at, CATALOG_USING)
    return "\n".join(lines)


# --------------------------------------------------------------------------
# The rewrite.
# --------------------------------------------------------------------------

def code_mask(text: str):
    """A per-character flag: is this offset inside CODE (not a comment)?

    A literal inside a doc comment is prose about the code, not the code, and
    rewriting it would change documentation. The scanner tracks string and
    char literals so that a `//` inside a string (a URL, a path) does not
    start a comment.
    """
    mask = [True] * len(text)
    i = 0
    n = len(text)
    state = "code"
    while i < n:
        ch = text[i]
        nxt = text[i + 1] if i + 1 < n else ""
        if state == "code":
            if ch == "/" and nxt == "/":
                state = "line"
                mask[i] = mask[i + 1] = False
                i += 2
                continue
            if ch == "/" and nxt == "*":
                state = "block"
                mask[i] = mask[i + 1] = False
                i += 2
                continue
            if ch == '"':
                state = "string"
                i += 1
                continue
            if ch == "'":
                state = "char"
                i += 1
                continue
            i += 1
            continue
        if state == "line":
            if ch == "\n":
                state = "code"
            else:
                mask[i] = False
            i += 1
            continue
        if state == "block":
            mask[i] = False
            if ch == "*" and nxt == "/":
                mask[i + 1] = False
                state = "code"
                i += 2
                continue
            i += 1
            continue
        if state == "string":
            if ch == "\\":
                mask[i] = False
                if i + 1 < n:
                    mask[i + 1] = False
                i += 2
                continue
            if ch == '"':
                state = "code"
            i += 1
            continue
        if state == "char":
            if ch == "\\":
                mask[i] = False
                if i + 1 < n:
                    mask[i + 1] = False
                i += 2
                continue
            if ch == "'":
                state = "code"
            i += 1
            continue
    return mask


def rewrite(text: str, rel: str, members: dict):
    """Apply every rule to one file. Returns (new_text, [(rule, name), ...])."""
    applied = []
    for rule_id, prefixes, pattern, replacement in RULES:
        if not in_file_set(rel, prefixes):
            continue

        # The mask is recomputed per rule: a rule's rewrite changes the text,
        # so the offsets the previous mask was built on no longer line up.
        mask = code_mask(text)

        if rule_id == "tool-name-set":
            text, hits = _rewrite_set(text, members, mask)
            applied.extend(hits)
            continue

        def repl(match, _replacement=replacement, _rule=rule_id):
            name = match.group("name")
            if name not in members or not mask[match.start()]:
                return match.group(0)
            applied.append((_rule, name))
            fields = {key: (value or "") for key, value in match.groupdict().items()}
            fields["member"] = CATALOG_TYPE + "." + members[name]
            return _replacement.format(**fields)

        text = pattern.sub(repl, text)

    text = _explicit_conditional_conversion(text, applied)
    return text, applied


def _explicit_conditional_conversion(text: str, applied):
    """Make the ToolName -> string conversion explicit in a conditional.

    `cond ? someString : typedField` is legal — a `ToolName` converts to
    `string` implicitly, and the conditional takes the string branch — but the
    reader has to know that to follow it. The conversion is written out
    instead, so the line says what it does.
    """
    typed = set(re.findall(
        r"static\s+readonly\s+ToolName\s+(?P<id>\w+)\s*=", text))
    for name in sorted(typed):
        # The lookahead keeps the pass idempotent: a field that already
        # carries the explicit conversion is left alone.
        pattern = re.compile(
            r"(?P<head>\?[^;\n]*?:\s*)" + re.escape(name) + r"(?!\s*\.Value)\b")
        text = pattern.sub(lambda m: m.group("head") + name + ".Value", text)
    return text


def _rewrite_set(text: str, members: dict, mask):
    """new[] { "a", "b" } -> new string[] { Member.A, Member.B }.

    Every element must be a catalog name: a set that mixes a tool name with
    something else is not a tool set, and rewriting half of it would change
    what the set means.
    """
    applied = []

    def repl(match):
        body = match.group("body")
        names = re.findall(r"\"([a-z][a-z0-9_]*)\"", body)
        if not names or any(name not in members for name in names):
            return match.group(0)
        if re.sub(r"\"[a-z][a-z0-9_]*\"\s*,?\s*", "", body).strip():
            return match.group(0)
        if not mask[match.start()]:
            return match.group(0)
        for name in names:
            applied.append(("tool-name-set", name))
        elements = ", ".join(CATALOG_TYPE + "." + members[name] for name in names)
        return "new string[] { " + elements + " }"

    return re.sub(r"new\[\]\s*\{(?P<body>[^{}]*?)\}", repl, text), applied


# --------------------------------------------------------------------------
# The audit: what is left, and why.
# --------------------------------------------------------------------------

# Reasons a literal is left alone, keyed by a test over the line it sits on.
def classify(line: str, stripped: str, previous: str = "") -> str:
    if stripped.startswith("//") or stripped.startswith("///") or stripped.startswith("*"):
        return "documentation"
    if re.search(r"\[ToolInfo\(", line) or re.search(r"\[ToolInfo\($", previous.strip()):
        return "C# requires a compile-time constant (attribute argument)"
    if re.search(r"\bcase\s+\"", line):
        return "C# requires a compile-time constant (case label)"
    if re.search(r"\"[a-z][a-z0-9_]*\"\s*:", line) and "properties" in line:
        return "JSON schema property"
    if re.search(r"GetProperty\(|GetString\(|RequiredString\(|RequiredNonBlankString\(", line):
        return "JSON argument key"
    if re.search(r"\bPlural\(", line):
        return "display label, not a tool name"
    return "not a tool name in this position"


def audit(repo: Path, members: dict):
    """Every tool-name literal still in the tree, with the reason it stays."""
    rows = []
    for path in product_files(repo, AUDIT_ROOTS):
        rel = relative(repo, path)
        if rel == CATALOG_REL:
            continue
        previous = ""
        for number, line in enumerate(path.read_text(encoding="utf-8").split("\n"), 1):
            for name in sorted(members):
                quoted = '"' + name + '"'
                if quoted not in line:
                    continue
                stripped = line.strip()
                if rel.startswith("samples/"):
                    reason = ("samples/ is out of the rewrite scope: a CS-source plugin is "
                              "compiled against a fixed set of injected namespaces")
                else:
                    reason = classify(line, stripped, previous)
                for _ in range(line.count(quoted)):
                    rows.append((rel, number, name, reason, stripped))
            previous = line
    return rows


# --------------------------------------------------------------------------
# Entry point.
# --------------------------------------------------------------------------

def main(argv) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--repo", default=None,
                        help="repository root (default: the parent of tools/)")
    parser.add_argument("--check", action="store_true",
                        help="report the pending rewrites and exit 1 if there are any")
    parser.add_argument("--apply", action="store_true",
                        help="write the rewrites")
    parser.add_argument("--audit", action="store_true",
                        help="list the tool-name literals that stay, with the reason")
    args = parser.parse_args(argv)

    repo = Path(args.repo).resolve() if args.repo else Path(__file__).resolve().parent.parent
    members = read_catalog(repo)

    if args.audit:
        rows = audit(repo, members)
        by_reason = {}
        for rel, number, name, reason, text in rows:
            by_reason.setdefault(reason, []).append((rel, number, name, text))
        print("tool-name literals left in %s: %d\n" % (", ".join(AUDIT_ROOTS), len(rows)))
        for reason in sorted(by_reason):
            entries = by_reason[reason]
            print("%s (%d)" % (reason, len(entries)))
            for rel, number, name, text in entries:
                print("  %s:%d  \"%s\"  %s" % (rel, number, name, text.strip()[:90]))
            print("")
        return 0

    pending = []
    for path in product_files(repo):
        rel = relative(repo, path)
        if rel == CATALOG_REL:
            continue
        text = path.read_text(encoding="utf-8")
        new_text, applied = rewrite(text, rel, members)
        if not applied:
            continue
        if needs_using(path, text) and "WellKnownTools." in new_text:
            new_text = insert_using(new_text)
        pending.append((rel, applied, text, new_text))

    total = sum(len(applied) for _, applied, _, _ in pending)
    if not pending:
        print("nothing to rewrite: every tool-name literal that the rules cover is already a catalog member")
        return 0

    for rel, applied, _, _ in pending:
        names = ", ".join(sorted({name for _, name in applied}))
        print("%s: %d literal(s) -> %s" % (rel, len(applied), names))

    print("\n%d file(s), %d literal(s)" % (len(pending), total))

    if args.apply:
        for rel, _, text, new_text in pending:
            path = repo / rel
            path.write_text(new_text, encoding="utf-8")
        print("applied")
        return 0

    print("re-run with --apply to write these changes")
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
