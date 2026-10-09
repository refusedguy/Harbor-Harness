#!/usr/bin/env python3
"""Validate packed Harbor.* NuGet packages (#429).

Stdlib only — no pip install, no network, no dotnet. Runs in CI right after
`dotnet pack` (see the pack steps in .github/workflows/ci.yml).

What it checks for every packable src/ project (IsPackable=true):
  - the expected {PackageId}.{Version}.nupkg exists in the packages dir
  - the embedded .nuspec carries: non-empty description, MIT license
    expression, repository url/type pointing at the real repository,
    a project URL, and >= 3 tags
  - the .nupkg contains README.md at the root and at least one
    lib/net10.0/*.dll

Non-src packages that land in the same output dir (the PackAsTool CLI
package, samples/* plugin packages) are reported and ignored: they keep
their own per-project pack shape.

Usage:
  python3 tools/pack-validate.py out/packages [--src src] [--selftest]
"""

import re
import sys
import tempfile
import zipfile
from pathlib import Path
import xml.etree.ElementTree as ET

REPO_URL = "https://github.com/refusedguy/Harbor-Harness"
LICENSE = "MIT"
MIN_TAGS = 3


def _child(el: ET.Element, name: str) -> ET.Element | None:
    # Namespace-agnostic: SDK-packed manifests have used more than one
    # nuspec xmlns over the years, and the checks below do not care which.
    for c in el:
        if c.tag == name or c.tag.endswith("}" + name):
            return c
    return None


def _text(el: ET.Element | None) -> str:
    return (el.text or "").strip() if el is not None else ""


def repo_version(root: Path) -> str:
    props = (root / "Directory.Build.props").read_text(encoding="utf-8")
    m = re.search(r"<Version>([^<]+)</Version>", props)
    if not m:
        raise SystemExit("cannot find <Version> in Directory.Build.props")
    return m.group(1).strip()


def package_id(csproj: Path) -> str:
    text = csproj.read_text(encoding="utf-8")
    m = re.search(r"<PackageId>([^<]+)</PackageId>", text)
    return m.group(1).strip() if m else csproj.stem


def expected_ids(src: Path) -> list[str]:
    ids = []
    for csproj in sorted(src.glob("*/*.csproj")):
        text = csproj.read_text(encoding="utf-8")
        if "<IsPackable>true</IsPackable>" in text:
            ids.append(package_id(csproj))
    return ids


def check_nuspec(nuspec: ET.Element, pkg_id: str, version: str) -> list[str]:
    errs: list[str] = []
    md = _child(nuspec, "metadata")
    if md is None:
        return [f"{pkg_id}: no metadata element in nuspec"]
    txt = lambda tag: _text(_child(md, tag))
    if txt("id") != pkg_id:
        errs.append(f"{pkg_id}: nuspec id is {txt('id')!r}")
    if txt("version") != version:
        errs.append(f"{pkg_id}: nuspec version is {txt('version')!r}, want {version!r}")
    if not txt("description"):
        errs.append(f"{pkg_id}: empty description")
    lic = _child(md, "license")
    if lic is None or _text(lic) != LICENSE or lic.get("type") != "expression":
        errs.append(f"{pkg_id}: license is not expression:{LICENSE}")
    repo = _child(md, "repository")
    if repo is None or repo.get("url") != REPO_URL or repo.get("type") != "git":
        errs.append(f"{pkg_id}: repository must be type=git url={REPO_URL}")
    if "refusedguy/Harbor-Harness" not in txt("projectUrl"):
        errs.append(f"{pkg_id}: projectUrl missing the real repository")
    tags = [t for t in re.split(r"[\s;,]+", txt("tags")) if t]
    if len(tags) < MIN_TAGS:
        errs.append(f"{pkg_id}: only {len(tags)} tag(s), need >= {MIN_TAGS}")
    return errs


def check_files(zf: zipfile.ZipFile, pkg_id: str) -> list[str]:
    errs: list[str] = []
    names = zf.namelist()
    if "README.md" not in names:
        errs.append(f"{pkg_id}: README.md missing from package")
    if not [n for n in names if n.startswith("lib/net10.0/") and n.endswith(".dll")]:
        errs.append(f"{pkg_id}: no lib/net10.0/*.dll in package")
    return errs


def validate(packages: Path, src: Path) -> list[str]:
    version = repo_version(src.parent if src.name == "src" else Path("."))
    ids = expected_ids(src)
    if not ids:
        return ["no IsPackable=true projects found under " + str(src)]
    errs: list[str] = []
    have = {p.name for p in packages.glob("*.nupkg")}
    for pkg_id in ids:
        fname = f"{pkg_id}.{version}.nupkg"
        if fname not in have:
            errs.append(f"{pkg_id}: missing {fname}")
            continue
        with zipfile.ZipFile(packages / fname) as zf:
            names = zf.namelist()
            # Prefer the manifest itself: a package may carry other
            # .nuspec-suffixed content files.
            nuspec_name = f"{pkg_id}.nuspec" if f"{pkg_id}.nuspec" in names else next(
                (n for n in names if n.endswith(".nuspec")), None
            )
            if nuspec_name is None:
                errs.append(f"{pkg_id}: no .nuspec inside {fname}")
                continue
            nuspec = ET.fromstring(zf.read(nuspec_name))
            errs += check_nuspec(nuspec, pkg_id, version)
            errs += check_files(zf, pkg_id)
    expected_files = {f"{i}.{version}.nupkg" for i in ids}
    for extra in sorted(have - expected_files):
        print(f"info: ignoring non-src package {extra}")
    return errs


def selftest() -> int:
    """Prove the validator passes a good package and fails a bad one."""
    with tempfile.TemporaryDirectory() as td:
        td = Path(td)
        (td / "Directory.Build.props").write_text(
            "<Project><PropertyGroup><Version>0.4.0-alpha</Version></PropertyGroup></Project>",
            encoding="utf-8",
        )
        src = td / "src" / "Harbor.Demo"
        src.mkdir(parents=True)
        (src / "Harbor.Demo.csproj").write_text(
            "<Project><PropertyGroup><IsPackable>true</IsPackable>"
            "<PackageId>Harbor.Demo</PackageId></PropertyGroup></Project>",
            encoding="utf-8",
        )
        out = td / "out"
        out.mkdir()

        def make_nupkg(tags: str, readme: bool, xmlns: str = (
            ' xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"'
        )) -> None:
            nuspec = (
                f"<package{xmlns}>"
                "<metadata><id>Harbor.Demo</id><version>0.4.0-alpha</version>"
                "<description>demo</description>"
                '<license type="expression">MIT</license>'
                f'<repository type="git" url="{REPO_URL}" />'
                f"<projectUrl>{REPO_URL}</projectUrl>"
                f"<tags>{tags}</tags></metadata></package>"
            )
            with zipfile.ZipFile(out / "Harbor.Demo.0.4.0-alpha.nupkg", "w") as zf:
                zf.writestr("Harbor.Demo.nuspec", nuspec)
                zf.writestr("lib/net10.0/Harbor.Demo.dll", b"dll")
                if readme:
                    zf.writestr("README.md", b"readme")

        make_nupkg("harbor;demo;test", True)
        errs = validate(out, td / "src")
        if errs:
            print("selftest FAIL: good package rejected:", errs)
            return 1
        # Manifests have shipped under more than one nuspec xmlns (and the
        # checks do not care which): a namespace-less manifest must pass too.
        make_nupkg("harbor;demo;test", True, xmlns="")
        errs = validate(out, td / "src")
        if errs:
            print("selftest FAIL: xmlns-less package rejected:", errs)
            return 1
        make_nupkg("lonely", False)
        errs = validate(out, td / "src")
        if len(errs) < 2 or not any("tag" in e for e in errs) or not any("README" in e for e in errs):
            print("selftest FAIL: bad package accepted:", errs)
            return 1
    print("selftest: ok")
    return 0


def main(argv: list[str]) -> int:
    if "--selftest" in argv:
        return selftest()
    packages = Path(argv[1]) if len(argv) > 1 else Path("out/packages")
    src = Path(argv[2]) if len(argv) > 2 else Path("src")
    if not packages.is_dir():
        print(f"error: packages dir {packages} does not exist")
        return 1
    errs = validate(packages, src)
    if errs:
        print(f"{len(errs)} package problem(s):")
        for e in errs:
            print(f"  - {e}")
        return 1
    print(f"pack-validate: ok ({len(expected_ids(src))} packages)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
