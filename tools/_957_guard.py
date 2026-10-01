#!/usr/bin/env python3
"""#957 — scope-resolved port of VestigialExtensionsPackageReferenceRule.

Mirrors the C# guard so the algorithm can be validated without a build.
"""
import os, re, glob
from collections import defaultdict

NUGET = os.path.expanduser("~/.nuget/packages")
TF, EXT = "net10.0", "Microsoft.Extensions."
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
os.chdir(ROOT)

PINS = dict(re.findall(r'<PackageVersion Include="([^"]+)" Version="([^"]+)"',
                       open("Directory.Packages.props", encoding="utf-8").read()))

def pdir(p):
    return os.path.join(NUGET, p.lower(), PINS.get(p, "0.0.0"))

def add(names, raw):
    fq = raw.strip()
    if fq.startswith("{"):
        fq = fq[1:fq.index("}")] if "}" in fq else None
        if fq is None: return
        if "," in fq: fq = fq.split(",")[0]
        fq = fq.strip()
        if fq.startswith("T:"): fq = fq[2:]
    t = fq.find("`")
    if t >= 0: fq = fq[:t]
    b = fq.find("{")
    if b >= 0: fq = fq[:b]
    if fq.startswith(EXT): names.add(fq)

def surface(pkg):
    names = set()
    for x in glob.glob(os.path.join(pdir(pkg), "lib", TF, "*.xml")):
        t = open(x, encoding="utf-8", errors="replace").read()
        for n in re.findall(r'<member name="([TM]:[^"(]+)', t):
            add(names, n.split(":", 1)[1])
    return names or None

def deps(pkg):
    ns = glob.glob(os.path.join(pdir(pkg), "*.nuspec"))
    if not ns: return set()
    t = open(ns[0], encoding="utf-8", errors="replace").read()
    for a, b in re.findall(r'<group\b([^>]*)>(.*?)</group>', t, re.S):
        if '"%s"' % TF in a:
            return set(re.findall(r'<dependency id="([^"]+)"', b))
    return set()

projs = {}
for r in ("src", "apps", "tests", "samples"):
    for dp, dn, fn in os.walk(r):
        parts = dp.split(os.sep)
        if "contrib" in parts or "bin" in parts or "obj" in parts: continue
        for f in fn:
            if not f.endswith(".csproj"): continue
            full = os.path.relpath(os.path.join(dp, f))
            txt = open(full, encoding="utf-8").read()
            prs = []
            for m in re.findall(r'<ProjectReference Include="([^"]+)"', txt):
                q = os.path.normpath(os.path.join(dp, m.replace("\\", os.sep)))
                if not q.endswith(".csproj"): q += ".csproj"
                if os.path.isfile(q): prs.append(os.path.relpath(q))
            projs[full] = dict(
                pk=re.findall(r'<PackageReference Include="([^"]+)"', txt),
                prs=prs, name=os.path.basename(f)[:-7], dir=os.path.dirname(full))

allme = sorted({p for d in projs.values() for p in d["pk"] if p.startswith(EXT)})
DECL = {p: surface(p) for p in allme}
DEPS = {p: deps(p) for p in allme}

def walk(roots):
    seen, st = set(), list(roots)
    while st:
        x = st.pop()
        if x in seen: continue
        seen.add(x); st += list(DEPS.get(x, ()))
    return seen

GU = re.compile(r'(?m)^\s*global\s+using\s+(?!static\s)([A-Za-z_][\w.]*)\s*;')
US = re.compile(r'(?m)^\s*using\s+(static\s+)?([A-Za-z_][\w.]*)\s*;')
NM = re.compile(r"[A-Za-z_][A-Za-z0-9_]*")
STR = re.compile(r'@"(?:[^"]|"")*"|"(?:\\.|[^"\\\n])*"')

src_cache = {}
def sources(d):
    if d in src_cache: return src_cache[d]
    out = []
    for dp, dn, fn in os.walk(d):
        p = dp.split(os.sep)
        if "bin" in p or "obj" in p: continue
        out += [os.path.join(dp, f) for f in fn if f.endswith(".cs")]
    src_cache[d] = sorted(out); return src_cache[d]

def globals_of(files):
    g = set()
    for f in files:
        g |= {m.group(1) for m in GU.finditer(open(f, encoding="utf-8", errors="replace").read())}
    return g

def bound(files, packages, extra):
    idx = defaultdict(set)
    for p in packages:
        s = DECL.get(p)
        if not s: continue
        for fq in s:
            c = fq.rfind(".")
            if c > 0: idx[fq.split(".")[-1]].add(fq[:c])
    hits = set()
    for f in files:
        t = open(f, encoding="utf-8", errors="replace").read()
        sc = set(extra)
        sc |= {m.group(1) for m in GU.finditer(t)}
        sc |= {m.group(2) for m in US.finditer(t) if not m.group(1)}
        b = re.sub(r'//[^\n]*', ' ', t)
        b = US.sub(' ', b)
        b = STR.sub('""', b)
        for m in NM.finditer(b):
            w = m.group(0)
            if w not in idx: continue
            if any(ns in sc for ns in idx[w]):
                hits.add(w)
                continue
            # A MEMBER binds through the namespace its OWNER lives in, not through
            # a static using of the owner: LoggerExtensions.LogInformation and
            # ConsoleLoggerExtensions.AddSimpleConsole are called on an ILogger /
            # ILoggingBuilder with only `using Microsoft.Extensions.Logging;` (or a
            # global using) in scope. Without this, the extension-method shape reads
            # as an unused package — which is exactly how the four sample plugins
            # and src/Harbor.Ipc.Server (two AddSimpleConsole() call sites reached
            # through GlobalUsings.cs) look deletable when they are not.
            if any(any(o.startswith(ns + ".") for ns in sc) for o in idx[w]):
                hits.add(w)
    return hits

findings = []
for full, d in sorted(projs.items()):
    files = sources(d["dir"])
    if not files: continue
    pk = [p for p in d["pk"] if p.startswith(EXT)]
    if not pk: continue
    g = globals_of(files)
    for pkg in pk:
        if not DECL.get(pkg): continue
        if bound(files, [pkg], g): continue
        roots = set(d["pk"]) | ({"TUnit"} if d["name"].endswith(".Tests") else set())
        lost = walk(roots) - walk(roots - {pkg})
        if any(DECL.get(l) and bound(files, [l], g) for l in lost): continue
        cons = [c for c, cd in projs.items() if c != full and full in cd["prs"]]
        risk = []
        for c in cons:
            cf = sources(projs[c]["dir"])
            if not cf: continue
            cg = globals_of(cf)
            # A consumer is at risk when it BINDS a name the package (or something
            # only this reference supplied) provides, and does not declare the
            # package itself. That is #910's OpenAiCompatible shape exactly:
            # Harbor.Benchmarks / LoadTests / Providers.Tests never mentioned the
            # package and lost it anyway.
            needs = bool(bound(cf, [pkg], cg)) or \
                    any(DECL.get(l) and bound(cf, [l], cg) for l in lost)
            if needs and pkg not in projs[c]["pk"]:
                risk.append(c)
        if risk: continue
        findings.append((full, pkg))

print("GUARD findings: %d" % len(findings))
for r, p in findings:
    print("  %-50s %s" % (r, p))