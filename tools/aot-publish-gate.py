#!/usr/bin/env python3
"""aot-publish-gate.py — the AOT publish inventory gate (issue #413).

WHAT THIS CHECKS
----------------
`dotnet publish -p:HarborWithAot=true` on `apps/Harbor.App.Cli` produces a log.
This tool reads that log and compares the trim/AOT diagnostics in it against a
committed inventory, and it is deliberately unable to pass on a log that was
never produced.

    0  the log was produced by a publish that reported success, and its
       diagnostic inventory equals the committed inventory exactly
    1  drift, or a baseline row that would not survive review
    2  the gate could not RUN — no log, log too small, no publish sentinel

Exit code 2 is a separate value on purpose. The difference between "the AOT
publish is clean" and "nothing was published and I could not tell" is the whole
point of this file, and collapsing the two is how a gate goes green on a
process that died (cf. #988, where a shard with no zero-test guard reported
success having run nothing). A `--selftest` flag proves each of those states is
reachable and is distinguishable, on fixtures where a hit is guaranteed.

WHY AN INVENTORY AND NOT A COUNT
--------------------------------
"Zero IL2026 warnings" is not the pass criterion and this tool does not
implement it. The pass criterion is: the publish completed, and every
diagnostic it emitted is a row someone wrote a reason for. The inventory is
therefore an exact set — new ids, changed counts, changed first-party sites AND
rows that no longer correspond to anything all fail. An exact set is the only
form of this check that fails on a *removed* warning, which is how a stale
permission gets caught.

TRIAGE GRANULARITY, AND WHY NOT file:line
-----------------------------------------
The inventory keys on diagnostic id, occurrence count, and the SET OF
FIRST-PARTY FILES (anything under `src/` or `apps/`). Line numbers are
deliberately excluded: a baseline keyed on line numbers must be edited by every
unrelated edit to the file above it, and a baseline that churns on every commit
is a baseline contributors delete. First-party files are the granularity that
is both actionable and stable — a warning that moves to a new file is a real
change, a warning whose line moved by three is not. The full text with line
numbers is in the publish log, which the job uploads as an artifact, so a
reviewer can read the precise site without the baseline having to carry it.
Third-party sites (Roslyn, ASP.NET, the BCL) are counted but not listed
file-by-file: a package's internals move with its version and are not
actionable per site.

SCOPE OF THE INVENTORY
----------------------
`IL2xxx` (trim / unreferenced code) and `IL3xxx` (AOT / dynamic code). Those
are the two families issue #413 names. `IL0xxx` compiler diagnostics are not
here: they are not trim warnings, they are already errors via
TreatWarningsAsErrors in Directory.Build.props, and a build that emitted one
never reached this tool.

See docs/AOT_PUBLISH.md for the gate's semantics, the publish recipe, and the
list of things this tool deliberately does not check.
"""

from __future__ import annotations

import argparse
import os
import re
import sys
from collections import Counter, defaultdict

TOOL = "aot-publish-gate"

#: Written by the CI job only after `dotnet publish` has exited 0. Absence of
#: this token in a log is what distinguishes "the publish is clean" from
#: "there is no publish here".
DEFAULT_SENTINEL = "HARBOR_AOT_PUBLISH_OK"

#: A log smaller than this cannot be a real `dotnet publish` of a 40-project
#: closure — it is a truncated file, an empty file, or the wrong file. Set low
#: enough that it cannot fail a healthy run.
DEFAULT_MIN_BYTES = 2000

#: `path(line,col): warning IL2026: text [project]`. The severity is captured
#: because an `error IL…` line must fail the gate even if the surrounding step
#: failed to propagate the exit code.
_DIAGNOSTIC = re.compile(
    r"(?:^|[\s\]])(?P<severity>warning|error)\s+(?P<id>IL\d{4})\b",
    re.IGNORECASE,
)

#: A first-party source path as MSBuild prints it: repo-relative in some
#: verbosities, absolute under the runner's workspace in others. Both are
#: matched on the `/src/` or `/apps/` segment so the recorded site is the same
#: string either way.
_FIRST_PARTY = re.compile(r"(?:^|[/\\])(?P<rel>(?:src|apps)[/\\][^\s:\]]+\.cs)(?::|\()")

#: Only the two trim/AOT families are in scope. See module docstring.
_IN_SCOPE = re.compile(r"^IL[23]\d{3}$")

_VERDICTS = ("ACCEPTED", "REJECTED")


class Row:
    """One committed inventory row."""

    __slots__ = ("ident", "count", "verdict", "files", "reason", "lineno")

    def __init__(self, ident, count, verdict, files, reason, lineno):
        self.ident = ident
        self.count = count
        self.verdict = verdict
        self.files = files  # frozenset[str], empty == third-party only
        self.reason = reason
        self.lineno = lineno

    def key(self):
        return (self.count, self.verdict, self.files)


class Inventory:
    """What the log actually contained."""

    def __init__(self, counts, first_party, error_ids, sentinel_rids, log_bytes, log_lines):
        self.counts = counts  # Counter[str]
        self.first_party = first_party  # dict[str, set[str]]
        self.error_ids = error_ids  # set[str]
        self.sentinel_rids = sentinel_rids  # set[str]
        self.log_bytes = log_bytes
        self.log_lines = log_lines

    def key(self):
        return {i: (c, frozenset(self.first_party.get(i, ()))) for i, c in self.counts.items()}


class GateError(Exception):
    """The gate could not run (exit 2). Distinct from a gate that ran and said no."""


def read_text(path, what):
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as handle:
            return handle.read()
    except OSError as exc:
        raise GateError(f"cannot read the {what} at {path}: {exc}") from exc


def parse_sentinel(text, sentinel):
    """Every `rid=` value on a sentinel line, so a stale log cannot be swapped in."""
    rids = set()
    for line in text.splitlines():
        if sentinel not in line:
            continue
        match = re.search(r"\brid=([A-Za-z0-9._-]+)", line)
        if match:
            rids.add(match.group(1))
    return rids


def parse_log(text, log_path, min_bytes, sentinel, expect_rid):
    """Read a publish log into an Inventory, or raise GateError."""
    log_bytes = len(text.encode("utf-8", errors="replace"))
    if log_bytes < min_bytes:
        raise GateError(
            f"the publish log is {log_bytes} bytes, under the {min_bytes}-byte floor. "
            "That is a truncated or empty file, not a publish of a 40-project closure. "
            "Raising the floor is a decision to make in review, not in the failing job."
        )

    rids = parse_sentinel(text, sentinel)
    if not rids:
        raise GateError(
            f"the publish log at {log_path} carries no '{sentinel}' line, so no publish "
            "reported success into it. Treating this as success is how a gate goes green on "
            "a process that died: the job writes the sentinel only after `dotnet publish` "
            "exits 0, and its absence is the whole signal."
        )
    if expect_rid and expect_rid not in rids:
        raise GateError(
            f"the publish log at {log_path} carries sentinel rid(s) {sorted(rids)}, not the "
            f"expected rid={expect_rid}. This is a different publish's log."
        )

    counts = Counter()
    first_party = defaultdict(set)
    error_ids = set()
    for line in text.splitlines():
        match = _DIAGNOSTIC.search(line)
        if match is None:
            continue
        ident = match.group("id").upper()
        if not _IN_SCOPE.match(ident):
            continue
        if match.group("severity").lower() == "error":
            error_ids.add(ident)
            continue
        counts[ident] += 1
        site = _FIRST_PARTY.search(line)
        if site is not None:
            first_party[ident].add(site.group("rel").replace("\\", "/"))

    return Inventory(counts, dict(first_party), error_ids, rids, log_bytes,
                     len(text.splitlines()))


def parse_baseline(text, baseline_path):
    """Read the committed inventory, or raise GateError / return policy failures."""
    rows = {}
    failures = []
    for lineno, raw in enumerate(text.splitlines(), start=1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue

        parts = [p.strip() for p in line.split("|")]
        if len(parts) < 5:
            failures.append(
                f"{baseline_path}:{lineno}: expected 5 '|'-separated fields "
                "(ID | count | verdict | first-party files | reason) and found {len(parts)}: {line!r}"
            )
            continue

        ident, count_text, verdict, files_text, reason = parts[:5]
        ident = ident.upper()
        if not _IN_SCOPE.match(ident):
            failures.append(
                f"{baseline_path}:{lineno}: {ident!r} is not an IL2xxx/IL3xxx id. This "
                "inventory is the trim/AOT one; compiler diagnostics are errors already."
            )
            continue
        if ident in rows:
            failures.append(f"{baseline_path}:{lineno}: {ident} already has a row on line {rows[ident].lineno}")
            continue
        if not count_text.isdigit() or int(count_text) < 1:
            failures.append(
                f"{baseline_path}:{lineno}: count {count_text!r} is not a positive integer. A row "
                "for a diagnostic that does not occur is a permission, not a measurement — delete it."
            )
            continue
        if verdict not in _VERDICTS:
            failures.append(
                f"{baseline_path}:{lineno}: verdict {verdict!r} is not one of {_VERDICTS}. Every "
                "emitted diagnostic is either accepted with a reason or rejected as a bug to fix; "
                "'tolerated for now' is not one of the two."
            )
            continue
        if len(reason) < 40:
            failures.append(
                f"{baseline_path}:{lineno}: reason is {len(reason)} characters; a reason that "
                "cannot fit in fewer than 40 is not an argument (same floor as ExemptionReason)."
            )
            continue
        if re.match(r"^\W*https?://\S+[.,;]?\W*$", reason):
            failures.append(
                f"{baseline_path}:{lineno}: the reason is a URL. A URL says where the debt is "
                "tracked, not why it is tolerated here."
            )
            continue

        files = frozenset() if files_text == "-" else frozenset(
            f.strip().replace("\\", "/") for f in files_text.split(",") if f.strip()
        )
        for first_party_file in files:
            if not first_party_file.startswith(("src/", "apps/")):
                failures.append(
                    f"{baseline_path}:{lineno}: {first_party_file!r} is neither a first-party path "
                    "nor '-'. Third-party-only is '-'; a first-party site is src/… or apps/…"
                )
        rows[ident] = Row(ident, int(count_text), verdict, files, reason, lineno)

    if failures:
        raise GateError("the baseline is not well formed:\n  " + "\n  ".join(failures))
    return rows


def describe(counts, first_party):
    """Never a bare 'Found 0' — #901 reported 'Found 0' from a matcher that matched nothing."""
    if not counts:
        return "found 0 distinct diagnostic id(s), 0 occurrence(s) (ids: none)"
    ids = " ".join(sorted(counts))
    total = sum(counts.values())
    owned = sum(len(v) for v in first_party.values())
    return (f"found {len(counts)} distinct diagnostic id(s), {total} occurrence(s) "
            f"(ids: {ids}); first-party sites: {owned}")


def compare(baseline, inventory):
    """Both directions: a new diagnostic fails, and a row nothing produces fails."""
    problems = []
    expected = {ident: row.key() for ident, row in baseline.items()}
    actual = inventory.key()

    for ident in sorted(set(actual) - set(expected)):
        sites = sorted(inventory.first_party.get(ident, ()))
        where = f" at {', '.join(sites)}" if sites else " (third-party only)"
        problems.append(
            f"NEW  {ident}: {inventory.counts[ident]} occurrence(s){where} — the baseline has no row "
            f"for it. Add a row with a verdict and a reason, or fix the code that emits it."
        )

    for ident in sorted(set(expected) - set(actual)):
        problems.append(
            f"GONE  {ident}: the baseline carries a row for a diagnostic this publish no longer "
            "emits. The warning was fixed or the dependency moved — delete the row in this same "
            "diff, or the row has become a permission for a concession nobody is making."
        )

    for ident in sorted(set(expected) & set(actual)):
        want_count, want_verdict, want_files = expected[ident]
        got_count, got_files = actual[ident]
        if got_count != want_count:
            problems.append(
                f"COUNT {ident}: {got_count} occurrence(s) now, {want_count} in the baseline. "
                "Re-triage the delta and update the count in the same diff."
            )
        if got_files != want_files:
            added = sorted(got_files - want_files)
            removed = sorted(want_files - got_files)
            detail = []
            if added:
                detail.append(f"new first-party site(s): {', '.join(added)}")
            if removed:
                detail.append(f"site(s) that no longer warn: {', '.join(removed)}")
            problems.append(
                f"SITES {ident}: " + "; ".join(detail) + f" (baseline: "
                f"{', '.join(sorted(want_files)) if want_files else '-'}, now: "
                f"{', '.join(sorted(got_files)) if got_files else '-'} — '-' means third-party only)."
            )
        if want_verdict == "REJECTED":
            problems.append(
                f"VERDICT {ident}: the baseline marks this REJECTED, i.e. a bug to fix rather than a "
                "concession to carry. It is still being emitted, so the fix has not landed."
            )

    if inventory.error_ids:
        problems.append(
            "ERRORS the log contains error-severity trim/AOT diagnostics "
            f"({', '.join(sorted(inventory.error_ids))}). The publish step should have failed on "
            "these; if it did not, the exit code did not propagate and the gate is the only thing "
            "left that noticed."
        )

    return problems


def run(log_path, baseline_path, sentinel, min_bytes, expect_rid, out):
    """Returns the process exit code."""
    try:
        log_text = read_text(log_path, "publish log")
        inventory = parse_log(log_text, log_path, min_bytes, sentinel, expect_rid)
        baseline = parse_baseline(read_text(baseline_path, "baseline"), baseline_path)
    except GateError as exc:
        out(f"{TOOL}: CANNOT RUN (exit 2) — {exc}")
        return 2

    out(f"{TOOL}: publish log {log_path} — {inventory.log_bytes} bytes, {inventory.log_lines} lines; "
        f"sentinel rid={','.join(sorted(inventory.sentinel_rids))}")
    out(f"{TOOL}: {describe(inventory.counts, inventory.first_party)}")
    out(f"{TOOL}: baseline {baseline_path} — {len(baseline)} row(s): "
        f"{', '.join(sorted(baseline)) if baseline else 'none'}")

    problems = compare(baseline, inventory)
    if problems:
        out("")
        out(f"{TOOL}: FAIL (exit 1) — the published inventory does not match the committed one:")
        for problem in problems:
            out(f"  {problem}")
        out("")
        out("Gate semantics: the pass criterion is NOT 'zero IL2026 warnings'. It is")
        out("'the publish completed, and every diagnostic it emitted has a committed row with a")
        out("verdict and a reason'. See docs/AOT_PUBLISH.md.")
        return 1

    out(f"{TOOL}: PASS (exit 0) — publish completed and the inventory matches the committed rows.")
    return 0


# ----------------------------------------------------------------------------
# Self-test. Every case below is a fixture where the outcome is guaranteed by
# construction, and every assertion checks the exit code AND the substance of
# the message, so a matcher that fails for the wrong reason cannot pass.
# ----------------------------------------------------------------------------

_SELFTEST_LOG = """\
{rid} Using .NET SDK 10.0.302 [/home/runner/work/Harbor-Harness/Harbor-Harness/global.json]
  Determining projects to restore...
  Restored /home/runner/work/Harbor-Harness/Harbor-Harness/src/Harbor.Application/Harbor.Application.csproj (in 412 ms).
  Harbor.Application -> /home/runner/work/Harbor-Harness/Harbor-Harness/src/Harbor.Application/bin/Release/net10.0/Harbor.Application.dll
{rid} /home/runner/work/Harbor-Harness/Harbor-Harness/src/Harbor.Application/Session.cs(88,13): warning IL2026: The 'PublishAot' analyzer warned: 'this call site will not be preserved because its containing type is not seen' [HARBOR_APP_CLI]
{rid} /home/runner/work/Harbor-Harness/Harbor-Harness/src/Harbor.Application/Session.cs(91,9): warning IL2026: The 'PublishAot' analyzer warned: 'this call site will not be preserved' [HARBOR_APP_CLI]
{rid} /home/runner/work/Harbor-Harness/Harbor-Harness/src/Harbor.Plugins.Compilation/RoslynPluginCompiler.cs(140,22): warning IL3050: The 'PublishAot' analyzer warned: 'call site can cause AOT analysis warnings' [HARBOR_APP_CLI]
Trimmer warnings summary:
  ILLink: 118 warning(s) from 6 assembly(s) — Microsoft.CodeAnalysis.dll, System.Text.RegularExpressions.dll, ...
  Total: 118
  Sentinel: {sentinel} rid={rid}
"""


def fixture(rid="linux-x64", sentinel=DEFAULT_SENTINEL, extra_lines=(), body=None):
    """A publish log whose bytes exceed the floor, so only the intended defect can fail it."""
    text = body if body is not None else _SELFTEST_LOG.format(rid=rid, sentinel=sentinel)
    padding = "\n".join(f"  linker: pass 0x{i:x} ilc: emit rodata" for i in range(200))
    return text + "\n" + padding + "\n" + "\n".join(extra_lines) + "\n"


def selftest(out):
    failures = []
    checks = 0

    def check(name, got, want_exit, must_contain=(), must_not_contain=()):
        nonlocal checks
        checks += 1
        lines = []
        code = run(*got, out=lines.append)
        blob = "\n".join(lines)
        if code != want_exit:
            failures.append(f"{name}: exit {code}, wanted {want_exit}\n    {blob}")
            return
        for needle in must_contain:
            if needle not in blob:
                failures.append(f"{name}: exit was right but the message never said {needle!r}\n    {blob}")
        for needle in must_not_contain:
            if needle in blob:
                failures.append(f"{name}: message must not say {needle!r}\n    {blob}")

    with _tmpdir() as tmp:
        def path(name):
            return os.path.join(tmp, name)

        def write(name, text):
            with open(path(name), "w", encoding="utf-8") as handle:
                handle.write(text)
            return path(name)

        empty_baseline = "# nothing emitted yet\n"
        two_rows = (
            "IL2026 | 2 | ACCEPTED | src/Harbor.Application/Session.cs | "
            "first-party site, see docs/AOT_PUBLISH.md for the argument\n"
            "IL3050 | 1 | ACCEPTED | src/Harbor.Plugins.Compilation/RoslynPluginCompiler.cs | "
            "Roslyn needs the JIT; the split point is #419 and the ADR is #418\n"
        )

        # 1. The green case, and the exact wording that makes "zero" legible.
        check("green_two_rows",
              (write("log1", fixture()), write("base1", two_rows),
               DEFAULT_SENTINEL, 2000, "linux-x64"),
              0,
              must_contain=["found 2 distinct diagnostic id(s), 3 occurrence(s)",
                            "first-party sites: 2", "PASS (exit 0)"])

        # 2. Zero is only ever printed with its ids and a stated expectation. This
        #    is the state a broken matcher also produces, so it has to read as a
        #    claim about a publish that happened, not as an absence (#901).
        warning_free = "\n".join(
            line for line in _SELFTEST_LOG.format(rid="linux-x64", sentinel=DEFAULT_SENTINEL).splitlines()
            if not _DIAGNOSTIC.search(line)) + "\n"
        check("zero_is_described_not_bare",
              (write("log2", fixture(body=warning_free)), write("base2", empty_baseline),
               DEFAULT_SENTINEL, 2000, "linux-x64"),
              0,
              must_contain=["found 0 distinct diagnostic id(s), 0 occurrence(s)",
                            "ids: none", "PASS (exit 0)"],
              must_not_contain=["Found 0"])

        # 3. THE VACUITY CASE (#988 form): no sentinel, i.e. no publish reported
        #    success. This must be exit 2 and must never be 0, even though the
        #    log is large, well-formed and warning-free.
        check("no_sentinel_cannot_go_green",
              (write("log3", fixture(sentinel="SOMETHING_ELSE")), write("base3", empty_baseline),
               DEFAULT_SENTINEL, 2000, None),
              2,
              must_contain=["CANNOT RUN (exit 2)", "carries no 'HARBOR_AOT_PUBLISH_OK' line"])

        # 4. A log that is a different publish's (wrong rid) is not this gate's evidence.
        check("wrong_rid_is_rejected",
              (write("log4", fixture(rid="win-x64")), write("base4", empty_baseline),
               DEFAULT_SENTINEL, 2000, "linux-x64"),
              2,
              must_contain=["not the expected rid=linux-x64"])

        # 5. THE PLANTED-HIT CASE (#591 form): one extra id in the log, in scope
        #    for this inventory, that the baseline knows nothing about. The
        #    message must NAME it — a drift report that says "something
        #    changed" is the #901 shape.
        check("planted_new_id_fails",
              (write("log5", fixture(extra_lines=[
                  "  /home/runner/work/Harbor-Harness/Harbor-Harness/src/Harbor.Tui.CellForge/Screen.cs(40,5): "
                  "warning IL3001: analyzer warned: planted for the self-test [HARBOR_APP_CLI]"])),
               write("base5", two_rows),
               DEFAULT_SENTINEL, 2000, None),
              1,
              must_contain=["NEW  IL3001", "src/Harbor.Tui.CellForge/Screen.cs", "FAIL (exit 1)"])

        # 5b. A new id at a THIRD-PARTY site is still red — the row is required
        #     whether the site is ours or a package's, only the file column
        #     differs. A matcher that skipped unresolvable paths would pass here.
        check("planted_third_party_id_fails",
              (write("log5b", fixture(extra_lines=[
                  "  /home/runner/.nuget/packages/microsoft.codeanalysis.csharp/5.6.0/lib/netstandard2.0/Microsoft.CodeAnalysis.dll(9,9): "
                  "warning IL2104: linker warning: planted for the self-test [HARBOR_APP_CLI]"])),
               write("base5b", two_rows),
               DEFAULT_SENTINEL, 2000, None),
              1,
              must_contain=["NEW  IL2104", "third-party only"])

        # 5c. An OUT-OF-SCOPE id is not drift. IL0xxx compiler diagnostics are
        #     errors via TreatWarningsAsErrors, so a publish that emitted one
        #     never reached this tool; inventing a row for it would make the
        #     inventory lie about what it inventories.
        check("out_of_scope_id_is_ignored",
              (write("log5c", fixture(extra_lines=[
                  "  /home/runner/work/Harbor-Harness/Harbor-Harness/src/Harbor.Hosting/HostBuilder.cs(9,1): "
                  "warning IL9999: not a trim or AOT id, out of scope [HARBOR_APP_CLI]"])),
               write("base5c", two_rows),
               DEFAULT_SENTINEL, 2000, None),
              0,
              must_contain=["found 2 distinct diagnostic id(s), 3 occurrence(s)"])

        # 6. THE HALVED-RULE CASE (#591 form): the rule is "count occurrences",
        #    and a matcher that reports one line per id instead of counting them
        #    would pass here. Baseline says 2, log has 2 → green; a third
        #    occurrence of an id already in the baseline must turn it red.
        check("occurrence_count_is_not_deduplicated",
              (write("log6", fixture()), write("base6", two_rows), DEFAULT_SENTINEL, 2000, None),
              0,
              must_contain=["found 2 distinct diagnostic id(s), 3 occurrence(s)"])
        check("a_fourth_occurrence_turns_it_red",
              (write("log7", fixture(extra_lines=[
                  "  /home/runner/work/Harbor-Harness/Harbor-Harness/src/Harbor.Application/Session.cs(120,5): "
                  "warning IL2026: analyzer warned: one more [HARBOR_APP_CLI]"])),
               write("base7", two_rows),
               DEFAULT_SENTINEL, 2000, None),
              1,
              must_contain=["COUNT IL2026: 3 occurrence(s) now, 2 in the baseline"])

        # 7. A first-party site that appears is drift even when the count is
        #    unchanged — the granularity that makes this gate actionable.
        check("new_first_party_site_fails",
              (write("log8", fixture(extra_lines=[
                  "  /home/runner/work/Harbor-Harness/Harbor-Harness/src/Harbor.Tui.CellForge/Screen.cs(41,5): "
                  "warning IL3050: analyzer warned: our code now calls Roslyn [HARBOR_APP_CLI]"])),
               write("base8", two_rows),
               DEFAULT_SENTINEL, 2000, None),
              1,
              must_contain=["SITES IL3050", "src/Harbor.Tui.CellForge/Screen.cs"])

        # 8. Liveness: a row for a diagnostic nothing emits is a stale permission.
        check("stale_baseline_row_fails",
              (write("log9", fixture()), write("base9",
                  "IL2026 | 2 | ACCEPTED | src/Harbor.Application/Session.cs | first-party site, tracked in docs/AOT_PUBLISH.md\n"
                  "IL3050 | 1 | ACCEPTED | - | Roslyn needs the JIT; split point is #419, ADR is #418\n"
                  "IL2104 | 7 | ACCEPTED | - | per-assembly trim rollup inside a referenced package\n"),
              DEFAULT_SENTINEL, 2000, None),
              1,
              must_contain=["GONE  IL2104", "delete the row in this same diff"])

        # 9. A REJECTED row that is still emitted has not been fixed.
        check("rejected_row_still_emitting_fails",
              (write("log10", fixture()), write("base10",
                  "IL2026 | 2 | REJECTED | src/Harbor.Application/Session.cs | a bug to fix, not a concession; owner is the PR that fixes it\n"
                  "IL3050 | 1 | ACCEPTED | src/Harbor.Plugins.Compilation/RoslynPluginCompiler.cs | Roslyn needs the JIT; split point is #419, ADR is #418\n"),
              DEFAULT_SENTINEL, 2000, None),
              1,
              must_contain=["VERDICT IL2026", "the fix has not landed"])

        # 10. An error-severity trim diagnostic fails even if the exit code did not.
        check("error_severity_fails",
              (write("log11", fixture(extra_lines=[
                  "  /home/runner/work/Harbor-Harness/Harbor-Harness/src/Harbor.Hosting/HostBuilder.cs(9,1): "
                  "error IL3070: 'System.Reflection.Emit' cannot be used in NativeAOT [HARBOR_APP_CLI]"])),
               write("base11", two_rows),
               DEFAULT_SENTINEL, 2000, None),
              1,
              must_contain=["ERRORS the log contains", "IL3070"])

        # 11. A short log is a truncated file, not a clean publish.
        check("truncated_log_cannot_run",
              (write("log12", "Sentinel: " + DEFAULT_SENTINEL + " rid=linux-x64\n"),
               write("base12", empty_baseline),
               DEFAULT_SENTINEL, 2000, None),
              2,
              must_contain=["under the 2000-byte floor"])

        # 12. No log at all.
        check("absent_log_cannot_run",
              (path("does-not-exist.log"), write("base13", empty_baseline),
               DEFAULT_SENTINEL, 2000, None),
              2,
              must_contain=["CANNOT RUN (exit 2)"])

        # 13. Baseline rows that would not survive review are refused, so a
        #     permission cannot be added by editing the inventory alone.
        for name, row, needle in [
            ("no_verdict", "IL2026 | 2 | TOLERATED | - | some reason that is long enough to pass the floor\n",
             "verdict 'TOLERATED' is not one of"),
            ("zero_count", "IL2026 | 0 | ACCEPTED | - | a row for something that does not occur is a permission not a measurement\n",
             "is not a positive integer"),
            ("url_reason", "IL2026 | 2 | ACCEPTED | - | https://github.com/refusedguy/Harbor-Harness/issues/413\n",
             "the reason is a URL"),
            ("short_reason", "IL2026 | 2 | ACCEPTED | - | later\n", "is 5 characters"),
            ("out_of_scope", "CS1591 | 2 | ACCEPTED | - | a compiler diagnostic is not a trim warning at all, ok\n",
             "is not an IL2xxx/IL3xxx id"),
            ("wrong_field_count", "IL2026 | 2 | ACCEPTED\n", "expected 5 '|'-separated fields"),
            ("third_party_path_claimed",
             "IL2026 | 2 | ACCEPTED | nuget/roslyn/x.cs | a first-party site is src/… or apps/… and nothing else is a site\n",
             "is neither a first-party path"),
        ]:
            check(f"baseline_{name}",
                  (write(f"log14{name}", fixture()), write(f"base14{name}", row),
                   DEFAULT_SENTINEL, 2000, None),
                  2,
                  must_contain=[needle, "the baseline is not well formed"])

    if failures:
        out(f"{TOOL}: SELFTEST FAILED — {len(failures)} of {checks} case(s) wrong")
        for failure in failures:
            out(f"  {failure}")
        return 1

    out(f"{TOOL}: SELFTEST PASSED — {checks} cases, each on a fixture where the outcome is "
        "guaranteed (planted extra id, planted extra occurrence, planted first-party site, "
        "missing sentinel, wrong rid, truncated log, absent log, 7 malformed baseline rows).")
    return 0


class _tmpdir:
    """A scratch directory, so the self-test writes nothing into the checkout."""

    def __enter__(self):
        import tempfile

        self._dir = tempfile.TemporaryDirectory(prefix="aot-gate-selftest-")
        return self._dir.name

    def __exit__(self, *exc):
        self._dir.cleanup()
        return False


def main(argv):
    parser = argparse.ArgumentParser(
        prog=TOOL,
        description="Compare an AOT publish log against the committed trim/AOT inventory.")
    parser.add_argument("--log", help="path to the captured `dotnet publish` log")
    parser.add_argument("--baseline", help="path to the committed inventory")
    parser.add_argument("--sentinel", default=DEFAULT_SENTINEL,
                        help=f"token the publish step writes on success (default: {DEFAULT_SENTINEL})")
    parser.add_argument("--min-bytes", type=int, default=DEFAULT_MIN_BYTES,
                        help=f"floor on the log's size (default: {DEFAULT_MIN_BYTES})")
    parser.add_argument("--expect-rid", default=None,
                        help="require the sentinel to name this runtime identifier")
    parser.add_argument("--selftest", action="store_true",
                        help="run the self-test against guaranteed-hit fixtures and exit")
    args = parser.parse_args(argv)

    out = lambda line: print(line, file=sys.stdout, flush=True)  # noqa: E731

    if args.selftest:
        return selftest(out)
    if not args.log or not args.baseline:
        parser.error("--log and --baseline are both required (or pass --selftest)")
    return run(args.log, args.baseline, args.sentinel, args.min_bytes, args.expect_rid, out)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
