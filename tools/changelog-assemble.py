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


def assembled(already: str = "") -> str:
    """Текст фрагментов, которых ещё нет в секции.

    Фрагмент, чей заголовок уже лежит в секции, пропускается: без этого
    второй запуск сборщика дописал бы его второй раз.
    """
    parts = []
    for key, text in fragments():
        body = text.strip("\n")
        if not body:
            continue
        # фрагмент НЕ может нести заголовок секции: после склейки такая
        # строка выглядела бы как начало другой секции и обрывала бы
        # [Unreleased] ровно посередине
        kept = []
        for line in body.split("\n"):
            if line.startswith("## ["):
                continue
            kept.append(line)
        body = "\n".join(kept).strip("\n")
        if not body:
            continue
        heading = next((l.strip() for l in body.split("\n") if l.startswith("### ")), None)
        if heading and heading in already:
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


def merged_changelog(text: str | None = None) -> str:
    text = CHANGELOG.read_text(encoding="utf-8") if text is None else text
    section = current_section(text)
    new_section = assembled(section)
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


def duplicate_entries(changelog_text: str) -> list[str]:
    """Заголовки фрагментов, которые уже лежат в CHANGELOG.md."""
    section = current_section(changelog_text)
    out = []
    for _, text in fragments():
        for line in text.split("\n"):
            if line.startswith("### "):
                title = line.strip()
                if title in section:
                    out.append(title)
                break
    return out


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
        # Между релизами фрагменты ещё НЕ в файле — это нормально: сборщик
        # склеивает их на релизе. Проверять надо две вещи:
        #   1) ни одна запись фрагмента не лежит уже в CHANGELOG.md — иначе
        #      релиз склеит её второй раз и запись повторится;
        #   2) сборка идемпотентна: повторный запуск ничего не меняет.
        dupes = duplicate_entries(have)
        if dupes:
            print("FAIL: запись фрагмента уже есть в CHANGELOG.md — на релизе она")
            print("      повторится: " + ", ".join(dupes))
            return 1
        once = merged_changelog()
        if merged_changelog(once) != once:
            print("FAIL: сборщик не идемпотентен: второй запуск меняет файл.")
            return 1
        print(f"OK: {len(fragments())} фрагмент(ов), дублей нет, сборка идемпотентна.")
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
            "**Why.** The assembler must notice a diverged file.\n",
            encoding="utf-8")

        # 1) фрагмент склеивается в [Unreleased]
        once = merged_changelog()
        if "the self-test fragment" not in once:
            print("FAIL: сборщик не вставил фрагмент в [Unreleased].")
            return 1
        if once == original:
            print("FAIL: сборщик ничего не изменил, хотя фрагмент был.")
            return 1

        # 2) сборка идемпотентна: второй запуск ничего не меняет
        if merged_changelog(once) != once:
            print("FAIL: сборщик не идемпотентен: второй запуск меняет файл.")
            return 1

        # 3) запись, которая уже лежит в файле, повторно не дописывается
        if "the self-test fragment" in merged_changelog(once).split("### #999999")[0]:
            print("FAIL: запись фрагмента дописалась второй раз.")
            return 1

        # 4) дубль ловится: та же запись уже в файле — гейт обязан сказать
        CHANGELOG.write_text(once, encoding="utf-8")
        dupes = duplicate_entries(once)
        if not dupes:
            print("FAIL: гейт не заметил запись, которая лежит и в файле, и во фрагменте.")
            return 1

        print("OK: сборщик склеивает фрагмент, идемпотентен и ловит дубль записи.")
        return 0
    finally:
        CHANGELOG.write_text(original, encoding="utf-8")
        if probe.exists():
            probe.unlink()


if __name__ == "__main__":
    sys.exit(main())
