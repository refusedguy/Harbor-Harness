#!/usr/bin/env python3
"""Fail when a recorded demo GIF no longer shows every distinct screen it promised.

Stdlib only — no pip, no SDK, no network. WHY PYTHON: the drift gate in
demo.yml already reads the GIFs with a stdlib walker after recording, and this
check runs in the same place. Its self-test runs in docs.yml on a pull request
that has no build at all, where a compiled gate could not be rebuilt. The
measurement itself comes from the committed gates binary, so the GIF walker is
not duplicated here.

The minimum distinct screens per scene is pinned in demo/toolchain.lock, NEXT
TO the frame-budget keys, not in assets/demo/baseline.json. The recording
workflow rewrites the manifest, so a minimum read from it would ratchet itself
down: a recording that lost a state would rewrite the minimum to match. The
lock is edited by hand in the same commit as a deliberate scene change.
"""

import contextlib
import io
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path

GIFS = ("hero", "markdown", "approval", "plain")
MIN_KEY = "GIF_MIN_SCREENS_{}"
GATES = "tools/bin/harbor-gates-linux-x64"
LOCK = "demo/toolchain.lock"
GIF_DIR = "assets/demo"

FAIL = "shows only {found} distinct screen(s) but at least {minimum} are required — " \
       "the recording lost its states (every frame shows the same screen); check the " \
       "tape before re-baselining"


def read_lock(path):
    """Read the KEY=VALUE lock the same way the shell scripts source it."""
    values = {}
    for raw in Path(path).read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, value = line.split("=", 1)
        value = value.strip()
        if len(value) >= 2 and value[0] == value[-1] and value[0] in "\"'":
            value = value[1:-1]
        values[key.strip()] = value.replace("\\$", "$")
    return values


def unique_frames(gif, gates):
    """Ask demo-repro how many distinct LZW payloads the GIF carries."""
    result = subprocess.run(
        [gates, "demo-repro", "measure", "--input", str(gif)],
        capture_output=True, text=True, check=False,
    )
    if result.returncode != 0:
        raise RuntimeError("demo-repro measure failed on {}: {}".format(
            gif, result.stderr.strip()))
    return json.loads(result.stdout)["uniqueFrames"]


def check(gif_dir, lock, gates):
    """Return (findings, summary text). One finding per scene below minimum."""
    findings = []
    rows = []
    for name in GIFS:
        gif = Path(gif_dir) / "{}-compressed.gif".format(name)
        key = MIN_KEY.format(name.upper())
        if key not in lock:
            findings.append("`{}` has no `{}` in {} — the screen minimum is "
                            "missing, so this scene is not checked".format(
                                name, key, LOCK))
            rows.append("| `{}` | — | — | ❌ no minimum |".format(name))
            continue
        minimum = int(lock[key])
        if minimum < 2:
            findings.append("`{}` has a minimum of {} in {} — a scene is a "
                            "sequence, so the floor must be at least 2".format(
                                name, minimum, LOCK))
            rows.append("| `{}` | — | {} | ❌ invalid minimum |".format(
                name, minimum))
            continue
        if not gif.is_file():
            findings.append("missing demo GIF: {}".format(gif))
            rows.append("| `{}` | — | {} | ❌ missing |".format(name, minimum))
            continue
        try:
            found = unique_frames(gif, gates)
        except (RuntimeError, ValueError, KeyError) as ex:
            findings.append("unreadable demo asset {}: {}".format(gif, ex))
            rows.append("| `{}` | — | {} | ❌ unreadable |".format(name, minimum))
            continue
        if found < minimum:
            findings.append("`{}` ".format(name) + FAIL.format(
                found=found, minimum=minimum))
            rows.append("| `{}` | {} | {} | ❌ below minimum |".format(
                name, found, minimum))
        else:
            rows.append("| `{}` | {} | {} | ✅ ok |".format(name, found, minimum))

    text = "\n".join([
        "## Demo screen coverage",
        "",
        "Minimum distinct screens are pinned in [`{}`]({}) (#2509), not in the "
        "manifest: the recording workflow rewrites the manifest, so a minimum "
        "read from it would follow a lost state down. A scene fails here even "
        "when its frame count, duration and size still match the envelope."
        .format(LOCK, LOCK),
        "",
        "| gif | distinct screens found | minimum | verdict |",
        "|---|---|---|---|",
        *rows,
        "",
    ])
    if findings:
        text += "**{} finding(s) — a promised state is not on screen.**\n\n".format(
            len(findings)) + "\n".join(findings) + "\n"
    else:
        text += "**Every scene still shows at least its pinned number of screens.**\n"
    return findings, text


def run(gif_dir, lock_path, gates, summary):
    return run_check(gif_dir, read_lock(lock_path), gates, summary)


def run_check(gif_dir, lock, gates, summary):
    findings, text = check(gif_dir, lock, gates)
    for finding in findings:
        print("::error::" + finding)
    print(text)
    if summary:
        path = os.environ.get("GITHUB_STEP_SUMMARY")
        if path:
            with open(path, "a", encoding="utf-8") as handle:
                handle.write(text + "\n")
    return findings, text


def gif_with_frames(payloads):
    """A hand-made GIF whose image frames carry the given LZW payloads.

    The gate hashes those payloads, so repeating one is the smallest possible
    model of an artificially flattened recording; distinct bytes are a model of
    distinct screens. No decoder is involved.
    """
    out = bytearray(b"GIF89a")
    out += bytes([1, 0, 1, 0, 0x80, 0, 0])          # 1x1, two-colour global table
    out += bytes([0xFF, 0xFF, 0xFF, 0, 0, 0])
    for byte in payloads:
        out += bytes([0x21, 0xF9, 0x04, 0x01, 0x08, 0, 0, 0])  # 80 ms delay
        out += bytes([0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0])         # image descriptor
        out += bytes([0x02, 0x01, byte, 0])                     # LZW payload
    out += bytes([0x3B])
    return bytes(out)


def self_test():
    """Prove the gate is red on a flattened GIF and green on a varied one."""
    root = Path(__file__).resolve().parent.parent
    gates = str(root / GATES)
    saved = os.environ.pop("GITHUB_STEP_SUMMARY", None)
    try:
        with tempfile.TemporaryDirectory(prefix="demo-screens-selftest-") as tmp:
            gif_dir = Path(tmp) / "gifs"
            gif_dir.mkdir()
            for name in GIFS:
                # hero is artificially flat: four frames, one payload.
                payloads = [0x44] * 4 if name == "hero" else [0x40, 0x41, 0x42]
                (gif_dir / "{}-compressed.gif".format(name)).write_bytes(
                    gif_with_frames(payloads))
            lock = {
                "GIF_MIN_SCREENS_HERO": "3",
                "GIF_MIN_SCREENS_MARKDOWN": "2",
                "GIF_MIN_SCREENS_APPROVAL": "2",
                "GIF_MIN_SCREENS_PLAIN": "2",
            }
            lock_file = Path(tmp) / "toolchain.lock"
            lock_file.write_text("\n".join(
                "{}={}".format(key, value) for key, value in lock.items()) + "\n",
                encoding="utf-8")
            # Exercise the real CLI path, including its exit code. Capture the
            # output: a self-test that is green must not leave ::error:: lines
            # on a passing step.
            out = io.StringIO()
            with contextlib.redirect_stdout(out):
                code = main(["--dir", str(gif_dir), "--lock", str(lock_file)])
            findings, text = check(gif_dir, lock, gates)
            printed = out.getvalue()
            problems = []
            if code != 1:
                problems.append("a flattened recording exited {} instead of 1".format(
                    code))
            if len(findings) != 1:
                problems.append("expected exactly one finding, got {}: {}".format(
                    len(findings), findings))
            else:
                wanted = "shows only 1 distinct screen(s) but at least 3 are required"
                if wanted not in findings[0] or wanted not in printed:
                    problems.append("the finding does not name found and minimum: "
                                    + findings[0])
                if "lost its states" not in findings[0]:
                    problems.append("the finding does not explain what was lost: "
                                    + findings[0])
            if "| `hero` | 1 | 3 | ❌ below minimum |" not in text:
                problems.append("the summary does not show found vs minimum for hero")
            if "| `markdown` | 3 | 2 | ✅ ok |" not in text:
                problems.append("a varied GIF did not pass the positive control")
            if problems:
                for problem in problems:
                    print("self-test FAILED: " + problem, file=sys.stderr)
                return 1
            print("self-test OK: a flattened GIF is red with found vs minimum in "
                  "the message, and a varied GIF stays green")
            return 0
    finally:
        if saved is not None:
            os.environ["GITHUB_STEP_SUMMARY"] = saved


def main(argv):
    if "--self-test" in argv:
        return self_test()
    gif_dir = GIF_DIR
    lock_path = LOCK
    index = 0
    while index < len(argv):
        if argv[index] == "--dir" and index + 1 < len(argv):
            gif_dir = argv[index + 1]
            index += 2
        elif argv[index] == "--lock" and index + 1 < len(argv):
            lock_path = argv[index + 1]
            index += 2
        else:
            print("usage: check-demo-screens.py [--dir DIR] [--lock FILE] "
                  "[--self-test]", file=sys.stderr)
            return 2
    gates = str(Path(__file__).resolve().parent / "bin" / "harbor-gates-linux-x64")
    findings, _ = run(gif_dir, lock_path, gates, True)
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
