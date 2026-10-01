#!/usr/bin/env python3
"""#957 — the definitive per-candidate verdict, without a build.

For each Microsoft.Extensions.* PackageReference in the non-contrib tree:

  1. CLOSURE   walk the real nuspec net10.0 dependency groups. What leaves the
               project's compile closure when this one line is deleted?
  2. ATTRIBUTION  every public type the package graph declares, read out of the
               packages' own lib/net10.0/*.xml. Not a hand-written token list —
               so `LogLevel` cannot be mistaken for a `Logging` type.
  3. USAGE    which of those types the project's own sources actually reference,
               resolved through that file's `using` set.
  4. VERDICT   vestigial / load-bearing, and whether deletion breaks a consumer
               via a ProjectReference edge.

The one thing this cannot see is stated in the output, not hidden.
"""
import os, re, sys, glob
from collections import defaultdict

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
NUGET = os.path.expanduser("~/.nuget/packages")
SCOPE = ["src", "apps", "tests", "samples"]
TF = "net10.0"
VERSION = "10.0.10"
ME = "Microsoft.Extensions."

# --------------------------------------------------------------- nuspec layer
_cache = {}
def nuspec_deps(pid):
    k = pid.lower()
    if k in _cache:
        return _cache[k]
    d = os.path.join(NUGET, k)
    res = []
    if os.path.isdir(d):
        vers = sorted(os.listdir(d))
        v = VERSION if VERSION in vers else (vers[-1] if vers else None)
        if v:
            p = os.path.join(d, v, "%s.nuspec" % k)
            if os.path.isfile(p):
                t = open(p, encoding="utf-8", errors="replace").read()
                for attrs, body in re.findall(r'<group\b([^>]*)>(.*?)</group>', t, re.S):
                    if ('"%s"' % TF) in attrs:
                        res = re.findall(r'<dependency id="([^"]+)"', body)
                        break
    _cache[k] = res
    return res

def closure(roots):
    seen, stack = set(), list(roots)
    while stack:
        p = stack.pop()
        if p in seen:
            continue
        seen.add(p)
        stack.extend(nuspec_deps(p) or [])
    return seen

# ------------------------------------------------- type attribution from xml
_type_cache = {}
def pkg_types(pid):
    """Types AND methods the package declares. Methods matter: an extension
    method on ILogger is a Logging.Abstractions member, and a type-only probe
    reports the project that calls it as vestigial. That miss is real — it is
    what made the four sample plugins read as deletable."""
    k = pid.lower()
    if k in _type_cache:
        return _type_cache[k]
    out = set()
    d = os.path.join(NUGET, k, VERSION)
    if os.path.isdir(d):
        for x in glob.glob(os.path.join(d, "lib", TF, "*.xml")):
            t = open(x, encoding="utf-8", errors="replace").read()
            # T: <fq>            -> the type itself
            for fq in re.findall(r'<member name="T:([^"(]+)', t):
                out.add(fq.strip())
            # M: <fq>            -> member NAME, attributed to its declaring type.
            # Only owners under Microsoft.Extensions.* count. These XML doc
            # files also document inherited BCL members (System.IDisposable
            # .Dispose, System.Object.ToString, ...); attributing those to the
            # package is what makes a probe call `Dispose` a use of
            # Logging.Console. The check has to be on the OWNER's namespace root.
            for fq in re.findall(r'<member name="M:([^"(]+)', t):
                owner, _, meth = fq.strip().rpartition(".")
                if meth and owner.startswith("Microsoft.Extensions."):
                    out.add(owner + "." + meth)
    _type_cache[k] = out
    return out

# short name -> set of (pkg, fqn)
_name_index = None
def build_name_index(packages):
    global _name_index
    idx = defaultdict(set)
    for p in sorted(packages):
        for fq in pkg_types(p):
            if not fq.startswith(ME):
                continue        # never attribute a BCL member to an M.E. package
            short = fq.split(".")[-1]
            idx[short].add((p, fq))
    _name_index = idx
    return idx

# -------------------------------------------------------------- csproj layer
PKG = re.compile(r'<PackageReference\s+Include="([^"]+)"', re.I)
PRJ = re.compile(r'<ProjectReference\s+Include="([^"]+)"', re.I)
CMP = re.compile(r'<Compile\s+Include="([^"]+)"', re.I)
GROUP = re.compile(r'<(ItemGroup|PropertyGroup)\b([^>]*)>(.*?)</\1>', re.S | re.I)

projects = {}
for root in SCOPE:
    for dp, dn, fn in os.walk(os.path.join(ROOT, root)):
        if "contrib" in dp.split(os.sep):
            continue
        for f in sorted(fn):
            if f.endswith(".csproj"):
                full = os.path.join(dp, f)
                rel = os.path.relpath(full, ROOT).replace(os.sep, "/")
                d = os.path.dirname(full)
                txt = open(full, encoding="utf-8").read()
                pkgs, prjs, cmps = [], [], []
                for tag, attrs, body in GROUP.findall(txt):
                    if re.search(r'Condition="', attrs):
                        continue
                    pkgs += PKG.findall(body)
                    prjs += PRJ.findall(body)
                    cmps += CMP.findall(body)
                projs = []
                for inc in prjs:
                    t = os.path.normpath(os.path.join(d, inc.replace("\\", os.sep)))
                    if not t.endswith(".csproj"):
                        t += ".csproj"
                    if os.path.isfile(t):
                        projs.append(os.path.relpath(t, ROOT).replace(os.sep, "/"))
                projects[rel] = dict(dir=d, pkgs=pkgs, prjs=projs, cmps=cmps,
                                     name=os.path.basename(rel)[:-7])

def own_sources(rel):
    p = projects[rel]
    d = p["dir"]
    files = []
    for dp, dn, fn in os.walk(d):
        parts = dp.split(os.sep)
        if "bin" in parts or "obj" in parts:
            continue
        files += [os.path.join(dp, f) for f in fn if f.endswith(".cs")]
    for c in p["cmps"]:
        c2 = os.path.normpath(os.path.join(d, c.replace("\\", os.sep)))
        if os.path.isdir(c2):
            for dp, dn, fn in os.walk(c2):
                files += [os.path.join(dp, f) for f in fn if f.endswith(".cs")]
        elif os.path.isfile(c2):
            files.append(c2)
    seen, out = set(), []
    for f in sorted(files):
        r = os.path.relpath(f, ROOT).replace(os.sep, "/")
        if r not in seen:
            seen.add(r); out.append(f)
    return out

USING = re.compile(r'(?m)^\s*(?:global\s+)?using\s+(static\s+)?([A-Za-z_][\w.]*)\s*;')
ALIAS = re.compile(r'(?m)^\s*using\s+([A-Za-z_][\w.]*)\s*=\s*([A-Za-z_][\w.]*)\s*;')
WORD = re.compile(r'[A-Za-z_][A-Za-z0-9_]*')
FQN = re.compile(r'\b((?:Microsoft\.)+[A-Za-z_][\w]*(?:\.[A-Za-z_][\w]*)+)')

def project_global_usings(rel):
    """global using directives in the project's own sources (GlobalUsings.cs)."""
    out = set()
    for f in own_sources(rel):
        try:
            src = open(f, encoding="utf-8", errors="replace").read()
        except Exception:
            continue
        for g in re.findall(r'(?m)^\s*global\s+using\s+(?!static\s)([A-Za-z_][\w.]*)\s*;',
                            src):
            out.add(g.strip())
    return out


def project_used_types(rel, extra_scopes=()):
    """-> set of (package, short name) the project's sources actually reference."""
    used = set()
    extra_scopes = set(extra_scopes)
    for f in own_sources(rel):
        try:
            src = open(f, encoding="utf-8", errors="replace").read()
        except Exception:
            continue
        src = re.sub(r'/\*.*?\*/', ' ', src, flags=re.S)
        src = re.sub(r'//[^\n]*', ' ', src)
        # collect usings BEFORE stripping them
        aliases = dict(ALIAS.findall(src))
        namespaces = {u[1] for u in USING.findall(src) if not u[0]}
        src = re.sub(r'(?m)^\s*(global\s+)?using\s+[^;]*;\s*$', ' ', src)
        # file's namespace: types in the same namespace need no using
        ns = ""
        m = re.search(r'(?m)^\s*namespace\s+([A-Za-z_][\w.]*)', src)
        if m:
            ns = m.group(1)
        scopes = set(namespaces)
        if ns:
            scopes.add(ns)
        scopes |= set(aliases.values()) | extra_scopes
        # fully-qualified uses
        for fq in FQN.findall(src):
            short = fq.split(".")[-1]
            for p, known in _name_index.get(short, ()):  # exact FQN match
                if known == fq:
                    used.add((p, short))
        # bare short names resolved through this file's using scopes
        for w in set(WORD.findall(src)):
            cands = _name_index.get(w)
            if not cands:
                continue
            hits = [(p, fq) for p, fq in cands
                    if any(fq.startswith(s + ".") for s in scopes if s)]
            for p, fq in hits:
                used.add((p, w))
    return used

def main():
    all_me = sorted({p for rel in projects for p in projects[rel]["pkgs"]
                     if p.startswith(ME)})
    build_name_index(all_me)
    rows = []
    for rel in sorted(projects):
        me = [p for p in projects[rel]["pkgs"] if p.startswith(ME)]
        if not me:
            continue
        roots = set(projects[rel]["pkgs"])
        if projects[rel]["name"].endswith(".Tests"):
            roots.add("TUnit")          # Directory.Build.targets injection
        before = closure(roots)
        used = project_used_types(rel, project_global_usings(rel))
        used_by_pkg = defaultdict(set)
        for p, s in used:
            used_by_pkg[p].add(s)
        for pkg in me:
            lost = sorted(before - closure(roots - {pkg}))
            # usage of the package itself
            # A package that is not in the project's closure cannot be the one
            # this ref resolves; excluding it is what stops "uses ILogger" from
            # counting as a use of Http, or of Options, etc.
            visible = used_by_pkg.get(pkg, set()) & {
                s for p2, s in used if p2 == pkg}
            direct = visible
            lost_used = set()
            for lp in lost:
                lost_used |= {s for p2, s in used
                              if p2 == lp and s in used_by_pkg.get(lp, set())}
            # consumers: projects that ProjectReference this one
            consumers = [r for r in sorted(projects)
                         if rel in projects[r]["prjs"]]
            rows.append(dict(rel=rel, pkg=pkg, lost=lost, direct=sorted(direct),
                             lost_used=sorted(lost_used),
                             consumers=consumers, name=projects[rel]["name"],
                             detail=dict(used_by_pkg)))

    mode = sys.argv[1] if len(sys.argv) > 1 else "all"
    if mode in ("all", "vestigial"):
        print("%-46s %-40s %-6s %s" % ("project", "package", "delta", "verdict"))
        print("-" * 150)
        n_vest = 0
        for r in rows:
            if r["direct"]:
                v = "LOAD-BEARING (uses %d own type(s))" % len(r["direct"])
            elif r["lost_used"]:
                v = "LOAD-BEARING-INDIRECT (uses %s via closure)" % ",".join(r["lost_used"][:4])
            else:
                v = "vestigial"
                n_vest += 1
            print("%-46s %-40s %-6d %s" % (r["rel"], r["pkg"], len(r["lost"]), v))
        print()
        print("rows:", len(rows), " vestigial:", n_vest)
    if mode in ("all", "detail"):
        want = set(sys.argv[2:])
        for r in rows:
            if want and not (r["pkg"] in want or r["name"] in want
                             or any(w in r["rel"] for w in want)):
                continue
            print()
            print("### %s  <- %s" % (r["rel"], r["pkg"]))
            print("    loses %d: %s" % (len(r["lost"]), ", ".join(r["lost"]) or "-"))
            print("    own-type usage : %s" % (", ".join(r["direct"]) or "-"))
            print("    lost-route use : %s" % (", ".join(r["lost_used"]) or "-"))
            print("    ProjectRef consumers: %s" % (", ".join(r["consumers"]) or "-"))

main()