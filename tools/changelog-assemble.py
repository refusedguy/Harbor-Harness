#!/usr/bin/env python3
"""Склеить changelog-фрагменты в секцию [Unreleased] файла CHANGELOG.md.

Зачем. Каждый pull request писал свою запись в КОНЕЦ секции `[Unreleased]`
одного файла. Два pull request'а, дописывающие в одно место, всегда
конфликтуют, а конфликт в CHANGELOG.md означает: ветка расходится с dev →
обязательный ре-merge → ветка меняется → CI перезапускается → десяток минут
на каждый pull request. Очередь вставала на этом круге.

Фрагмент решает это по построению: у каждого pull request'а свой файл, два
никогда не трогают один и тот же, конфликт невозможен. Запись при этом
остаётся той же прозой с измеренными фактами.

Как работает:
  changelog-assemble.py            собрать и записать в CHANGELOG.md
  changelog-assemble.py --check    проверить, что собранный вид совпадает с
                                   закоммиченным (без записи)
  changelog-assemble.py --self-test  доказать, что гейт ловит расхождение

Фрагмент — это markdown-файл с одной или несколькими секциями `### ...`.
Порядок: по номеру pull request'а, затем по имени файла.
"""
import argparse
import re
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
FRAGMENT_DIR = REPO / "changelog" / "fragments"
CHANGELOG = REPO / "CHANGELOG.md"
HEADER = "## [Unreleased]"


def fragments() -> list[tuple[str, str]]:
    """Фрагменты в порядке: номер pull request'а, потом имя файла."""
    if not FRAGMENT_DIR.is_dir():
        return []
    out = []
    for path in sorted(FRAGMENT_DIR.glob("*.md")):
        if path.name.upper().startswith("README"):
            continue
        m = re.match(r"^(\d+)", path.stem)
        out.append(((m.group(1) if m else "999999", path.name), path.read_text(encoding="utf-8")))
    out.sort(key=lambda kv: kv[0])
    return out


def assembled() -> str:
    """Текст, который должен стоять в [Unreleased] после заголовка."""
    parts = []
    for _, text in fragments():
        body = text.strip("\n")
        if not body:
            continue
        parts.append(body)
    return "\n\n".join(parts)


def current_section(text: str) -> str:
    lines = text.split("\n")
    start = None
    for i, line in enumerate(lines):
        if line.strip() == HEADER:
            start = i
            break
    if start is None:
        raise SystemExit("CHANGELOG.md: секция [Unreleased] не найдена")
    end = len(lines)
    for i in range(start + 1, len(lines)):
        if lines[i].startswith("## ["):
            end = i
            break
    return "\n".join(lines[start + 1:end]).strip("\n")


def merged_changelog() -> str:
    text = CHANGELOG.read_text(encoding="utf-8")
    section = current_section(text)
    new_section = assembled()
    if not new_section:
        return text
    # фрагменты идут после того, что уже есть в секции
    parts = [p for p in (section, new_section) if p.strip()]
    body = "\n\n".join(parts)
    lines = text.split("\n")
    start = None
    for i, line in enumerate(lines):
        if line.strip() == HEADER:
            start = i
            break
    end = len(lines)
    for i in range(start + 1, len(lines)):
        if lines[i].startswith("## ["):
            end = i
            break
    head = lines[:start + 1]
    tail = lines[end:]
    return "\n".join(head + ["", body, ""] + tail).rstrip("\n") + "\n"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true")
    ap.add_argument("--self-test", action="store_true")
    args = ap.parse_args()

    if args.self_test:
        return self_test()

    want = merged_changelog()
    have = CHANGELOG.read_text(encoding="utf-8")
    if args.check:
        if want != have:
            print("FAIL: CHANGELOG.md не совпадает с собранными фрагментами.")
            print("      Запусти: python3 tools/changelog-assemble.py")
            return 1
        print("OK: CHANGELOG.md совпадает с собранными фрагментами.")
        return 0

    if want == have:
        print("нечего собирать: CHANGELOG.md уже совпадает с фрагментами.")
        return 0
    CHANGELOG.write_text(want, encoding="utf-8")
    print(f"собрано: {len(fragments())} фрагмент(ов) в CHANGELOG.md")
    return 0


def self_test() -> int:
    """Гейт обязан ловить расхождение и склеивать фрагмент."""
    original = CHANGELOG.read_text(encoding="utf-8")
    probe = FRAGMENT_DIR / "999999-self-test.md"
    try:
        probe.write_text(
            "### #999999 — the self-test fragment\n\n"
            "**Why.** The assembler must notice a diverged CHANGELOG.md.\n",
            encoding="utf-8")
        want = merged_changelog()
        if "the self-test fragment" not in want:
            print("FAIL: сборщик не вставил фрагмент в [Unreleased].")
            return 1
        if want == original:
            print("FAIL: сборщик ничего не изменил, хотя фрагмент был.")
            return 1
        # расхождение: правим файл так, чтобы он разошёлся с фрагментами
        tampered = original.replace("## [Unreleased]", "## [Unreleased]\n\n<!-- сдвиг -->", 1)
        CHANGELOG.write_text(tampered, encoding="utf-8")
        if merged_changelog() == CHANGELOG.read_text(encoding="utf-8"):
            print("FAIL: гейт не заметил расхождение между CHANGELOG.md и фрагментами.")
            return 1
        print("OK: сборщик склеивает фрагмент и ловит расхождение в CHANGELOG.md.")
        return 0
    finally:
        CHANGELOG.write_text(original, encoding="utf-8")
        if probe.exists():
            probe.unlink()


if __name__ == "__main__":
    sys.exit(main())
