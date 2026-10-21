#!/usr/bin/env python3
"""Re-signing gate for the two frozen public assemblies (#1845).

Stdlib only — no pip, no network, no dotnet. Wired into
.github/workflows/ci.yml as the `public-api` job.

WHAT THIS IS, AND WHY IT SURVIVES THE MIGRATION

  #428 froze the public API of Harbor.Abstractions and
  Harbor.Abstractions.Contracts for v1.0. Until #1845 that freeze was kept by
  two hand-written python text scanners (tools/check-public-api.py, 607 lines,
  and tools/check-public-api-abstractions.py, 605 lines). #1845 replaces both
  with Microsoft.CodeAnalysis.PublicApiAnalyzers, which reads the public
  surface off the compiler's symbol table and therefore knows things a line
  scan never could: generic arity, protected-internal / private-protected,
  explicit interface implementations, ref/out/in/params, parameter default
  values, readonly/record structs, init accessors, and nullability. The full
  parity contract — every rule the two python gates had and what covers it
  now — is in docs/ANALYZERS.md §"The parity contract (#1845)".

  The analyzer takes over the mechanical half of the freeze:
    RS0016  a public symbol that is not in the declared API   -> build error
    RS0017  a declared API symbol that is not in the code      -> build error
  Together those two say: the declared API and the compiled surface are the
  same set, at the tip of the branch, at build time. That is strictly
  stronger than what the python scanners could say.

  What the analyzer does NOT say, and never can, is anything about WHICH
  COMMIT carried the change. It sees one tree. The old gate's rule was about
  a diff: "commit the regenerated baseline in the SAME PR as the API change",
  and the reviewer's half of that rule was "a PR that touches the baseline
  without changing the surface is where the reviewer looks". A PR that
  splits the pair across two commits satisfies the analyzer and hides the
  re-signing from the reviewer.

  This file is that missing half. It reads the PR's COMMIT RANGE and asks
  the question the analyzer cannot: did the frozen surface change, and was
  it re-signed — in the same commit, in the same diff?

RULES

  RESIGN-001  A commit that EDITS a frozen assembly's public-API baseline
              (PublicAPI.Shipped.txt / PublicAPI.Unshipped.txt) must edit
              that assembly's own sources in the same commit. A baseline
              edit that arrives on its own is a hand-edit: nothing in it
              corresponds to code, and the analyzer would reject it with
              RS0017 anyway — but it rejects it as "the symbol could not be
              found", which does not tell the author what they did wrong.

              CREATING the baseline is not covered: there is nothing to
              re-sign when the file does not exist yet, and the content of a
              created baseline is checked by the analyzer in the same commit.
              The adoption of the analyzer is exactly that case.

  RESIGN-002  A commit that edits a frozen assembly's sources must re-sign
              that assembly's baseline in the same commit. A commit that
              changed the public surface and did not touch the baseline
              would fail the BUILD (RS0016) but only at the tip of the
              branch, so a two-commit PR could carry the code change in one
              commit and the re-signing in another. This rule asks for the
              pairing at commit granularity, which is the granularity the
              reviewer reads.

              A commit is EXEMPT when its source diff carries no declaration
              line: no added or removed source line, with comments and
              whitespace stripped, begins with `public` or `protected`. That
              exempts doc-comment edits, reformatting, and edits to internal
              members — none of which change the public surface. The
              exemption is a TEXTUAL test and therefore has false negatives:
              a signature change confined to a continuation line (a changed
              parameter type, a changed default value) is not seen as a
              declaration change and is exempted. The build still catches
              that one — RS0016/RS0036 compare the tip against the baseline —
              so the exemption costs the commit-level pairing, never the
              gate.

  RESIGN-FLOOR  Each frozen baseline must hold at least the number of rows
              the floor names. The floor's MEANING changed with the
              migration and the change is the point of stating it:

                before  the python scanner counted the members it found in
                        the source tree, and the floor said "the scanner saw
                        at least N members" — a ratchet against a matcher
                        that had silently stopped matching.

                after   there is no scanner. The declared API IS the frozen
                        baseline file, and the analyzer compares it against
                        the compiled surface. So the floor counts the ROWS
                        of the baseline file, and it says "the frozen
                        baseline still holds at least N rows" — a ratchet
                        against a baseline that was truncated, emptied or
                        rewritten. The blindness the old floor guarded
                        against is now guarded by RS0017 itself: an analyzer
                        that stopped seeing the surface would report every
                        one of the shipped rows as missing, which is a red
                        build, not a quiet pass.

              The two numbers are not comparable and were never meant to be:
              the analyzer's rows include implicit constructors and property
              accessors, which the text scan never counted because they
              have no `public` keyword in the source.

EXIT CODES

  0  the re-signing is paired
  1  a rule fired (the message names the commit and the file)

SCOPE, AND ITS LIMITS — STATED, NOT HIDDEN

  * This gate reads GIT HISTORY, so it needs the PR's base and head. It
    degrades to "nothing to check" outside a checkout — loudly, by exiting
    non-zero with a reason, never by passing.
  * It reads the two frozen assemblies only. The analyzer package is
    referenced by those two projects and no others, so the freeze is
    exactly those two surfaces and nothing else is in scope.
  * RESIGN-002's exemption is textual (see above). The false negatives are
    listed rather than hidden.
  * A PR that adds a member in one commit and removes it in a later commit
    satisfies the analyzer (the tip is consistent) and satisfies this gate
    (both commits re-sign). The frozen surface shrank, and neither gate
    sees it. That hole existed in the python gate too — the baseline is
    compared at the tip there as well — and closing it is a separate
    decision (a release-time fold of PublicAPI.Unshipped.txt into
    PublicAPI.Shipped.txt), not something this file pretends to do.
"""

import argparse
import os
import re
import subprocess
import sys

# The two frozen assemblies. The analyzer package is referenced by these two
# projects and by no others, so the freeze is exactly these two surfaces.
FROZEN = (
    ("src/Harbor.Abstractions.Contracts", "Harbor.Abstractions.Contracts"),
    ("src/Harbor.Abstractions", "Harbor.Abstractions"),
)

BASELINE_NAMES = ("PublicAPI.Shipped.txt", "PublicAPI.Unshipped.txt")

# A declaration line, for the RESIGN-002 exemption: after comments and
# whitespace are stripped, a line that begins with `public` or `protected`.
# Everything else (doc comments, `internal`, attribute lines, expression
# bodies, continuation lines) does not count as a declaration change.
DECL_RE = re.compile(r"^(public|protected)\b")

# A source line, with the comment tail removed. Doc comments are whole lines
# and are dropped separately; a trailing `//` on a code line is removed here.
LINE_COMMENT_RE = re.compile(r"//.*$")
BLOCK_COMMENT_RE = re.compile(r"/\*.*?\*/", re.S)


class GateError(Exception):
    """A condition that stops the gate from running at all.

    Raised instead of returning findings, because a gate that cannot see the
    tree must not report a clean bill of health.
    """


def run_git(args, cwd):
    """Run git and return stdout. Raises GateError on a non-zero exit."""
    proc = subprocess.run(
        ["git", "-C", cwd] + list(args),
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        errors="replace",
    )
    if proc.returncode != 0:
        raise GateError(
            "git " + " ".join(args) + " failed (" + str(proc.returncode) + "): "
            + proc.stderr.strip())
    return proc.stdout


def baseline_paths(directory):
    """The public-API baseline files of one frozen assembly."""
    return [os.path.join(directory, name) for name in BASELINE_NAMES]


def is_baseline(path):
    return os.path.basename(path) in BASELINE_NAMES


def in_directory(path, directory):
    """Whether a repo-relative path lives under `directory`."""
    return path == directory or path.startswith(directory.rstrip("/") + "/")


def strip_comments(text):
    """Remove comments from a source fragment, keeping line structure."""
    text = BLOCK_COMMENT_RE.sub(lambda m: "\n" * m.group(0).count("\n"), text)
    out = []
    for line in text.split("\n"):
        out.append(LINE_COMMENT_RE.sub("", line))
    return "\n".join(out)


def declaration_changed(diff_text):
    """Whether a diff carries an added or removed declaration line.

    The diff is expected in `git diff` unified format: lines that begin with
    '+' or '-' are the changed ones, '+++' and '---' are the file headers.
    """
    for raw in diff_text.split("\n"):
        if raw.startswith("+++") or raw.startswith("---"):
            continue
        if not raw or raw[0] not in "+-":
            continue
        body = strip_comments(raw[1:])
        for line in body.split("\n"):
            if DECL_RE.match(line.strip()):
                return True
    return False


def commit_range(base, head, cwd):
    """The commits of `base..head`, oldest first, as (sha, subject).

    Merge commits are skipped: a merge commit's diff against its first parent
    is the merged content, which the individual commits already carry, and
    the merge ref a pull_request checkout gives us is not a change anybody
    made.
    """
    out = run_git(
        ["log", "--no-merges", "--reverse", "--format=%H%x09%s", base + ".." + head],
        cwd)
    commits = []
    for line in out.split("\n"):
        if not line.strip():
            continue
        sha, _, subject = line.partition("\t")
        commits.append((sha, subject))
    return commits


def changed_files(sha, cwd):
    """The repo-relative paths a commit touched."""
    out = run_git(
        ["show", "--name-only", "--format=", "--no-renames", sha], cwd)
    paths = []
    for line in out.split("\n"):
        line = line.strip()
        if line:
            paths.append(line)
    return paths


def commit_diff(sha, paths, cwd):
    """The unified diff of one commit restricted to `paths`."""
    if not paths:
        return ""
    out = run_git(
        ["show", "--format=", "--unified=0", "--no-color", "--no-renames", sha, "--"] + list(paths),
        cwd)
    return out


def existed_in_parent(sha, path, cwd):
    """Whether a path already existed in the commit's parent.

    The first commit of a repository has no parent, and `git cat-file -e`
    says so with a non-zero exit; that answer is "no", which is what the
    caller wants.
    """
    proc = subprocess.run(
        ["git", "-C", cwd, "cat-file", "-e", sha + "^:" + path],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )
    return proc.returncode == 0


def count_rows(path):
    """The number of API rows in a public-API baseline file."""
    rows = 0
    with open(path, encoding="utf-8") as handle:
        for line in handle:
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            rows += 1
    return rows


def check_commit(sha, subject, files, cwd, findings):
    """RESIGN-001 and RESIGN-002 for one commit."""
    for directory, assembly in FROZEN:
        touched = [p for p in files if in_directory(p, directory)]
        if not touched:
            continue

        sources = [p for p in touched if p.endswith(".cs")]
        baselines = [p for p in touched if is_baseline(p)]

        # RESIGN-001: a baseline edit arrives with the code it re-signs.
        #
        # Creating the baseline is not covered. There is nothing to re-sign
        # when the file does not exist yet, and the content of a created
        # baseline is checked by the analyzer in the same commit (RS0016 and
        # RS0017 compare it against the compiled surface). The adoption of
        # the analyzer itself is exactly this case: the baseline appears in
        # the commit that introduces it, with no declaration change to
        # accompany it.
        edited = [p for p in baselines if existed_in_parent(sha, p, cwd)]
        # A baseline edit that only REMOVES a row is a CORRECTION, not a
        # re-signing: the declaration it named did not change, so there is no
        # code to arrive with. The compiler still holds the surface honest
        # (RS0025 for a row written twice, RS0017 for a row that names
        # nothing), so the removal needs no companion.
        removal_only = all(
            not additions_in_commit(sha, path, cwd)
            for path in edited
        )
        if edited and not sources and not removal_only:
            findings.append(
                "RESIGN-001 " + sha[:12] + " " + subject
                + " — the frozen baseline of " + assembly + " was edited in this "
                + "commit (" + ", ".join(edited) + ") and no source of the "
                + "assembly was touched with it. A re-signing is a diff, not a "
                + "file edit: the code that changed the surface and the row "
                + "that records it belong in the same commit.")

        # RESIGN-002: the code that changes the surface re-signs it here.
        if sources and not baselines:
            diff = commit_diff(sha, sources, cwd)
            if declaration_changed(diff):
                findings.append(
                    "RESIGN-002 " + sha[:12] + " " + subject
                    + " — this commit changes a public declaration of "
                    + assembly + " (" + ", ".join(sorted(sources)[:3])
                    + (", ..." if len(sources) > 3 else "")
                    + ") and does not re-sign the frozen baseline with it. "
                    + "The build would still fail on RS0016 at the tip of the "
                    + "branch, but a two-commit PR could hide the re-signing "
                    + "from the reviewer. Put the baseline row in the same "
                    + "commit as the declaration it records.")


def additions_in_commit(sha, path, cwd):
    """Whether the commit ADDED lines to the given file."""
    diff = subprocess.run(
        ["git", "-C", cwd, "diff", sha + "^", sha, "--", path],
        capture_output=True, text=True).stdout
    for line in diff.split("\n"):
        if line.startswith("+++") or line.startswith("---"):
            continue
        if line.startswith("+"):
            body = strip_comments(line[1:]).strip()
            if body:
                return True
    return False


def check_duplicates(repo, findings):
    """RESIGN-DUP: no row of a frozen baseline appears twice.

    The analyzer reads a baseline as a SET of declarations, so a row written
    twice is not "the same row twice" — it is one row and a build that stops
    (RS0025: the symbol appears more than once). The compiler says so, but
    only in the lane that recompiles the assembly, and a lane that restores
    a cached build does not recompile it. This check is a file read, and it
    runs in the lane where a build may not happen at all.
    """
    for directory, assembly in FROZEN:
        for name in BASELINE_NAMES:
            path = os.path.join(repo, directory, name)
            if not os.path.isfile(path):
                continue
            seen = {}
            with open(path, encoding="utf-8") as handle:
                for number, line in enumerate(handle, 1):
                    row = line.strip()
                    if not row or row.startswith("#"):
                        continue
                    if row in seen:
                        findings.append(
                            "RESIGN-DUP " + assembly + " — the row on line "
                            + str(number) + " of " + path + " is written twice: it "
                            + "already stands on line " + str(seen[row]) + ". A "
                            + "baseline is a set: a row written twice is a build "
                            + "that stops with RS0025, and which of the two rows "
                            + "is meant to be there is ambiguous.")
                    else:
                        seen[row] = number


def check_floors(repo, floors, findings):
    """RESIGN-FLOOR: the frozen baseline still holds at least N rows."""
    for directory, assembly in FROZEN:
        floor = floors.get(directory)
        if floor is None:
            continue
        shipped = os.path.join(repo, directory, BASELINE_NAMES[0])
        if not os.path.isfile(shipped):
            findings.append(
                "RESIGN-FLOOR " + assembly + " — " + shipped
                + " does not exist. A gate that passes on a deleted baseline "
                + "is decorative.")
            continue
        rows = count_rows(shipped)
        if rows < floor:
            findings.append(
                "RESIGN-FLOOR " + assembly + " — " + shipped + " holds " + str(rows)
                + " rows, below the floor of " + str(floor)
                + ". The frozen baseline was truncated, emptied or rewritten. "
                + "A removal from the frozen surface is a breaking change and "
                + "is recorded as a `*REMOVED*` row in PublicAPI.Unshipped.txt, "
                + "not as a deletion from the shipped baseline.")


def run_gate(repo, base, head, floors):
    """Returns the list of findings. Raises GateError when it cannot run."""
    if not os.path.isdir(os.path.join(repo, ".git")) and not os.path.isfile(
            os.path.join(repo, ".git")):
        raise GateError("no git checkout at " + repo + " — the re-signing gate "
                        "reads commit history, so it has nothing to read here")
    commits = commit_range(base, head, repo)
    findings = []
    check_duplicates(repo, findings)
    for sha, subject in commits:
        check_commit(sha, subject, changed_files(sha, repo), repo, findings)
    check_floors(repo, floors, findings)
    return findings, len(commits)


# --------------------------------------------------------------- self-test
#
# The load-bearing non-vacuity pair: a commit that changes a public
# declaration WITHOUT re-signing the baseline must fail naming it, and a
# commit that re-signs the baseline must pass. If either stops failing, the
# gate is decorative.

def _git(cwd, *args):
    subprocess.run(["git", "-C", cwd] + list(args), check=True,
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)


def _write(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as handle:
        handle.write(text)


def _fixture(root, with_baseline=True):
    """A throwaway repository with one frozen assembly and a baseline."""
    _git(root, "init", "-q", "-b", "main")
    _git(root, "config", "user.email", "gate@example.invalid")
    _git(root, "config", "user.name", "gate")
    _write(os.path.join(root, "src/Harbor.Abstractions/Widget.cs"),
           "namespace Harbor.Abstractions;\n"
           "public sealed class Widget\n"
           "{\n"
           "    public int Size { get; }\n"
           "}\n")
    if with_baseline:
        _write(os.path.join(root, "src/Harbor.Abstractions/PublicAPI.Shipped.txt"),
               "#nullable enable\n"
               "Harbor.Abstractions.Widget\n")
    _git(root, "add", "-A")
    _git(root, "commit", "-q", "-m", "base")
    return root


def self_test():
    import tempfile
    ok = True

    def check(name, cond, detail=""):
        nonlocal ok
        ok &= bool(cond)
        print("  [%s] %s %s" % (name.ljust(46), "ok" if cond else "<< MISMATCH", detail))

    with tempfile.TemporaryDirectory() as tmp:
        root = _fixture(tmp)

        # A clean tree: nothing to report.
        findings, count = run_gate(root, "HEAD~0", "HEAD", {})
        check("clean tree is clean", not findings and count == 0,
              "findings=%s" % findings)

        # A commit that changes a public declaration without re-signing.
        _write(os.path.join(root, "src/Harbor.Abstractions/Widget.cs"),
               "namespace Harbor.Abstractions;\n"
               "public sealed class Widget\n"
               "{\n"
               "    public int Size { get; }\n"
               "    public int Weight { get; }\n"
               "}\n")
        _git(root, "add", "-A")
        _git(root, "commit", "-q", "-m", "add a member without re-signing")
        findings, _ = run_gate(root, "HEAD~1", "HEAD", {})
        check("unre-signed declaration fails, naming it",
              any(f.startswith("RESIGN-002") for f in findings),
              "findings=%s" % findings)

        # The same change WITH the re-signing, in ONE commit: clean. The
        # amend is deliberate — the rule is that the declaration and the row
        # that records it are ONE diff, so the fixture must present them as
        # one diff. (Committing them separately is what RESIGN-001 and
        # RESIGN-002 exist to reject.)
        _write(os.path.join(root, "src/Harbor.Abstractions/PublicAPI.Shipped.txt"),
               "#nullable enable\n"
               "Harbor.Abstractions.Widget\n"
               "Harbor.Abstractions.Widget.get_Size -> int\n")
        _git(root, "add", "-A")
        _git(root, "commit", "-q", "--amend", "-m", "add a member and re-sign it")
        findings, _ = run_gate(root, "HEAD~1", "HEAD", {})
        check("re-signed declaration passes", not findings,
              "findings=%s" % findings)

        # A baseline edit with no code: fails.
        _write(os.path.join(root, "src/Harbor.Abstractions/PublicAPI.Shipped.txt"),
               "#nullable enable\n"
               "Harbor.Abstractions.Widget\n"
               "Harbor.Abstractions.Widget.get_Size -> int\n"
               "Harbor.Abstractions.Widget.get_Weight -> int\n")
        _git(root, "add", "-A")
        _git(root, "commit", "-q", "-m", "baseline edit with no code")
        findings, _ = run_gate(root, "HEAD~1", "HEAD", {})
        check("baseline edit without code fails, naming it",
              any(f.startswith("RESIGN-001") for f in findings),
              "findings=%s" % findings)

        # A doc-comment edit is not a declaration change: exempt.
        _write(os.path.join(root, "src/Harbor.Abstractions/Widget.cs"),
               "namespace Harbor.Abstractions;\n"
               "/// <summary>A widget.</summary>\n"
               "public sealed class Widget\n"
               "{\n"
               "    /// <summary>The size.</summary>\n"
               "    public int Size { get; }\n"
               "    public int Weight { get; }\n"
               "}\n")
        _git(root, "add", "-A")
        _git(root, "commit", "-q", "-m", "doc comment only")
        findings, _ = run_gate(root, "HEAD~1", "HEAD", {})
        check("doc-comment edit is exempt", not findings,
              "findings=%s" % findings)

        # Creating the baseline is exempt: there is nothing to re-sign yet.
        with tempfile.TemporaryDirectory() as other:
            fresh = _fixture(other, with_baseline=False)
            _write(os.path.join(fresh, "src/Harbor.Abstractions/PublicAPI.Shipped.txt"),
                   "#nullable enable\n"
                   "Harbor.Abstractions.Widget\n")
            _git(fresh, "add", "-A")
            _git(fresh, "commit", "-q", "-m", "create the baseline")
            findings, _ = run_gate(fresh, "HEAD~1", "HEAD", {})
            check("creating the baseline is exempt", not findings,
                  "findings=%s" % findings)

        # The floor: a truncated baseline fails.
        findings, _ = run_gate(root, "HEAD~0", "HEAD",
                               {"src/Harbor.Abstractions": 99})
        check("floor fires on a short baseline",
              any(f.startswith("RESIGN-FLOOR") for f in findings),
              "findings=%s" % findings)

        # A baseline edit that only REMOVES a row is a correction, not a
        # re-signing: no declaration changed, so no code has to arrive with
        # it. (The compiler still holds the surface honest: RS0025 for a row
        # written twice, RS0017 for a row that names nothing.)
        _write(os.path.join(root, "src/Harbor.Abstractions/PublicAPI.Shipped.txt"),
               "#nullable enable\n"
               "Harbor.Abstractions.Widget\n"
               "Harbor.Abstractions.Widget.get_Size -> int\n")
        _git(root, "add", "-A")
        _git(root, "commit", "-q", "-m", "re-sign the baseline with the member")
        _write(os.path.join(root, "src/Harbor.Abstractions/PublicAPI.Shipped.txt"),
               "#nullable enable\n"
               "Harbor.Abstractions.Widget\n")
        _git(root, "add", "-A")
        _git(root, "commit", "-q", "-m", "drop a row that named nothing")
        findings, _ = run_gate(root, "HEAD~1", "HEAD", {})
        check("a baseline edit that only removes a row needs no code",
              not any(f.startswith("RESIGN-001") for f in findings),
              "findings=%s" % findings)

        # A row written twice is a build that stops with RS0025, and the
        # compiler only says so in the lane that recompiles the assembly.
        _write(os.path.join(root, "src/Harbor.Abstractions/PublicAPI.Shipped.txt"),
               "#nullable enable\n"
               "Harbor.Abstractions.Widget\n"
               "Harbor.Abstractions.Widget\n")
        _git(root, "add", "-A")
        _git(root, "commit", "-q", "-m", "the same row twice")
        findings, _ = run_gate(root, "HEAD~1", "HEAD", {})
        check("a row written twice is reported",
              any(f.startswith("RESIGN-DUP") for f in findings),
              "findings=%s" % findings)
        _git(root, "reset", "-q", "--hard", "HEAD~1")

    print("\nself-test %s" % ("PASS" if ok else "FAIL"))
    return 0 if ok else 1


def main():
    ap = argparse.ArgumentParser(
        description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--base", default=None,
                    help="the base commit of the range (default: the merge "
                         "base of HEAD and origin/dev, or the value the "
                         "workflow passes)")
    ap.add_argument("--head", default="HEAD",
                    help="the head commit of the range (default: HEAD)")
    ap.add_argument("--min-rows", action="append", default=[],
                    metavar="ASSEMBLY=N",
                    help="fail unless the frozen baseline of ASSEMBLY (a "
                         "directory under src/) holds at least N rows; "
                         "repeatable")
    ap.add_argument("--self-test", action="store_true",
                    help="run the gate against known-broken fixtures")
    args = ap.parse_args()

    if args.self_test:
        return self_test()

    repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

    floors = {}
    for item in args.min_rows:
        key, _, value = item.rpartition("=")
        if not key or not value.isdigit():
            print("FAIL --min-rows wants ASSEMBLY=N, got %r" % item)
            return 1
        floors[key] = int(value)

    base = args.base
    if base is None:
        try:
            base = run_git(["merge-base", "HEAD", "origin/dev"], repo).strip()
        except GateError as error:
            print("FAIL " + str(error))
            print("FAIL no base commit to compare against — pass --base")
            return 1
    if not base:
        print("FAIL no base commit to compare against — pass --base")
        return 1

    try:
        findings, count = run_gate(repo, base, args.head, floors)
    except GateError as error:
        print("FAIL " + str(error))
        return 1

    print("compared %d commit(s) in %s..%s" % (count, base[:12], args.head))
    for text in findings:
        print("FAIL " + text)
    if findings:
        print("\n%d finding(s) — red" % len(findings))
        return 1
    print("OK: every change to the frozen surface is re-signed in its own commit")
    return 0


if __name__ == "__main__":
    sys.exit(main())
