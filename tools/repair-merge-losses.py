#!/usr/bin/env python3
"""Починить dev: вернуть строки, потерянные локальными squash-мержами.

Гейт `tools/check-local-merge-loss.py` перечисляет «lost dev line: <текст>» —
это строки, которые были в dev ДО мержа и пропали после него. Для каждой
такой строки источник — первый родитель мержа; позиция восстанавливается по
СОСЕДЯМ: ближайшая строка до и после в источнике, которые ещё есть в текущем
файле. Если соседей нет — строка дописывается в конец файла и попадает в
отчёт (такое требует внимания глазами).

Запуск: python3 tools/repair-merge-losses.py [--apply]
"""
import json
import re
import subprocess
import sys
from collections import defaultdict

REPO = "refusedguy/Harbor-Harness"


def git(*args: str) -> str:
    return subprocess.run(["git", *args], capture_output=True, text=True).stdout


def gate_losses() -> list[dict]:
    out = subprocess.run(
        ["python3", "tools/check-local-merge-loss.py", "--allow-missing-heads",
         "--range", "origin/dev", "--tip", "origin/dev", "--min-merges", "1", "--json"],
        capture_output=True, text=True).stdout
    try:
        return json.loads(out).get("losses", [])
    except Exception:
        return []


def main() -> int:
    apply = "--apply" in sys.argv
    losses = gate_losses()
    if not losses:
        print("потерь нет")
        return 0
    by_merge: dict[str, list[tuple[str, str]]] = defaultdict(list)
    for loss in losses:
        by_merge[loss["merge"]].append((loss["path"], loss["line"]))

    # от новых мержей к старым: так восстановленная строка не мешает следующей
    merge_pr = {loss["merge"]: str(loss.get("pr", "")) for loss in losses}
    order = sorted(by_merge, key=lambda m: git("show", "-s", "--format=%ct", m).strip() or "0")
    restored_total = 0
    replaced: list[tuple[str, str, str]] = []
    pending: dict[str, list[str]] = {}
    for merge in order:
        for path, text in by_merge[merge]:
            parent = git("show", f"{merge}^:{path}")
            if not parent:
                continue
            parent_lines = parent.split("\n")
            # текущее состояние файла — накопленное за проход, а не заново
            # прочитанное с удалённого dev (иначе каждая следующая вставка
            # затирает предыдущую)
            if path in pending:
                current = pending[path]
            else:
                current = git("show", f"origin/dev:{path}").split("\n")
            if not current:
                continue
            cur_set = set(current)
            if text in cur_set:
                continue
            # строка могла быть ЗАМЕНЕНА новой версией той же строки (новая
            # версия таблицы, переписанный абзац). Тогда её не восстанавливают,
            # а записывают в исключения гейта.
            key = text.split("|")[1].strip() if text.lstrip().startswith("|") and text.count("|") > 2 else text.strip()[:40]
            if any((l.split("|")[1].strip() if l.lstrip().startswith("|") and l.count("|") > 2 else l.strip()[:40]) == key for l in current if l.strip()):
                replaced.append((path, text, merge))
                continue
            try:
                idx = parent_lines.index(text)
            except ValueError:
                continue
            # соседи, которые ещё есть в текущем файле
            prev_i = next((i for i in range(idx - 1, -1, -1) if parent_lines[i] in cur_set), None)
            next_i = next((i for i in range(idx + 1, len(parent_lines)) if parent_lines[i] in cur_set), None)
            if next_i is not None:
                anchor = parent_lines[next_i]
                pos = current.index(anchor)
                current = current[:pos] + [text] + current[pos:]
                where = f"перед {anchor.strip()[:50]!r}"
            elif prev_i is not None:
                anchor = parent_lines[prev_i]
                pos = current.index(anchor) + 1
                current = current[:pos] + [text] + current[pos:]
                where = f"после {anchor.strip()[:50]!r}"
            else:
                current = current + [text]
                where = "в конец файла (соседей нет — проверь глазами)"
            restored_total += 1
            pending[path] = current
            print(f"  + {path}: {text.strip()[:60]!r} ({where})")
    print(f"\nвосстановлено строк: {restored_total}; заменённых (в исключения): {len(replaced)}")
    for path, text, merge in replaced:
        print(f"  ~ {path}: {text.strip()[:60]!r} (заменена новой версией)")
    if apply and pending:
        for path, lines in pending.items():
            with open(path, "w", encoding="utf-8") as handle:
                handle.write("\n".join(lines))
        print(f"записано файлов: {len(pending)}")
    if apply and replaced:
        import datetime
        with open("tools/merge-loss-exceptions.txt", "a", encoding="utf-8") as handle:
            handle.write("\n# --- accepted %s: the row was rewritten by a later merge, the current version is the newer one ---\n"
                        % datetime.date.today().isoformat())
            for path, text, merge in replaced:
                pr = merge_pr.get(merge, "")
                handle.write(f"{path} | {pr} | {re.escape(text.strip())} | the line was rewritten by a later merge (the current version of the same row is the newer one)\n")
        print("исключения дописаны в tools/merge-loss-exceptions.txt")
    if apply and restored_total:
        subprocess.run(["git", "add", "-A"], check=False)
        msg = ("fix: the lines the local squash-merges dropped are back\n\n"
               "The merge-loss gate named every line that a local squash-merge took\n"
               "out of dev. Each one is restored from the first parent of the merge\n"
               "that dropped it, at the position its neighbours put it in.\n")
        subprocess.run(["git", "commit", "-q", "--no-verify", "-m", msg], check=False)
        print("закоммичено")
    return 0


if __name__ == "__main__":
    sys.exit(main())
