#!/usr/bin/env python3
"""#957 — closure resolver. Answers, per project, per Microsoft.Extensions.*
PackageReference: if the line is deleted, does the package still reach the
project's compile closure by another route? Pure nuspec walk. No build.
"""
import os, re, sys
from collections import defaultdict

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
NUGET = os.path.expanduser("~/.nuget/packages")
SCOPE = ["src", "apps", "tests", "samples"]
VERSION = "10.0.10"
TF = "net10.0"
ME_PREFIX = "Microsoft.Extensions."

_nuspec_cache = {}
def nuspec_deps(pid):
    """net10.0 dependency ids for pid, or None when the package isn't cached."""
    key = pid.lower()
    if key in _nuspec_cache:
        return _nuspec_cache[key]
    d = os.path.join(NUGET, key)
    res = None
    if os.path.isdir(d):
        # pick the pinned version if present, else the highest present
        vers = sorted(os.listdir(d))
        v = VERSION if VERSION in vers else (vers[-1] if vers else None)
        if v:
            p = os.path.join(d, v, "%s.nuspec" % key)
            if os.path.isfile(p):
                t = open(p, encoding="utf-8", errors="replace").read()
                for attrs, body in re.findall(r'<group\b([^>]*)>(.*?)</group>', t, re.S):
                    if ('"%s"' % TF) in attrs:
                        res = re.findall(r'<dependency id="([^"]+)"', body)
                        break
                if res is None:          # no net10.0 group (older pkg)
                    res = []
            else:
                res = []
    _nuspec_cache[key] = res
    return res

def closure(roots, missing=()):
    """Transitive package closure of roots over the net10.0 nuspec groups."""
    seen, stack = set(), list(roots)
    while stack:
        p = stack.pop()
        if p in seen or p in missing:
            continue
        seen.add(p)
        for c in (nuspec_deps(p) or []):
            if c not in seen:
                stack.append(c)
    return seen

# ---------------------------------------------------- reverse dependency map
rev = defaultdict(set)
for d in sorted(os.listdir(NUGET)):
    if not d.startswith("microsoft.extensions"):
        continue
    for c in (nuspec_deps(d) or []):
        rev[c].add(d)

# ------------------------------------------------------------ csproj reading
PKG = re.compile(r'<PackageReference\s+Include="([^"]+)"', re.I)
PRJ = re.compile(r'<ProjectReference\s+Include="([^"]+)"', re.I)
GROUP = re.compile(r'<(ItemGroup|PropertyGroup)\b([^>]*)>(.*?)</\1>', re.S | re.I)

def read(p):
    return open(p, encoding="utf-8").read()

projects = {}
for root in SCOPE:
    for dp, dn, fn in os.walk(os.path.join(ROOT, root)):
        if "contrib" in dp.split(os.sep):
            continue
        for f in fn:
            if f.endswith(".csproj"):
                full = os.path.join(dp, f)
                rel = os.path.relpath(full, ROOT).replace(os.sep, "/")
                d = os.path.dirname(full)
                txt = read(full)
                pkgs, prjs, cond_pkgs = [], [], []
                for tag, attrs, body in GROUP.findall(txt):
                    c = re.search(r'Condition="([^"]*)"', attrs)
                    cond = c.group(1) if c else None
                    for inc in PKG.findall(body):
                        (cond_pkgs if cond else pkgs).append((inc, cond))
                    if not cond:
                        for inc in PRJ.findall(body):
                            t = os.path.normpath(os.path.join(d, inc.replace("\\", os.sep)))
                            if not t.endswith(".csproj"):
                                t += ".csproj"
                            if os.path.isfile(t):
                                prjs.append(os.path.relpath(t, ROOT).replace(os.sep, "/"))
                projects[rel] = dict(dir=d, pkgs=pkgs, prjs=prjs,
                                     cond_pkgs=cond_pkgs, name=os.path.basename(rel)[:-7])

TUNIT = "TUnit"   # injected for *.Tests by Directory.Build.targets

def own_roots(rel):
    """Packages the project pulls directly, incl. the implicit TUnit injection."""
    p = dict(projects[rel])
    pk = set(x for x, _ in p["pkgs"])
    if p["name"].endswith(".Tests"):
        pk.add(TUNIT)
    return pk

if __name__ == "__main__":
    mode = sys.argv[1]
    if mode == "rev":
        for t in sorted(rev):
            print("%-52s <- %s" % (t, ", ".join(sorted(rev[t]))))
    elif mode == "table":
        rows = []
        for rel in sorted(projects):
            pkgs = projects[rel]["pkgs"]
            me = [x for x, _ in pkgs if x.startswith(ME_PREFIX)]
            if not me:
                continue
            roots = own_roots(rel)
            full = closure(roots)
            for pkg in me:
                after = closure(roots - {pkg})
                still = pkg in after
                # which other direct ref drags it in
                routes = [r for r in (roots - {pkg})
                          if pkg in closure({r})]
                rows.append((rel, pkg, still, routes,
                             sorted(x for x in roots if x.startswith(ME_PREFIX))))
        print("%-50s %-42s %-7s %s" % ("project", "package", "reachable", "routes"))
        for rel, pkg, still, routes, me in rows:
            print("%-50s %-42s %-7s %s" % (rel, pkg,
                  "YES" if still else "NO",
                  ",".join(routes)[:70] if routes else "-"))
    elif mode == "delta":
        # For every Microsoft.Extensions.* ref: what LEAVES the compile closure
        # when the line is deleted, and is the project's own direct-ref set
        # enough to keep any of it.
        CAND = set(sys.argv[2:])
        for rel in sorted(projects):
            me = [x for x, _ in projects[rel]["pkgs"] if x.startswith(ME_PREFIX)]
            for pkg in me:
                if CAND and (pkg not in CAND) and (projects[rel]["name"] not in CAND):
                    continue
                roots = own_roots(rel)
                before, after = closure(roots), closure(roots - {pkg})
                lost = sorted(before - after)
                print("%-50s %-42s LOST(%d): %s" % (rel, pkg, len(lost),
                      ", ".join(lost)))
    elif mode == "prj":
        for rel in sorted(projects):
            me = [x for x, _ in projects[rel]["pkgs"] if x.startswith(ME_PREFIX)]
            if me:
                print("%-50s -> %s" % (rel, ",".join(projects[rel]["prjs"])))