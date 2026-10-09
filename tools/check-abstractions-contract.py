#!/usr/bin/env python3
"""Check the NuGet dependency surface of the projects #428 guards.

Stdlib only — no pip, no network, no dotnet. Wired into .github/workflows/docs.yml
alongside check-md-links.py, md-lint.py, check-doc-cites.py and check-doc-facts.py,
and it must not overlap any of them:

  check-md-links.py   does the link resolve?
  md-lint.py          is the file shaped the way the repo says it is?
  check-doc-cites.py  does a `file:line` still point at a line, and a named type
                      still exist?
  check-doc-facts.py  does a COUNT a document states still equal the count of
                      tracked files it is counting?
  THIS ONE            is every <PackageReference> a guarded project declares
                      actually load-bearing, and is every non-BCL namespace its
                      sources use actually covered by a declaration?

WHY THIS FILE EXISTS (issue #428, slice: the Harbor.Abstractions contract)

  #428 asks for v1.0 to FREEZE the public API of `Harbor.Abstractions` +
  `Harbor.Abstractions.Contracts`. Those two projects are the only ones every
  other Harbor assembly points inward at, so a dependency that lands in either
  one is a dependency every consumer inherits — which is why the freeze has to
  be a gate and not a promise in a README.

  The promise is currently made in prose and is false. Measured on dev@fa93ebcb
  by this file's own `--report` mode:

    Harbor.Abstractions           7 PackageReference, 6 load-bearing
    Harbor.Abstractions.Contracts 2 PackageReference, 2 load-bearing

  `AGENTS.md` states the contract as "zero-dep (only CSharpFunctionalExtensions)"
  and six documents repeat a "zero deps" claim. Reality is 7 packages on the
  facade. Worse than the wrong number: `CommunityToolkit.HighPerformance` is
  declared and NOTHING uses it — not this project, not the project it references,
  not one file in `src/`. The `Frozen*` usage it looks like it was added for is
  `System.Collections.Frozen`, BCL since .NET 8, and the `Span<T>` / `HashData`
  its README bullet justifies it with are `System` and
  `System.Security.Cryptography`. It is a phantom edge on the exact project the
  freeze is about, and nothing in the tree notices.

WHY THE PERIMETER IS FOUR PROJECTS AND NOT TWO (#1016 follow-up)

  Removing that one edge left the same phantom standing in two more projects that
  the first version of this file named in its own "what this does not do" section
  and left alone: `src/Harbor.Extensions` and `src/Harbor.Application` each
  declared `CommunityToolkit.HighPerformance` and used nothing from it. Widening
  is the obvious continuation and costs no code, so it was done — but the premise
  that it was free was measured rather than assumed, and the measurement changed
  two things:

  * The `App.Avalonia` worry is FALSE, and was worth checking. `App.Avalonia` is
    not in `Harbor.slnx` and builds only transitively (via
    `Harbor.App.Avalonia.Tests`, #1008), so a package dropped from a project it
    references would break a build that is not in the local feedback loop. It
    turns out every `using CommunityToolkit` in the repository —
    `CommunityToolkit.Mvvm.ComponentModel` / `.Input` / `.Messaging` — belongs to
    `CommunityToolkit.Mvvm`, a DIFFERENT package, which `App.Avalonia` declares
    itself. Read out of the NuGet cache, `CommunityToolkit.Mvvm` 8.4.2's nuspec
    has no dependency on `CommunityToolkit.HighPerformance` either: the two
    dependency graphs are disjoint. So the edge carried nothing and removing it
    cannot reach `App.Avalonia`. That is why `src/Harbor.Application` is in this
    list and why its copy of the edge is gone.

  * The attribution was wrong in the way the phantom itself is. Both projects do
    use `System.Collections.Frozen` — `DefaultFileTreePolicy`,
    `PathArgExtractionPolicy`, `FileClaimRegistry` in `Harbor.Application`;
    `CollectionExtensions` in `Harbor.Extensions` — which is what the package was
    evidently added for. `System.Collections.Frozen` has been in-box since .NET 8,
    so the real dependency is on the framework and no `PackageReference` declares
    it. `Harbor.Application`'s pooled-`StringBuilder` story is the same shape: the
    README credited `CommunityToolkit.HighPerformance` for it, and the pooling is
    `Harbor.Extensions.StringBuilderPool`, a hand-written `ConcurrentBag` pool.
    A phantom that hides a satisfied BCL need is the more interesting half of this
    finding, because the code reads as if it needed the package.

  `Directory.Packages.props` keeps its `PackageVersion` pin for the package even
  though no `csproj` references it. That is deliberate and is not a phantom edge
  in this gate's sense: a `PackageVersion` is a version catalogue entry, not a
  declared dependency, and with `CentralPackageTransitivePinningEnabled=true` it
  is what a transitive consumer of the package would resolve against. This gate
  counts `<PackageReference>` elements, so the pin costs it nothing.

WHY THE COMPARISON IS AGAINST FILES, NOT AGAINST PROSE

  #939 is why: a gate that cross-checks the COPIES of a prose fact is red on
  copies that are allowed to be wrong and green on the live defect — six of
  thirteen definitions of one term disagree with each other and no amount of
  reading separates them. So this gate never reads a document. Both sides of
  every comparison are files: the `<PackageReference>` elements of the two
  `.csproj` files against the `using` directives of the `.cs` files they
  compile. A README that lies about this is out of this gate's reach, which is
  stated here rather than implied — this gate measures the contract, it does not
  police the prose describing the contract.

WHY THERE IS NO LIST OF PACKAGES OR TYPES IN THIS FILE

  A hand-maintained table of expected dependencies is the failure #847 closed
  (an allowance with no reader), #921 deleted (an allowance nothing consumed),
  and #594 standardised against. Such a table also drifts: it is a copy of the
  answer, and a copy of the answer is the thing #933 is about.

  So nothing here is enumerated. The only knowledge encoded is a NAMING
  CONVENTION for turning a package id into the namespace prefixes that package
  plausibly owns — `Microsoft.Extensions.Logging.Abstractions` owns
  `Microsoft.Extensions.Logging`, `CommunityToolkit.HighPerformance` owns
  `CommunityToolkit.HighPerformance` and `CommunityToolkit`. That is a function
  of the id, not a record of the repository: give it a package it has never seen
  and it still produces an answer, and give it a package nobody references and it
  still produces one. A convention can be wrong about a package; a table can only
  be right or stale.

RULES

  CONTRACT-UNUSED-PACKAGE  A `<PackageReference>` covers no namespace that any
                           `.cs` file in the project — or, for a facade, in a
                           project it directly `<ProjectReference>`s — actually
                           `using`s. A declared edge nothing reaches.
  CONTRACT-UNDECLARED-PACKAGE
                           A `.cs` file uses a namespace that is neither BCL,
                           nor this solution's own `Harbor.*`/`internal*`, nor
                           covered by a package this project declares, nor by one
                           a project it `<ProjectReference>`s declares. A used
                           edge nothing declared.
  CONTRACT-BLIND          A scan dimension came back empty, which this tool
                           reports rather than passes: a pattern edited until it
                           matches nothing is indistinguishable from a tree with
                           nothing to declare, and only one of those is clean.
                           This is the #591 shape.

  The emitted rule name is `CONTRACT-UNUSED-PACKAGE`, which is what this section
  has always called it. It was emitted as `CONUSED` — a truncation of a different
  length than the other two rules' — while the prose above already spelled it out.
  A gate whose output names do not match its own specification is a gate a reader
  has to reconcile by hand before they can trust it, and nothing outside this file
  ever matched the short name, so the output now says what the file says.

WHY A PROJECTREFERENCE IS REACHABLE FROM BOTH SIDES

  `Harbor.Abstractions.csproj` states its own rationale in a comment: it is a
  facade that re-publishes the contract models, so it legitimately re-declares
  what `Harbor.Abstractions.Contracts` already declares — `MemoryPack` is used
  only in the referenced project and is declared in both. Treating a facade's
  own sources as the whole population would report that as phantom and the tool
  would be pushing an owner decision (drop the re-export, or keep it) dressed as
  a rule. So one hop of `<ProjectReference>` counts as reachable, and the finding
  that remains is the one that is actually unreachable by any reading.

  That reachability has to be symmetric or the gate is wrong in the other
  direction, and measuring the widened perimeter is what found it. The
  `Harbor.Application` half of the perimeter declares no
  `CSharpFunctionalExtensions`, and its `GlobalUsings.cs` uses one — supplied by
  its `Harbor.Abstractions` `ProjectReference`, which declares it. Under a
  one-sided rule that is a CONTRACT-UNDECLARED-PACKAGE, i.e. the gate would have
  demanded a declaration for a dependency the tree already has and the compiler
  already resolves, and the honest fix would have been an exemption list naming
  `CSharpFunctionalExtensions` — the allowance-with-no-reader shape of #921/#594,
  except that here it would at least have had a reader. Instead a namespace used
  by a guarded project is satisfied by a package that project declares OR by one a
  project it references declares, read out of the referenced `.csproj` at run
  time. Same reachability, both rules, no list.

  This is the distinction the widened perimeter makes load-bearing, and it is the
  difference between a phantom and a carrier:

    phantom   declared, and no `using` under it anywhere the project reaches —
              nobody asked for it, so nothing keeps it honest;
    carrier   declared, and the `using` is real but lives in the project it
              references — it re-publishes a dependency, which is what a facade
              and a shared-helper layer are FOR, and dropping it would be a
              packaging decision, not a cleanup.

  Only the first is a finding. A peer project in the same scope is NOT reach: two
  siblings in `src/` are not a reference relationship, and letting one peer's use
  vouch for another's declaration would launder every phantom in the repository
  into silence the first time a popular package appeared twice. Fixtures 11 and
  13 pin both halves of that.

  A referenced project whose `.csproj` cannot be read contributes no prefixes.
  That is the under-recognising direction, and it is the same one the convention
  above already takes: it can only make the rule stricter, and a stricter
  undeclared-package rule still fails on a compile error the build catches.

NON-VACUITY

  --min-packages / --min-namespaces / --min-files are floors on how much was
  compared, wired into docs.yml like the other gates' floors. They are set AT the
  measured counts, not below them, and that is the whole point: a floor with slack
  under it cannot tell "the tree got smaller" from "the matcher stopped matching",
  and only one of those is a change a reviewer should be made to look at. A PR
  that genuinely removes a dependency lowers them in the same diff a reviewer can
  see — that is the ratchet, and the ratchet is meant to cost something.

  The floors therefore have to be RECOMPUTED whenever the perimeter changes,
  because a perimeter change moves the numbers they are compared against:
  widening from two projects to four without lowering them would leave floors
  that can no longer fail on emptiness, which is #901 wearing a wider hat. They
  are derived from `--report`, not written from memory, and the report prints
  what the three counters are so the two can be diffed. Measured over the four
  guarded projects: 16 packages declared, 7 distinct non-BCL namespaces, 152
  `.cs` files — of which two packages are the phantoms the follow-up removed, so
  the floors in docs.yml are 14 / 7 / 152.

  --self-test runs fifteen fixtures covering every vacuity shape measured while
  writing this: a declared package nothing uses, a used namespace nothing
  declares, a tree that is genuinely clean, a project with no declarations at all
  (must report BLIND, never pass), a package whose only "use" is inside an XML doc
  comment (must still report phantom — the half-halving that gave #591 its
  plausible zeros), a source generator that has no namespace to match, a package
  family that is not framework, a facade's re-export one hop away, a `using`-shaped
  line inside a block comment and a raw string, a carrier whose use is one hop
  away, a namespace supplied by a referenced project's declaration, a peer that
  must not vouch for a sibling's phantom, a URL-bearing string literal sitting
  between the noise stripper and the `using` directives after it, and a package
  that must not claim a sibling package under the same vendor. Six of the
  fifteen failed against the first version of this file, and every fixture added
  after it was checked by mutation — it fails when its own fix is reverted, and
  the rest still pass. That is the only claim here that is not self-reported, and
  it is the one the #591 shape makes load-bearing.

WHAT THIS DOES NOT DO, STATED RATHER THAN IMPLIED

  * It does not read any document. See WHY THE COMPARISON IS AGAINST FILES.
  * It does not decide what the contract's dependency set SHOULD be. It reports
    declared-but-unreachable and used-but-undeclared; shrinking the set is the
    owner's call, and #471 already recorded that structural calls on the guarded
    projects are separate decisions.
  * It does not resolve transitive NuGet dependencies. It reads declarations and
    source, plus the declarations of projects one `<ProjectReference>` hop away. A
    package whose namespace is used only in a package it depends on is invisible
    here, and that is stated rather than papered over.
  * It does not read the `PackageVersion` pins in `Directory.Packages.props`, and
    a pin nothing references is not a finding: a version catalogue entry is not a
    declared dependency, and with transitive pinning on it is what a transitive
    consumer would resolve against.
  * It does not follow a `ProjectReference` past one hop, and it does not treat a
    peer project in the same directory tree as reachable. Both limits are
    deliberate and are the subject of the two most recent fixtures.
"""
import argparse
import os
import re
import shutil
import sys
import tempfile

# The guarded projects, and the one-hop ProjectReference each is allowed to reach.
# Read from the .csproj files at run time — nothing here is a table of what a
# project "is supposed to" reference.
#
# This started as the two projects #428 freezes. `src/Harbor.Extensions` and
# `src/Harbor.Application` were added when the same phantom package was measured
# in them; see WHY THE PERIMETER IS FOUR PROJECTS AND NOT TWO for what the
# measurement settled and, more usefully, for what it disproved. A path is a scope,
# not a claim about the repository, so widening the list is a reviewable diff.
SCOPE = (
    "src/Harbor.Abstractions",
    "src/Harbor.Abstractions.Contracts",
    "src/Harbor.Extensions",
    "src/Harbor.Application",
)
# The list is named SCOPE and not FROZEN because two of the four projects are not
# frozen. An alias left behind for "readers of the first version" would have had
# no reader — this file's own argument, applied to itself.

# Namespaces that are this repository's own, or the compiler's. A namespace in
# one of these needs no PackageReference to justify itself.
OWN_PREFIXES = ("Harbor.", "internal")

# ---------------------------------------------------------------------------
# Naming convention: package id -> plausible owning namespace prefixes.
#
# This is a function of the id, not a registry. See "WHY THERE IS NO LIST".
# ---------------------------------------------------------------------------


def namespace_prefixes(package_id):
    """Plausible namespace prefixes a NuGet package id may own.

    One convention is encoded, and it is a property of the id itself: a trailing
    `.Abstractions` is a *flavour* marker, not the namespace root.
    `Microsoft.Extensions.Logging.Abstractions` ships its types in
    `Microsoft.Extensions.Logging`, and a package named for the flavour while
    declaring the undecorated root is the single most common shape in this
    repository's package set.

    Deliberately NOT encoded: a `Vendor.Product` split, so that
    `CommunityToolkit.HighPerformance` would also be allowed to claim
    `CommunityToolkit.Mvvm.ComponentModel`. A loose vendor rule was tried and
    removed — it made CONTRACT-UNUSED-PACKAGE unable to distinguish "this
    package serves that namespace" from "something else under the same vendor
    does", which is the half-halving shape this gate is supposed to be immune
    to. The id alone is enough for every package this repo actually declares.

    Under-recognising a package can only make CONTRACT-UNDECLARED-PACKAGE
    stricter, and that direction fails safe: an undeclared namespace is a
    compile error, so the build catches it whether or not this function knew
    the package.
    """
    parts = package_id.split(".")
    candidates = {package_id}
    if parts[-1] == "Abstractions" and len(parts) > 1:
        candidates.add(".".join(parts[:-1]))
    return candidates


# ---------------------------------------------------------------------------
# Reading the tree
# ---------------------------------------------------------------------------

# One alternation, matched left to right, so whichever construct starts first
# wins, and every branch states the line bound it needs rather than inheriting
# one. `re.S` is on for the whole pattern because the raw-string and block-comment
# branches must span lines, which makes it a live hazard for the line-comment
# branch sitting next to them: `//.*` under `re.S` is not a line comment, it is
# "the rest of the file", and it silently swallows every `using` after the first
# comment. That is measured, not hypothesised — fixture 14 is the file that
# breaks when that branch is written that way. `_USING` is line-anchored, so a
# swallowed `using` reads as a namespace nothing imports, and a declared package
# then reads as phantom: the gate gets quieter as the file gets longer, which is
# the worst direction for a gate to be wrong in.
#
# Two bounds are deliberate rather than incidental. The raw-string branch is
# FIRST, because `"""…"""` would otherwise be read as three empty strings and its
# body left in as live source — a line inside a raw string can start with `using`,
# which is how the self-test caught it. The string branch is `[^\"\\\n]`, not
# `[\s\S]`, so a quote pairs only with a quote on its own line: a line-spanning
# string branch would let one stray quote pair with another arbitrarily far away
# and blank the code in between, usings included. That bound is DEFENSIVE, and it
# is worth being straight about the difference from the two above — for the C# in
# this repository, every construct the stripper meets either terminates its quotes
# on the same line or is already claimed by the raw-string and verbatim delimiters,
# so mutation testing could not produce an input where widening this branch
# changes an answer. The two branches with a fixture behind them are the ones a
# reachable edit breaks: the line-comment bound (fixture 14) and the raw-string
# branch (fixture 10).
_NOISE = re.compile(
    r'"""[\s\S]*?"""'
    r"|//[^\n]*"          # line comment
    r"|/\*.*?\*/"        # block comment
    r"|\"(?:[^\"\\\n]|\\.)*\""  # string literal
    r"|'(?:[^'\\\n]|\\.)*'",   # char literal
    re.S,
)
_USING = re.compile(
    r"^[ \t]*(?:global[ \t]+)?using[ \t]+(?:static[ \t]+)?([A-Za-z_][A-Za-z0-9_.]*)[ \t]*;",
    re.M,
)
_PACKAGE_REF = re.compile(
    r"<PackageReference\b[^>]*?/>|<PackageReference\b[^>]*?>.*?</PackageReference>", re.S
)
_INCLUDE = re.compile(r"Include\s*=\s*\"([^\"]+)\"")
# A source generator / analyzer contributes generated code and no types, so the
# signal that it is one is that its own declaration hands `analyzers` to the
# compiler and does not also hand it `compile`. Read off the element rather than
# off a list of which packages are generators — a list of those is the table this
# gate exists not to be.
_INCLUDE_ASSETS = re.compile(
    r"<IncludeAssets\b[^>]*?>(.*?)</IncludeAssets>|"
    r"<IncludeAssets\b[^>]*?Include\s*=\s*\"([^\"]+)\"",
    re.S,
)
_ASSET_TOKEN = re.compile(r"[A-Za-z]+")


def is_generator_package(element):
    """Whether a PackageReference element declares an analyzer-only package."""
    tokens = set()
    for match in _INCLUDE_ASSETS.finditer(element):
        tokens |= set(_ASSET_TOKEN.findall(match.group(1) or match.group(2) or ""))
    return "analyzers" in tokens and "compile" not in tokens
_PROJECT_REF = re.compile(
    r"<ProjectReference\s+[^>]*?Include\s*=\s*\"([^\"]+)\"", re.S
)
# A namespace is BCL when its root is one the framework ships. `Microsoft` is
# NOT in this list, and that is the whole point of the rule rather than an
# omission: `Microsoft.Extensions.Logging` / `.DependencyInjection` /
# `.Configuration` are NuGet packages, and an earlier version of this file had
# `Microsoft` here, which silently reclassified all three as framework and hid
# the facade's real dependency surface. Treating an unrecognised root as
# external is also the safe direction: it demands a declaration, and an
# undeclared namespace is a compile error the build catches.
BCL_ROOTS = ("System", "Windows", "Mono")


def strip_noise(source):
    """Blank comments and literals, preserving line count.

    Comments are blanked rather than deleted so a line number computed from the
    result still points at real source. Literals are blanked too: a package id
    quoted inside a string is not a use of that package, and #591 is the shape
    where a matcher was widened until prose satisfied it.
    """
    return _NOISE.sub(_blank, source)


def _blank(match):
    return "".join("\n" if ch == "\n" else " " for ch in match.group(0))


def read(path):
    try:
        with open(path, encoding="utf-8", errors="replace") as handle:
            return handle.read()
    except OSError:
        return ""


def cs_files(root, project):
    """Every .cs under a project, minus build output."""
    base = os.path.join(root, project)
    found = []
    for dirpath, dirnames, filenames in os.walk(base):
        dirnames[:] = [d for d in dirnames if d not in ("bin", "obj")]
        for name in sorted(filenames):
            if name.endswith(".cs"):
                found.append(os.path.join(dirpath, name))
    return sorted(found)


def namespaces_used(paths):
    """The set of namespaces a set of .cs files actually imports."""
    used = set()
    for path in paths:
        for match in _USING.finditer(strip_noise(read(path))):
            used.add(match.group(1))
    return used


def is_external(namespace):
    """Whether a namespace needs a NuGet package to justify itself."""
    if namespace in ("", "System"):
        return False
    if namespace.startswith(OWN_PREFIXES):
        return False
    root = namespace.split(".")[0]
    return root not in BCL_ROOTS


def declared_packages(xml):
    """The package ids a .csproj declares, with which of them are generators.

    One implementation for both the project under test and the project it
    references, so the two sides of the reachability rule cannot drift apart —
    a second copy of this parser is a second thing to keep honest, and the two
    sides of that rule disagreeing is exactly what produced the false
    CONTRACT-UNDECLARED-PACKAGE the widened perimeter surfaced.
    """
    declarations = []
    for match in _PACKAGE_REF.finditer(xml):
        element = match.group(0)
        include = _INCLUDE.search(element)
        if not include:
            continue
        declarations.append(
            {"id": include.group(1), "generator": is_generator_package(element)}
        )
    return sorted({d["id"] for d in declarations}), sorted(
        d["id"] for d in declarations if d["generator"]
    )


def analyse(root):
    """Return (census, findings). Both are derived; neither is declared."""
    census = {"projects": {}, "totals": {}}
    findings = []
    total_packages = 0
    total_namespaces = set()
    total_files = 0

    for project in SCOPE:
        csproj = os.path.join(root, project, project.rsplit("/", 1)[-1] + ".csproj")
        if not os.path.exists(csproj):
            findings.append(
                Finding(
                    "CONTRACT-BLIND",
                    project,
                    "no .csproj at the guarded path — the gate is guarding nothing "
                    "if the project is not where the gate says it is",
                )
            )
            continue

        xml = read(csproj)
        packages, generators = declared_packages(xml)
        project_refs = _PROJECT_REF.findall(xml)

        own = cs_files(root, project)
        # One hop of ProjectReference: a facade legitimately re-declares what the
        # contract project it fronts declares. See WHY A PROJECTREFERENCE IS
        # REACHABLE FROM BOTH SIDES. The hop is taken to the referenced file's
        # DIRECTORY — handing cs_files the .csproj path itself walks a file, finds
        # nothing, and reports the facade's re-exports as phantom, which is how
        # the first version of this file mis-called MemoryPack.
        reachable = list(own)
        hop_dirs = []
        for ref in project_refs:
            target = os.path.normpath(
                os.path.join(root, project, ref.replace("\\", "/"))
            )
            hop_dir = os.path.dirname(target)
            hop_dirs.append(hop_dir)
            reachable.extend(
                cs_files(root, os.path.relpath(hop_dir, root))
            )

        # The same hop, on the other rule: what a project this one references
        # DECLARES counts toward covering a namespace this project's own sources
        # use. A package reference brings its PackageReferences with it, so
        # `CSharpFunctionalExtensions` used by `Harbor.Application` and declared
        # by `Harbor.Abstractions` is declared as far as the compiler is
        # concerned, and a rule that said otherwise would be demanding a second
        # declaration for one dependency. A hop whose .csproj cannot be read
        # contributes nothing: under-recognising is the direction that stays
        # strict, and a too-strict undeclared-package finding still fails on a
        # compile error the build catches.
        hop_packages = []
        for hop_dir in hop_dirs:
            name = os.path.basename(hop_dir)
            hop_xml = read(os.path.join(hop_dir, name + ".csproj"))
            if hop_xml:
                hop_packages.extend(declared_packages(hop_xml)[0])

        own_ns = namespaces_used(own)
        reachable_ns = namespaces_used(reachable)
        external = sorted(n for n in own_ns if is_external(n))
        total_files += len(own)
        total_packages += len(packages)
        total_namespaces.update(external)

        census["projects"][project] = {
            "packages": packages,
            "generators": generators,
            "project_refs": project_refs,
            "hop_packages": sorted(set(hop_packages)),
            "cs_files": len(own),
            "external_namespaces": external,
            "reachable_namespaces": sorted(
                n for n in reachable_ns if is_external(n)
            ),
        }

        # --- rule 1: declared but nothing reaches it -----------------------
        for package in packages:
            if package in generators:
                continue
            prefixes = namespace_prefixes(package)
            hit = next(
                (n for n in sorted(reachable_ns) if any(n == p or n.startswith(p + ".") for p in prefixes)),
                None,
            )
            if hit is None:
                findings.append(
                    Finding(
                        "CONTRACT-UNUSED-PACKAGE",
                        project,
                        f"PackageReference {package} is declared but no .cs file in "
                        f"this project or in a project it ProjectReferences has a "
                        f"`using` under one of its namespace prefixes "
                        f"({', '.join(sorted(prefixes))}). A dependency nobody reaches "
                        f"is a promise with no referent — and a package reference "
                        f"here is one every assembly inheriting this one also "
                        f"inherits.",
                        package,
                    )
                )

        # --- rule 2: used but nothing declares it --------------------------
        # `packages` and `hop_packages` are the two halves of one question:
        # does SOMETHING in this project's reference graph declare the namespace
        # its own sources use? Kept as two lists rather than one merged set so
        # the message can name where the declaration was looked for and not
        # found, which is the difference between "you forgot a dependency" and
        # "nothing in your graph declares this".
        own_prefixes = set()
        for package in packages:
            own_prefixes |= namespace_prefixes(package)
        hop_prefixes = set()
        for package in hop_packages:
            hop_prefixes |= namespace_prefixes(package)
        for namespace in external:
            if any(
                namespace == p or namespace.startswith(p + ".")
                for p in own_prefixes | hop_prefixes
            ):
                continue
            where = (
                f"in {project.rsplit('/', 1)[-1]}.csproj or in a project it "
                f"ProjectReferences"
            )
            findings.append(
                Finding(
                    "CONTRACT-UNDECLARED-PACKAGE",
                    project,
                    f"`using {namespace}` is covered by no PackageReference {where}. "
                    f"The dependency surface grew without a declaration to justify it.",
                    namespace,
                )
            )

        # --- rule 3: blindness --------------------------------------------
        # Deliberately NOT relaxed to "no packages here AND none one hop away".
        # This dimension is the project's OWN declaration list, and a hop that
        # happens to declare something is no evidence that this project's parser
        # is still working. Reporting here is the only way an emptied pattern
        # announces itself.
        if not packages:
            findings.append(
                Finding(
                    "CONTRACT-BLIND",
                    project,
                    "no PackageReference at all in this project's own .csproj — "
                    "either the project really is declaration-free, or this gate "
                    "stopped seeing the element. Reporting rather than passing: "
                    "those are not the same tree.",
                )
            )
        if not own:
            findings.append(
                Finding(
                    "CONTRACT-BLIND",
                    project,
                    "no .cs file found under the guarded project path.",
                )
            )

    census["totals"] = {
        "packages": total_packages,
        "namespaces": len(total_namespaces),
        "cs_files": total_files,
    }
    return census, findings


class Finding:
    def __init__(self, rule, project, message, subject=None):
        self.rule = rule
        self.project = project
        self.message = message
        self.subject = subject

    def __str__(self):
        return f"{self.rule}  {self.project}: {self.message}"


# ---------------------------------------------------------------------------
# Self-test — the failure path, on every run of the gate in CI.
# ---------------------------------------------------------------------------

CSPROJ = """<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
{refs}  </ItemGroup>
</Project>
"""

# The two projects added to the perimeter after the frozen two. Every fixture
# gets them as a clean baseline unless the fixture is specifically about one of
# them, because `analyse` walks the WHOLE scope: a fixture that built only the
# frozen two would pick up a CONTRACT-BLIND for each missing project and then be
# asserting against a finding list carrying two entries that have nothing to do
# with what it is testing. Building them centrally also means the widened scope is
# walked by every fixture rather than only by the new ones, so a regression in
# the perimeter's extra projects cannot hide behind a fixture that never built
# them. The baseline declares one package and uses it — genuinely clean, so it
# contributes no findings and cannot mask one.
WIDENED = (
    "src/Harbor.Extensions",
    "src/Harbor.Application",
)


def widened_baseline():
    files = {}
    for project in WIDENED:
        name = project.rsplit("/", 1)[-1]
        files[f"{project}/{name}.csproj"] = CSPROJ.format(
            refs='    <PackageReference Include="CSharpFunctionalExtensions"/>\n'
        )
        files[f"{project}/Baseline.cs"] = "using CSharpFunctionalExtensions;\n"
    return files


def fixture(files):
    """A throwaway tree. `files` maps repo-relative path -> contents."""
    root = tempfile.mkdtemp(prefix="contract-gate-")
    merged = widened_baseline()
    merged.update(files)
    for rel, body in merged.items():
        path = os.path.join(root, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as handle:
            handle.write(body)
    return root


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

    # 1. A declared package nothing uses MUST be found.
    root = fixture(
        {
            "src/Harbor.Abstractions/Harbor.Abstractions.csproj": CSPROJ.format(
                refs='    <PackageReference Include="CSharpFunctionalExtensions"/>\n'
                '    <PackageReference Include="Contoso.Numerics"/>\n'
            ),
            "src/Harbor.Abstractions/A.cs": "using CSharpFunctionalExtensions;\n",
            "src/Harbor.Abstractions.Contracts/Harbor.Abstractions.Contracts.csproj":
                CSPROJ.format(refs='    <PackageReference Include="CSharpFunctionalExtensions"/>\n'),
            "src/Harbor.Abstractions.Contracts/B.cs": "using CSharpFunctionalExtensions;\n",
        }
    )
    _, found = analyse(root)
    check(
        "unused-package is reported",
        any(f.rule == "CONTRACT-UNUSED-PACKAGE" and f.subject == "Contoso.Numerics" for f in found),
        str(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 2. A used namespace nothing declares MUST be found.
    root = fixture(
        {
            "src/Harbor.Abstractions/Harbor.Abstractions.csproj": CSPROJ.format(
                refs='    <PackageReference Include="CSharpFunctionalExtensions"/>\n'
            ),
            "src/Harbor.Abstractions/A.cs": (
                "using CSharpFunctionalExtensions;\nusing Fabrikam.Text;\n"
            ),
            "src/Harbor.Abstractions.Contracts/Harbor.Abstractions.Contracts.csproj":
                CSPROJ.format(refs='    <PackageReference Include="CSharpFunctionalExtensions"/>\n'),
            "src/Harbor.Abstractions.Contracts/B.cs": "using CSharpFunctionalExtensions;\n",
        }
    )
    _, found = analyse(root)
    check(
        "undeclared-package is reported",
        any(
            f.rule == "CONTRACT-UNDECLARED-PACKAGE" and f.subject == "Fabrikam.Text"
            for f in found
        ),
        str(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 3. A genuinely clean tree MUST stay green — the false-positive control.
    root = fixture(
        {
            "src/Harbor.Abstractions/Harbor.Abstractions.csproj": CSPROJ.format(
                refs='    <PackageReference Include="CSharpFunctionalExtensions"/>\n'
                '    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions"/>\n'
            ),
            "src/Harbor.Abstractions/A.cs": (
                "using CSharpFunctionalExtensions;\n"
                "using Microsoft.Extensions.Logging;\n"
            ),
            "src/Harbor.Abstractions.Contracts/Harbor.Abstractions.Contracts.csproj":
                CSPROJ.format(refs='    <PackageReference Include="CSharpFunctionalExtensions"/>\n'),
            "src/Harbor.Abstractions.Contracts/B.cs": "using CSharpFunctionalExtensions;\n",
        }
    )
    _, found = analyse(root)
    check("a clean tree stays green", not found, str(found))
    shutil.rmtree(root, ignore_errors=True)

    # 4. A project with NO declarations MUST report BLIND, never pass. This is
    #    the #591 shape: an element that quietly stops matching is a green gate.
    root = fixture(
        {
            "src/Harbor.Abstractions/Harbor.Abstractions.csproj": CSPROJ.format(refs=""),
            "src/Harbor.Abstractions/A.cs": "using CSharpFunctionalExtensions;\n",
            "src/Harbor.Abstractions.Contracts/Harbor.Abstractions.Contracts.csproj":
                CSPROJ.format(refs='    <PackageReference Include="CSharpFunctionalExtensions"/>\n'),
            "src/Harbor.Abstractions.Contracts/B.cs": "using CSharpFunctionalExtensions;\n",
        }
    )
    _, found = analyse(root)
    check(
        "an empty declaration list is BLIND, not green",
        any(f.rule == "CONTRACT-BLIND" for f in found),
        str(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 5. A package named only inside an XML doc comment MUST still be reported
    #    phantom. The half-halving that gave #591 its plausible zeros was a
    #    matcher widened to read prose; this fixture fails if that happens here.
    root = fixture(
        {
            "src/Harbor.Abstractions/Harbor.Abstractions.csproj": CSPROJ.format(
                refs='    <PackageReference Include="CSharpFunctionalExtensions"/>\n'
                '    <PackageReference Include="Contoso.Numerics"/>\n'
            ),
            "src/Harbor.Abstractions/A.cs": (
                "using CSharpFunctionalExtensions;\n"
                "/// <see cref=\"Contoso.Numerics.Big\" /> is what we would use.\n"
                '// using Contoso.Numerics;\n'
                'var s = "using Contoso.Numerics;";\n'
            ),
            "src/Harbor.Abstractions.Contracts/Harbor.Abstractions.Contracts.csproj":
                CSPROJ.format(refs='    <PackageReference Include="CSharpFunctionalExtensions"/>\n'),
            "src/Harbor.Abstractions.Contracts/B.cs": "using CSharpFunctionalExtensions;\n",
        }
    )
    _, found = analyse(root)
    check(
        "a comment- and string-only mention does not count as a use",
        any(f.rule == "CONTRACT-UNUSED-PACKAGE" and f.subject == "Contoso.Numerics" for f in found),
        str(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 6. The convention must answer for a package it has never seen, which is
    #    what makes it a function rather than a table of this repository.
    check(
        "namespace_prefixes generalises to an unseen package",
        namespace_prefixes("Fabrikam.Widgets") == {"Fabrikam.Widgets"}
        and namespace_prefixes("Fabrikam.Widgets.Abstractions")
        == {"Fabrikam.Widgets.Abstractions", "Fabrikam.Widgets"}
        and namespace_prefixes("Fabrikam") == {"Fabrikam"},
        str(namespace_prefixes("Fabrikam.Widgets")),
    )

    # 7. A source generator has no namespace to match and must not be reported
    #    phantom. It ships generated code, so it ran on the compilation. This is
    #    the shape `ZLinq.DropInGenerator` is in on the real tree, and it is the
    #    one legitimate reason a declaration has no `using` under it.
    root = fixture(
        {
            "src/Harbor.Abstractions/Harbor.Abstractions.csproj": CSPROJ.format(
                refs='    <PackageReference Include="CSharpFunctionalExtensions"/>\n'
                '    <PackageReference Include="Fabrikam"/>\n'
                '    <PackageReference Include="Fabrikam.DropIn">\n'
                "      <PrivateAssets>all</PrivateAssets>\n"
                "      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>\n"
                "    </PackageReference>\n"
            ),
            "src/Harbor.Abstractions/A.cs": (
                "using CSharpFunctionalExtensions;\nusing Fabrikam;\n"
            ),
            "src/Harbor.Abstractions.Contracts/Harbor.Abstractions.Contracts.csproj":
                CSPROJ.format(refs='    <PackageReference Include="CSharpFunctionalExtensions"/>\n'),
            "src/Harbor.Abstractions.Contracts/B.cs": "using CSharpFunctionalExtensions;\n",
        }
    )
    _, found = analyse(root)
    check(
        "a source generator is not reported phantom",
        not any(f.rule == "CONTRACT-UNUSED-PACKAGE" for f in found),
        str(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 8. `Microsoft.Extensions.*` is a PACKAGE, not framework. An earlier version
    #    of this file listed `Microsoft` as a BCL root, which reclassified the
    #    whole Microsoft.Extensions family as framework and made the facade's real
    #    dependency surface invisible — a gate that reported a clean tree about a
    #    tree it was not looking at. Pinned by asserting that such a `using` is
    #    counted external AND covered by the `.Abstractions`-flavoured package.
    root = fixture(
        {
            "src/Harbor.Abstractions/Harbor.Abstractions.csproj": CSPROJ.format(
                refs='    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions"/>\n'
            ),
            "src/Harbor.Abstractions/A.cs": "using Microsoft.Extensions.Logging;\n",
            "src/Harbor.Abstractions.Contracts/Harbor.Abstractions.Contracts.csproj":
                CSPROJ.format(refs='    <PackageReference Include="CSharpFunctionalExtensions"/>\n'),
            "src/Harbor.Abstractions.Contracts/B.cs": "using CSharpFunctionalExtensions;\n",
        }
    )
    census, found = analyse(root)
    check(
        "Microsoft.Extensions.* counts as external and is covered by the package",
        not found
        and "Microsoft.Extensions.Logging"
        in census["projects"]["src/Harbor.Abstractions"]["external_namespaces"],
        str(found) + str(census["projects"]["src/Harbor.Abstractions"]),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 9. A facade's re-declared package, used only one ProjectReference hop away,
    #    is NOT phantom. The hop is taken to the referenced file's DIRECTORY;
    #    handing the walk the .csproj path itself finds no sources and reports
    #    MemoryPack unused on the real tree. Pinned here.
    root = fixture(
        {
            "src/Harbor.Abstractions/Harbor.Abstractions.csproj": CSPROJ.format(
                refs='    <PackageReference Include="MemoryPack"/>\n'
                '    <ProjectReference Include="..\\Harbor.Abstractions.Contracts\\Harbor.Abstractions.Contracts.csproj"/>\n'
            ),
            "src/Harbor.Abstractions/A.cs": "public interface I { }\n",
            "src/Harbor.Abstractions.Contracts/Harbor.Abstractions.Contracts.csproj":
                CSPROJ.format(refs='    <PackageReference Include="MemoryPack"/>\n'),
            "src/Harbor.Abstractions.Contracts/B.cs": "using MemoryPack;\n",
        }
    )
    _, found = analyse(root)
    check(
        "a facade re-export one ProjectReference hop away is not phantom",
        not any(f.rule == "CONTRACT-UNUSED-PACKAGE" for f in found),
        str(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 10. A block comment and a multi-line raw string literal can each contain a line
    #     that LOOKS like a `using` directive, because `_USING` is line-anchored
    #     and neither construct can un-anchor itself. Without the noise stripper
    #     both read as uses and a phantom declaration goes unreported. Verified by
    #     mutation: deleting `strip_noise` fails exactly this fixture and passes
    #     the other eight, which is why it exists separately from fixture 5.
    root = fixture(
        {
            "src/Harbor.Abstractions/Harbor.Abstractions.csproj": CSPROJ.format(
                refs='    <PackageReference Include="CSharpFunctionalExtensions"/>\n'
                '    <PackageReference Include="Contoso.Numerics"/>\n'
            ),
            "src/Harbor.Abstractions/A.cs": (
                "using CSharpFunctionalExtensions;\n"
                "/*\nusing Contoso.Numerics;\n*/\n"
                'internal const string Sample = """\nusing Contoso.Numerics;\n""";\n'
            ),
            "src/Harbor.Abstractions.Contracts/Harbor.Abstractions.Contracts.csproj":
                CSPROJ.format(refs='    <PackageReference Include="CSharpFunctionalExtensions"/>\n'),
            "src/Harbor.Abstractions.Contracts/B.cs": "using CSharpFunctionalExtensions;\n",
        }
    )
    _, found = analyse(root)
    check(
        "a using-shaped line inside a block comment or raw string is not a use",
        any(f.rule == "CONTRACT-UNUSED-PACKAGE" and f.subject == "Contoso.Numerics" for f in found),
        str(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 11. A CARRIER: declared here, used only by the project this one references.
    #     The same shape as fixture 9's facade re-export, but in a project the
    #     widened perimeter added, because "phantom" and "carrying" is the
    #     distinction the widening turns on and it has to hold on the new ground
    #     too — not just where the first version of the perimeter stood.
    root = fixture(
        {
            "src/Harbor.Application/Harbor.Application.csproj": CSPROJ.format(
                refs='    <PackageReference Include="CSharpFunctionalExtensions"/>\n'
                '    <PackageReference Include="Contoso.Numerics"/>\n'
                '    <ProjectReference Include="..\\Harbor.Abstractions\\Harbor.Abstractions.csproj"/>\n'
            ),
            "src/Harbor.Application/A.cs": "using CSharpFunctionalExtensions;\n",
            "src/Harbor.Abstractions/Harbor.Abstractions.csproj": CSPROJ.format(
                refs='    <PackageReference Include="CSharpFunctionalExtensions"/>\n'
            ),
            "src/Harbor.Abstractions/B.cs": (
                "using CSharpFunctionalExtensions;\nusing Contoso.Numerics;\n"
            ),
        }
    )
    _, found = analyse(root)
    check(
        "a carrier used one hop away is not a phantom",
        not any(
            f.rule == "CONTRACT-UNUSED-PACKAGE" and f.subject == "Contoso.Numerics"
            for f in found
        ),
        str(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 12. The other side of the hop. A namespace this project's own sources use,
    #     declared by a project it references and by nothing here, is DECLARED —
    #     a PackageReference carries its PackageReferences. This is a real defect
    #     the widened perimeter surfaced rather than a shape invented for the
    #     self-test: `Harbor.Application`'s GlobalUsings.cs uses
    #     `CSharpFunctionalExtensions` and its .csproj declares no such package,
    #     because `Harbor.Abstractions` — which it references — declares it. The
    #     one-sided version of this rule called that a defect, and the only way to
    #     make it quiet would have been an exemption naming the package, which is
    #     the allowance-with-no-reader shape. Verified by mutation: reverting the
    #     `hop_packages` contribution fails exactly this fixture and passes the
    #     other thirteen.
    root = fixture(
        {
            "src/Harbor.Application/Harbor.Application.csproj": CSPROJ.format(
                refs='    <PackageReference Include="ZLinq"/>\n'
                '    <ProjectReference Include="..\\Harbor.Abstractions\\Harbor.Abstractions.csproj"/>\n'
            ),
            "src/Harbor.Application/A.cs": (
                "using CSharpFunctionalExtensions;\nusing ZLinq;\n"
            ),
            "src/Harbor.Abstractions/Harbor.Abstractions.csproj": CSPROJ.format(
                refs='    <PackageReference Include="CSharpFunctionalExtensions"/>\n'
            ),
            "src/Harbor.Abstractions/B.cs": "using CSharpFunctionalExtensions;\n",
        }
    )
    _, found = analyse(root)
    check(
        "a namespace declared one hop away is not undeclared",
        not any(
            f.rule == "CONTRACT-UNDECLARED-PACKAGE"
            and f.subject == "CSharpFunctionalExtensions"
            for f in found
        ),
        str(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 13. A PEER is not a hop. Two sibling projects in the perimeter, one of them
    #     using the package and the other declaring it and using nothing: the
    #     declaring one is a phantom and must say so. This is the non-vacuity pin
    #     for the widening. Four projects in scope instead of two means every
    #     namespace is now seen in the context of three unrelated projects, and the
    #     tempting "shortcut" — treat the whole perimeter as one population, or
    #     union the namespaces of every guarded project — would turn every
    #     phantom in the repository green the moment a popular package appeared in
    #     two places. A `git grep` proves such a rule never fails: the union always
    #     contains the use, so the finding count is always zero, and zero is what
    #     a broken matcher also reports. This fixture is what tells those apart.
    root = fixture(
        {
            "src/Harbor.Extensions/Harbor.Extensions.csproj": CSPROJ.format(
                refs='    <PackageReference Include="Contoso.Numerics"/>\n'
            ),
            "src/Harbor.Extensions/A.cs": "public static class Empty { }\n",
            "src/Harbor.Application/Harbor.Application.csproj": CSPROJ.format(
                refs='    <PackageReference Include="Contoso.Numerics"/>\n'
            ),
            "src/Harbor.Application/B.cs": "using Contoso.Numerics;\n",
        }
    )
    _, found = analyse(root)
    check(
        "a peer's use does not vouch for a sibling's declaration",
        any(
            f.rule == "CONTRACT-UNUSED-PACKAGE"
            and f.subject == "Contoso.Numerics"
            and f.project == "src/Harbor.Extensions"
            for f in found
        )
        and not any(
            f.rule == "CONTRACT-UNUSED-PACKAGE"
            and f.project == "src/Harbor.Application"
            for f in found
        ),
        str(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 14. A URL-bearing string literal and a URL-bearing line comment, both
    #     followed by the `using` directives the fixture is about. `_NOISE` is
    #     compiled with `re.S` because its raw-string and block-comment branches
    #     must span lines, and `re.S` sitting one alternative away from
    #     `//[^\n]*` is a live trap: written as `//.*` that branch is not a line
    #     comment, it is "the rest of the file", and it eats every `using` after
    #     the first comment in the file. `_USING` is line-anchored, so a swallowed
    #     `using` reads as an unused namespace, a declared package reads as
    #     phantom, and the gate gets QUIETER as the file gets longer — the worst
    #     direction for a gate to be wrong in, and the direction a plausible zero
    #     comes from. Verified by mutation: `//.*` fails exactly this fixture and
    #     passes the other fourteen, so the bound is pinned rather than asserted.
    #     This is the trap the widened perimeter walks into more often, since
    #     `Harbor.Extensions` and `Harbor.Application` are four times the sources.
    root = fixture(
        {
            "src/Harbor.Application/Harbor.Application.csproj": CSPROJ.format(
                refs='    <PackageReference Include="CSharpFunctionalExtensions"/>\n'
                '    <PackageReference Include="Contoso.Numerics"/>\n'
            ),
            "src/Harbor.Application/A.cs": (
                "namespace Harbor.Application\n"
                "{\n"
                '    internal static string Docs = "https://example.invalid/api";\n'
                "    // and the mirror at http://example.invalid/api2\n"
                "    internal static class Probe { }\n"
                "\n"
                "    using CSharpFunctionalExtensions;\n"
                "    using Contoso.Numerics;\n"
                "}\n"
            ),
        }
    )
    _, found = analyse(root)
    check(
        "a URL literal and a URL comment do not swallow the usings after them",
        not any(
            f.rule == "CONTRACT-UNUSED-PACKAGE" and f.subject == "Contoso.Numerics"
            for f in found
        )
        and not any(
            f.rule == "CONTRACT-UNDECLARED-PACKAGE" and f.subject == "Contoso.Numerics"
            for f in found
        ),
        str(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 15. The convention is strict on purpose and this pins it. A declared
    #     `Contoso.Numerics` whose only nearby namespace is `Contoso.SomethingElse`
    #     is a phantom: same vendor, different package, and a rule that let a
    #     package claim its whole vendor's namespaces could not tell those apart.
    #     Such a rule was tried while writing the first version and removed, and
    #     the module docstring still says so — which made this a claim in prose
    #     with nothing behind it, the same defect the file exists to catch. This
    #     fixture is a synthetic where the hit is guaranteed: it is RED for the
    #     right reason, so a future widening of `namespace_prefixes` has to
    #     account for it rather than quietly turning a real finding into a
    #     plausible zero.
    root = fixture(
        {
            "src/Harbor.Application/Harbor.Application.csproj": CSPROJ.format(
                refs='    <PackageReference Include="CSharpFunctionalExtensions"/>\n'
                '    <PackageReference Include="Contoso.Numerics"/>\n'
            ),
            "src/Harbor.Application/A.cs": (
                "using CSharpFunctionalExtensions;\nusing Contoso.SomethingElse;\n"
            ),
        }
    )
    _, found = analyse(root)
    check(
        "a package does not claim a sibling package under the same vendor",
        any(
            f.rule == "CONTRACT-UNUSED-PACKAGE" and f.subject == "Contoso.Numerics"
            for f in found
        ),
        str(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    # 16. The gate must not be able to count itself. This is the #901 shape — a
    #     rule that finds nothing because it is looking at its own text, and
    #     reports the zero as a result. It is a property of the LAYOUT rather than
    #     of a synthetic tree, so it is asserted against the real repository: no
    #     project in the perimeter may contain this file, because a perimeter that
    #     reached `tools/` would put `CommunityToolkit.HighPerformance` — a string
    #     that appears in this file's own comments — into a scanned population and
    #     then satisfy a phantom finding with it. Cheap to assert, and the whole
    #     class of bug is invisible to a synthetic fixture by construction.
    here = os.path.realpath(__file__)
    containing = [p for p in SCOPE if here.startswith(os.path.realpath(p) + os.sep)]
    check(
        "the gate's own source is outside every guarded project",
        not containing,
        f"__file__ is inside {containing}",
    )

    if failures:
        print(f"\nself-test FAILED: {len(failures)} of {len(ran)} fixture(s): {', '.join(failures)}")
        return 1
    # Counted from the checks that actually ran rather than written into the
    # string: a hardcoded "14" beside fourteen fixtures is one more copy of the
    # answer to keep, and this file exists because copies of the answer drift.
    print(f"\nself-test passed: {len(ran)} fixtures")
    return 0


# ---------------------------------------------------------------------------


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument(
        "--root", default=".", help="repository root (default: cwd)"
    )
    parser.add_argument(
        "--report",
        action="store_true",
        help="print the derived dependency census and exit 0 regardless",
    )
    parser.add_argument("--min-packages", type=int, default=0)
    parser.add_argument("--min-namespaces", type=int, default=0)
    parser.add_argument("--min-files", type=int, default=0)
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()

    if args.self_test:
        return selftest()

    root = os.path.abspath(args.root)
    census, findings = analyse(root)

    if args.report:
        for project, data in census["projects"].items():
            print(f"\n{project}")
            print(f"  .cs files                     {data['cs_files']}")
            print(f"  ProjectReference              {', '.join(data['project_refs']) or '-'}")
            print(f"  declared by those refs        {', '.join(data['hop_packages']) or '-'}")
            print(f"  PackageReference              {', '.join(data['packages']) or '-'}")
            print(f"  of which source generators    {', '.join(data['generators']) or '-'}")
            print("  external namespaces used      " + ", ".join(data["external_namespaces"]))
            print("  reachable via ProjectReference " + ", ".join(data["reachable_namespaces"]))
        totals = census["totals"]
        print(
            f"\ntotals: {totals['packages']} packages declared, "
            f"{totals['namespaces']} external namespaces, "
            f"{totals['cs_files']} .cs files"
        )
        return 0

    floors = (
        ("packages", args.min_packages, census["totals"]["packages"]),
        ("namespaces", args.min_namespaces, census["totals"]["namespaces"]),
        ("files", args.min_files, census["totals"]["cs_files"]),
    )
    breached = [f for f in floors if f[1] and f[2] < f[1]]

    if findings:
        print("Guarded-project dependency gate — findings:\n")
        for finding in findings:
            print(f"  {finding}")
        print()
    if breached:
        for name, floor, actual in breached:
            print(f"  CONTRACT-BLIND  scan floor: {name} compared {actual}, floor is {floor}")
        print()

    if breached and not findings:
        print("Guarded-project dependency gate: clean, but a scan floor was breached.")
        print()
    return 1 if (findings or breached) else 0


if __name__ == "__main__":
    sys.exit(main())