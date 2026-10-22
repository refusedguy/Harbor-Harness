# ADR-023: оркестратор сборки переезжает с NUKE на Fallout

> **Status (2026-10-22):** dated record, not normative. Это решение принято
> владельцем 2026-10-22. Документ не утверждает машинно-проверяемых фактов о
> дереве, поэтому не несёт баннер `Status: normative`. Части, которые должны
> остаться правдой, огорожены там, где живут: список таргетов — в
> `build/_build/Meta/TargetCatalog.cs` (метод `Verify`), проверка точки входа в
> CI — в `.github/workflows/ci.yml` (шаг `list --format json`), версия сборщика — в
> `build/_build.csproj`, запрет на NUKE — в гейте `tools/gates/check-no-nuke/`.

## Status

**Decided: оркестратор сборки переезжает с NUKE на Fallout.** Дата решения:
2026-10-22.

Слова владельца, на которых основано решение: *«NUKE 100 % просто забит автором в
пучины»*, *«уверен что нам придется переезжать на fallout»*.

Эти слова — основание решения, но не его доказательство. Доказательство — факты
ниже, и оно получено независимо от слов владельца.

## Context

Harbor собирается NUKE: оркестратор — проект `build/_build.csproj` (32 файла `.cs`,
3 718 строк, 15 таргетов), пакет `Nuke.Common 10.1.0`, bootstrap — `build/build.sh`
и `build/build.ps1`. В CI оркестратор зовётся из двух мест: `.github/workflows/ci.yml:1354`
(`bash build/build.sh Compile --configuration Release`) и `.github/workflows/ci.yml:1562`
(`bash build/build.sh list --format json > /dev/null`).

NUKE — проект, который выбирали осознанно; он стоит в дереве с коммита `a9ed7fff`
(2026-07-18). Вопрос «что делать с ним дальше» в репозитории не решён: ADR о сборщике
нет (проверено по всем 23 файлам `docs/adr/`), в `docs/ROADMAP.md` — одно
упоминание, в `AGENTS.md` и `CLAUDE.md` — ни одного.

### Факты о NUKE (проверено 2026-10-22)

| Факт | Значение | Источник |
|---|---|---|
| Флаг `archived` на GitHub | **`false`** | `GET https://api.github.com/repos/nuke-build/nuke` |
| Последний коммит | **2025-12-02T10:17:32+01:00** (Matthias Koch) | `git log` в полном клоне `nuke-build/nuke` |
| Последний релиз | **10.1.0**, 2025-12-02T09:58:36Z | `GET /repos/nuke-build/nuke/releases` |
| Коммитов за 12 месяцев (2025-10-22…2026-10-22) | **20** (18 Matthias Koch, 2 Marko Lahma), все до 2025-12-03 | `git log --since=2025-10-22` |
| Коммитов за 6 месяцев (2026-04-22…2026-10-22) | **0** | `git log --since=2026-04-22` |
| Ответов мейнтейнера после 2025-12-02 | **0** | комментарии к issue #1585…#1601: все от сообщества |
| Открытых issue / PR | **65 / 55** | `GET /search/issues?q=repo:nuke-build/nuke` |
| Сайт `nuke.build` | **не открывается** | [issue #1601](https://github.com/nuke-build/nuke/issues/1601), 2026-07-24 |
| Отчёт о CVE без ответа | [#1591](https://github.com/nuke-build/nuke/issues/1591): CVE-2026-33116 + Scriban 6.2.1 (1 critical, 5 high), 2026-04-16, 0 комментариев | GitHub API |
| Запрос поддержки `.slnx` без ответа | [#1586](https://github.com/nuke-build/nuke/issues/1586), 2026-01-09 | GitHub API |
| Лицензия | MIT | `LICENSE.md` |

Ответ на исходный вопрос владельца: **«забит автором в пучины» — подтверждается по
датам и цифрам.** Проект не помечен как архивный (флаг `archived` не выставлен), но
фактически не обслуживается: 324 дня без коммита, ноль коммитов за полгода, ноль
ответов мейнтейнера за то же время, 65 открытых issue без разбора, сайт проекта не
работает.

### Факты о Fallout (проверено 2026-10-22)

| Факт | Значение | Источник |
|---|---|---|
| Репозиторий | `github.com/Fallout-build/Fallout` | — |
| Тип форка | **жёсткий форк с сохранённой историей**; на GitHub `fork: false` | `GET /repos/Fallout-build/Fallout` |
| Точка форка | **NUKE `develop` = `27e8077b8ab01222c16c3610a899c25d1bce910c`, 2025-12-02** — последний коммит NUKE | `git merge-base --is-ancestor 27e8077b8 HEAD` → ancestor: **YES** |
| Лицензия | **MIT** (с атрибуцией NUKE) | `LICENSE` в репозитории; `<license type="expression">MIT</license>` в `.nuspec` |
| Своих коммитов сверх точки форка | **459**, авторов — **17** | `git rev-list --count 27e8077b8..HEAD` |
| Коммитов за 30 / 90 дней | **21 / 146** | `git log --since` |
| Последний пуш | **2026-10-22T04:54:39Z** | `GET /repos` |
| Поддержка `.NET 10` | **да** — `Fallout.Common` публикуется только как `lib/net10.0/` | `api.nuget.org/v3-flatcontainer` |
| Поддержка `.slnx` | **да** — `Fallout.Persistence.Solution` содержит `SlnxFile`, `SlnxSerializerSettings`, `Slnx.xsd`; «Internal parser for .sln/.slnx solution files used by Fallout.Solution» | содержимое `.nupkg` 10.4.0 и 11.0.18 |
| NativeAOT | типизированного `PublishAot` в API нет; наш код ставит его **MSBuild-свойством**, поэтому поведение не зависит от API. **Прямым прогоном не подтверждено** — проверяется шагом 0 | строки в `Fallout.Common.dll`; `build/_build/Components/PublishVariantBuilder.cs` |
| Путь миграции | **`fallout-migrate`** (глобальный dotnet-инструмент, на nuget.org), **`Fallout.Migrate.Analyzers`** (кодфикс `FALLOUT004`), переходный shim на GitHub Packages | `docs/Migration/from-nuke.md` |
| Оговорка shim'а | «sealed-class wrappers and static helper classes like `DotNetTasks` are not bridged yet — tracked in #69» → **shim-путь нам не подходит**, `DotNetTasks` мы зовём | там же |
| Скачиваний | `Fallout.Common` — **103 378** против `Nuke.Common` — **39 894 159** (разрыв **386×**) | `azuresearch-usnc.nuget.org` |

## Decision

**Оркестратор сборки переезжает с NUKE на Fallout.** Версия, которую мы пиним:
**`Fallout.Common 10.4.0`** — последняя листинговая на nuget.org (`11.x` по
ADR-0002 проекта fallout'а публикуется на nuget.org только по явному флагу и
невидима в поиске).

Переезд идёт **шагами, каждый из которых ничего не ломает**:

| Шаг | Что | Issue | Зависит от |
|---|---|---|---|
| 0 | Пилот: один таргет (`Pack`) на fallout, тот же артефакт, параллельно NUKE-пути | [#2179](https://github.com/refusedguy/Harbor-Harness/issues/2179) | — |
| 1 | Весь `build/_build/` на API fallout | [#2180](https://github.com/refusedguy/Harbor-Harness/issues/2180) | #2179 |
| 2 | Точки вызова в CI и bootstrap-скрипты | [#2181](https://github.com/refusedguy/Harbor-Harness/issues/2181) | #2180 |
| 3 | Документация и комментарии в конфигурации | [#2182](https://github.com/refusedguy/Harbor-Harness/issues/2182) | #2180 (параллельно с #2181) |
| 4 | Удаление NUKE-пути | [#2183](https://github.com/refusedguy/Harbor-Harness/issues/2183) | #2180, #2181, #2182 |
| 5 | Гейт, который не даёт вернуться к NUKE | [#2184](https://github.com/refusedguy/Harbor-Harness/issues/2184) | #2183 |

Эпик: [#2178](https://github.com/refusedguy/Harbor-Harness/issues/2178). Решение
владельца и схема зависимостей зафиксированы комментарием к эпику.

Подробный план, смета в числах, риск-реестр и граница «что остаётся в YAML» —
в [BUILDSYSTEM_STUDY.md](../BUILDSYSTEM_STUDY.md).

## Почему не остаёмся на NUKE

1. **Факт заброшенности** (таблица выше): 324 дня без коммита, ноль ответов
   мейнтейнера, 65 открытых issue, неработающий сайт проекта, неотвеченный отчёт
   о CVE с 1 critical. Мы остались бы на версии 10.1.0 навсегда.
2. **Мы уже обслуживаем чужой долг вручную.** `build/_build.csproj` держит четыре
   пина, которых нет в `Directory.Packages.props`, и комментарий в самом файле
   объясняет почему: `System.Security.Cryptography.Xml 10.0.13` закрывает
   11 advisory-диапазонов, `Microsoft.Build* 18.0.2` — нижняя граница, которую
   просит `Nuke.ProjectModel`, `NuGet.Packaging 6.12.5` — выше advisory
   GHSA-g4vj-cjjj-v7hg. Эти пины придётся поддерживать, пока оркестратор заморожен.
3. **Запрос `.slnx` в NUKE висит без ответа** с 2026-01-09 (issue #1586) — при том
   что мы уже на `.slnx` (`Harbor.slnx`).
4. **Альтернатива «остаться и чинить самим» не дешевле.** Мы не хотим быть
   мейнтейнером чужого форка; мы хотим быть пользователем живого.

## Почему не «ничего не менять»

Вариант «NUKE работает, репозиторий на нём собирается, миграция ради миграции —
риск без выигрыша» был рассмотрен и отклонён **не по вкусу, а по факту
заброшенности**: держаться не на чем. Выигрыш скромный по объёму (3,5-7,5 дней
работы по оценке объёма правки), но он не в сумме правок, а в том, что проект
перестаёт зависеть от репозитория, который не обновлялся 324 дня и не отвечает.

## Что должно остаться истинным и где это зашито

| Что должно остаться правдой | Где это огорожено | Чем проверяется |
|---|---|---|
| Список таргетов не изменился: 15 штук, те же имена | `build/_build/Meta/TargetCatalog.cs`, метод `Verify` (строка 332) — сверяет каталог из 15 записей с рефлексией | шаг CI `bash build/build.sh list --format json > /dev/null` (`.github/workflows/ci.yml:1562`) |
| `Compile` даёт тот же артефакт | шаг 0 (сравнение) и джоб `build` (`.github/workflows/ci.yml:799`) | CI |
| `Pack` даёт тот же пакет | шаг 0 (сравнение состава и CRC) и джоб `pack` (`.github/workflows/ci.yml:2125`) | CI |
| NativeAOT-публикация работает | джоб `aot-publish` (`.github/workflows/ci.yml:2972`) | CI |
| Версия сборщика задана в одном месте | `build/_build.csproj` — CPM для `build/` выключен, поэтому пин там единственный | restore |
| В дереве нет ссылок на NUKE | гейт `tools/gates/check-no-nuke/` (шаг 5) — по образцу `tools/check-no-test-sdk`, который не даёт вернуться к VSTest | CI |

## Последствия

- **Положительные.** Сборщик получает активное сопровождение (459 своих коммитов,
  21 за последние 30 дней, 17 авторов). Закрываются две дыры, которые висели в
  NUKE: поддержка `.slnx` и отчёт о CVE. Отпадает ручное обслуживание транзитивных
  пинов в пользу пина на форк.
- **Отрицательные.** Мы переходим на проект с 103 тыс. скачиваний против 39,9 млн у
  NUKE: это ранняя стадия, и API может ещё двигаться (переезд `Fallout.Common.ProjectModel`
  → `Fallout.Solutions` уже случился в 11.x). Отсюда — **пин версии** и **шаг 0 как
  пилот**. Транзитивные зависимости fallout'а шире (`Octokit 14.0.0`,
  `Azure.Identity 1.17.1`, `YamlDotNet 16.3.0`), restore станет медленнее.
- **Нейтральные.** Формат workflow-файлов не меняется: Fallout умеет генерировать
  `.github/workflows/*.yml` из атрибута `[GitHubActions]`, и это меняет горизонт
  (см. §«Что остаётся в YAML» в отчёте), но **в объём переезда не входит**:
  генератор не пишет `needs:` (топологию джобов), а у нас 63 джоба в 17 файлах.

## Правила зависимостей по каталогам

Сведено из дерева, файл за файлом. Это то, что определяет, чем сборщику разрешено
пользоваться при переезде.

| Каталог | Какие правила на него распространяются | Можно ли брать зависимости | Чем проверяется |
|---|---|---|---|
| `src/`, `apps/`, `tests/` | корневой `Directory.Build.props` (анализаторы, AOT/трим, banned-API, CPM **включён**); `src/`, `apps/`, `tests/` **импортируют родителя** | **да**, версия обязана лежать в `Directory.Packages.props`; `Version=` в `.csproj` — NU1008 | `tools/gates/check-abstractions-contract` (периметр: `Harbor.Abstractions.Contracts`, `Harbor.Extensions`, `Harbor.Application`, `Harbor.PolicyConfig`), матрица слоёв в `tests/Harbor.Architecture.Tests/`, `check-analyzer-scope`, `check-no-test-sdk`, `check-test-packages` |
| `tools/` (C#) | тот же корневой props — своих `Directory.Build.props` у `tools/` нет | **да**, версия в `Directory.Packages.props` | restore; NU1008 |
| **`build/`** (сборщик) | **корневой props НЕ применяется**: `build/Directory.Build.props` не импортирует родителя; CPM **выключен** в двух местах (`build/Directory.Build.props:39-40` и `build/_build.csproj`); анализаторы off, `TreatWarningsAsErrors` off | **да, свободно** — версия прямо в `build/_build.csproj`; запись в `Directory.Packages.props` для сборщика **не нужна** (мёртвый текст — ровно то, что #1896 убрал для `Nuke.Common`) | restore; правило зафиксировано **документом** — `docs/CENTRAL_PACKAGE_MANAGEMENT.md` §4.4 — а не гейтом. Гейта на «версия сборщика в одном месте» нет |
| **`tools/gates/`** (Go) | отдельный Go-модуль `harbor.local/gates`, `go 1.27.3`, **ноль внешних `require`**. `docs/CI_GATES.md` правило 6: *«with the standard library only and no external module»*; там же: *«there is no `go.sum`: a gate that fetched something at run time could not run on a runner with no network»*; `tools/gates/README.md` правило 4 — то же | **нет** — внешний модуль запрещён **правилом репозитория**, а не просто неудобен | `go-quality` (gofmt, `go vet`, регистрация, полы `--min-files 75 --min-packages 28`); `gates-binary` → джоб `verify-committed` пересобирает бинарь и сверяет байты |
| `contrib/` | не собирается CI | — | — |

**Что из этого следует для переезда.** Правила AOT, запретов и слоёв к сборщику не
относятся: он вне матрицы слоёв и вне периметра `check-abstractions-contract`. CPM на
него не распространяется, поэтому новый пакет сборщика — это одна строка в
`build/_build.csproj`. **Оговорка:** пакет приходит со своим транзитивным деревом, а
ни один гейт сегодня на дерево зависимостей сборщика не смотрит (`check-test-packages`
покрывает тесты и сэмплы). Это новый долг, а не бесплатный выигрыш.

**И NU1605 при выключенном CPM — это про прямой `PackageReference`:** наш пин
`NuGet.Packaging 6.12.5` ниже транзитивного требования `Fallout.Tooling` (6.14.3) →
ошибка restore. Лечится поднятием пина до **6.14.3**; `CentralPackageTransitivePinningEnabled`
это не спасёт, потому что он выключен и для `build/`.

**Где живёт генерация CI-конфигурации.** Отдельное решение, и оно **не** в объёме
переезда. Если её делать — то **в `tools/gates/`, на Go, без внешних зависимостей**,
по образцу `gen-seam-graph` (генератор пишет `--write`, CI падает на устаревшей
коммитированной копии). Причины, по фактам:

- Гейт должен срабатывать там, где нет SDK и нет сборки дерева, — ради этого гейты и
  стали компилируемым бинарём в git. C#-генератор потребовал бы restore и сборку
  оркестратора, то есть **не сработал бы в lanes документации и в PR, ломающем
  сборку**, — ровно там, где расхождение и должно ловиться.
- Внешняя зависимость в Go-гейте **запрещена правилом** (`docs/CI_GATES.md` правило 6),
  а `vendor/` был бы нарушением этого правила для всех гейтов репозитория. Выход:
  YAML писать руками, как `gen-seam-graph` пишет markdown руками. Тогда `go.mod`
  остаётся пустым, `GOFLAGS=-mod=mod` в `tools/build-gates.sh` остаётся герметичным
  (сеть не нужна, потому что тянуть нечего), и воспроизводимость байтов не меняется.
- Fallout-генератор нашу модель джобов не выражает: он не пишет `needs:`,
  `strategy:`/`matrix:`, джобные `if:`, `permissions:`, `outputs:` и `env:` — и у него
  id и имя джоба выведены из **одной** строки (`GitHubActionsJob.Write` пишет
  `$"{Name}:"` и `$"name: {Name}"`), тогда как у нас 7 из 8 обязательных проверок
  имеют имя, отличное от job id, а пробел в job id GitHub не допускает.

## Alternatives considered

- **Остаться на NUKE** — отклонено: раздел «Почему не остаёмся».
- **Ничего не менять** — отклонено: раздел «Почему не „ничего не менять"».
- **Cake** — скриптовый DSL, наш оркестратор разложен по SRP на 32 класс-файла;
  переписывать его в скрипты — потеря структуры без выигрыша. Детально не изучалось.
- **FAKE, Bullseye, MSBuild-native оркестрация** — детально не изучались; в этом
  исследовании не сравнивались по численным критериям. Общий довод: Fallout — прямой
  форк того же NUKE с сохранённой историей, готовым миграционным инструментом и
  покрытой `.slnx`-поддержкой, то есть наименьший шаг из возможных. Это довод, а не
  измерение.
- **Сгенерировать workflow-файлы сборщиком** — возможность реальная и описана в
  отчёте, но это отдельная задача с отдельным гейтом-сверкой, а не часть переезда.

## References

- Полный отчёт: [BUILDSYSTEM_STUDY.md](../BUILDSYSTEM_STUDY.md).
- Точка форка и коммиты форка: `Fallout-build/Fallout` (ветка `develop`).
- Миграционный гайд fallout'а:
  [`docs/Migration/from-nuke.md`](https://github.com/Fallout-build/Fallout/blob/main/docs/Migration/from-nuke.md).
- Документация fallout'а: [docs.fallout.build](https://docs.fallout.build/),
  в частности [CI/CD → GitHub Actions](https://docs.fallout.build/docs/cicd/github-actions).
- ADR проекта fallout'а: [ADR-0002 (v11 off nuget.org by default)](https://github.com/Fallout-build/Fallout/blob/main/docs/adr/0002-v11-off-nuget-by-default.md),
  [ADR-0010 (no telemetry)](https://github.com/Fallout-build/Fallout/blob/main/docs/adr/0010-no-telemetry-collection.md).
