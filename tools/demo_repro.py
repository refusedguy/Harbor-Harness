#!/usr/bin/env python3
"""Deterministic normalisation and the record-twice reproducibility check for the
README demo GIFs (issue #426).

Why this exists
---------------
A VHS capture is a screen recording: how many frames land, and what each one
holds, depends on *when* the sampler fired relative to the CLI's output. Two runs
of the same tape measured on the same machine produced 140 frames / 73 475 bytes
and 167 frames / 96 554 bytes — a +31 % size swing and a ±frame count, which the
drift gate could not distinguish from a real regression. Three things had to be
made deterministic, and only the third lives here:

  1. the data path      — `demo/run-scene.sh` unsets every API key, scrubs the
                          temp-HOME GUID and the per-scene stopwatch, and pins
                          TZ/LANG/LC_ALL/PS1, so the CLI's own output is
                          byte-identical between runs (verified per scene);
  2. the toolchain      — `demo/install-toolchain.sh` installs vhs / ttyd /
                          the font at the sha256-pinned versions in
                          `demo/toolchain.lock`;
  3. the frame pacing   — this file: every GIF is re-emitted at exactly
                          `GIF_FRAMES` frames and `GIF_FPS` fps.

Usage
-----
    tools/demo_repro.py normalize --input IN.gif --output OUT.gif
    tools/demo_repro.py compare   --a A.gif --b B.gif          # exit 1 on drift
    tools/demo_repro.py report    --input OUT.gif

`normalize` drops the leading frames a slow boot leaves on the tape, holds the
tail with the last frame (`tpad=stop_mode=clone`) and forces a fixed frame count,
so the *envelope* — frame count, duration, dimensions, per-frame delay — is a
constant of the toolchain, not of the machine. `compare` is the machine-checkable
form of "deterministic playback": the two passes must agree on the envelope and
on the content hash of every frame; the per-frame digest walk is stdlib-only (no
GIF decoder, no new package), the same walk the drift gate uses.

Stdlib only (issue #425's constraint carries over). ffmpeg is invoked for the
re-encode; it is a recorder dependency, not a new one.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
LOCK_PATH = REPO_ROOT / "demo" / "toolchain.lock"

TAPES = ("hero", "markdown", "approval", "plain")
TAPE_SCENES = {"hero": "hero", "markdown": "markdown", "approval": "approval", "plain": "hero"}


# --------------------------------------------------------------------------- lock


def read_lock(path: Path = LOCK_PATH) -> dict[str, str]:
    """Parse the KEY=VALUE toolchain lock (same file the shell scripts source)."""
    if not path.is_file():
        raise SystemExit(f"demo_repro: missing {path}")
    values: dict[str, str] = {}
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or "=" not in line:
            continue
        key, _, value = line.partition("=")
        value = value.strip()
        if len(value) >= 2 and value[0] == value[-1] and value[0] in "\"'":
            value = value[1:-1]
        # The lock escapes a literal `$` in the prompt so sourcing it is safe.
        values[key.strip()] = value.replace("\\$", "$")
    return values


# --------------------------------------------------------------------------- gif


def _sub_blocks(buf: bytes, i: int) -> tuple[bytes, int]:
    out = bytearray()
    while True:
        if i >= len(buf):
            raise ValueError("truncated GIF: sub-block chain runs past end of file")
        size = buf[i]
        i += 1
        if size == 0:
            return bytes(out), i
        if i + size > len(buf):
            raise ValueError("truncated GIF: sub-block runs past end of file")
        out += buf[i:i + size]
        i += size


def measure(path: Path) -> dict:
    """Walk a GIF block by block: size, shape, per-frame delay and payload digests.

    Note on hashing: the digest is over the *raw LZW payload*, so it reflects the
    image data and the palette the encoder chose — not the container. Two renders
    of the same screen can differ in payload while being pixel-identical (an
    encoder may pick a different palette or a different transparent index), so
    `compare` treats a payload mismatch as a *candidate* difference and confirms
    it on decoded pixels before failing.
    """
    buf = Path(path).read_bytes()
    if buf[:6] not in (b"GIF89a", b"GIF87a"):
        raise ValueError(f"not a GIF (bad signature {buf[:6]!r})")

    packed = buf[10]
    i = 13 + (3 * (1 << ((packed & 0x07) + 1)) if packed & 0x80 else 0)

    digests: list[str] = []
    delays: list[int] = []
    delay_cs = 0

    while i < len(buf) and buf[i] != 0x3B:  # 0x3B = trailer
        marker = buf[i]
        if marker == 0x21:  # extension
            if i + 2 > len(buf):
                raise ValueError("truncated GIF: extension introducer runs past end of file")
            if buf[i + 1] == 0xF9:  # graphic control -> next frame delay
                delay_cs = int.from_bytes(buf[i + 4:i + 6], "little")
            _, i = _sub_blocks(buf, i + 2)
            continue
        if marker != 0x2C:  # 0x2C = image descriptor
            raise ValueError(f"unexpected GIF block 0x{marker:02X} at offset {i}")
        local = buf[i + 9]
        i += 10 + (3 * (1 << ((local & 0x07) + 1)) if local & 0x80 else 0) + 1
        payload, i = _sub_blocks(buf, i)
        digests.append(hashlib.sha256(payload).hexdigest())
        delays.append(delay_cs)
        delay_cs = 0

    if not digests:
        raise ValueError("truncated GIF: no image frames found")

    rollup = hashlib.sha256()
    for digest in digests:
        rollup.update(digest.encode("ascii"))

    return {
        "path": str(path),
        "bytes": len(buf),
        "width": int.from_bytes(buf[6:8], "little"),
        "height": int.from_bytes(buf[8:10], "little"),
        "frames": len(digests),
        "durationMs": sum(delays) * 10,
        "delays": sorted(set(delays)),
        "uniqueFrames": len(set(digests)),
        "fileSha256": hashlib.sha256(buf).hexdigest(),
        "framesSha256": rollup.hexdigest(),
        "_digests": digests,
    }


def _screen_similarity(a_pgm: bytes, b_pgm: bytes, threshold: int = 24) -> float:
    """Fraction of pixels that differ by more than `threshold` levels (0-255).

    A hash is the wrong tool here. Three layers of encoder noise sit on top of the
    actual image, and the innermost one is the dither: `paletteuse` scatters ±1-level
    noise across large flat areas (measured on two renders of the same screen:
    22 755 differing grey samples of 744 000, max delta 1 on the quantised image,
    54 on the raw one). A hash reports that as a total mismatch; a reader sees two
    identical screens. Thresholding at 24 levels ignores the dither entirely while
    still catching anything actually visible — a different character, a different
    colour, a leaked GUID or timestamp changes far more than that.
    """
    header = b"P5\n"
    if not a_pgm.startswith(header) or not b_pgm.startswith(header):
        return 0.0
    # Skip the P5 header (magic, whitespace-separated width/height, single space).
    def body(blob: bytes) -> bytes:
        rest = blob[len(header):]
        fields, idx = [], 0
        while len(fields) < 2:
            while idx < len(rest) and rest[idx:idx + 1].isspace():
                idx += 1
            start = idx
            while idx < len(rest) and not rest[idx:idx + 1].isspace():
                idx += 1
            fields.append(rest[start:idx])
        return rest[idx + 1:]

    a_body, b_body = body(a_pgm), body(b_pgm)
    if len(a_body) != len(b_body) or not a_body:
        return 0.0
    differing = sum(1 for x, y in zip(a_body, b_body) if abs(x - y) > threshold)
    return differing / len(a_body)


def _decoded_screens(gif: Path, work: Path, width: int, height: int) -> list[bytes]:
    """Decoded greyscale frames, one per distinct screen shown.

    The GIF palette and the dither are dropped by decoding to greyscale: the result
    is a function of what is on screen, not of the encoder's choices. Consecutive
    duplicates are collapsed, so the caller compares *screens*, not frame counts.
    """
    ffmpeg = shutil.which("ffmpeg") or "ffmpeg"
    frames_dir = work / f"px-{gif.name.replace('/', '_')}"
    frames_dir.mkdir(parents=True, exist_ok=True)
    done = subprocess.run(
        [ffmpeg, "-y", "-loglevel", "error", "-i", str(gif),
         "-vf", f"scale={width}:{height}:flags=neighbor,format=gray",
         "-f", "image2", "-c:v", "pgm", str(frames_dir / "f%05d.pgm")],
        capture_output=True, text=True)
    if done.returncode != 0:
        raise SystemExit(f"demo_repro: could not decode {gif}:\n{done.stderr.strip()}")

    screens: list[bytes] = []
    previous: bytes | None = None
    for frame in sorted(frames_dir.glob("*.pgm")):
        pixels = frame.read_bytes()
        if previous is None or _screen_similarity(previous, pixels) > 0.0005:
            screens.append(pixels)
            previous = pixels
    shutil.rmtree(frames_dir, ignore_errors=True)
    return screens


# ----------------------------------------------------------------------- normalize


def _collapse(digests: list[str]) -> list[tuple[int, int]]:
    """Collapse a frame sequence into (representative index, run length) runs.

    A screen recording repeats each visible screen for an arbitrary number of
    frames — that count is wall-clock and cannot be pinned. The *sequence of
    screens* can be, because it is a function of the text that was written. This
    returns one representative frame per run, so the animation can be re-paced
    onto a fixed cadence instead of being sampled at a random offset.
    """
    runs: list[tuple[int, int]] = []
    for index, digest in enumerate(digests):
        if runs and digests[runs[-1][0]] == digest:
            runs[-1] = (runs[-1][0], runs[-1][1] + 1)
        else:
            runs.append((index, 1))
    return runs


def _ffmpeg(args: list[str], what: str) -> None:
    ffmpeg = shutil.which("ffmpeg") or "ffmpeg"
    done = subprocess.run([ffmpeg, "-y", "-loglevel", "error", *args],
                          capture_output=True, text=True)
    if done.returncode != 0:
        raise SystemExit(f"demo_repro: ffmpeg failed while {what}:\n{done.stderr.strip()}")


def normalize(input_path: Path, output_path: Path, lock: dict[str, str],
              dry_run: bool = False) -> dict:
    """Re-emit a capture at the pinned cadence, rebuilding it from its screen sequence.

    A VHS capture is a wall-clock screen recording: how many frames each visible
    screen occupies, and therefore which frame a given burst lands in, is a
    function of how fast the machine happened to be. Re-encoding that recording
    verbatim just re-freezes the noise (measured: 140 vs 167 frames, 73 475 vs
    96 554 bytes for two passes of the same tape).

    So instead of re-timing the recording, we throw its timing away:

      1. walk the frames and collapse consecutive duplicates into the *sequence of
         screens* — with `demo/run-scene.sh`'s frame-locked pacing that sequence is
         a pure function of the scripted output, so it is identical on every run;
      2. re-emit each screen for exactly GIF_STATE_FRAMES frames at GIF_FPS;
      3. hold the settled screen for the remaining GIF_TAIL_FRAMES frames.

    The result is byte-identical across runs: same screens, same order, same
    frame count, same duration, regardless of how fast the recorder or the CLI
    was. GIF_FRAMES is the hard ceiling that catches a scene growing past its budget.
    """
    frames = int(lock["GIF_FRAMES"])
    fps = int(lock["GIF_FPS"])
    width = int(lock["GIF_WIDTH"])
    height = int(lock["GIF_HEIGHT"])
    state_frames = int(lock["GIF_STATE_FRAMES"])
    tail_frames = int(lock["GIF_TAIL_FRAMES"])

    before = measure(input_path)
    runs = _collapse(before["_digests"])
    states = [index for index, _ in runs]
    needed = len(states) * state_frames + tail_frames
    if needed > frames:
        raise SystemExit(
            f"demo_repro: {input_path} has {len(states)} distinct screens, which needs {needed} "
            f"frames at {state_frames} frames/screen but the budget is {frames} — raise "
            f"GIF_FRAMES/GIF_STATE_FRAMES in demo/toolchain.lock, or the scene grew"
        )
    if dry_run:
        return {"input": str(input_path), "states": states, "framesNeeded": needed,
                "budget": frames, "sourceFrames": before["frames"]}

    with tempfile.TemporaryDirectory(prefix="demo-repro-") as work:
        work_dir = Path(work)
        frames_dir = work_dir / "frames"
        frames_dir.mkdir()

        # Materialise the paced frame list: one PNG per distinct screen, then a
        # hard link per frame that screen is held for. Doing this by hand (rather
        # than via ffmpeg's `select=…*n`, `loop` or a concat manifest) is what makes
        # the frame count and duration exact — every one of those routes re-derives
        # the timing downstream, and the GIF muxer quantises per-frame delays to
        # centiseconds, so a computed schedule drifts.
        select = "+".join(f"eq(n\\,{index})" for index in states)
        _ffmpeg(["-i", str(input_path), "-vf", f"select='{select}',"
                                           f"scale={width}:{height}:flags=lanczos",
                 "-fps_mode", "passthrough", "-c:v", "png", "-f", "image2",
                 str(frames_dir / "screen-%04d.png")],
                f"extracting the screen sequence of {input_path}")

        screens = sorted(frames_dir.glob("screen-*.png"))
        if len(screens) != len(states):
            raise SystemExit(f"demo_repro: expected {len(states)} screens from {input_path}, "
                             f"ffmpeg produced {len(screens)}")

        paced: list[Path] = []
        for screen in screens[:-1]:
            paced.extend([screen] * state_frames)
        paced.extend([screens[-1]] * (state_frames + tail_frames))
        for position, screen in enumerate(paced):
            (frames_dir / f"frame-{position:05d}.png").hardlink_to(screen)

        # A fixed input framerate over an explicitly sized frame list is the only
        # way to land on an exact frame count *and* an exact duration.
        _ffmpeg(["-framerate", str(fps), "-i", str(frames_dir / "frame-%05d.png"),
                 "-vf", "split[a][b];[a]palettegen=max_colors=256:stats_mode=diff[p];"
                        "[b][p]paletteuse=dither=bayer:bayer_scale=3",
                 "-loop", "0", str(output_path)], f"re-pacing {input_path}")

    result = measure(output_path)
    expected_frames = len(states) * state_frames + tail_frames
    # The GIF muxer quantises per-frame delays to centiseconds, so 12 fps lands on
    # 8.3 cs and the total is only defined to within a frame's worth of rounding.
    if result["frames"] != expected_frames:
        raise SystemExit(
            f"demo_repro: {output_path} has {result['frames']} frames, expected {expected_frames} "
            f"({len(states)} screens x {state_frames} + {tail_frames} tail) — the re-pacing is off"
        )
    if result["frames"] > frames:
        raise SystemExit(f"demo_repro: {output_path} has {result['frames']} frames, "
                         f"over the {frames}-frame budget")
    expected_duration = expected_frames * 1000 / fps
    if abs(result["durationMs"] - expected_duration) > 1000 // fps + 1:
        raise SystemExit(
            f"demo_repro: {output_path} is {result['durationMs']} ms, expected ~{expected_duration:.0f} ms "
            f"at {fps} fps — the re-pacing is off"
        )
    result.pop("_digests", None)
    return {"input": str(input_path), "output": str(output_path), "sourceFrames": before["frames"],
            "screens": len(states), "frames": result["frames"], "gif": result}


# ------------------------------------------------------------------------- compare


def compare(a_path: Path, b_path: Path) -> tuple[bool, list[str], list[str], dict, dict]:
    """Record-twice check. Returns (equal, errors, notes, a, b).

    What is asserted, and why:

    * the *envelope* — frame count, duration, per-frame delay, raster — must be
      identical. `normalize` pins all four, so a difference here means the toolchain
      lock and the recorder disagree;
    * the *sequence of screens* must be identical, compared on decoded pixels
      rather than on the encoded bytes. A screen recorder samples a live process on
      a wall clock, so how many frames a screen is held for is not reproducible —
      but *which* screens are shown is, and that is the invariant worth enforcing:
      it is exactly where a leaked GUID, timestamp, counter or spinner would show up;
    * the *settled* final screen must be identical, for the same reason.

    Comparing decoded pixels rather than the LZW payloads matters: a palette or
    transparent-index change makes byte-identical-looking frames hash differently,
    and treating that as a failure would make this check cry wolf on every run.
    """
    a = measure(a_path)
    b = measure(b_path)
    errors: list[str] = []
    notes: list[str] = []

    for field, label in (("frames", "frame count"), ("durationMs", "duration"),
                         ("width", "width"), ("height", "height")):
        if a[field] != b[field]:
            errors.append(
                f"{label} differs between the two passes: {a[field]} vs {b[field]} "
                f"— frame pacing is not deterministic"
            )

    if a["delays"] != b["delays"]:
        errors.append(f"per-frame delays differ: {a['delays']} vs {b['delays']}")

    with tempfile.TemporaryDirectory(prefix="demo-compare-") as work:
        work_dir = Path(work)
        screens_a = _decoded_screens(a_path, work_dir, a["width"], a["height"])
        screens_b = _decoded_screens(b_path, work_dir, b["width"], b["height"])

    # Screens are compared pairwise in order, with a small tolerance for how many
    # differing pixels still counts as "the same screen" (anti-aliasing on glyph
    # edges). A screen that drifted by even one line shifts every pixel below it.
    tolerance = float(os.environ.get("DEMO_PIXEL_TOLERANCE", "0.002"))
    if len(screens_a) != len(screens_b):
        errors.append(
            f"the two passes show a different number of screens: {len(screens_a)} vs "
            f"{len(screens_b)} — the recorded output is not reproducible"
        )
    else:
        worst = 0.0
        worst_at = -1
        for index, (left, right) in enumerate(zip(screens_a, screens_b)):
            delta = _screen_similarity(left, right)
            if delta > worst:
                worst, worst_at = delta, index
        if worst > tolerance:
            errors.append(
                f"screen {worst_at + 1}/{len(screens_a)} differs between the two passes "
                f"({worst:.2%} of pixels beyond the ±24-level noise floor, tolerance "
                f"{tolerance:.2%}) — the recorded output is not reproducible"
            )
        else:
            notes.append(f"all {len(screens_a)} screens match within "
                         f"{worst:.3%} (dither tolerance)")

    if screens_a and screens_b:
        final_delta = _screen_similarity(screens_a[-1], screens_b[-1])
        if final_delta <= tolerance:
            notes.append("settled final screen matches")
        else:
            errors.append(
                f"the settled final screen differs between the two passes ({final_delta:.2%} of "
                f"pixels) — something time- or random-dependent reached the recording"
            )

    if a["bytes"] != b["bytes"]:
        delta = 100.0 * (b["bytes"] - a["bytes"]) / a["bytes"] if a["bytes"] else 0.0
        notes.append(f"file size {a['bytes']:,} vs {b['bytes']:,} bytes ({delta:+.1f} %)")

    for m in (a, b):
        m.pop("_digests", None)
    return (not errors), errors, notes, a, b


# -------------------------------------------------------------------------- report


def _toolchain_provenance(lock: dict[str, str]) -> str:
    lines = [f"- vhs **{lock['VHS_VERSION']}** (sha256 `{lock['VHS_SHA256'][:12]}…`)",
             f"- ttyd **{lock['TTYD_VERSION']}** (sha256 `{lock['TTYD_SHA256'][:12]}…`)"]
    ffmpeg = shutil.which("ffmpeg")
    if ffmpeg:
        try:
            out = subprocess.run([ffmpeg, "-version"], capture_output=True, text=True).stdout
            reported = out.split()[2] if len(out.split()) > 2 else "?"
            pinned = lock.get("FFMPEG_VERSION", "")
            suffix = "" if reported.startswith(pinned) else f" — does not match the pinned {pinned}"
            lines.append(f"- ffmpeg **{reported}**{suffix}")
        except (OSError, IndexError):
            pass
    lines.append(f"- {lock['FONT_FAMILY']} **{lock['FONT_VERSION']}** (sha256 `{lock['FONT_SHA256'][:12]}…`)")
    lines.append(f"- envelope **{lock['GIF_STATE_FRAMES']} frames/screen @ {lock['GIF_FPS']} fps**, "
                 f"{lock['GIF_WIDTH']}×{lock['GIF_HEIGHT']}, "
                 f"{lock['GIF_TAIL_FRAMES']}-frame settled tail, "
                 f"{lock['GIF_FRAMES']}-frame ceiling")
    return "\n".join(lines)


def report(gif_dir: Path) -> tuple[int, str]:
    lock = read_lock()
    rows, errors, notes = [], [], []
    for name in TAPES:
        path = gif_dir / f"{name}-compressed.gif"
        if not path.is_file():
            errors.append(f"missing demo GIF: {path}")
            continue
        try:
            m = measure(path)
        except (OSError, ValueError, IndexError) as ex:
            errors.append(f"unreadable demo asset: {path}: {ex}")
            continue
        # The budget is a ceiling, not a target: a scene legitimately produces
        # fewer frames than GIF_FRAMES. `normalize` is what guarantees the
        # envelope, so the check is that nothing exceeded the ceiling.
        if m["frames"] > int(lock["GIF_FRAMES"]):
            errors.append(f"`{path}` has {m['frames']} frames, over the "
                          f"{lock['GIF_FRAMES']}-frame budget pinned by demo/toolchain.lock")
        rows.append(f"| `{name}` | {m['bytes']:,} | {m['frames']} | {m['durationMs']} ms "
                    f"| {m['width']}×{m['height']} | {m['uniqueFrames']} | `{m['framesSha256'][:12]}…` |")

    text = "\n".join([
        "## Demo recording determinism",
        "",
        _toolchain_provenance(lock),
        "",
        "| gif | bytes | frames | duration | raster | unique frames | content hash |",
        "|---|---|---|---|---|---|---|",
        *rows,
        "",
    ])
    if notes:
        text += "\n".join(f"- {n}" for n in notes) + "\n"
    if errors:
        text += f"**{len(errors)} finding(s)**\n\n" + "\n".join(f"- {e}" for e in errors) + "\n"
    else:
        text += "**Every GIF matches the pinned envelope.**\n"
    for e in errors:
        print(f"::error::{e}", flush=True)
    for n in notes:
        print(f"::notice::{n}", flush=True)
    return (1 if errors else 0), text


# ----------------------------------------------------------------------------- cli


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)

    p_norm = sub.add_parser("normalize", help="re-emit a capture at the pinned envelope")
    p_norm.add_argument("--input", required=True, type=Path)
    p_norm.add_argument("--output", required=True, type=Path)
    p_norm.add_argument("--dry-run", action="store_true")

    p_cmp = sub.add_parser("compare", help="record-twice check: exit 1 when the passes differ")
    p_cmp.add_argument("--a", required=True, type=Path)
    p_cmp.add_argument("--b", required=True, type=Path)
    p_cmp.add_argument("--name", default="")
    p_cmp.add_argument("--summary", action="store_true",
                       help="also append the verdict to GITHUB_STEP_SUMMARY")

    p_rep = sub.add_parser("report", help="envelope report for a directory of GIFs")
    p_rep.add_argument("--dir", required=True, type=Path)

    p_meas = sub.add_parser("measure", help="print the shape of a single GIF")
    p_meas.add_argument("--input", required=True, type=Path)

    args = parser.parse_args(argv)

    if args.command == "normalize":
        result = normalize(args.input, args.output, read_lock(), args.dry_run)
        print(json.dumps(result, indent=2))
        return 0

    if args.command == "compare":
        equal, errors, notes, a, b = compare(args.a, args.b)
        label = args.name or args.a.name
        for note in notes:
            print(f"::notice::{label}: {note}", flush=True)
        for error in errors:
            print(f"::error::{label}: {error}", flush=True)
        if args.summary:
            verdict = "reproducible" if equal else "NOT reproducible"
            text = "\n".join([
                f"### `{label}` — {verdict}",
                "",
                f"| pass | bytes | frames | duration | raster | distinct screens |",
                f"|---|---|---|---|---|---|",
                f"| A | {a['bytes']:,} | {a['frames']} | {a['durationMs']} ms | "
                f"{a['width']}×{a['height']} | {a['uniqueFrames']} |",
                f"| B | {b['bytes']:,} | {b['frames']} | {b['durationMs']} ms | "
                f"{b['width']}×{b['height']} | {b['uniqueFrames']} |",
                "",
                *(f"- {n}" for n in notes),
                *(f"- **{e}**" for e in errors),
                "",
            ])
            summary = os.environ.get("GITHUB_STEP_SUMMARY")
            if summary:
                with open(summary, "a", encoding="utf-8") as fh:
                    fh.write(text)
        print(json.dumps({"a": a, "b": b, "equal": equal, "errors": errors}, indent=2))
        return 0 if equal else 1

    if args.command == "report":
        code, text = report(args.dir)
        print(text)
        summary = os.environ.get("GITHUB_STEP_SUMMARY")
        if summary:
            with open(summary, "a", encoding="utf-8") as fh:
                fh.write(text)
        return code

    m = measure(args.input)
    m.pop("_digests", None)
    print(json.dumps(m, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
