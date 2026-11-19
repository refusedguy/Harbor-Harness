# ADR-072: file-level rewind с pre-image (#2659) — проект решения

> **Status (2026-11-19):** dated record, not normative. Issue
> [#2659](https://github.com/refusedguy/Harbor-Harness/issues/2659) прочитан
> полностью. Проверка `origin/dev` (`27c312f7`), комментариев issue,
> `gh pr list --search 2659` и `git log --all --grep=2659` показала, что issue
> OPEN, комментариев нет, влитого PR и предыдущего ADR-вердикта нет. Ни один из
> пунктов U01-U06 не влит: file blob store, pre-image, `touchedPaths`, tombstone
> и guard «оригинал изменился» в дереве отсутствуют. Код, тесты, гейты, пороги,
> golden и AOT-профиль этим PR не меняются. Номер 072 выбран, потому что 071 закрепил
> за собой PR #2998 (issue #2662), пока этот PR был открыт.

## Status

**Decided:** зафиксировать проект решения и границы future slice. Реализация в
этом PR не выполняется: фича затрагивает contracts, storage, CLI и DI и не может
быть проверена без локальной сборки. Issue остаётся OPEN как источник сравнения;
комментарий содержит только хеш коммита этого ADR. Пересмотр — по новому факту:
владелец называет потребителя, интерфейс и измеримую цель.

## Context

Issue #2659 выведен из `product-feature-gaps.md`, раздел U01-U06. Сейчас откат
файлов опирается на git и не работает в грязном/worktree-less состоянии. В дереве
есть `ISessionCheckpointStore`, `JsonlCheckpointStore` и `SqliteCheckpointStore`,
но они хранят чекпойнты сообщений, а не содержимое файлов. `SessionRewindEntry`
считает сообщения, но не трогает файловую систему. [ADR-022](ADR-022-session-log-append-only.md)
объявляет журнал сессии append-only, поэтому любое решение должно дописывать
запись, а не перезаписывать JSONL.

Шесть пунктов источника задают требуемую форму:

| Пункт | Требование |
|---|---|
| U01 | content-addressed blob store, `FilePreImage { path, blob, version, null=tombstone }` |
| U02 | захват pre-image перед первым write-инструментом в turn, дедуп по файлу |
| U03 | rewind только по `touchedPaths`, не полный snapshot воркспейса |
| U04 | tombstone для файлов, созданных агентом после чекпойнта |
| U05 | трёхуровневый guard «оригинал изменился» перед restore, явный `Conflict` |
| U06 | path containment и symlink guard при restore через существующий `PathGuard` |

## Decision

Проект решения для будущего slice, в порядке зависимостей:

1. **Хранилище.** `$HARBOR_HOME/rewind/<session>/objects/<sha256>` — blob по
   SHA-256 содержимого, запись идемпотентна (уже существующий blob не
   перезаписывается). Метаданные — append-only запись `FilePreImage` в журнале
   сессии: путь, хеш blob, версия и `existedBefore`. Отсутствие blob (`null`)
   означает tombstone: файла до первого касания не было.
2. **Точка захвата.** `IFileHistoryTracker.TrackBeforeWrite(path)` вызывается
   edit/write/notebook и path-записи bash до применения правки. Дедуп по
   `(session, turn, path)`: первое касание файла в turn фиксирует pre-image,
   последующие правки того же файла новую версию не создают. Контракт живёт в
   абстракциях, реализация — в storage, вызовы — в tools.
3. **Объём отката.** Чекпойнт хранит реестр `touchedPaths`. `rewind --files`
   восстанавливает только эти пути и не трогает остальной воркспейс. Это
   отличает его от `ChangeSetFreezer`, который работает с полным change set.
4. **Tombstone.** Для записи с `existedBefore == false` restore выполняет
   `File.Delete` с tolerant обработкой ENOENT: parallel-правка, уже удалившая
   файл, не считается ошибкой.
5. **Guard «оригинал изменился».** Перед restore три уровня проверки: stat
   (mode + size), затем mtime, затем content hash сохранённого blob. Расхождение
   на любом уровне даёт явный `Conflict`, а не тихий overwrite. Пользовательские
   правки после snapshot имеют приоритет над pre-image.
6. **Безопасность пути.** Restore переиспользует `ToolSafetyProfile.Path()` /
   `PathGuard`: абсолютный путь, `..` и symlink вне разрешённого корня
   отклоняются до любой записи на диск.
7. **Поверхность.** `harbor sessions rewind --files` — отдельный флаг существующей
   команды; режимы `--messages`/`--both` остаются за U08 и в этот slice не входят.

Порядок slice-strip: storage-контракт и blob store (U01, U03, U04) → точка захвата
в write-инструментах (U02) → guard и restore (U05, U06) → CLI-флаг и hermetic-тесты
на parallel-правки и изменившийся оригинал. Каждый шаг заканчивается
проверяемым состоянием и не требует изменения гейтов или порогов.

## Consequences

- Документ фиксирует форму будущего slice и dependency order; он не открывает
  работу заодно с этим PR и не меняет публичные API.
- Реализация без локальной сборки и тестов не принимается: контракты в
  `Harbor.Abstractions.Contracts` подпадают под public-API freeze, а новый
  storage-тип требует обновления seam graph. Проверка — только CI.
- Гейты, пороги, golden-файлы и AOT-профиль не меняются. Issue остаётся OPEN;
  комментарий содержит только хеш коммита ADR.
- Пересмотр по новому факту: владелец называет потребителя `IFileHistoryTracker`,
  измеримую цель для `Conflict` и согласовывает владельца жизненного цикла
  реестра `touchedPaths`.

## Alternatives considered

- **git-based rollback** (`git checkout` / `git restore`) — rejected: не работает
  в грязном дереве и без worktree, именно тот gap, который описывает issue.
- **Полный snapshot воркспейса** — rejected: дорого и затирает чужие правки;
  U03 требует восстановления только `touchedPaths`.
- **Тихий overwrite при restore** — rejected: U05 требует явного `Conflict`,
  пользовательские правки после snapshot должны выживать.
