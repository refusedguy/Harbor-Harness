#!/usr/bin/env python3
"""Gate the package set of the test and sample projects (issue #1882).

Stdlib only — no pip, no network, no dotnet build. Wired into
.github/workflows/ci.yml as the `test-packages` job, and it must not overlap
the sibling gates:

  tools/check-abstractions-contract.py  the four guarded src/ projects: is
                                       every declared <PackageReference>
                                       load-bearing, and is every non-BCL
                                       namespace used covered by one?
  tools/aot-publish-gate.py            the shipped surface: does the
                                       published binary keep its trim/AOT
                                       contract? (src/ + apps/)
  THIS ONE                             the test and sample projects: which
                                       packages may they pull in — directly
                                       or transitively — and is a BANNED one
                                       among them?

WHY THIS FILE EXISTS (issue #1882)

  Nothing in this repository gated the package set of a test project. Measured
  on dev@69fb5c80, before this file existed:

    BannedSymbols.txt (`N:Newtonsoft.Json`)   an RS0030 analyzer input. It
                                              covers SOURCE CODE only, and
                                              RS0030 is switched OFF for
                                              tests/ and samples/ in
                                              Directory.Build.props (the
                                              condition on
                                              `MSBuildProjectDirectory.
                                              Contains('tests')`), so the
                                              ban does not even reach the
                                              test tree as a diagnostic.
    tools/check-abstractions-contract.py     four src/ projects.
    tools/aot-publish-gate.py                src/ + apps/.
    AGENTS.md §"What NOT to do" #2           prose.

  So a TRANSITIVE dependency could put a banned package into any test
  project's `bin/` and not one rule would notice. The concrete case is not
  hypothetical: `TngTech.ArchUnitNET` 0.13.4 — proposed in #1841 — declares
  `Newtonsoft.Json 13.0.4` as a dependency for every target framework it
  supports (its nuspec, read out of the NuGet cache), so the package this
  repository bans outright would land in
  `tests/Harbor.Architecture.Tests/bin/Release/net10.0/` the moment that
  proposal is accepted, with nothing in the repository saying so.

  The package that is ALREADY there is `NetArchTest.Rules` 1.3.2, whose only
  dependency is `Mono.Cecil` (its nuspec says 0.11.3; the test project declares
  the package itself and pins 0.11.6 since #1865). It drags in nothing
  banned, so this gate is green on the tree as it stands — a fact this file
  measures on every run rather than assumes.

HOW THE RESOLVED SET IS OBTAINED — NO BUILD, NO NETWORK

  Two sources, and the difference between them is the whole point:

    1. `obj/project.assets.json` — the file NuGet writes during RESTORE. It
       carries the resolved graph: every package in the closure, its
       version, which of them are direct, and the exact asset paths that
       will be copied into the output directory. This is the transitive
       half of the question, and it is what makes the gate answer "directly
       or transitively" rather than "directly".
    2. the `<PackageReference>` elements of the `.csproj` itself — the
       direct half, readable with no restore at all.

  A build is NOT needed, and neither is a network: the assets file is
  produced by `dotnet restore`, which the CI job runs before this gate. A
  project whose assets file is missing is reported as PACKAGE-BLIND rather
  than passing: "I could not verify this project" and "this project is
  clean" are not the same answer, and only one of them is a green light.

WHAT "BANNED" MEANS — ONE SOURCE OF TRUTH, NOT A SECOND LIST

  The banned names are read out of `BannedSymbols.txt`, the file the
  BannedApiAnalyzers already consume. The convention is mechanical:

    `N:Newtonsoft.Json`  ->  the namespace `Newtonsoft.Json`, which is the
                              package id AND the assembly simple name of
                              the package that ships it.

  `T:` and `M:` entries name types and methods. A type does not name the
  package that ships it (the repository's own types do not either), so they
  contribute nothing here — and that limitation is stated rather than
  implied: a type-only ban on a third-party type is invisible to this gate
  unless the file also carries the `N:` entry for its namespace. The
  repository's file carries both for Newtonsoft.Json.

  There is deliberately NO second list of banned packages here. A second
  list is a copy of the answer, and a copy of the answer drifts — the shape
  #847 closed and #933 is about. To ban another package, add its `N:`
  entry to `BannedSymbols.txt`.

RULES

  BANNED-PACKAGE         A banned package is in a scope project's resolved
                          package graph (direct or transitive) and no
                          exception covers it. The message names the
                          package, its version, whether it is direct or
                          transitive, the project, and the asset path that
                          would land in the output directory.
  BANNED-ASSEMBLY        A banned assembly is (a) among the runtime assets
                          the resolved graph says will be copied into the
                          output directory, or (b) already present in a
                          scope project's `bin/` tree. This is the issue's
                          own question — "is the forbidden DLL in the output
                          directory" — asked from the resolved graph when
                          there is no build output yet, and from the output
                          directory when there is.
  EXCEPTION-STALE        An exception in the exception file matches nothing.
                          An exception that outlives the dependency that
                          needed it is an allowance with no referent, and it
                          is the shape this file exists to prevent.
  EXCEPTION-UNJUSTIFIED  An exception line is malformed, or its reason or
                          its owner is empty. An exception nobody can
                          justify is a silent hole.
  PACKAGE-BLIND          Something the gate needed to look at was not there:
                          a scope project with no resolved package graph
                          (its transitive set is unverified), an empty ban
                          set (every package is allowed by definition), or
                          no output directory at all in `--output-only`
                          mode. Reported, not passed.

TWO MODES, TWO MECHANISMS

  The default mode asks the whole question: the `.csproj`'s own declarations
  plus the resolved graph, and it reports a banned package wherever it finds
  one. `--output-only` asks the issue's question literally and nothing else:
  is a banned assembly in the OUTPUT DIRECTORY of a scope project? It reads
  no `.csproj` and no resolved graph, so it needs neither a restore nor a
  waiver, which is what makes it the right mode for a job that has already
  built the tree. The two mechanisms fail independently, which is why both
  are wired into CI: the resolved graph says what will be copied into the
  output directory, and the output directory says what is in it.

THE EXCEPTION MECHANISM — NAMED, JUSTIFIED, NEVER SILENT

  `tools/test-package-exceptions.txt` is the allow-list. One exception per
  line, four fields, `|`-separated:

      <package or assembly name> | <project glob> | <reason> | <owner>

  Every field is mandatory. A line without a reason is an unjustified
  exception; a line without an owner is an exception nobody is accountable
  for. A glob of `*` covers every scope project; a glob is matched against
  the project's repository-relative path (`tests/Harbor.Architecture.Tests`)
  and against its bare directory name.

  An exception that matches nothing is reported as stale, so the file
  cannot accumulate permissions that no longer apply.

  THE NEWTONSOFT CASE, AND WHY IT IS NOT IN THAT FILE

  `Newtonsoft.Json` is the one real candidate, and it is deliberately NOT
  listed as an exception. The repository bans Newtonsoft.Json outright
  (`BannedSymbols.txt`, and AGENTS.md §"What NOT to do" #2: "use
  System.Text.Json only"), and the treatment of a package that drags it in
  is therefore: DROP THE DEPENDENCY, DO NOT EXCEPT IT. The exception file
  exists for a package that cannot be dropped — one this repository needs
  and cannot replace — and Newtonsoft.Json is not that: no test project
  needs it, and the two candidates that would bring it are optional.

    * `NetArchTest.Rules` 1.3.2 (already referenced by
      `tests/Harbor.Architecture.Tests`) declares exactly one dependency,
      `Mono.Cecil` (its nuspec says 0.11.3; the test project declares the
      package itself and pins 0.11.6 since #1865). It does NOT bring
      Newtonsoft.Json. If a future
      version of it did, the fix would be to drop it: the layering rules
      have a zero-dependency reflection fallback already
      (`tests/Harbor.Architecture.Tests/LayerDependencyTests.cs`), so the
      NetArchTest DSL is a convenience, not a requirement.
    * `TngTech.ArchUnitNET` 0.13.4 (proposed in #1841) declares
      `Newtonsoft.Json 13.0.4`. If #1841 is accepted with that package,
      this gate goes red, and the answer is to not take that package — or to
      take it and answer the finding here with a named exception that says
      WHY a test-only Newtonsoft is worth having, in a file a reviewer can
      see. What must not happen is the package arriving quietly.

  The exception file is therefore empty of entries today. It is not absent:
  its presence is what makes the "drop it or name it" choice explicit
  rather than a default nobody made.

NON-VACUITY

  `--self-test` builds throwaway trees and asserts the failure path. The
  fixtures cover: a banned package declared directly by a `.csproj`; a
  banned package that arrives TRANSITIVELY through the resolved graph; a
  banned assembly sitting in a project's `bin/` tree; a named exception
  silencing exactly that finding and not matching anything else; an
  exception that matches nothing (stale); an exception with no reason
  (unjustified); a clean tree staying clean; a project with no resolved
  graph reporting BLIND rather than passing; the same report being
  waivable explicitly; a package whose id merely CONTAINS a banned name not
  being flagged (the match is exact and case-insensitive, as NuGet ids are);
  the ban set being read from the `N:` entries only; an empty ban set being
  reported rather than passed; and, for `--output-only`, a banned assembly
  in the output directory being found, the resolved graph being ignored,
  and a tree with no output at all being reported rather than passed.

  Every fixture added after the first version was checked by mutation — it
  fails when its own fix is reverted — and the two mechanisms (resolved
  graph, output directory) are covered separately, because either one alone
  can be broken without the other noticing.

WHAT THIS DOES NOT DO, STATED RATHER THAN IMPLIED

  * It does not read any document. Both sides of the comparison are files:
    the `.csproj` and the resolved package graph.
  * It does not build anything, and it does not restore anything. The CI
    job restores first; this file reads what the restore produced.
  * It does not decide what a test project MAY reference. It reports the
    banned ones. Which packages are appropriate for a test project is a
    reviewer's call, and the only mechanical line drawn here is the banned
    set, which is inherited from `BannedSymbols.txt` rather than restated.
  * It does not cover `src/` and `apps/` (the AOT gate and the guarded-
    contract gate do) and it does not cover `contrib/`, which this
    repository does not compile in CI.
"""
import argparse
import fnmatch
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile

# The directories whose projects are in scope. A path is a scope, not a claim
# about the repository: widening it is a reviewable diff.
SCOPE_DIRS = ("tests", "samples")

# The banned-API analyzer input, and the single source of the banned names.
BANNED_SYMBOLS_FILE = "BannedSymbols.txt"

# The allow-list. One exception per line; see the module docstring for the
# format and for why the file is empty of entries today.
EXCEPTIONS_FILE = os.path.join("tools", "test-package-exceptions.txt")

# `<PackageReference Include="X" ...>` — self-closing or with a body, and
# the attribute may sit on any line of the element.
_PACKAGE_REF = re.compile(
    r"<PackageReference\b[^>]*?Include\s*=\s*\"([^\"]+)\"", re.S
)

# An `N:` entry of BannedSymbols.txt: a banned NAMESPACE. `T:` and `M:`
# entries name types and methods and do not name a package.
_NAMESPACE_BAN = re.compile(r"^N:([^;\s]+)")

# The restore inputs, and the property that turns them on (#1857).
#
# `RestorePackagesWithLockFile` makes NuGet WRITE a `packages.lock.json`
# next to every project; `RestoreLockedMode` makes the restore FAIL when
# that file is out of date. The property is repo-wide or it is nothing:
# one project with a lock file and one without is two different restore
# behaviours in one tree, and a lock file nobody regenerates rots into a
# red build for everybody else.
LOCK_FILE_NAME = "packages.lock.json"
_WANTS_LOCK_FILE = re.compile(
    r"<RestorePackagesWithLockFile>\s*true\s*</RestorePackagesWithLockFile>", re.I
)
_LOCKED_MODE = re.compile(r"<RestoreLockedMode>\s*true\s*</RestoreLockedMode>", re.I)

# The directories whose projects are NOT restored by CI, and therefore do
# not take part in the all-or-nothing question: contrib/ is unmaintained and
# not compiled by CI, and a `bin/` or `obj/` project is build output.
_NOT_RESTORED = ("contrib",)


class Finding:
    def __init__(self, rule, project, message, subject=None):
        self.rule = rule
        self.project = project
        self.message = message
        self.subject = subject

    def __str__(self):
        return f"{self.rule}  {self.project}: {self.message}"


def read(path):
    try:
        with open(path, encoding="utf-8", errors="replace") as handle:
            return handle.read()
    except OSError:
        return ""


# ---------------------------------------------------------------------------
# The scope
# ---------------------------------------------------------------------------


def scope_projects(root):
    """Every `.csproj` under the scope directories, repository-relative.

    The INDEX (`git ls-files`) rather than a glob, for the reason
    `tools/md_gate.py` states: an untracked scratch project must not be able
    to change what the gate sees, and a tracked project must not be able to
    hide from it. The walk is the fallback for a throwaway fixture tree,
    which is not a git repository.
    """
    try:
        proc = subprocess.run(
            ["git", "ls-files", "--", *SCOPE_DIRS],
            cwd=root,
            capture_output=True,
            text=True,
            check=True,
        )
        tracked = sorted(
            line for line in proc.stdout.splitlines() if line.endswith(".csproj")
        )
        if tracked:
            return tracked
    except (OSError, subprocess.CalledProcessError):
        pass

    found = []
    for scope in SCOPE_DIRS:
        base = os.path.join(root, scope)
        for dirpath, dirnames, filenames in os.walk(base):
            dirnames[:] = [d for d in dirnames if d not in ("bin", "obj")]
            for name in filenames:
                if name.endswith(".csproj"):
                    full = os.path.join(dirpath, name)
                    found.append(
                        os.path.relpath(full, root).replace(os.sep, "/")
                    )
    return sorted(found)


# ---------------------------------------------------------------------------
# The restore inputs: all-or-nothing (#1857)
# ---------------------------------------------------------------------------


def all_projects(root):
    """Every `.csproj` CI restores, repository-relative.

    The index (`git ls-files`) for the reason `scope_projects` states, with
    the same walk fallback for a throwaway fixture tree. `contrib/` is
    excluded: it is unmaintained and not compiled by CI, so a lock file
    there would say nothing about the tree that ships.
    """
    try:
        proc = subprocess.run(
            ["git", "ls-files", "--", "*.csproj"],
            cwd=root,
            capture_output=True,
            text=True,
            check=True,
        )
        tracked = sorted(
            line for line in proc.stdout.splitlines() if line.endswith(".csproj")
        )
        if tracked:
            return [p for p in tracked if p.split("/")[0] not in _NOT_RESTORED]
    except (OSError, subprocess.CalledProcessError):
        pass

    found = []
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [
            d for d in dirnames if d not in ("bin", "obj", ".git")
        ]
        for name in filenames:
            if not name.endswith(".csproj"):
                continue
            relative = os.path.relpath(
                os.path.join(dirpath, name), root
            ).replace(os.sep, "/")
            if relative.split("/")[0] in _NOT_RESTORED:
                continue
            found.append(relative)
    return sorted(found)


def restore_input_findings(root):
    """Is the tree's restore input all-or-nothing?

    The property `RestorePackagesWithLockFile` is repo-wide or it is
    nothing, and a lock file without it is a file NuGet never reads. The
    four states that are not "every project, or none" are reported here,
    because each of them is a tree where two projects restore differently
    from each other, and the failure surfaces in somebody else's PR, not
    in the one that caused it.
    """
    projects = all_projects(root)
    owners = [
        p for p in projects
        if os.path.exists(os.path.join(root, os.path.dirname(p), LOCK_FILE_NAME))
    ]

    wants_lock_file = False
    locked_mode = False
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [
            d for d in dirnames if d not in ("bin", "obj", ".git")
            and d.split("/")[0] not in _NOT_RESTORED
        ]
        for name in filenames:
            if not name.endswith((".props", ".targets", ".csproj")):
                continue
            body = read(os.path.join(dirpath, name))
            if _WANTS_LOCK_FILE.search(body):
                wants_lock_file = True
            if _LOCKED_MODE.search(body):
                locked_mode = True

    findings = []
    if owners and not wants_lock_file:
        findings.append(
            Finding(
                "RESTORE-INPUTS-UNENFORCED",
                LOCK_FILE_NAME,
                f"{len(owners)} project(s) carry a {LOCK_FILE_NAME} but "
                "nothing sets RestorePackagesWithLockFile, so NuGet never "
                "writes it, never checks it, and the restore ignores it. "
                "The file rots silently until somebody sets the property.",
            )
        )
    if wants_lock_file and not owners:
        findings.append(
            Finding(
                "RESTORE-INPUTS-UNWRITTEN",
                "RestorePackagesWithLockFile",
                "RestorePackagesWithLockFile is set but no project has a "
                f"{LOCK_FILE_NAME}: every restore writes one, so the tree "
                "is dirty after every restore and the lock file is not a "
                "record of anything.",
            )
        )
    if locked_mode and not wants_lock_file:
        findings.append(
            Finding(
                "RESTORE-LOCKED-WITHOUT-FILES",
                "RestoreLockedMode",
                "RestoreLockedMode is set but RestorePackagesWithLockFile is "
                f"not: the restore demands a {LOCK_FILE_NAME} that is never "
                "written, and fails.",
            )
        )

    # Half-migrated, per scope: the first path segment is the scope, so a
    # lock file under src/ says nothing about tests/, and a tree that
    # migrates one scope at a time is reported once per scope.
    scopes = {}
    for project in projects:
        scopes.setdefault(project.split("/")[0], []).append(project)
    for scope, members in sorted(scopes.items()):
        with_lock = [
            p for p in members
            if os.path.exists(os.path.join(root, os.path.dirname(p), LOCK_FILE_NAME))
        ]
        if with_lock and len(with_lock) != len(members):
            missing = sorted(set(members) - set(with_lock))
            findings.append(
                Finding(
                    "RESTORE-INPUTS-PARTIAL",
                    f"{scope}/",
                    f"{len(with_lock)} of {len(members)} project(s) under "
                    f"{scope}/ carry a {LOCK_FILE_NAME} and "
                    f"{len(missing)} do not: the tree is half-migrated, so "
                    "two projects in the same scope restore differently. "
                    f"Without: {', '.join(missing[:5])}"
                    + (" ..." if len(missing) > 5 else ""),
                )
            )
    return findings


# ---------------------------------------------------------------------------
# The banned set — read from the analyzer input, never restated
# ---------------------------------------------------------------------------


def banned_names(root):
    """The banned package/assembly names, read out of BannedSymbols.txt.

    One convention, and it is a property of the file rather than a table of
    this repository: an `N:` entry names a namespace, and the package that
    ships it is the namespace itself. `Newtonsoft.Json` is both the
    namespace, the package id and the assembly simple name, so one string
    covers all three questions the gate asks.

    `T:` and `M:` entries are types and methods: they name no package, so
    they contribute nothing here.
    """
    names = set()
    for line in read(os.path.join(root, BANNED_SYMBOLS_FILE)).splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        match = _NAMESPACE_BAN.match(line)
        if match:
            names.add(match.group(1))
    return names


# ---------------------------------------------------------------------------
# The exception file
# ---------------------------------------------------------------------------


class Exception:
    """One line of the allow-list: a name, a scope, a reason, an owner."""

    def __init__(self, subject, project_glob, reason, owner, line_no):
        self.subject = subject
        self.project_glob = project_glob
        self.reason = reason
        self.owner = owner
        self.line_no = line_no

    def matches(self, name, project_path):
        """Whether this exception covers `name` in `project_path`."""
        if self.subject.lower() != name.lower():
            return False
        glob = self.project_glob
        if glob in ("", "*"):
            return True
        if fnmatch.fnmatchcase(project_path, glob):
            return True
        return fnmatch.fnmatchcase(os.path.basename(project_path), glob)


def parse_exceptions(root):
    """(exceptions, findings) read from the allow-list file.

    A line with the wrong number of fields, or with an empty reason or an
    empty owner, is a finding and not an exception: the file is the one
    place in this repository where a permission is granted, and a
    permission nobody can justify is exactly the hole this gate closes.
    """
    exceptions = []
    findings = []
    path = os.path.join(root, EXCEPTIONS_FILE)
    if not os.path.exists(path):
        findings.append(
            Finding(
                "EXCEPTION-UNJUSTIFIED",
                EXCEPTIONS_FILE,
                "the exception file does not exist. An allow-list that is "
                "absent is indistinguishable from an allow-list that grants "
                "everything, so its absence is reported rather than "
                "read as 'no exceptions'.",
            )
        )
        return exceptions, findings

    for number, raw in enumerate(read(path).splitlines(), start=1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        fields = [f.strip() for f in line.split("|")]
        if len(fields) != 4:
            findings.append(
                Finding(
                    "EXCEPTION-UNJUSTIFIED",
                    f"{EXCEPTIONS_FILE}:{number}",
                    f"expected 4 fields "
                    f"(<name> | <project glob> | <reason> | <owner>), got "
                    f"{len(fields)}: {raw!r}",
                )
            )
            continue
        subject, glob, reason, owner = fields
        if not subject or not reason or not owner:
            missing = [
                name
                for name, value in (
                    ("name", subject),
                    ("reason", reason),
                    ("owner", owner),
                )
                if not value
            ]
            findings.append(
                Finding(
                    "EXCEPTION-UNJUSTIFIED",
                    f"{EXCEPTIONS_FILE}:{number}",
                    f"the {', '.join(missing)} field is empty. An exception "
                    f"without a reason is a silent allowance.",
                )
            )
            continue
        exceptions.append(Exception(subject, glob, reason, owner, number))
    return exceptions, findings


# ---------------------------------------------------------------------------
# The resolved graph
# ---------------------------------------------------------------------------


def declared_packages(csproj_xml):
    """The package ids a `.csproj` declares itself."""
    return sorted(set(_PACKAGE_REF.findall(csproj_xml)))


def resolved_packages(assets):
    """The resolved package graph of a restored project.

    `obj/project.assets.json` carries, per target framework, every package
    in the closure with its version and its asset paths, and marks which of
    them the project declares directly. Read out of `targets` (what will be
    in the output) and `project.frameworks.*.dependencies` (what the
    project asked for), so the gate can say "direct" or "transitive"
    instead of leaving the reader to work it out.
    """
    direct = set()
    for framework in assets.get("project", {}).get("frameworks", {}).values():
        for name, spec in framework.get("dependencies", {}).items():
            if isinstance(spec, dict) and spec.get("target") == "Package":
                direct.add(name)

    packages = {}
    for framework, entries in assets.get("targets", {}).items():
        for key, spec in entries.items():
            if spec.get("type") != "package":
                continue
            name, _, version = key.partition("/")
            runtime = sorted(spec.get("runtime", {}).keys())
            compile_assets = sorted(spec.get("compile", {}).keys())
            record = packages.setdefault(
                name,
                {
                    "id": name,
                    "versions": set(),
                    "frameworks": set(),
                    "direct": False,
                    "runtime": set(),
                    "compile": set(),
                },
            )
            record["versions"].add(version)
            record["frameworks"].add(framework)
            record["direct"] = record["direct"] or name in direct
            record["runtime"].update(runtime)
            record["compile"].update(compile_assets)
    return packages


def output_dirs(project_dir):
    """The build-output roots of a project, if it has been built.

    `bin/` is the root; the configuration and target-framework directories
    live under it. A project that was never built has none, and that is a
    fact the caller has to be able to see rather than infer.
    """
    base = os.path.join(project_dir, "bin")
    return [base] if os.path.isdir(base) else []


def output_dlls(project_dir):
    """The DLLs sitting in a project's build output tree, if it has one.

    The resolved graph says what WOULD be copied into the output directory.
    This says what IS in it, for a tree that has been built. Both are asked
    because they fail independently: a restore-only checkout has no `bin/`,
    and a stale `bin/` from an older commit says something the graph does
    not.
    """
    base = os.path.join(project_dir, "bin")
    found = []
    if not os.path.isdir(base):
        return found
    for dirpath, dirnames, filenames in os.walk(base):
        dirnames[:] = [d for d in dirnames if d != "ref"]
        for name in filenames:
            if name.lower().endswith(".dll"):
                found.append(os.path.join(dirpath, name))
    return sorted(found)


# ---------------------------------------------------------------------------
# The analysis
# ---------------------------------------------------------------------------


def analyse(root, allow_missing_assets=False, output_only=False):
    """Return (census, findings). Both are derived; neither is declared.

    `output_only` asks the issue's question literally and nothing else: is a
    banned assembly in the OUTPUT DIRECTORY of a scope project? It reads no
    `.csproj` and no resolved graph, so it needs no restore and no waiver -
    which is what makes it the right mode for a job that has already built
    the tree.
    """
    findings = []
    census = {
        "projects": {},
        "banned": [],
        "exceptions": [],
        "totals": {},
    }

    banned = banned_names(root)
    census["banned"] = sorted(banned)
    if not banned:
        # The gate with an empty ban set reports a clean tree for the same
        # reason a gate whose matcher stopped matching does: it found
        # nothing because it was looking for nothing. Reported, not passed.
        findings.append(
            Finding(
                "PACKAGE-BLIND",
                BANNED_SYMBOLS_FILE,
                "no banned name was read. The ban set is empty, so every "
                "package in every test project is allowed by definition - a "
                "gate that bans nothing cannot report a banned package. "
                "Check that the file exists and carries at least one `N:` "
                "entry.",
            )
        )
    exceptions, exception_findings = parse_exceptions(root)
    census["exceptions"] = exceptions
    findings.extend(exception_findings)

    # The restore inputs are what produce the package set this gate reads,
    # so the question "is the tree half-migrated to lock files" belongs
    # here (#1857). It is answered for the WHOLE tree, not for the scope
    # directories: a lock file under src/ and none under tests/ is one
    # tree with two restore behaviours.
    findings.extend(restore_input_findings(root))

    projects = scope_projects(root)
    if not projects:
        findings.append(
            Finding(
                "PACKAGE-BLIND",
                "/".join(SCOPE_DIRS),
                "no project was found in scope. A gate that examined "
                "nothing cannot report that everything is fine.",
            )
        )

    used_exceptions = set()
    total_declared = 0
    total_resolved = 0
    total_output_dirs = 0
    total_output_dlls = 0

    banned_lower = {name.lower() for name in banned}

    for relative in projects:
        project_dir = os.path.dirname(relative)
        csproj = os.path.join(root, relative)
        declared = [] if output_only else declared_packages(read(csproj))
        total_declared += len(declared)

        assets_path = os.path.join(
            root, project_dir, "obj", "project.assets.json"
        )
        packages = {}
        has_graph = os.path.exists(assets_path)
        if output_only:
            has_graph = False
        elif has_graph:
            try:
                packages = resolved_packages(json.loads(read(assets_path)))
            except (ValueError, OSError) as error:
                if not allow_missing_assets:
                    findings.append(
                        Finding(
                            "PACKAGE-BLIND",
                            relative,
                            f"the resolved package graph could not be read "
                            f"({error}). Re-run `dotnet restore`.",
                        )
                    )
                has_graph = False
        elif not allow_missing_assets and not output_only:
            findings.append(
                Finding(
                    "PACKAGE-BLIND",
                    relative,
                    "no resolved package graph (obj/project.assets.json). The "
                    "transitive package set of this project is unverified, "
                    "so run `dotnet restore` before the gate. An unverified "
                    "project is not a clean project.",
                )
            )

        resolved = sorted(packages)
        total_resolved += len(resolved)

        census["projects"][relative] = {
            "declared": declared,
            "resolved": resolved,
            "direct": sorted(
                name for name, spec in packages.items() if spec["direct"]
            ),
            "has_graph": has_graph,
            "output_dlls": 0,
        }

        # One finding per (project, banned name), with every piece of
        # evidence collected first. The three sources - the `.csproj`, the
        # resolved graph and the output directory - are independent, so one
        # finding carrying all of them says more than three findings that
        # each say one thing.
        evidence = {}

        def note(name, kind, detail):
            entry = evidence.setdefault(
                name.lower(),
                {"name": name, "kinds": set(), "details": []},
            )
            entry["kinds"].add(kind)
            entry["details"].append(detail)

        for name in declared:
            if name.lower() in banned_lower:
                note(name, "package", f"declared by {os.path.basename(csproj)}")

        for name in resolved:
            spec = packages[name]
            if name.lower() in banned_lower:
                how = "direct" if spec["direct"] else "transitive"
                version = ", ".join(sorted(spec["versions"]))
                note(
                    name,
                    "package",
                    f"{version} in the resolved package graph ({how})",
                )
            for asset in sorted(spec["runtime"]):
                stem = os.path.basename(asset)
                if stem.lower().endswith(".dll"):
                    stem = stem[:-4]
                if stem.lower() not in banned_lower:
                    continue
                if stem.lower() == name.lower():
                    note(name, "package", f"runtime asset {asset}")
                else:
                    note(stem, "assembly", f"runtime asset {asset}")

        # The output directory, asked directly. The resolved graph says what
        # WOULD land there; this says what IS there.
        roots = output_dirs(os.path.join(root, project_dir))
        total_output_dirs += len(roots)
        dlls = output_dlls(os.path.join(root, project_dir))
        census["projects"][relative]["output_dirs"] = len(roots)
        census["projects"][relative]["output_dlls"] = len(dlls)
        total_output_dlls += len(dlls)
        for dll in dlls:
            stem = os.path.basename(dll)[:-4]
            if stem.lower() not in banned_lower:
                continue
            note(
                stem,
                "assembly",
                "present in the output directory at "
                + os.path.relpath(dll, root).replace(os.sep, "/"),
            )

        for lowered, entry in sorted(evidence.items()):
            name = entry["name"]
            matched = _covering_exception(exceptions, name, project_dir)
            if matched is not None:
                used_exceptions.add(matched)
                continue
            rule = (
                "BANNED-PACKAGE"
                if "package" in entry["kinds"]
                else "BANNED-ASSEMBLY"
            )
            details = "; ".join(entry["details"])
            findings.append(
                Finding(
                    rule,
                    relative,
                    f"{name} is banned by {BANNED_SYMBOLS_FILE} "
                    f"(`N:{name}`) and appears in this project: {details}. "
                    f"Either drop the dependency that drags it in, or record "
                    f"a named exception in {EXCEPTIONS_FILE} saying why this "
                    f"test project cannot do without it.",
                    name,
                )
            )
    if output_only and total_output_dirs == 0:
        findings.append(
            Finding(
                "PACKAGE-BLIND",
                "/".join(SCOPE_DIRS),
                "no build output was found for any project in scope, so the "
                "output directories were not looked at. A gate that examined "
                "nothing cannot report that everything is fine - build the "
                "tree first.",
            )
        )

    for index, exception in enumerate(exceptions):
        if index in used_exceptions:
            continue
        findings.append(
            Finding(
                "EXCEPTION-STALE",
                f"{EXCEPTIONS_FILE}:{exception.line_no}",
                f"the exception for {exception.subject} "
                f"(projects: {exception.project_glob}) matches no package "
                f"in any project in scope. An exception that outlives the "
                f"dependency that needed it is a permission with no "
                f"referent — delete it.",
                exception.subject,
            )
        )

    census["totals"] = {
        "projects": len(projects),
        "declared": total_declared,
        "resolved": total_resolved,
        "output_dirs": total_output_dirs,
        "output_dlls": total_output_dlls,
    }
    return census, findings


def _covering_exception(exceptions, name, project):
    """The index of the exception covering (name, project), or None."""
    for index, exception in enumerate(exceptions):
        if exception.matches(name, project):
            return index
    return None


# ---------------------------------------------------------------------------
# Self-test — the failure path, on every run of the gate in CI.
# ---------------------------------------------------------------------------

CSPROJ = """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
{refs}  </ItemGroup>
</Project>
"""

BANNED_SYMBOLS_FIXTURE = """# Banned APIs — fixture
N:Newtonsoft.Json
T:Newtonsoft.Json.JsonConvert
T:System.Xml.Serialization.XmlSerializer
M:System.Threading.Thread.Sleep(System.Int32)
"""

CSPROJ_PACKAGE = """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="{pkg}"/>
  </ItemGroup>
</Project>
"""


def fixture(files):
    """A throwaway tree. `files` maps repo-relative path -> contents."""
    root = tempfile.mkdtemp(prefix="test-package-gate-")
    for relative, body in files.items():
        path = os.path.join(root, relative)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as handle:
            handle.write(body)
    return root


def assets_json(packages, direct=()):
    """A minimal `project.assets.json` for a fixture.

    Only the two sections the gate reads are filled in: `targets` (what is
    resolved) and `project.frameworks` (what the project declared). The
    rest of the file is noise for this purpose, and a fixture that carried
    it would be a fixture of the file format rather than of the rule.
    """
    targets = {}
    for name, version in packages:
        entry = {"type": "package"}
        if name in direct:
            entry["dependencies"] = {}
        entry["runtime"] = {f"lib/net6.0/{name}.dll": {}}
        entry["compile"] = {f"lib/net6.0/{name}.dll": {}}
        targets[f"{name}/{version}"] = entry
    dependencies = {
        name: {"target": "Package", "version": f"[{version}, )"}
        for name, version in direct
    }
    return json.dumps(
        {
            "version": 4,
            "targets": {"net10.0": targets},
            "libraries": {
                f"{name}/{version}": {"type": "package"}
                for name, version in packages
            },
            "project": {
                "frameworks": {"net10.0": {"dependencies": dependencies}}
            },
        }
    )


def base_fixture(files=None):
    """The clean baseline every fixture starts from."""
    merged = {
        BANNED_SYMBOLS_FILE: BANNED_SYMBOLS_FIXTURE,
        EXCEPTIONS_FILE: "# no exceptions\n",
        "tests/Clean.Tests/Clean.Tests.csproj": CSPROJ_PACKAGE.format(
            pkg="TUnit.Assertions"
        ),
        "tests/Clean.Tests/obj/project.assets.json": assets_json(
            [("TUnit.Assertions", "1.61.0")], direct=[("TUnit.Assertions", "1.61.0")]
        ),
    }
    if files:
        merged.update(files)
    return fixture(merged)


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
        """The findings, readably. An object repr is not a diagnostic."""
        return " | ".join(str(f) for f in found)

    # 1. A banned package DECLARED by a .csproj must be found, with no
    #    resolved graph at all — the direct half of the question does not
    #    need a restore.
    root = base_fixture(
        {
            "tests/Bad.Tests/Bad.Tests.csproj": CSPROJ_PACKAGE.format(
                pkg="Newtonsoft.Json"
            ),
        }
    )
    _, found = analyse(root, allow_missing_assets=True)
    check(
        "a banned package declared by a .csproj is reported",
        any(
            f.rule == "BANNED-PACKAGE" and f.subject == "Newtonsoft.Json"
            for f in found
        ),
        shown(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 2. The TRANSITIVE half — the case the issue is actually about. The
    #    package is not declared anywhere; it arrives through the resolved
    #    graph, which is what a restore produces.
    root = base_fixture(
        {
            "tests/Transitive.Tests/Transitive.Tests.csproj": CSPROJ_PACKAGE.format(
                pkg="Fabrikam.Arch"
            ),
            "tests/Transitive.Tests/obj/project.assets.json": assets_json(
                [
                    ("Fabrikam.Arch", "1.0.0"),
                    ("Newtonsoft.Json", "13.0.4"),
                ],
                direct=[("Fabrikam.Arch", "1.0.0")],
            ),
        }
    )
    _, found = analyse(root)
    check(
        "a transitively resolved banned package is reported",
        any(
            f.rule == "BANNED-PACKAGE"
            and f.subject == "Newtonsoft.Json"
            and "transitive" in f.message
            for f in found
        ),
        shown(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 3. The other mechanism: a banned assembly already sitting in a
    #    project's output directory. The issue's own question, asked of the
    #    `bin/` tree rather than of the graph.
    root = base_fixture(
        {
            "tests/Output.Tests/Output.Tests.csproj": CSPROJ_PACKAGE.format(
                pkg="TUnit.Assertions"
            ),
            "tests/Output.Tests/obj/project.assets.json": assets_json(
                [("TUnit.Assertions", "1.61.0")], direct=[("TUnit.Assertions", "1.61.0")]
            ),
            "tests/Output.Tests/bin/Release/net10.0/Newtonsoft.Json.dll": "MZ",
        }
    )
    _, found = analyse(root)
    check(
        "a banned assembly in the output directory is reported",
        any(
            f.rule == "BANNED-ASSEMBLY" and f.subject == "Newtonsoft.Json"
            for f in found
        ),
        shown(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 4. A NAMED exception silences exactly that finding — and nothing else.
    root = base_fixture(
        {
            EXCEPTIONS_FILE: (
                "Newtonsoft.Json | tests/Excused.Tests | the arch library "
                "requires it | @someone\n"
            ),
            "tests/Excused.Tests/Excused.Tests.csproj": CSPROJ_PACKAGE.format(
                pkg="Newtonsoft.Json"
            ),
            "tests/Excused.Tests/obj/project.assets.json": assets_json(
                [("Newtonsoft.Json", "13.0.4")], direct=[("Newtonsoft.Json", "13.0.4")]
            ),
        }
    )
    _, found = analyse(root, allow_missing_assets=True)
    check(
        "a named exception silences the finding it covers",
        not any(f.rule == "BANNED-PACKAGE" for f in found)
        and not any(f.rule == "EXCEPTION-STALE" for f in found),
        shown(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 5. An exception that matches NOTHING is stale. An allowance with no
    #    referent is the shape this file exists to prevent, and it is
    #    reported rather than ignored.
    root = base_fixture(
        {
            EXCEPTIONS_FILE: (
                "Newtonsoft.Json | tests/Nothing.Tests | no longer needed | "
                "@someone\n"
            ),
        }
    )
    _, found = analyse(root)
    check(
        "an exception that matches nothing is stale",
        any(f.rule == "EXCEPTION-STALE" for f in found),
        shown(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 6. An exception with no reason (or no owner) is unjustified. A
    #    permission nobody can justify is a silent hole.
    root = base_fixture(
        {
            EXCEPTIONS_FILE: "Newtonsoft.Json | tests/Excused.Tests |  | \n",
        }
    )
    _, found = analyse(root)
    check(
        "an exception without a reason is unjustified",
        any(f.rule == "EXCEPTION-UNJUSTIFIED" for f in found),
        shown(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 7. A clean tree stays clean — the false-positive control.
    root = base_fixture()
    _, found = analyse(root)
    check("a clean tree stays green", not found, str(found))
    shutil.rmtree(root, ignore_errors=True)

    # 7a. The restore inputs are all-or-nothing (#1857). A tree where one
    #     project of a scope carries a lock file and another does not is
    #     the half-migrated tree, and it is reported: two projects in the
    #     same scope would restore differently from each other.
    root = base_fixture(
        {
            "src/One/One.csproj": "<Project Sdk=\"Microsoft.NET.Sdk\" />",
            "src/One/packages.lock.json": '{"version": 1}',
            "src/Two/Two.csproj": "<Project Sdk=\"Microsoft.NET.Sdk\" />",
        }
    )
    _, found = analyse(root)
    check(
        "a half-migrated tree is reported",
        any(f.rule == "RESTORE-INPUTS-PARTIAL" for f in found),
        shown(found),
    )
    check(
        "a lock file nobody regenerates is reported",
        any(f.rule == "RESTORE-INPUTS-UNENFORCED" for f in found),
        shown(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 7b. The property set and the lock files committed together is the
    #     green case: every project of the scope has one, and the property
    #     that writes them is set. A gate that only knows how to fail is
    #     not a gate.
    root = base_fixture(
        {
            "Directory.Build.props": (
                "<Project><PropertyGroup>"
                "<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>"
                "</PropertyGroup></Project>"
            ),
            "src/One/One.csproj": "<Project Sdk=\"Microsoft.NET.Sdk\" />",
            "src/One/packages.lock.json": '{"version": 1}',
            "src/Two/Two.csproj": "<Project Sdk=\"Microsoft.NET.Sdk\" />",
            "src/Two/packages.lock.json": '{"version": 1}',
        }
    )
    _, found = analyse(root)
    check(
        "a fully migrated tree is green",
        not any(f.rule.startswith("RESTORE-INPUTS") for f in found)
        and not any(f.rule == "RESTORE-LOCKED-WITHOUT-FILES" for f in found),
        shown(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 8. A project with NO resolved graph is BLIND, never green. "I could
    #    not verify this" and "this is clean" are not the same answer.
    root = base_fixture(
        {
            "tests/Blind.Tests/Blind.Tests.csproj": CSPROJ_PACKAGE.format(
                pkg="TUnit.Assertions"
            ),
        }
    )
    _, found = analyse(root)
    check(
        "a project with no resolved graph is BLIND, not green",
        any(f.rule == "PACKAGE-BLIND" for f in found),
        shown(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 9. The same project is NOT reported when the caller says the graph is
    #    optional — the escape hatch for a local run without a restore.
    root = base_fixture(
        {
            "tests/Blind.Tests/Blind.Tests.csproj": CSPROJ_PACKAGE.format(
                pkg="TUnit.Assertions"
            ),
        }
    )
    _, found = analyse(root, allow_missing_assets=True)
    check(
        "the blind report can be waived explicitly",
        not any(f.rule == "PACKAGE-BLIND" for f in found),
        shown(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 10. The match is EXACT and case-insensitive, as NuGet ids are. A
    #     different package whose id merely contains a banned name is not
    #     the banned package.
    root = base_fixture(
        {
            "tests/Near.Tests/Near.Tests.csproj": CSPROJ_PACKAGE.format(
                pkg="Newtonsoft.Json.Schema"
            ),
            "tests/Near.Tests/obj/project.assets.json": assets_json(
                [("Newtonsoft.Json.Schema", "3.0.0")],
                direct=[("Newtonsoft.Json.Schema", "3.0.0")],
            ),
        }
    )
    _, found = analyse(root)
    check(
        "a package whose id merely contains a banned name is not flagged",
        not any(f.rule.startswith("BANNED") for f in found),
        shown(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 11. The ban set is read from the `N:` entries and ONLY from them. A
    #     `T:` entry names a type, not a package, and a type that is not
    #     banned must not become a banned package by accident.
    root = fixture({BANNED_SYMBOLS_FILE: BANNED_SYMBOLS_FIXTURE})
    check(
        "the ban set comes from the N: entries only",
        banned_names(root) == {"Newtonsoft.Json"},
        str(banned_names(root)),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 12. `--output-only` asks the issue's question literally: a banned
    #     assembly in the OUTPUT DIRECTORY. It must find it, and it must not
    #     need a resolved graph to do so - that is what makes it usable in a
    #     job that has already built the tree.
    root = base_fixture(
        {
            "tests/Output.Tests/Output.Tests.csproj": CSPROJ_PACKAGE.format(
                pkg="TUnit.Assertions"
            ),
            "tests/Output.Tests/bin/Release/net10.0/Newtonsoft.Json.dll": "MZ",
        }
    )
    _, found = analyse(root, output_only=True)
    check(
        "output-only mode finds a banned assembly in the output directory",
        any(
            f.rule == "BANNED-ASSEMBLY" and f.subject == "Newtonsoft.Json"
            for f in found
        )
        and not any(f.rule == "PACKAGE-BLIND" for f in found),
        shown(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 13. The same mode on a tree that was never built looked at NOTHING.
    #     Reporting that is the difference between "clean" and "not checked".
    root = base_fixture()
    _, found = analyse(root, output_only=True)
    check(
        "output-only mode on a tree with no output is BLIND, not green",
        any(f.rule == "PACKAGE-BLIND" for f in found),
        shown(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 14. `--output-only` means the OUTPUT DIRECTORY and nothing else: a
    #     banned package the resolved graph knows about, but which is not in
    #     the output directory, is not this mode's business. The two
    #     mechanisms are separate questions, and a mode that quietly
    #     answered both would answer neither of them precisely.
    root = base_fixture(
        {
            "tests/Graph.Tests/Graph.Tests.csproj": CSPROJ_PACKAGE.format(
                pkg="Fabrikam.Arch"
            ),
            "tests/Graph.Tests/obj/project.assets.json": assets_json(
                [
                    ("Fabrikam.Arch", "1.0.0"),
                    ("Newtonsoft.Json", "13.0.4"),
                ],
                direct=[("Fabrikam.Arch", "1.0.0")],
            ),
        }
    )
    _, found = analyse(root, output_only=True)
    check(
        "output-only mode ignores the resolved graph",
        not any(f.rule.startswith("BANNED") for f in found),
        shown(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 15. An EMPTY ban set is blindness, not cleanliness: with nothing
    #     banned, every package in every project is allowed by definition.
    root = base_fixture({BANNED_SYMBOLS_FILE: "# nothing banned here\n"})
    _, found = analyse(root)
    check(
        "an empty ban set is reported, not passed",
        any(f.rule == "PACKAGE-BLIND" for f in found),
        shown(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 16. The gate must not be able to count itself. A rule that finds
    #     nothing because it is looking at its own text reports the zero as
    #     a result; this file lives under tools/, and the scope is tests/
    #     and samples/, so the two cannot overlap.
    here = os.path.realpath(__file__)
    inside = [
        scope
        for scope in SCOPE_DIRS
        if here.startswith(os.path.realpath(os.path.join(here, "..", "..", scope)) + os.sep)
    ]
    check(
        "the gate's own source is outside the scope",
        not inside,
        f"__file__ is inside {inside}",
    )

    if failures:
        print(
            f"\nself-test FAILED: {len(failures)} of {len(ran)} fixture(s): "
            f"{', '.join(failures)}"
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
    parser.add_argument(
        "--root",
        default=os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
        help="repository root (default: the root of this checkout)",
    )
    parser.add_argument(
        "--report",
        action="store_true",
        help="print the derived package census and exit 0 regardless",
    )
    parser.add_argument(
        "--allow-missing-assets",
        action="store_true",
        help="do not report a project whose resolved package graph is "
        "missing (a local run without a restore)",
    )
    parser.add_argument(
        "--output-only",
        action="store_true",
        help="only inspect the build output directories (the issue's "
        "literal question): is a banned assembly in the output of a scope "
        "project? Reads no .csproj and no resolved graph, so it needs "
        "neither a restore nor a waiver",
    )
    parser.add_argument("--min-projects", type=int, default=0)
    parser.add_argument("--min-declared", type=int, default=0)
    parser.add_argument("--min-packages", type=int, default=0)
    parser.add_argument("--min-output-dirs", type=int, default=0)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()

    if args.self_test:
        return selftest()

    root = os.path.abspath(args.root)
    census, findings = analyse(
        root,
        allow_missing_assets=args.allow_missing_assets,
        output_only=args.output_only,
    )

    if args.report:
        print(f"banned names (from {BANNED_SYMBOLS_FILE}): "
              f"{', '.join(census['banned']) or '-'}")
        print(f"exceptions in force: {len(census['exceptions'])}")
        for relative, data in census["projects"].items():
            print(f"\n{relative}")
            print(f"  declared PackageReference  "
                  f"{', '.join(data['declared']) or '-'}")
            print(f"  resolved packages          {len(data['resolved'])} "
                  f"({len(data['direct'])} direct)")
            print(f"  output DLLs on disk       {data['output_dlls']}")
        totals = census["totals"]
        print(
            f"\ntotals: {totals['projects']} projects, "
            f"{totals['declared']} declared references, "
            f"{totals['resolved']} resolved package entries"
        )
        distinct = sorted(
            {
                name
                for data in census["projects"].values()
                for name in data["resolved"]
            }
        )
        print(f"distinct resolved packages: {len(distinct)}")
        for name in distinct:
            print(f"  {name}")
        return 0

    floors = [("projects", args.min_projects, census["totals"]["projects"])]
    if not args.output_only:
        # The two counters below are only meaningful when the resolved graph
        # was read, so `--output-only` does not carry them: a floor that
        # compares a counter the mode never filled in is a floor that always
        # fails, which is a different defect from the one it is there for.
        floors.append(
            ("declared references", args.min_declared, census["totals"]["declared"])
        )
        floors.append(
            ("resolved packages", args.min_packages, census["totals"]["resolved"])
        )
    floors.append(
        (
            "output directories",
            args.min_output_dirs,
            census["totals"]["output_dirs"],
        )
    )
    breached = [f for f in floors if f[1] and f[2] < f[1]]

    if findings:
        print("Test/sample package-set gate — findings:\n")
        for finding in findings:
            print(f"  {finding}")
        print()
    if breached:
        for name, floor, actual in breached:
            print(
                f"  PACKAGE-BLIND  scan floor: {name} compared {actual}, "
                f"floor is {floor}"
            )
        print()

    if findings or breached:
        return 1

    totals = census["totals"]
    if args.output_only:
        print(
            f"OK: {totals['projects']} test/sample projects, "
            f"{totals['output_dirs']} output directories, "
            f"{totals['output_dlls']} assemblies inspected, "
            f"no banned assembly among them"
        )
        return 0
    print(
        f"OK: {totals['projects']} test/sample projects, "
        f"{totals['declared']} declared references, "
        f"{totals['resolved']} resolved package entries, "
        f"no banned package among them"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
