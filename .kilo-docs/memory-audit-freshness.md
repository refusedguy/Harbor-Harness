Готово. Отчёт: `/home/nbook/graft-freshness-research-2026-08-25.md`; код Graft склонирован в `/home/nbook/graft-research/`.

## Что найдено (все пруфы — из кода/issues, проверены по клону)

**1) Freshness в Graft (`trailhq/Graft`, 4856★, активен; бывш. `nanonets/graft`)**
- **Rebuild-per-query**: `src/graph/refresh.ts` — `ensureFreshGraph` перед каждым ответом пробует рабочее дерево (~3ms stat-probe, `src/graph/fingerprint.ts`: «Measured at ~3ms for 280 files»), при drift пересобирает структурный граф. Свойства: $0/offline (никогда LLM), never-fatal (fail → ответ из старого графа, но с note), анти-stampede (общий лок + re-probe после ожидания).
- **Check-сигнал двух уровней**: `graft check` (полный re-hash, diff по `body_hash`, для CI) и `indexFreshness`+`staleBanner` (stat-only на старте сессии). Баннер не просто флаг, а инструкция агенту: «don't chase it» (branch switch → индекс «confidently points at files that aren't there»).
- **TTL нет вообще** — свежесть = изменение байт, не возраст. Плюс version-stamp экстрактора: чужой штамп → fingerprint недоверенный; null-fingerprint = «unknown», никогда «clean».

**2) Цена ложной памяти (пруфы конфликтов)**
- До v0.8.1 mid-turn запросы отвечали из графа, не соответствующего файлу, который агент сам только что изменил; внешние правки не инвалидовали ничего — statusline показывал «✓ synced» (CHANGELOG 0.8.1, докстринг refresh.ts).
- Issues: #172 — неудачный meaning-pass оставлял вечный `pending`, CI на check не проходил никогда; #213 (open) — сам check выдаёт ложный постоянный drift; #146 — хуки строили граф не там; #116/#120 — недетерминированные рёбра у одинакового кода; #104/#161 — заведомо неверные edges «poison call resolution»; #122 (open) — rebuild OOM на больших монорепах.
- Внешний бенчмарк Meetless (10 моделей × 3 вендора): CLAUDE.md с протухшими фактами → **все модели пишут стухший ответ, прочитав 0 файлов** («not a retrieval failure — a verification failure»); промпт «проверяй заметки» чинит 6/10 непредсказуемо; единственное, что держало 6/6 — доставка in-force значения самим харнестом.

**3) Guardrails для харнеста (18 пунктов в отчёте), ключевые**
- Change-detection/hash-check вместо TTL; absence ≠ clean; stat решает «пересобирать ли», но никогда «что смотрит rebuild».
- Trust-levels: ready/stale/pending + разделение structural drift vs meaning-staleness vs coverage (#127: печатать долю, не счётчик).
- Аудитор независим: check-инструмент не имеет права рефрешить перед собой (иначе всегда «OK») — `src/mcp/tools.ts:199`.
- Money guard: авто-пути $0/offline; деградация всегда видимая (note), лок освобождается на SIGTERM.
- Главный урок: память формулировать pointer-style, а не assert-фактами — summary с готовым ответом подавляет проверку у 100% моделей; конфликты адъюдицировать до инъекции, не надеясь на модель.

Проблемы: web_search частично падал (403 Firecrawl), GitHub API потребовал редирект (репо переехал nanonets→trailhq), Meetless заблокирован для web_extract — вытащил curl'ом. На выводы не повлияло.