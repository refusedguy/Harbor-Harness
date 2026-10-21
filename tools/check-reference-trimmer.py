#!/usr/bin/env python3
"""Gate: does the tree carry a reference the compiler never uses?

Stdlib only — no pip, no network, no dotnet. It lives in `tools/` next to
check-abstractions-contract.py / check-test-packages.py / check-test-projects /
check-test-dup.py / aot-publish-gate.py and it asks the question none of them
asks about the WHOLE tree:

  check-abstractions-contract.py  is every <PackageReference> of FIVE projects
                                 load-bearing? (src/Harbor.Abstractions,
                                 src/Harbor.Abstractions.Contracts,
                                 src/Harbor.Extensions, src/Harbor.Application,
                                 src/Harbor.PolicyConfig)
  check-test-packages.py          may a test/sample project pull a BANNED
                                 package in, directly or transitively?
  check-test-projects              does every test project actually run in CI?
  THIS ONE                       does any project in the tree reference a
                                 package or a sibling project that the
                                 compiler never uses?

WHY THIS FILE EXISTS (issue #1853)

  Until now the answer to "is this dependency needed?" existed for four
  projects out of about a hundred and ten, and the answer for the rest was
  "nobody looked". A dead `<PackageReference>` is not a style defect: it
  drags a transitive closure into `bin/`, it costs restore time, and — with
  `CentralPackageTransitivePinningEnabled=true` in Directory.Packages.props —
  removing one can change the RESOLVED VERSION of a transitive package that
  three other projects depend on (#910 is the recorded instance of that
  happening by hand).

  The measurement is done by ReferenceTrimmer 3.5.13 (MIT), a build-time
  analyzer that asks Roslyn which assemblies the compiled code actually uses
  and reports the declared references that none of them reach. The tool is
  NOT run by this script — this script reads what it produced.

WHAT THE TOOL CANNOT SEE, AND WHY THAT MATTERS HERE

  ReferenceTrimmer sees SOURCE. It cannot see reflection, it cannot see a
  DI registration by convention, it cannot see a plugin loaded by name, and
  it cannot see an assembly that is only reached through a runtime lookup.
  This repository forbids dynamic code in `src/` and `apps/` (#626,
  enforced by the compiler through /BannedSymbols.DynamicCode.txt), so the
  blind spot is small — but `src/Harbor.Plugins.*` is the documented
  exception, and the exception is exactly where a bare source analysis is
  least reliable.

  So every finding this gate reports is a CANDIDATE, and the only thing this
  gate is allowed to assert about a candidate is that it is NEW. The
  baseline file carries the candidates that have been looked at and left
  where they are, each with the reason it was left there. A finding that is
  not in the baseline has not been looked at, and the gate is red.

THE BASELINE IS A RATCHET

  `tools/reference-trimmer-baseline.txt` carries the findings the tree
  produces, one row each, with a reason and an owner. The gate passes only
  when the set of findings the tree produces is exactly the set written
  there:

    * a finding the file does not carry is RED. A NEW unused reference fails.
    * a row the tree no longer produces is RED. A CLOSED finding must have
      its row deleted in the same diff that closed it.

  So the file can only shrink, and the gate gets stricter as the tree is
  fixed. A closed finding cannot stay blessed.

WHAT THE GATE CANNOT ANSWER, STATED RATHER THAN IMPLIED

  It cannot decide whether a candidate is TRUE. It reads the compiler's
  answer and compares it with a recorded, reviewed answer. The truth of a
  candidate is a human judgement and it lives in the baseline's reason
  column, not here.

  It does not run the analyzer. The analyzer is a dotnet build-time package,
  and this file is deliberately dotnet-free so it can run in a job that has
  no SDK. The CI job that produces the findings is the `reference-trimmer`
  job in `.github/workflows/ci.yml`.

  It does not read the project files to decide whether a finding is real.
  A second opinion computed from the sources would be a second implementation
  of the tool, and a second implementation of the tool is a second answer to
  drift from the first.

RULES

  RT-NEW-FINDING     The tree produced a finding the baseline does not
                     carry. The candidate has not been looked at.
  RT-STALE-BASELINE  A baseline row the tree no longer produces. The
                     finding was closed and the row was not deleted.
  RT-UNJUSTIFIED     A baseline row with no reason, a reason that is too
                     short to be a reason, or no owner.
  RT-DOC-OFF         RT0000: the analyzer found a project whose C#
                     documentation generation is off. The tool says so
                     itself: without a doc file the "used references"
                     answer is less accurate, so the project was analysed
                     with a weaker instrument. A weaker instrument that is
                     allowed to under-report is not a measurement.
  RT-ANALYZER-ERROR  RT9999: the analyzer itself failed on a project. The
                     answer for that project is unknown, not "clean".
  RT-SCAN-BLIND      Something the gate needed to read was not there: the
                     findings file is missing or empty, or the coverage file
                     names fewer projects than the floor.
"""
import argparse
import os
import re
import sys

# The projects whose <PackageReference> set is already gated by
# tools/check-abstractions-contract.py. Their findings are not reported here:
# the same question is asked of them by a gate that reads the sources
# directly, and two gates answering the same question for the same projects
# means two baselines to keep in step.
#
# The list is read from the sibling gate rather than copied, so it cannot
# drift: if that gate widens its scope, this one follows on the next run.
def _guarded_scope(root):
    path = os.path.join(root, "tools", "check-abstractions-contract.py")
    try:
        with open(path, encoding="utf-8") as handle:
            text = handle.read()
    except OSError:
        return ()
    match = re.search(r"^SCOPE\s*=\s*\(([^)]*)\)", text, re.M | re.S)
    if not match:
        return ()
    return tuple(
        item.strip().strip('"').strip("'")
        for item in match.group(1).split(",")
        if item.strip()
    )


BASELINE_FILE = os.path.join("tools", "reference-trimmer-baseline.txt")

# The rule ids the analyzer emits. RT0000 and RT9999 are not findings of an
# unused reference — they are statements about the quality of the answer —
# and the gate treats them as failures of their own kind rather than as
# baseline rows, because a baseline row for them would be blessing a broken
# instrument.
UNUSED_RULES = ("RT0001", "RT0002", "RT0003")

# A reason has to be a reason. The floor is the same floor the sibling gate
# uses, and for the same reason: a short string is a category, not a reason,
# and a category cannot be reviewed.
MIN_REASON_LENGTH = 40


class Finding(object):
    """One row of the answer: a rule, a subject, and where it came from."""

    def __init__(self, rule, subject, message="", project=""):
        self.rule = rule
        self.subject = subject
        self.message = message
        self.project = project
        self.line_no = 0

    def key(self):
        return (self.rule, self.subject)

    def __repr__(self):
        return "Finding(%s, %s)" % (self.rule, self.subject)


class BaselineRow(object):
    """One row of the baseline file: a rule, a subject, a reason, an owner."""

    def __init__(self, rule, subject, reason, owner, line_no):
        self.rule = rule
        self.subject = subject
        self.reason = reason
        self.owner = owner
        self.line_no = line_no

    def key(self):
        return (self.rule, self.subject)


# ---------------------------------------------------------------------------
# Reading the analyzer's output
# ---------------------------------------------------------------------------

# MSBuild's canonical warning line. The upstream tool's own end-to-end test
# reads the same shape (`".+: (warning|error) (?<message>.+) \[(?<project>.+)\]"`),
# so the format is not a guess.
_CANDIDATE_RE = re.compile(
    r"(?:warning|error)\s+(?P<code>RT\d{4})\s*:\s*(?P<msg>.*?)\s*\[(?P<proj>[^\]]*)\]\s*$"
)
_FALLBACK_RE = re.compile(
    r"^(?P<loc>.+?)\s*:\s*(?:warning|error)\s+(?P<code>RT\d{4})\s*:\s*(?P<msg>.*?)\s*$"
)

# The analyzer's message formats, taken from its diagnostic descriptors:
#   RT0001 "Reference {0} can be removed"
#   RT0002 "ProjectReference {0} can be removed"
#   RT0003 "PackageReference {0} can be removed"
_SPEC_RE = re.compile(
    r"^(?P<kind>Reference|ProjectReference|PackageReference)\s+(?P<spec>.+?)\s+can be removed$"
)


def _normalize_path(raw, repo_root, known_projects):
    """Turn a path out of the build log into a repository-relative one.

    The log carries whatever MSBuild had: an absolute path on a CI runner, a
    relative one locally, and backslashes on Windows. All three have to land
    on the same string, or the baseline would be a different file on every
    machine.
    """
    path = raw.strip().strip('"').replace("\\", "/")
    if not path:
        return ""
    if path.startswith("/"):
        try:
            relative = os.path.relpath(path, repo_root).replace("\\", "/")
        except ValueError:
            relative = path
        if not relative.startswith(".."):
            return relative
        # The path is outside the repository root — a CI runner's workspace
        # path, or a checkout somewhere else. The repository's own directory
        # name is the next best anchor: the last component that matches it
        # starts the repository-relative part.
        root_name = os.path.basename(repo_root)
        if root_name:
            parts = path.split("/")
            for index in range(len(parts) - 1, -1, -1):
                if parts[index] == root_name:
                    candidate = "/".join(parts[index + 1:])
                    if candidate:
                        return candidate
    # A path that is not under the repository root (a package outside the
    # checkout, a path from another machine) is matched against the projects
    # this repository knows about, by suffix.
    for candidate in known_projects:
        if path.endswith(candidate):
            return candidate
    return path


def parse_findings(text, repo_root, known_projects):
    """(findings, quality_problems) read out of the analyzer's log.

    `quality_problems` are the statements about the instrument itself — RT0000
    (documentation generation off, so the answer is weaker) and RT9999 (the
    analyzer failed). They are not unused-reference findings and they are not
    baseline rows.
    """
    findings = []
    quality = []
    seen = set()
    for raw_line in text.splitlines():
        line = raw_line.rstrip()
        if "RT" not in line:
            continue
        match = _CANDIDATE_RE.search(line)
        project = ""
        if match:
            project = match.group("proj")
        else:
            match = _FALLBACK_RE.search(line)
            if not match:
                continue
            # No bracket: the location in front of the code carries the
            # project, and the analyzer reports project-level diagnostics
            # without a source location.
            location = match.group("loc")
            project = location
        code = match.group("code")
        message = match.group("msg").strip()
        project = _normalize_path(project, repo_root, known_projects)

        if code == "RT0000":
            quality.append(Finding("RT-DOC-OFF", project or "<unknown>",
                                   message, project))
            continue
        if code == "RT9999":
            quality.append(Finding("RT-ANALYZER-ERROR", project or "<unknown>",
                                   message, project))
            continue
        if code not in UNUSED_RULES:
            continue

        spec_match = _SPEC_RE.match(message)
        if not spec_match:
            # A message this gate cannot read is a message this gate cannot
            # judge. It is reported as a quality problem rather than dropped:
            # dropping it would let a format change turn the gate green by
            # never matching anything.
            quality.append(Finding("RT-UNREADABLE", project or "<unknown>",
                                   message, project))
            continue

        subject = "%s :: %s" % (project, spec_match.group("spec"))
        key = (code, subject)
        if key in seen:
            continue
        seen.add(key)
        findings.append(Finding(code, subject, message, project))
    findings.sort(key=lambda f: (f.rule, f.subject))
    quality.sort(key=lambda f: (f.rule, f.subject))
    return findings, quality


# ---------------------------------------------------------------------------
# The baseline file
# ---------------------------------------------------------------------------

def parse_baseline(root, baseline_path):
    """(rows, findings) read from the baseline file."""
    rows = []
    findings = []
    path = os.path.join(root, baseline_path)
    if not os.path.exists(path):
        findings.append(Finding(
            "RT-UNJUSTIFIED", baseline_path,
            "the baseline file does not exist. A baseline that is absent is "
            "indistinguishable from a baseline that forgives everything."))
        return rows, findings
    with open(path, encoding="utf-8") as handle:
        for line_no, raw in enumerate(handle, start=1):
            line = raw.strip()
            if not line or line.startswith("#"):
                continue
            parts = [part.strip() for part in line.split("|")]
            if len(parts) != 4:
                findings.append(Finding(
                    "RT-UNJUSTIFIED", "%s:%d" % (baseline_path, line_no),
                    "the row has %d fields; the format is "
                    "<rule> | <subject> | <reason> | <owner>." % len(parts)))
                continue
            rule, subject, reason, owner = parts
            if rule not in UNUSED_RULES:
                findings.append(Finding(
                    "RT-UNJUSTIFIED", "%s:%d" % (baseline_path, line_no),
                    "the rule %s is not one of %s." % (rule, ", ".join(UNUSED_RULES))))
                continue
            if len(reason) < MIN_REASON_LENGTH:
                findings.append(Finding(
                    "RT-UNJUSTIFIED", "%s:%d" % (baseline_path, line_no),
                    "the reason is %d characters; a reason has to be at least "
                    "%d, and a URL is not a reason." % (len(reason), MIN_REASON_LENGTH)))
                continue
            if not owner.startswith("@"):
                findings.append(Finding(
                    "RT-UNJUSTIFIED", "%s:%d" % (baseline_path, line_no),
                    "the owner %r is not a '@handle' somebody can be asked "
                    "about." % owner))
                continue
            rows.append(BaselineRow(rule, subject, reason, owner, line_no))
    return rows, findings


# ---------------------------------------------------------------------------
# The gate
# ---------------------------------------------------------------------------

def known_projects(root):
    """Every project file in the tree, as repository-relative paths."""
    found = []
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames
                       if d not in ("obj", "bin", ".git", "node_modules")]
        for name in filenames:
            if name.endswith(".csproj"):
                full = os.path.join(dirpath, name)
                found.append(os.path.relpath(full, root).replace("\\", "/"))
    found.sort()
    return found


def parsed_findings(root, text):
    """(findings, quality_problems, excluded_count) for one analyzer log.

    The exclusion is the four projects whose package set another gate
    already answers for. Their findings are dropped here, not silenced: the
    count is reported, so a reader can see that the gate did not look at
    them rather than being left to assume it did.
    """
    projects = known_projects(root)
    parsed, quality = parse_findings(text, root, projects)
    guarded = _guarded_scope(root)
    excluded = 0
    if guarded:
        kept = []
        for finding in parsed:
            directory = finding.project.rsplit("/", 1)[0] if "/" in finding.project else finding.project
            if any(directory == scope or directory.startswith(scope + "/")
                   for scope in guarded):
                excluded += 1
                continue
            kept.append(finding)
        parsed = kept
    return parsed, quality, excluded


def scan(root, findings_path, coverage_path, baseline_path, min_projects):
    findings = []
    stats = {"covered": 0, "parsed": 0, "excluded": 0}
    if not os.path.exists(findings_path):
        findings.append(Finding(
            "RT-SCAN-BLIND", findings_path,
            "the findings file does not exist. The analyzer did not run, or "
            "its output was not kept; an absent answer is not a clean answer."))
        return findings, stats

    with open(findings_path, encoding="utf-8", errors="replace") as handle:
        text = handle.read()

    if not text.strip():
        findings.append(Finding(
            "RT-SCAN-BLIND", findings_path,
            "the findings file is empty. The analyzer produced no output at "
            "all, which is not the same as producing no findings."))
        return findings, stats

    parsed, quality, excluded = parsed_findings(root, text)
    findings.extend(quality)
    stats["parsed"] = len(parsed)
    stats["excluded"] = excluded

    # The coverage file is the answer to "how many projects did the analyzer
    # actually run in". It is checked because a gate that reads one
    # project's log and calls that the tree's answer is green by never
    # scanning the rest.
    covered = 0
    if coverage_path and os.path.exists(coverage_path):
        with open(coverage_path, encoding="utf-8", errors="replace") as handle:
            covered = len([line for line in handle if line.strip()])
        stats["covered"] = covered
        if covered < min_projects:
            findings.append(Finding(
                "RT-SCAN-BLIND", coverage_path,
                "the analyzer ran in %d projects; the floor is %d. A scan "
                "that silently narrowed must go red, not quiet."
                % (covered, min_projects)))
    elif min_projects > 0:
        findings.append(Finding(
            "RT-SCAN-BLIND", coverage_path or "<no coverage file>",
            "no coverage file was given, so the number of projects the "
            "analyzer ran in is unknown and the floor of %d cannot be "
            "checked." % min_projects))

    rows, baseline_findings = parse_baseline(root, baseline_path)
    findings.extend(baseline_findings)

    carried = set(row.key() for row in rows)
    for row in rows:
        if row.key() not in set(f.key() for f in parsed):
            findings.append(Finding(
                "RT-STALE-BASELINE", row.subject,
                "the baseline row on line %d carries %s for a finding the "
                "tree no longer produces. Delete the row in the same diff "
                "that closed the finding." % (row.line_no, row.rule)))

    for finding in parsed:
        if finding.key() in carried:
            continue
        findings.append(Finding(
            "RT-NEW-FINDING", finding.subject,
            "%s says %s. The candidate has not been looked at: it is either "
            "a dead reference, which this gate exists to find, or a false "
            "positive, which has to be written into the baseline with the "
            "reason it is one."
            % (finding.rule, finding.message)))

    findings.sort(key=lambda f: (f.rule, f.subject))
    return findings, stats


def report(findings, root, baseline_path, stats=None):
    stats = stats or {}
    if not findings:
        print("OK: no unused reference outside the baseline.")
        print("  the analyzer ran in %d project(s); %d finding(s) parsed; "
              "%d dropped as another gate's scope."
              % (stats.get("covered", 0), stats.get("parsed", 0),
                 stats.get("excluded", 0)))
        return 0
    by_rule = {}
    for finding in findings:
        by_rule.setdefault(finding.rule, []).append(finding)
    print("ReferenceTrimmer gate: %d finding(s)." % len(findings))
    print("  the analyzer ran in %d project(s); %d finding(s) parsed; "
          "%d dropped as another gate's scope."
          % (stats.get("covered", 0), stats.get("parsed", 0),
             stats.get("excluded", 0)))
    for rule in sorted(by_rule):
        print("\n%s (%d):" % (rule, len(by_rule[rule])))
        for finding in by_rule[rule]:
            print("  - %s" % finding.subject)
            print("      %s" % finding.message)
    print("\nThe baseline is %s." % baseline_path)
    return 1


# ---------------------------------------------------------------------------
# The self-test: the gate is red when it should be, and green when it should
# ---------------------------------------------------------------------------

FIXTURE_FINDINGS = """/home/runner/work/Repo/Repo/src/Harbor.One/Harbor.One.csproj : warning RT0003: PackageReference Ghost.Package can be removed [/home/runner/work/Repo/Repo/src/Harbor.One/Harbor.One.csproj]
/home/runner/work/Repo/Repo/src/Harbor.One/Harbor.One.csproj : warning RT0003: PackageReference Ghost.Package can be removed [/home/runner/work/Repo/Repo/src/Harbor.One/Harbor.One.csproj]
/home/runner/work/Repo/Repo/src/Harbor.Two/Harbor.Two.csproj : warning RT0002: ProjectReference ../Harbor.One/Harbor.One.csproj can be removed [/home/runner/work/Repo/Repo/src/Harbor.Two/Harbor.Two.csproj]
"""

FIXTURE_COVERAGE = "src/Harbor.One\nsrc/Harbor.Two\n"

FIXTURE_BASELINE = """# fixture
RT0003 | src/Harbor.One/Harbor.One.csproj :: Ghost.Package | a reason that is long enough to be a reason and not a category | @refusedguy
RT0002 | src/Harbor.Two/Harbor.Two.csproj :: ../Harbor.One/Harbor.One.csproj | a reason that is long enough to be a reason and not a category | @refusedguy
"""


def _write(root, relative, text):
    path = os.path.join(root, relative)
    directory = os.path.dirname(path)
    if directory and not os.path.isdir(directory):
        os.makedirs(directory)
    with open(path, "w", encoding="utf-8") as handle:
        handle.write(text)


def _fixture_root():
    import tempfile
    root = tempfile.mkdtemp(prefix="rt-gate-")
    # The project files exist so that the path normalisation can anchor on
    # them: the fixture's log carries the paths a CI runner would produce,
    # and the gate has to bring them back to repository-relative ones.
    for relative in ("src/Harbor.One/Harbor.One.csproj",
                     "src/Harbor.Two/Harbor.Two.csproj"):
        _write(root, relative, "<Project />\n")
    return root


def self_test():
    import shutil
    import tempfile
    failures = []

    def check(name, condition, detail=""):
        if condition:
            print("  ok   %s" % name)
        else:
            print("  FAIL %s %s" % (name, detail))
            failures.append(name)

    base = tempfile.mkdtemp(prefix="rt-selftest-")
    try:
        # 1. The clean case: every finding the tree produces is carried.
        root = _fixture_root()
        _write(root, "findings.log", FIXTURE_FINDINGS)
        _write(root, "coverage.txt", FIXTURE_COVERAGE)
        _write(root, os.path.join("tools", "reference-trimmer-baseline.txt"),
               FIXTURE_BASELINE)
        found, _ = scan(root, os.path.join(root, "findings.log"),
                     os.path.join(root, "coverage.txt"),
                     os.path.join("tools", "reference-trimmer-baseline.txt"), 2)
        check("a carried finding is not a new finding",
              not [f for f in found if f.rule == "RT-NEW-FINDING"])

        # The same finding twice in the log is one finding: a project built
        # twice, or a warning reprinted, must not become two candidates.
        parsed, _, _ = parsed_findings(root, FIXTURE_FINDINGS)
        check("a duplicate log line is one finding", len(parsed) == 2,
              "the fixture holds two distinct findings and one duplicate line; "
              "parsed %d" % len(parsed))

        # The four projects another gate answers for are dropped, and the
        # drop is counted rather than silent.
        guarded_root = _fixture_root()
        _write(guarded_root, os.path.join("tools", "check-abstractions-contract.py"),
               'SCOPE = (\n    "src/Harbor.One",\n)\n')
        guarded_findings = (
            "/home/runner/work/Repo/Repo/src/Harbor.One/Harbor.One.csproj : "
            "warning RT0003: PackageReference Ghost.Package can be removed "
            "[/home/runner/work/Repo/Repo/src/Harbor.One/Harbor.One.csproj]\n")
        parsed, _, excluded = parsed_findings(guarded_root, guarded_findings)
        check("another gate's scope is excluded", excluded == 1 and not parsed,
              "excluded %d, parsed %d" % (excluded, len(parsed)))

        # 2. A finding the baseline does not carry.
        _write(root, os.path.join("tools", "reference-trimmer-baseline.txt"),
               "# fixture with no rows\n")
        found, _ = scan(root, os.path.join(root, "findings.log"),
                     os.path.join(root, "coverage.txt"),
                     os.path.join("tools", "reference-trimmer-baseline.txt"), 2)
        check("an uncarried finding is red",
              any(f.rule == "RT-NEW-FINDING" for f in found))

        # 3. A baseline row the tree no longer produces.
        _write(root, os.path.join("tools", "reference-trimmer-baseline.txt"),
               FIXTURE_BASELINE + "RT0002 | src/Harbor.Three/Harbor.Three.csproj :: Gone.Package | a reason that is long enough to be a reason and not a category | @refusedguy\n")
        found, _ = scan(root, os.path.join(root, "findings.log"),
                     os.path.join(root, "coverage.txt"),
                     os.path.join("tools", "reference-trimmer-baseline.txt"), 2)
        check("a stale baseline row is red",
              any(f.rule == "RT-STALE-BASELINE" for f in found))

        # 4. A baseline row without a reason.
        _write(root, os.path.join("tools", "reference-trimmer-baseline.txt"),
               "RT0003 | src/Harbor.One/Harbor.One.csproj :: Ghost.Package | too short | @refusedguy\n")
        found, _ = scan(root, os.path.join(root, "findings.log"),
                     os.path.join(root, "coverage.txt"),
                     os.path.join("tools", "reference-trimmer-baseline.txt"), 2)
        check("a baseline row without a reason is red",
              any(f.rule == "RT-UNJUSTIFIED" for f in found))

        # 5. RT0000 — the instrument says it is running weak.
        _write(root, "findings.log", FIXTURE_FINDINGS + "src/Harbor.One/Harbor.One.csproj : warning RT0000: Enable /doc parameter or in MSBuild set <GenerateDocumentationFile>true</GenerateDocumentationFile> for accuracy of used references detection [src/Harbor.One/Harbor.One.csproj]\n")
        found, _ = scan(root, os.path.join(root, "findings.log"),
                     os.path.join(root, "coverage.txt"),
                     os.path.join("tools", "reference-trimmer-baseline.txt"), 2)
        check("RT0000 is red",
              any(f.rule == "RT-DOC-OFF" for f in found))

        # 6. RT9999 — the analyzer itself failed.
        _write(root, "findings.log", FIXTURE_FINDINGS + "src/Harbor.One/Harbor.One.csproj : warning RT9999: ReferenceTrimmer encountered an unexpected error: boom. Please file a bug at https://github.com/dfederm/ReferenceTrimmer/issues [src/Harbor.One/Harbor.One.csproj]\n")
        found, _ = scan(root, os.path.join(root, "findings.log"),
                     os.path.join(root, "coverage.txt"),
                     os.path.join("tools", "reference-trimmer-baseline.txt"), 2)
        check("RT9999 is red",
              any(f.rule == "RT-ANALYZER-ERROR" for f in found))

        # 7. A missing findings file.
        found, _ = scan(root, os.path.join(root, "does-not-exist.log"),
                     os.path.join(root, "coverage.txt"),
                     os.path.join("tools", "reference-trimmer-baseline.txt"), 2)
        check("a missing findings file is red",
              any(f.rule == "RT-SCAN-BLIND" for f in found))

        # 8. An empty findings file.
        _write(root, "empty.log", "")
        found, _ = scan(root, os.path.join(root, "empty.log"),
                     os.path.join(root, "coverage.txt"),
                     os.path.join("tools", "reference-trimmer-baseline.txt"), 2)
        check("an empty findings file is red",
              any(f.rule == "RT-SCAN-BLIND" for f in found))

        # 9. A coverage count below the floor.
        _write(root, "findings.log", FIXTURE_FINDINGS)
        _write(root, "coverage.txt", "src/Harbor.One\n")
        found, _ = scan(root, os.path.join(root, "findings.log"),
                     os.path.join(root, "coverage.txt"),
                     os.path.join("tools", "reference-trimmer-baseline.txt"), 2)
        check("a coverage count below the floor is red",
              any(f.rule == "RT-SCAN-BLIND" for f in found))
    finally:
        shutil.rmtree(base, ignore_errors=True)

    if failures:
        print("\nself-test FAILED: %s" % ", ".join(failures))
        return 1
    print("\nself-test OK")
    return 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--findings", help="the analyzer's log, as written by the CI job")
    parser.add_argument("--coverage", help="the list of projects the analyzer ran in")
    parser.add_argument("--min-projects", type=int, default=100,
                        help="the floor on the number of projects analysed (0 disables)")
    parser.add_argument("--baseline", default=BASELINE_FILE,
                        help="the baseline file, relative to the repository root")
    parser.add_argument("--repo-root", default=None,
                        help="the repository root (default: the parent of tools/)")
    parser.add_argument("--print-rows", action="store_true",
                        help="print the baseline rows the current findings imply")
    parser.add_argument("--self-test", action="store_true",
                        help="prove the gate is red when it should be")
    args = parser.parse_args()

    if args.self_test:
        return self_test()

    if not args.findings:
        parser.error("--findings is required (or --self-test)")

    root = args.repo_root or os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    root = os.path.abspath(root)
    baseline_path = args.baseline
    if not os.path.isabs(baseline_path):
        baseline_path = os.path.join(root, baseline_path)

    findings, stats = scan(root, args.findings, args.coverage, baseline_path,
                           args.min_projects)

    if args.print_rows:
        with open(args.findings, encoding="utf-8", errors="replace") as handle:
            parsed, _, _ = parsed_findings(root, handle.read())
        for finding in parsed:
            print("%s | %s | TODO reason for this candidate | @refusedguy"
                  % (finding.rule, finding.subject))
        return 0

    return report(findings, root, args.baseline, stats)


if __name__ == "__main__":
    sys.exit(main())
