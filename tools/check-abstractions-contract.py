#!/usr/bin/env python3
"""Check the NuGet dependency surface of the two FROZEN contract projects.

Stdlib only — no pip, no network, no dotnet. Wired into .github/workflows/docs.yml
alongside check-md-links.py, md-lint.py, check-doc-cites.py and check-doc-facts.py,
and it must not overlap any of them:

  check-md-links.py   does the link resolve?
  md-lint.py          is the file shaped the way the repo says it is?
  check-doc-cites.py  does a `file:line` still point at a line, and a named type
                      still exist?
  check-doc-facts.py  does a COUNT a document states still equal the count of
                      tracked files it is counting?
  THIS ONE            is every <PackageReference> the two contract projects
                      declare actually load-bearing, and is every non-BCL
                      namespace their sources use actually covered by a
                      declaration?

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
                           covered by any declared package. A used edge nothing
                           declared.
  CONTRACT-BLIND          A scan dimension came back empty, which this tool
                           reports rather than passes: a pattern edited until it
                           matches nothing is indistinguishable from a tree with
                           nothing to declare, and only one of those is clean.
                           This is the #591 shape.

WHY A PROJECTREFERENCE IS INSIDE THE SCOPE OF "USED"

  `Harbor.Abstractions.csproj` states its own rationale in a comment: it is a
  facade that re-publishes the contract models, so it legitimately re-declares
  what `Harbor.Abstractions.Contracts` already declares — `MemoryPack` is used
  only in the referenced project and is declared in both. Treating a facade's
  own sources as the whole population would report that as phantom and the tool
  would be pushing an owner decision (drop the re-export, or keep it) dressed as
  a rule. So one hop of `<ProjectReference>` counts as reachable, and the finding
  that remains is the one that is actually unreachable by any reading.

NON-VACUITY

  --min-packages / --min-namespaces / --min-files are floors on how much was
  compared, wired into docs.yml like the other gates' floors. Measured today:
  9 packages declared, 17 distinct non-BCL namespaces used, 75 `.cs` files. The
  floors sit below those numbers and are a ratchet: a PR that genuinely removes a
  dependency lowers them in the same diff a reviewer can see.

  --self-test runs five fixtures covering every vacuity shape measured while
  writing this: a declared package nothing uses, a used namespace nothing
  declares, a tree that is genuinely clean, a project with no declarations at
  all (must report BLIND, never pass), and a package whose only "use" is inside
  an XML doc comment (must still report phantom — the half-halving that gave
  #591 its plausible zeros). Two of the five failed against the first version of
  this file, which is why they are here.

WHAT THIS DOES NOT DO, STATED RATHER THAN IMPLIED

  * It does not read any document. See WHY THE COMPARISON IS AGAINST FILES.
  * It does not decide what the contract's dependency set SHOULD be. It reports
    declared-but-unreachable and used-but-undeclared; shrinking the set is the
    owner's call, and #471 already recorded that structural calls on these two
    projects are separate decisions.
  * It does not resolve transitive NuGet dependencies. It reads declarations and
    source. A package whose namespace is used only in a package it depends on is
    invisible here, and that is stated rather than papered over.
  * It does not read `src/Harbor.Extensions` or `src/Harbor.Application`, which
    declare `CommunityToolkit.HighPerformance` and do not use it either. Measured
    and reported, deliberately not changed: they are outside this slice.
"""
import argparse
import os
import re
import shutil
import sys
import tempfile

# The frozen contract projects, and the one-hop ProjectReference each is allowed
# to reach. Read from the .csproj files at run time — nothing here is a table of
# what a project "is supposed to" reference.
FROZEN = (
    "src/Harbor.Abstractions",
    "src/Harbor.Abstractions.Contracts",
)

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
# wins. Three separate passes cannot do this and the difference is not academic:
# blanking `//` first ate the closing quote of a URL-bearing string literal, which
# unbalances the string scanner and silently swallows every `using` after it. That
# is not a hypothetical — it is what the first version of this file did, and it
# under-reported the facade's real dependency surface by three packages.
_NOISE = re.compile(
    # A raw string literal FIRST: `"""…"""` spans lines, so the ordinary
    # string alternative would match it as three empty strings and leave the
    # body as live source. A line inside a raw string can start with `using`,
    # which is how the self-test caught this.
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


def analyse(root):
    """Return (census, findings). Both are derived; neither is declared."""
    census = {"projects": {}, "totals": {}}
    findings = []
    total_packages = 0
    total_namespaces = set()
    total_files = 0

    for project in FROZEN:
        csproj = os.path.join(root, project, project.rsplit("/", 1)[-1] + ".csproj")
        if not os.path.exists(csproj):
            findings.append(
                Finding(
                    "CONTRACT-BLIND",
                    project,
                    "no .csproj at the frozen path — the freeze is guarding "
                    "nothing if the project is not where the freeze says it is",
                )
            )
            continue

        xml = read(csproj)
        declarations = []
        for match in _PACKAGE_REF.finditer(xml):
            element = match.group(0)
            include = _INCLUDE.search(element)
            if not include:
                continue
            declarations.append(
                {"id": include.group(1), "generator": is_generator_package(element)}
            )
        packages = sorted({d["id"] for d in declarations})
        generators = sorted(d["id"] for d in declarations if d["generator"])
        project_refs = _PROJECT_REF.findall(xml)

        own = cs_files(root, project)
        # One hop of ProjectReference: a facade legitimately re-declares what the
        # contract project it fronts declares. See the module docstring. The hop
        # is taken to the referenced file's DIRECTORY — handing cs_files the
        # .csproj path itself walks a file, finds nothing, and reports the
        # facade's re-exports as phantom, which is how the first version of this
        # file mis-called MemoryPack.
        reachable = list(own)
        for ref in project_refs:
            target = os.path.normpath(
                os.path.join(root, project, ref.replace("\\", "/"))
            )
            reachable.extend(
                cs_files(root, os.path.relpath(os.path.dirname(target), root))
            )

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
                        "CONUSED",
                        project,
                        f"PackageReference {package} is declared but no .cs file in "
                        f"this project or in a project it ProjectReferences has a "
                        f"`using` under one of its namespace prefixes "
                        f"({', '.join(sorted(prefixes))}). A dependency nobody reaches "
                        f"on the project #428 freezes is a promise with no referent.",
                        package,
                    )
                )

        # --- rule 2: used but nothing declares it --------------------------
        declared_prefixes = set()
        for package in packages:
            declared_prefixes |= namespace_prefixes(package)
        for namespace in external:
            if not any(
                namespace == p or namespace.startswith(p + ".")
                for p in declared_prefixes
            ):
                findings.append(
                    Finding(
                        "CONTRACT-UNDECLARED-PACKAGE",
                        project,
                        f"`using {namespace}` is not covered by any PackageReference "
                        f"in {project.rsplit('/', 1)[-1]}.csproj. The frozen contract's "
                        f"dependency surface grew without a declaration to justify it.",
                        namespace,
                    )
                )

        # --- rule 3: blindness --------------------------------------------
        if not packages:
            findings.append(
                Finding(
                    "CONTRACT-BLIND",
                    project,
                    "no PackageReference at all — either the contract really is "
                    "declaration-free, or this gate stopped seeing the element. "
                    "Reporting rather than passing: those are not the same tree.",
                )
            )
        if not own:
            findings.append(
                Finding(
                    "CONTRACT-BLIND",
                    project,
                    "no .cs file found under the frozen project path.",
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


def fixture(files):
    """A throwaway tree. `files` maps repo-relative path -> contents."""
    root = tempfile.mkdtemp(prefix="contract-gate-")
    for rel, body in files.items():
        path = os.path.join(root, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as handle:
            handle.write(body)
    return root


def selftest():
    failures = []

    def check(name, condition, detail=""):
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
        any(f.rule == "CONUSED" and f.subject == "Contoso.Numerics" for f in found),
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
        any(f.rule == "CONUSED" and f.subject == "Contoso.Numerics" for f in found),
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
        not any(f.rule == "CONUSED" for f in found),
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
        not any(f.rule == "CONUSED" for f in found),
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
        any(f.rule == "CONUSED" and f.subject == "Contoso.Numerics" for f in found),
        str(found),
    )
    shutil.rmtree(root, ignore_errors=True)

    if failures:
        print(f"\nself-test FAILED: {len(failures)} fixture(s): {', '.join(failures)}")
        return 1
    print("\nself-test passed: 10 fixtures")
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
        print("Frozen-contract dependency gate — findings:\n")
        for finding in findings:
            print(f"  {finding}")
        print()
    if breached:
        for name, floor, actual in breached:
            print(f"  CONTRACT-BLIND  scan floor: {name} compared {actual}, floor is {floor}")
        print()

    if breached and not findings:
        print("Frozen-contract dependency gate: clean, but a scan floor was breached.")
        print()
    return 1 if (findings or breached) else 0


if __name__ == "__main__":
    sys.exit(main())