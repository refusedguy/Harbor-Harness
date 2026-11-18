#!/usr/bin/env python3
"""Does every WCAG 2.1 AA token pair in the Avalonia themes MEASURE above its tier?

Stdlib only -- no pip, no network, no dotnet. Wired into the `contrast` job of
.github/workflows/docs.yml.

WHY THIS FILE EXISTS (issue #433)

  docs/ACCESSIBILITY.md §5.1 records contrast ratios for the Catppuccin Mocha
  (dark) and Latte (light) palettes, and the C# test in
  tests/Harbor.DesignSystem.Tests/AccessibilityTests.cs asserts them against
  HEX LITERALS PINNED IN THE TEST. That test catches a bad math change, but it
  cannot catch a palette edit: rewrite `MochaText` in Dark.axaml and the test
  still passes, because the test never reads the theme file.

  This gate asks the other question: read the `<Color x:Key="...">` tokens out
  of the real theme dictionaries, compute WCAG 2.1 contrast from those values,
  and fail when a declared pair falls below its documented tier. It is the
  measured half of the audit; it does not replace the C# math test.

  A pair is (theme file, foreground token, background token, threshold, tier).
  A foreground written as `#RRGGBB` is a literal accent-text colour, as in the
  audit table (white on LatteBlue). The 4.5:1 body and 3:1 large-text/UI
  thresholds are the WCAG 2.1 AA ones the audit uses.

Usage:
  python3 tools/check-contrast.py [--root DIR] [--min-pairs N]
  python3 tools/check-contrast.py --self-test

Exit codes:  0 = every measured pair clears its tier, 1 = findings,
             2 = bad invocation.
"""
from __future__ import annotations

import argparse
import io
import os
import re
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.dirname(HERE)

DARK = "apps/Harbor.App.Avalonia/Themes/Dark.axaml"
LIGHT = "apps/Harbor.App.Avalonia/Themes/Light.axaml"

# (theme, foreground, background, threshold, tier, label)
PAIRS = (
    (DARK, "MochaText", "MochaBase", 4.5, "body", "dark body text"),
    (DARK, "MochaSubtext0", "MochaBase", 4.5, "body", "dark secondary text"),
    (DARK, "MochaOverlay2", "MochaBase", 3.0, "large/UI", "dark muted text"),
    (DARK, "MochaCrust", "MochaBlue", 3.0, "large/UI", "dark text on accent"),
    (LIGHT, "LatteText", "LatteBase", 4.5, "body", "light body text"),
    (LIGHT, "#FFFFFF", "LatteBlue", 4.5, "body", "white on light accent"),
    (LIGHT, "LatteSubtext0", "LatteBase", 3.0, "large/UI", "light secondary text"),
    (LIGHT, "LatteCrust", "LatteBlue", 3.0, "large/UI", "light text on accent"),
)

COLOR_RE = re.compile(
    r'<Color\s+x:Key="([^"]+)"\s*>\s*(#[0-9A-Fa-f]{6})\s*</Color>')


def relative_luminance(hex_color):
    """WCAG 2.1 relative luminance of an sRGB #RRGGBB colour."""
    channels = [int(hex_color[index:index + 2], 16) / 255.0
                for index in (1, 3, 5)]
    linear = [channel / 12.92 if channel <= 0.03928
              else ((channel + 0.055) / 1.055) ** 2.4
              for channel in channels]
    return (0.2126 * linear[0] + 0.7152 * linear[1] + 0.0722 * linear[2])


def contrast_ratio(foreground, background):
    """The WCAG 2.1 contrast ratio, from 1.0 (same colour) to 21.0."""
    a = relative_luminance(foreground)
    b = relative_luminance(background)
    high, low = max(a, b), min(a, b)
    return (high + 0.05) / (low + 0.05)


def tokens_of(path):
    """The `<Color x:Key>` tokens of a theme, as name -> (hex, line)."""
    with open(path, encoding="utf-8") as handle:
        text = handle.read()
    tokens = {}
    for match in COLOR_RE.finditer(text):
        name, value = match.group(1), match.group(2).upper()
        tokens[name] = (value, text.count("\n", 0, match.start()) + 1)
    return tokens


def check(root, min_pairs, emit=None):
    """Measure every declared pair; return 0 clean, 1 on any finding."""
    emit = emit or (lambda line: print(line))
    problems = []
    cache = {}
    checked = 0
    lowest = None

    for theme, foreground, background, threshold, tier, label in PAIRS:
        path = os.path.join(root, theme)
        if theme not in cache:
            try:
                cache[theme] = tokens_of(path)
            except OSError as error:
                cache[theme] = {}
                problems.append(f"{theme} could not be read: {error}")
        tokens = cache[theme]

        def resolve(token):
            if token.startswith("#"):
                return (token.upper(), None)
            return tokens.get(token)

        fg = resolve(foreground)
        bg = resolve(background)
        for role, token, value in (("foreground", foreground, fg),
                                   ("background", background, bg)):
            if value is None:
                problems.append(
                    f"{theme}: {label} names {role} token {token!r}, which is "
                    f"not a <Color x:Key> in the theme dictionary")

        if fg is None or bg is None:
            continue

        ratio = contrast_ratio(fg[0], bg[0])
        checked += 1
        lowest = ratio if lowest is None else min(lowest, ratio)
        if ratio + 1e-9 < threshold:
            where = f"{theme}:{fg[1]}" if fg[1] is not None else theme
            problems.append(
                f"{where}: {label}: {foreground} on {background} = "
                f"{ratio:.2f}:1 < {threshold:.1f}:1 ({tier} tier)")

    if checked < min_pairs:
        problems.append(
            f"only {checked} of {len(PAIRS)} declared contrast pairs were "
            f"measured; the floor is --min-pairs {min_pairs}")

    for problem in problems:
        emit(f"::error::{problem}")
    if problems:
        emit(f"FAIL: {len(problems)} contrast problem(s)")
        return 1
    emit(f"OK: {checked} token pair(s) measured from the theme dictionaries, "
         f"lowest {lowest:.2f}:1, every pair clears its WCAG 2.1 AA tier")
    return 0


# --- fixture data, not code ---
DARK_FIXTURE = """<ResourceDictionary>
    <Color x:Key="MochaBase">#1E1E2E</Color>
    <Color x:Key="MochaBlue">#89B4FA</Color>
    <Color x:Key="MochaCrust">#11111B</Color>
    <Color x:Key="MochaOverlay2">#9399B2</Color>
    <Color x:Key="MochaSubtext0">#A6ADC8</Color>
    <Color x:Key="MochaText">#CDD6F4</Color>
</ResourceDictionary>
"""

LIGHT_FIXTURE = """<ResourceDictionary>
    <Color x:Key="LatteBase">#EFF1F5</Color>
    <Color x:Key="LatteBlue">#1E66F5</Color>
    <Color x:Key="LatteCrust">#DCE0E8</Color>
    <Color x:Key="LatteSubtext0">#6C6F85</Color>
    <Color x:Key="LatteText">#4C4F69</Color>
</ResourceDictionary>
"""
# --- end fixture data ---


def self_test():
    """Prove the gate fails on planted violations, and on a narrowed scan."""
    with tempfile.TemporaryDirectory() as tmp:
        dark_path = os.path.join(tmp, DARK)
        light_path = os.path.join(tmp, LIGHT)

        def write_fixtures(dark=DARK_FIXTURE, light=LIGHT_FIXTURE):
            for path, text in ((dark_path, dark), (light_path, light)):
                os.makedirs(os.path.dirname(path), exist_ok=True)
                with open(path, "w", encoding="utf-8") as handle:
                    handle.write(text)

        def run(dark=DARK_FIXTURE, light=LIGHT_FIXTURE, min_pairs=len(PAIRS)):
            write_fixtures(dark, light)
            output = io.StringIO()
            code = check(tmp, min_pairs,
                         emit=lambda line: output.write(line + "\n"))
            return code, output.getvalue()

        code, output = run()
        if code != 0:
            print("FAIL: the clean fixture is not clean\n" + output,
                  file=sys.stderr)
            return 1

        # 1. Planted dark violation: text becomes indistinguishable from base.
        code, output = run(dark=DARK_FIXTURE.replace(
            '<Color x:Key="MochaText">#CDD6F4</Color>',
            '<Color x:Key="MochaText">#1E1E2E</Color>'))
        if code != 1 or "MochaText" not in output or "1.00:1" not in output:
            print("FAIL: a planted dark contrast violation was not named\n"
                  + output, file=sys.stderr)
            return 1

        # 2. Planted light violation: the accent loses its white text.
        code, output = run(light=LIGHT_FIXTURE.replace(
            '<Color x:Key="LatteBlue">#1E66F5</Color>',
            '<Color x:Key="LatteBlue">#EFF1F5</Color>'))
        if code != 1 or "white on light accent" not in output:
            print("FAIL: a planted light contrast violation was not named\n"
                  + output, file=sys.stderr)
            return 1

        # 3. A token disappears from the dictionary: no silent skip.
        code, output = run(light=LIGHT_FIXTURE.replace(
            '    <Color x:Key="LatteCrust">#DCE0E8</Color>\n', ""))
        if code != 1 or "LatteCrust" not in output:
            print("FAIL: a missing token was not named\n" + output,
                  file=sys.stderr)
            return 1

        # 4. A narrowed scan cannot look clean below its floor.
        code, output = run(min_pairs=len(PAIRS) + 1)
        if code != 1 or "--min-pairs" not in output:
            print("FAIL: a scan below the floor was not caught\n" + output,
                  file=sys.stderr)
            return 1

    print("OK: the contrast gate fires on two planted low-contrast pairs, a "
          "missing token and a scan below its floor")
    return 0


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--root", default=REPO_ROOT)
    parser.add_argument("--min-pairs", type=int, default=len(PAIRS))
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args(argv)
    if args.self_test:
        return self_test()
    if args.min_pairs < 1 or args.min_pairs > len(PAIRS):
        parser.error("--min-pairs must be between 1 and %d" % len(PAIRS))
    return check(args.root, args.min_pairs)


if __name__ == "__main__":
    sys.exit(main())
