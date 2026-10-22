# Сборщик: NUKE → Fallout. План переезда

> **Статус (2026-10-22):** план, а не отчёт о рассмотрении. Владелец решил
> 2026-10-22, что оркестратор сборки переезжает с NUKE на
> [Fallout](https://github.com/Fallout-build/Fallout). Этот документ — обоснование
> (факты) и пошаговый план переезда с проверяемым результатом на каждом шаге.
>
> Все числа сняты 2026-10-22 по дереву `dev` = `82c7a1d0` и по GitHub API в тот же
> день. Число, которое не измерено, помечено как проекция.

## Что уже было в репозитории

Проверено перед тем, как писать план: **решения о смене сборщика в репозитории
нет**.

| Где искали | Что нашли |
|---|---|
| `docs/adr/` (23 файла, ADR-001…ADR-022) | ADR о сборщике нет. Ближайшее — ADR-019 (рендерер SharpConsoleUI уходит из продукта), где NUKE упоминается только как контекст. |
| `docs/ROADMAP.md` | Единственное упоминание NUKE — строка 144, про флаг `HarborWithPlugins` по умолчанию. Про судьбу сборщика — ничего. |
| `docs/DEVELOPMENT.md`, `CLAUDE.md`, `AGENTS.md` | Сборщик не описан: в `AGENTS.md` и `CLAUDE.md` слов «NUKE»/«build/_build» нет вовсе. `docs/DEVELOPMENT.md` описывает тестовые команды, но не оркестратор. |
| `git log --all --grep=nuke` | История из 20+ коммитов — все про поведение NUKE-таргета (ключи MSBuild, кэш, анализаторы), ни один — про замену сборщика. |
| `docs/BUILD.md`, `docs/AOT_PUBLISH.md`, `docs/CENTRAL_PACKAGE_MANAGEMENT.md`, `docs/RELEASE_ENGINEERING_ANALYSIS.md`, `docs/BENCHMARKS.md`, `docs/REFERENCE-TRIMMER.md`, `docs/ALTERNATIVE_UIS.md` | 8 документов описывают сборщик как данность. Их надо править. |

Вывод: спорных нет, противоречий нет, отчёт ни с чем не спорит.

---

## 1. Факты по NUKE: проект заброшен автором

### 1.1 Что показывает GitHub API (2026-10-22)

| Показатель | Значение | Источник |
|---|---|---|
| `archived` | **`false`** | `GET /repos/nuke-build/nuke` |
| последний коммит | **2025-12-02T10:17:32+01:00**, Matthias Koch | `git log` в полном клоне |
| последний релиз | **10.1.0**, 2025-12-02T09:58:36Z | `GET /releases` |
| коммитов за 12 мес (2025-10-22…2026-10-22) | **20** (18 Matthias Koch, 2 Marko Lahma), все до 2025-12-03 | `git log --since` |
| коммитов за 6 мес (2026-04-22…2026-10-22) | **0** | `git log --since` |
| открытых issue | **65** | `GET /search/issues` |
| открытых PR | **55** | `GET /search/issues` |
| всего issue (открытых + закрытых) | 931 | `GET /search/issues` |
| звёзд / форков | 3 748 / 457 | `GET /repos` |
| лицензия | MIT | `LICENSE.md` |
| сайт `nuke.build` | **не открывается** | issue [#1601](https://github.com/nuke-build/nuke/issues/1601) (2026-07-24), webfetch — transport error |

Итог по срокам: последний коммит NUKE — **2025-12-02**, то есть **323 дня** до
2026-10-22. Шестимесячного окна активности нет вообще.

### 1.2 Мейнтейнер не отвечает — это видно по датам, а не по впечатлению

Проверено: ни на одном открытом issue после 2025-12-02 нет комментария от
`matkoch` (Matthias Koch — автор NUKE). Комментарии есть, но все от сообщества:

| issue | создан | последний комментарий | кто |
|---|---|---|---|
| [#1601](https://github.com/nuke-build/nuke/issues/1601) сайт nuke.build недоступен | 2026-07-24 | 2026-07-24 | `ITaluone`, `ymalich-np4` (оба отвечают: «Come and see: https://docs.fallout.build») |
| [#1591](https://github.com/nuke-build/nuke/issues/1591) уязвимые транзитивные зависимости: CVE-2026-33116 (S.S.C.Xml) и Scriban 6.2.1 (1 critical, 5 high) | 2026-04-16 | **комментариев нет вообще** | — |
| [#1586](https://github.com/nuke-build/nuke/issues/1586) поддержка формата `.slnx` | 2026-01-09 | 2026-07-27 | `IT-VBFK` отвечает: «https://github.com/Fallout-build/Fallout» |
| [#1599](https://github.com/nuke-build/nuke/issues/1599) шифрование небезопасно | 2026-05-27 | **нет** | — |
| [#1585](https://github.com/nuke-build/nuke/issues/1585) build.ps1 path failure | 2026-01-06 | 2026-09-11 | `ITaluone` |

Это не «медленно отвечает». Это — ни одного ответа мейнтейнера за **323 дня**, при
том что открытых issue 65 и открытых PR 55.

### 1.3 Что из этого следует для нас

Наш билд сидит на `Nuke.Common 10.1.0` (пин в `build/_build.csproj`). Пока NUKE
не обновлялся, мы держали транзитивные уязвимости закрыты **своими** пинами в
`build/_build.csproj`: `System.Security.Cryptography.Xml 10.0.13` (11 advisory-диапазонов
закрыто), `Microsoft.Build* 18.0.2`, `NuGet.Packaging 6.12.5`. Комментарий в
`build/_build.csproj` называет причину прямо: пины нужны, потому что NUKE-версия
заморожена. Это состояние — «обслуживаем чужой долг вручную», и оно тем дороже, чем
дольше стоит.

### 1.4 Точная формулировка факта

Формулировка владельца — «NUKE 100 % просто забит автором в пучины» — **подтверждается
по датам и цифрам** с одной оговоркой: **флаг `archived` на GitHub не выставлен**
(`archived: false`). Проект не помечен как архивный, но фактически не обслуживается:
0 коммитов за 6 месяцев, 323 дня без единого коммита, ни одного ответа мейнтейнера,
сайт проекта не работает, отчёт о CVE с 1 critical без ответа четыре месяца.

Дальше в этом документе и в
[ADR-023](adr/ADR-023-build-orchestrator-moves-to-fallout.md) факт записан именно
так: «де-факто заброшен автором», со ссылкой на источники, а не со ссылкой на слова
владельца. Слова владельца идут отдельной строкой как **основание решения**.

---

## 2. Факты по Fallout

### 2.1 Что это

| Показатель | Значение | Источник |
|---|---|---|
| репозиторий | `github.com/Fallout-build/Fallout` | — |
| `fork` на GitHub | **`false`** — это «жёсткий форк» с сохранённой историей, а не fork-запись GitHub | `GET /repos/Fallout-build/Fallout` |
| точка форка | **NUKE `develop` = `27e8077b8ab01222c16c3610a899c25d1bce910c`, 2025-12-02** — это последний коммит NUKE и релиз 10.1.0 | `git merge-base --is-ancestor 27e8077b8 HEAD` в клоне Fallout → **ancestor: YES** |
| лицензия | **MIT** — `LICENSE`: «Copyright 2026 Maintainers of Fallout / Based on NUKE, originally created by Matthias Koch and contributors. Copyright 2017-2025 Maintainers of NUKE» | `LICENSE` в репозитории + `<license type="expression">MIT</license>` в `.nuspec` |
| `archived` | `false` | `GET /repos` |
| создан | 2026-05-18 | `GET /repos` |
| последний пуш | **2026-10-22T04:54:39Z** (сегодня) | `GET /repos` |
| своих коммитов сверх точки форка | **459** | `git rev-list --count 27e8077b8..HEAD` |
| авторов этих коммитов | **17** | `git log --format=%an` |
| коммитов за 30 дней | **21** | `git log --since=2026-09-22` |
| коммитов за 90 дней | **146** | `git log --since=2026-07-24` |
| звёзд / форков | 173 / 22 | `GET /repos` |
| открытых issue+PR | 214 | `GET /repos` |
| последний релиз на GitHub | `v10.5.0-rc.2`, 2026-10-21 | `GET /releases` |
| последняя версия на nuget.org | `11.0.18` | `api.nuget.org/v3-flatcontainer` |
| скачиваний `Fallout.Common` | **103 378** | `azuresearch-usnc.nuget.org` |
| скачиваний `Nuke.Common` | **39 894 159** | там же |

Топ-контрибьюторы Fallout (сверх точки форка): Chrison Simtian 285, Dennis Doomen 59,
Lukas Gasselsberger 32+9, Anže Videnič 26, ITaluone 11, Marko Lahma 10, Yousif Salah 8,
Alexander Nachtmann 6.

Разрыв по скачиваниям — **386×** (103 378 против 39 894 159). Это честная цифра
зрелости: форк молодой и им почти никто не пользуется. NUKE набирал 39.9 млн
скачиваний за годы; Fallout — 103 тысячи за пять месяцев. Ниже в риск-реестре это
учтено отдельной строкой.

### 2.2 Совместимость с нашим случаем — по фактам, не по предположению

| Требование | Статус | Чем подтверждено |
|---|---|---|
| **.NET 10** | **подтверждено** | `Fallout.Common 11.0.18` содержит только `lib/net10.0/`; то же у `10.4.0`. `Fallout.ProjectModel 11.0.18` — `lib/net10.0` + `net8.0` + `net9.0`. |
| **`.slnx`** | **подтверждено** | В дереве Fallout: `src/Persistence/Fallout.Persistence.Solution/Serializer/Xml/SlnxSerializerSettings.cs`, `Slnx.xsd`, `XmlDecorators/SlnxFile.cs`. В собранном пакете `Fallout.Persistence.Solution` (и 10.4.0, и 11.0.18) строки `SlnxFile`, `SlnxSerializerSettings`, `SlnXmlSerializer` и описание «Internal parser for .sln/.slnx solution files used by Fallout.Solution. Inlined from Microsoft.VisualStudio.SolutionPersistence (MIT)». Сам Fallout собирается на `fallout.slnx`. |
| **NativeAOT** | **подтверждено косвенно, прямого теста нет** | `Fallout.Common 11.0.18` содержит `SelfContained`, `PublishSingleFile`, `PublishTrimmed`, но **не** содержит `PublishAot` (0 вхождений в DLL). Наш код ставит `PublishAot` **как MSBuild-свойство** (`PublishVariantBuilder.ApplyVariant` → `SetProperty("PublishAot", "true")`), а не типизированным сеттером, — то есть поведение идентично и не зависит от поверхности API. Но «fallout публикует AOT» отдельным тестом **не подтверждено**: это надо доказать шагом 0. |
| **упаковка под разные RID** | **подтверждено** | `DotNetPublishSettings` в `Fallout.Common` имеет `Runtime`, `SelfContained`, `Output`, `Properties` — те же, что нужны нашим 6 вариантам публикации. |
| **API-совместимость DSL** | **высокая** | Миграционный гайд: «The 1:1 namespace prefix swap is the only structural change. Type names (other than `NukeBuild` / `INukeBuild`) keep their identifiers — `[Parameter]`, `[Solution]`, `[GitHubActions]`, `Solution`, `GitRepository`, etc. all stay the same.» |

### 2.3 Документированный путь миграции — есть

1. **`fallout-migrate`**, глобальный dotnet-инструмент (`Fallout.Migrate`, на
   nuget.org, последняя версия 11.0.18). Переписывает `PackageReference`,
   `using`-директивы, MSBuild-свойства, bootstrap-скрипты, env-переменные,
   переименовывает `.nuke/` → `.fallout/`. Идемпотентен, есть `--dry-run`.
2. **`Fallout.Migrate.Analyzers`** — Roslyn-анализатор, каждое упоминание `Nuke.*`
   даёт предупреждение `FALLOUT004` с кодфиксом.
3. **Переходный shim** — пакеты `Nuke.*` на GitHub Packages, переэкспортируют
   легаси-типы как подклассы канонических `Fallout.*`. Оговорка из гайда: «The shim
   covers most public types but skips a few (sealed-class wrappers and static helper
   classes like `DotNetTasks` are not bridged yet — tracked in #69)». **Для нас это
   важно: `DotNetTasks` не переброшен**, а мы зовём `DotNetTasks.DotNetBuild`,
   `DotNetTasks.DotNetPublish`, `DotNetTasks.DotNetPack`. Значит shim-путь нам не
   подходит, нужен `fallout-migrate`.
4. **`fallout :cake-convert`** — конвертер из Cake (для сравнения, не для нас).

Ограничения shim, цитатой: *«The shim is a transition aid, not a permanent home. It's
removed in 12.0. Plan to migrate before then.»*

### 2.4 Два решения о версии, которые надо принять в шаге 1

| Вопрос | Факт | Последствие для нас |
|---|---|---|
| **10.x или 11.x** | `10.4.0` — последняя **листинговая** версия на nuget.org (`azuresearch` отдаёт `latest=10.4.0`, в плоском контейнере при этом лежит `11.0.18`). `11.x` по [ADR-0002](https://github.com/Fallout-build/Fallout/blob/main/docs/adr/0002-v11-off-nuget-by-default.md) публикуется на nuget.org **только по явному флагу**; v11 — нестабильная поверхность. | Брать **10.4.0**: она листинговая, `dotnet add package` её найдёт, `Fallout.Solution 10.4.0` уже тянет `Fallout.Persistence.Solution` с `.slnx`-читалкой. |
| **пространство имён** | В `10.4.0` — `Fallout.Common.ProjectModel`; в `11.0.18` — переименовано в `Fallout.Solutions` (в DLL 11.0.18 строки `Fallout.Common.ProjectModel` уже нет). | Наш `using Nuke.Common.ProjectModel;` → `using Fallout.Common.ProjectModel;` на 10.x. **Рекомендация: 10.4.0.** |

### 2.5 Одна конкретная поломка, которую мы уже нашли

**Наш пин `NuGet.Packaging 6.12.5` станет downgrade-ошибкой.** `Fallout.Tooling
11.0.18` для `net10.0` требует `NuGet.Packaging 6.14.3`. Прямой `PackageReference`
6.12.5 рядом с транзитивным требованием 6.14.3 — это **NU1605 «Detected package
downgrade»**, то есть ошибка restore, а не предупреждение. Пин надо поднять до
6.14.3 (или убрать и позволить транзитивному).

Второй пин: `System.Security.Cryptography.Xml 10.0.13` — **оставить**. Fallout
11.0.18 приносит 10.0.6 для `net10.0` (10.4.0 — 10.0.10), и наш комментарий в
`build/_build.csproj` прямо говорит, что 10.0.13 — минимальная версия вне 11
advisory-диапазонов. Наш пин выше транзитивного, downgrade-ошибки не будет, но и
защита сохранится.

Третий: `Microsoft.Build* 18.0.2` — совпадает с тем, что требует Fallout для
`net10.0`. Менять не нужно.

---

## 3. План переезда

Правило, из которого выведены все шаги: **первый шаг миграции не должен ломать
полосу CI**. Поэтому пилот идёт **параллельно** старому пути, а переключение —
отдельным шагом с явной откаткой.

### Шаг 0. Пилот: один таргет на fallout, тот же артефакт

**Что делаем.** Заводим **второй** build-проект рядом с NUKE-овым:
`build/_build.fallout/` (или `build/_build2/`), тот же `Build.cs`, но на API
fallout. Ничего в `build/_build/` не трогаем.

**Какой таргет.** Выбор обоснован измерением:

| Кандидат | Почему нет |
|---|---|
| `Compile` | Артефакт — 114 проектов, сравнение по хешам непрактично; время прогона длинное; цена ошибки максимальная. |
| `Test` | Нет детерминированного артефакта: результат зависит от порядка шардов и окружения. |
| `Publish` / `PublishAot` | Долго (AOT-сборка), артефакт зависит от RID и среды. |
| **`Pack`** | **Да.** Даёт ровно один сравнимый артефакт: `.nupkg`. Побайтовое сравнение возможно (nupkg — zip, но состав и CRC фиксированы). |

**Выбран `Pack`**, потому что:
- наш `PackTarget` уже умеет dry-run и печатает полный argv (`DotNetArgv.RenderPublish`
  и его аналог) — argv сравнивается как строка, это дешёвый и точный признак;
- артефакт — один `.nupkg` с фиксированным именем; сравниваем список файлов внутри,
  их CRC и SHA-256 пакета;
- таргет не тянет RID, AOT и окружение: чистый `dotnet pack` + `dotnet nuget push`
  в dry-run;
- наш CI уже качает пакеты в джобе `pack` (`.github/workflows/ci.yml`, джоб `pack`,
  строки 1818+), то есть артефакт уже проходит через полосу — пилот можно
  положить в тот же джоб.

**Чем доказываем.** Пять проверок, все обязательны:

1. **argv совпадает.** `pack --dry-run --format json` на NUKE и на fallout даёт
   **одинаковую** строку argv (после перевода `Nuke.` → `Fallout.` в именах пакетов
   — сам argv не меняется).
2. **exit code совпадает.** 0 в обоих.
3. **состав пакета совпадает.** Список файлов внутри `.nupkg` — то же множество, те
   же CRC.
4. **SHA-256 `.nupkg` совпадает** — либо расходится только полем, которое NuGet
   пишет временем (тогда сравниваем состав + CRC, и это записывается в отчёт).
5. **`list --format json` совпадает.** Каталог таргетов (`TargetCatalog`) на обеих
   сборках даёт одинаковый список — это уже делает наш `TargetCatalog.Verify`,
   который падает на расхождении.

**Что может сломаться.**
- `fallout-migrate` не перепишет наш код полностью: у нас 32 файла, а не шаблон
  NUKE. Особенно `build/_build/Meta/DotNetArgv.cs` (66 строк) — он рендерит argv
  **вручную**, и это наш собственный код, а не API NUKE.
- NuGet-версия пакета может отличаться в транзитивных зависимостях → другой `.nuspec`
  внутри пакета → состав разойдётся. Это ожидаемо и не является регрессом; критерий
  приёмки — «тот же состав **исходников**», а не побайтовое равенство всего пакета.
- Наш `PackTarget` печатает и пушит в GitHub Release — пилот обязан быть в dry-run.

**Как откатить.** Пилот — это **добавленный** проект. Откат = удалить
`build/_build.fallout/` и один workflow-файл. NUKE-путь не тронут, полоса CI не
знает о пилоте, если пилот не включён в джоб `pack` — а он включается **новым
шагом**, а не заменой.

**Критерий приёмки.** Один PR, который добавляет (а) второй build-проект,
(б) workflow-шаг в джобе `pack`, который печатает оба argv и оба состава пакета,
(в) текстовое резюме сравнения. Полоса CI зелёная, NUKE-путь не изменился ни на
одной строке.

### Шаг 1. Весь `build/_build/` переводится на API fallout

**Что делаем.** Копия с пилота становится основной; NUKE-овый `build/_build/`
остаётся на месте до шага 4.

**Изменения по файлам.** Полный инвентарь `build/_build/` — **32 файла `.cs`, 3 718
строк** плюс `build/_build.csproj` и `build/Directory.Build.props`. Что меняется в
каждом:

| Файл | Строк | Что меняется |
|---|---:|---|
| `build/_build/Build.cs` | 382 | `using Nuke.Common;` → `using Fallout.Common;`; `NukeBuild` → `FalloutBuild`; `using Nuke.Common.ProjectModel;` → `using Fallout.Common.ProjectModel;`; `[Solution("Harbor.slnx")] private readonly Solution Solution;` — **тип и имя поля те же**, меняется только пространство имён. 13 таргетов остаются 13. |
| `build/_build/Components/ArchiveBuilder.cs` | 102 | `using Nuke.Common.IO;` → `using Fallout.Common.IO;`. `AbsolutePath` — тот же тип, другое пространство. |
| `build/_build/Components/ArtifactPathResolver.cs` | 104 | `using Nuke.Common.IO;` + `using Nuke.Common.Tools.DotNet;` → `Fallout.*`. |
| `build/_build/Components/CliBuildConfigurator.cs` | 86 | **не меняется** — своего кода NUKE не касается. |
| `build/_build/Components/GitHubReleaseUploader.cs` | 140 | `using Nuke.Common.IO;` → `Fallout.Common.IO;`. |
| `build/_build/Components/PublishVariantBuilder.cs` | 133 | `using Nuke.Common.IO;` + `using Nuke.Common.Tools.DotNet;` → `Fallout.*`. Типы `DotNetPublishSettings`/`DotNetTasks` — те же имена. **API-сеттеры совпадают**: `SetProject`, `EnableNoRestore`, `EnableNoBuild`, `DisableNoBuild`, `SetConfiguration`, `SetOutput`, `SetProperty`, `SetRuntime`, `SetSelfContained`. |
| `build/_build/Configuration/ArchiveFormat.cs` | 8 | не меняется |
| `build/_build/Configuration/BuildConfiguration.cs` | 7 | не меняется |
| `build/_build/Configuration/BuildSettings.cs` | 10 | не меняется |
| `build/_build/Configuration/FeatureFlags.cs` | 22 | не меняется |
| `build/_build/Configuration/PublishVariant.cs` | 11 | не меняется |
| `build/_build/Configuration/RendererSelection.cs` | 32 | не меняется |
| `build/_build/Extensions/DotNetPublishExtensions.cs` | 33 | `using Nuke.Common.Tools.DotNet;` → `Fallout.Common.Tools.DotNet;`. Расширения на `DotNetPublishSettings` переносятся как есть. |
| `build/_build/Extensions/FileSystemTasksExtensions.cs` | 59 | `using Nuke.Common.IO;` → `Fallout.Common.IO;`. |
| `build/_build/GlobalUsings.cs` | 3 | `global using Nuke.Common;` → `global using Fallout.Common;`. |
| `build/_build/Meta/BuildOutput.cs` | 314 | не меняется (свой код) |
| `build/_build/Meta/DoctorChecks.cs` | 481 | не меняется (свой код) |
| `build/_build/Meta/DotNetArgv.cs` | 66 | `using Nuke.Common.IO;` → `Fallout.Common.IO;`. **Рендер argv — наш код**, API не трогаем. |
| `build/_build/Meta/ImpactMapper.cs` | 441 | не меняется (свой код) |
| `build/_build/Meta/ImpactMapperTests.cs` | 142 | не меняется (свой код) |
| `build/_build/Meta/TargetCatalog.cs` | 425 | `using Nuke.Common;` + `using Nuke.Common.Tools.DotNet;` → `Fallout.*`. **Важно**: каталог отражает таргеты NUKE-класса — при переносе список должен совпасть 1:1, иначе `TargetCatalog.Verify` (его проверяет шаг `list` в CI) покраснеет. |
| `build/_build/Targets/AllTargets.cs` | 60 | `using Nuke.Common;` → `Fallout.Common;`. |
| `build/_build/Targets/ArchitectureTestTarget.cs` | 32 | `using Nuke.Common.Tools.DotNet;` → `Fallout.*`. |
| `build/_build/Targets/ArchiveTarget.cs` | 48 | `using Nuke.Common.IO;` → `Fallout.*`. |
| `build/_build/Targets/CleanTarget.cs` | 52 | `using Nuke.Common.IO;` → `Fallout.*`. |
| `build/_build/Targets/CompileTarget.cs` | 112 | `using Nuke.Common.ProjectModel;` + `using Nuke.Common.Tools.DotNet;` → `Fallout.*`. **Ключевое:** таргет передаёт настоящие switch'и MSBuild (`-maxcpucount:{ProcessorCount}`, `/nodeReuse:false`) — issue #2111. Эти switch'и передаются как аргументы процесса, то есть API-независимы и переносятся как есть. |
| `build/_build/Targets/IpcPublishTarget.cs` | 91 | `using Nuke.Common.IO;` + `using Nuke.Common.Tools.DotNet;` → `Fallout.*`. |
| `build/_build/Targets/PackTarget.cs` | 79 | `using Nuke.Common.IO;` + `using Nuke.Common.Tools.DotNet;` → `Fallout.*`. |
| `build/_build/Targets/PublishTarget.cs` | 51 | `using Nuke.Common.IO;` + `using Nuke.Common.Tools.DotNet;` → `Fallout.*`. |
| `build/_build/Targets/ReleaseTarget.cs` | 76 | `using Nuke.Common.IO;` → `Fallout.*`. |
| `build/_build/Targets/RestoreTarget.cs` | 28 | `using Nuke.Common.Tools.DotNet;` → `Fallout.*`. |
| `build/_build/Targets/TestTarget.cs` | 88 | `using Nuke.Common.Tools.DotNet;` → `Fallout.*`. |
| `build/_build.csproj` | 105 | `Nuke.Common 10.1.0` → `Fallout.Common 10.4.0`; `<NukeTelemetryVersion>` убрать (у fallout телеметрии нет, ADR-0010); **пин `NuGet.Packaging` 6.12.5 → 6.14.3** (иначе NU1605); пины `Microsoft.Build* 18.0.2` и `System.Security.Cryptography.Xml 10.0.13` **оставить**; комментарии переписать под новую реальность. |
| `build/Directory.Build.props` | 55 | `<NukeTelemetryVersion>1</NukeTelemetryVersion>` убрать; комментарии «NUKE-specific overrides» → «Fallout-specific overrides». Остальные свойства (`TreatWarningsAsErrors=false`, CPM off, анализаторы off) — те же. |
| `build/build.sh` | 78 | bootstrap: `dotnet nuke` → `dotnet fallout`, `.nuke/temp` → `.fallout/temp`, `NUKE_TELEMETRY_OPTOUT` убрать. **Либо** оставить наш «толстый» bootstrap (он сам собирает `_build.csproj` и зовёт `dotnet exec _build.dll`) — тогда меняется только имя пакетов, а не скрипт. |
| `build/build.ps1` | 118 | то же для Windows. |
| `Directory.Packages.props` | 615 | комментарий в строках 584-601 «NUKE build system … NO Nuke.Common ENTRY HERE» — переписать на fallout. **Пакетного пина не добавлять**: CPM для `build/` выключен. |
| `Directory.Build.props` (корень) | 268 | комментарии в строках 208, 218, 228, 256 упоминают «NUKE-driven release builds» и «NUKE spelling» — переписать. Свойства не меняются. |
| `.nuke/` → `.fallout/` | 2 файла | `build.schema.json` (6 776 байт) и `parameters.json` (39 байт) — переименовать каталог, содержимое сохраняется. |

**Итого по шагу 1:** 32 файла `.cs` (из них **13 вообще не меняются** — своего кода
NUKE не касается, правятся только `using`-ы в 19), 2 файла props, 2 bootstrap-скрипта,
1 `.csproj`, 1 каталог `.nuke/`.

**Чем доказываем.** Шаг 0 повторяется на каждом из 13 таргетов: argv совпадает,
exit code совпадает, `list --format json` совпадает 1:1. Плюс — сборка всех 13
таргетов на fallout проходит без NUKE.

**Что может сломаться.**
- `DotNetTasks` **не переброшен** в shim (issue #69) — значит любой `using Nuke.*`
  должен быть переписан, shim-путь не работает. Это уже учтено: план идёт через
  `fallout-migrate`, а не через shim.
- Транзитивные зависимости fallout'а другие (`Microsoft.Extensions.DependencyModel 10.0.0`,
  `Octokit 14.0.0`, `Azure.Identity 1.17.1`, `YamlDotNet 16.3.0` — в дереве NUKE
  их нет) → больше пакетов в restore, больше времени.
- Наш `Build.cs` использует `new Target Help` (`private new Target Help`) — «новый»
  ключевое слово означает скрытие члена базового класса. Если базовый класс
  переименует/перестроит `Help`, компиляция может сломаться. **Проверяется компиляцией,
  это и есть приёмка шага.**
- `RootDirectory`, `ArtifactsDirectory`, `Solution` — свойства базового класса
  `NukeBuild`/`FalloutBuild`. Имена те же по гайду, но это проверяется компиляцией.

**Как откатить.** Шаг 1 — добавление, а не замена: NUKE-овый `build/_build/` цел.
Откат = удалить новый каталог. Ни один workflow-файл на этом шаге не меняется.

### Шаг 2. Точки вызова в CI и bootstrap-скрипты

**Что делаем.** Переключаем CI на новую точку входа.

**Все места в `.github/workflows/` (полный список, снят grep'ом):**

| Файл | Строка | Что там | Что меняется |
|---|---:|---|---|
| `.github/workflows/ci.yml` | **1354** | `bash build/build.sh Compile --configuration Release` (джоб `build`, шаг «Build solution via NUKE (Release, zero warnings, docs, deterministic)») | `bash build/build.sh Compile --configuration Release` — **строка не меняется**, если bootstrap-скрипт сохраняет имя `build.sh`. Меняется то, что внутри скрипта. Если скрипт переименуется — строка меняется. |
| `.github/workflows/ci.yml` | **1562** | `bash build/build.sh list --format json > /dev/null` (джоб `build`, шаг «Validate installer scripts + build catalog») | то же |
| `.github/workflows/ci.yml` | 252, 956, 1485 | комментарии («via ./build.sh», «NUKE build-code edits validate here too», «NUKE target catalog») | переписать комментарии |
| `build/build.sh` | весь | bootstrap | `dotnet nuke` → `dotnet fallout` либо оставить свой запуск `_build.dll` |
| `build/build.ps1` | весь | bootstrap | то же |

**Всего: 2 исполняемые точки вызова + 3 комментария + 2 bootstrap-скрипта.**
Ни один другой workflow `build.sh` не зовёт — проверено grep'ом по всем 17 файлам.
Отдельные workflow-файлы (`release.yml`, `nightly.yml`, `demo.yml`, `test-os.yml`,
`benchmark.yml`, `docs.yml`, `gates-binary.yml`, `goldens.yml`, `evals.yml`,
`beta.yml`, `codeql.yml`, `contrib-dryrun.yml`, `docs-site.yml`,
`meas-analyzer-cost.yml`, `meas-build-vcpu.yml`, `meas-jit-aot.yml`) собирают
напрямую через `dotnet build`/`dotnet test` и сборщика не трогают — **их миграция
не нужна и в объём не входит**.

**Чем доказываем.** Джоб `build` в CI зелёный; шаг на строке 1562 (`list`) печатает
тот же каталог; шаг на строке 1354 даёт тот же артефакт `bin/Release/net10.0/`.

**Что может сломаться.** Bootstrap-скрипт — единственное место, где «NUKE» живёт
как имя процесса. Если наш «толстый» bootstrap останется (а он у нас толстый: сам
собирает `_build.csproj` и зовёт `dotnet exec _build.dll`), то **правок в CI не
нужно вовсе** — достаточно поменять пакеты в `_build.csproj`. Это самый дешёвый
вариант и он предпочтителен.

**Как откатой.** Переключение — это изменение **одной** строки в `build.sh`
(или ноль строк в CI). Откат = вернуть строку.

### Шаг 3. Документация и правила

**Что делаем.** Правим 8 документов + комментарии.

| Файл | Что правим |
|---|---|
| `docs/BUILD.md` | 8 упоминаний сборщика: «NUKE» → «Fallout», пути вызова, описание таргетов. |
| `docs/AOT_PUBLISH.md` | 3 упоминания: «NUKE `PublishAot` target», «only a NUKE build». |
| `docs/CENTRAL_PACKAGE_MANAGEMENT.md` | 19 упоминаний: §4.4 «The one project outside CPM» — весь раздел про `Nuke.Common 10.1.0` переписывается под `Fallout.Common 10.4.0` и новые пины. |
| `docs/RELEASE_ENGINEERING_ANALYSIS.md` | 9 упоминаний: «Harbor uses NUKE», «NUKE target», примеры workflow. |
| `docs/BENCHMARKS.md` | 12 упоминаний (в основном — что замеры делались на NUKE-сборке). |
| `docs/REFERENCE-TRIMMER.md` | 2 упоминания. |
| `docs/ALTERNATIVE_UIS.md` | 1 упоминание. |
| `docs/ROADMAP.md` | 1 упоминание (строка 144). |
| `AGENTS.md`, `CLAUDE.md`, `docs/DEVELOPMENT.md`, `docs/ARCHITECTURE.md` | **NUKE не упоминается ни в одном из них** (проверено grep'ом) — править нечего. Но `AGENTS.md` §«Build & test commands» описывает `dotnet build`/`dotnet run --project tests/...`, то есть не оркестратор. **Вывод: эти четыре файла не трогаем.** |
| `Directory.Build.props` (стр. 208, 218, 228, 256) | комментарии «NUKE-driven release builds», «NUKE spelling», «the NUKE tool». |
| `Directory.Packages.props` (стр. 584-601) | блок комментариев про отсутствие пина NUKE. |
| `build/Directory.Build.props` | «NUKE-specific overrides», `<NukeTelemetryVersion>`. |
| `build/_build.csproj` | длинный комментарий про то, «где решается версия NUKE» — переписать. |

**Итого: 8 документов + 3 файла конфигурации.** Строк — порядка 60-80 (это оценка
по объёму правки, не измерение).

**Чем доказываем.** `./tools/check-md-links.py` и `./tools/md-lint.py` — оба в
зеленом; `check-md-links` ловит битые ссылки на переименованные файлы.

**Что может сломаться.** `docs/CENTRAL_PACKAGE_MANAGEMENT.md` — документ, который
**обязан** совпадать с `Directory.Packages.props` и `build/_build.csproj`. Если
комментарии в трёх местах разъедутся, это расхождение надо закрыть в том же PR.
Есть готовый инструмент для проверки: `tools/check-abstractions-contract.py`
(уже проверяет пакеты `src/`), но для `build/` аналога нет — это в объём шага 5 не
входит, фиксируем как известный долг.

**Как откатить.** Правки документации обратимы независимо от кода.

### Шаг 4. Удаление старого

**Что делаем.** Удаляем NUKE-путь:
- каталог `build/_build/` (32 файла `.cs`, 3 718 строк);
- `build/_build.csproj` → переименовывается/заменяется на fallout-версию;
- каталог `.nuke/` (уже переименован в `.fallout/` на шаге 1);
- `Nuke.Common` из `build/_build.csproj` (на шаге 1);
- `NUKE_TELEMETRY_OPTOUT` из `build/build.sh` и `build/build.ps1`;
- `<NukeTelemetryVersion>` из `build/_build.csproj` и `build/Directory.Build.props`.

**Проверка «ни одного упоминания NUKE не осталось»** — это работа гейта шага 5.

**Чем доказываем.** Полоса CI зелёная; `grep -rn "Nuke" build/ .github/ Directory.*.props`
пусто.

**Что может сломаться.** Если что-то в CI ещё зовёт `build/_build.csproj` напрямую —
grep покажет. Проверено: `build/_build.csproj` упоминается в CI только в комментарии
(строка 1485), не в команде.

**Как откатой.** Откат шага 4 = возврат шага 1-3 (всё это в git). **Это единственный
необратимый на вид шаг, поэтому он идёт последним и только после того, как шаги 0-3
прожили полосу.**

### Шаг 5. Гейт, который не даёт вернуться к NUKE

**Что делаем.** Новый гейт в дереве гейтов: `tools/gates/check-no-nuke/`
(`Run(args []string) int`), регистрация в `tools/gates/main.go` **по алфавиту**,
сборка `bash tools/build-gates.sh`, оба файла `tools/bin/*` в коммите.

**Что проверяет (два независимых правила):**

1. **В дереве нет NuGet-ссылок на NUKE.** Ищет `PackageReference` c `Nuke.*` и
   `using Nuke.*` в `build/`, `src/`, `apps/`, `tests/`, `tools/`, `samples/`.
   Краснеет на любом вхождении.
2. **Workflow-файлы зовут новую точку входа.** Ищет `dotnet nuke`,
   `NUKE_TELEMETRY_OPTOUT`, `.nuke/temp` в `.github/workflows/**` и в
   `build/build.sh`, `build/build.ps1`. Краснеет на любом вхождении.

**Почему именно так.** Это ровно тот приём, который в этом репозитории уже применён
к `Microsoft.NET.Test.Sdk`: `tools/check-no-test-sdk.py` не даёт вернуться к VSTest.
Схема та же: правило зашито в инструмент, а не в память ревьюера.

**Чем доказываем.** Гейт краснеет на дереве до миграции (невакуумность) и зеленеет
после. Обе проверки должны иметь позитивный контроль — как того требует подход
[./ARCH_RULES.md](./ARCH_RULES.md) для архитектурных гвардов (у каждого правила
объявлен положительный контроль и нижняя граница обнаружения).

**Что может сломаться.** Ложное срабатывание: строка «NUKE» в историческом документе
(`docs/.kilo-docs/`, `docs/audit-archive/`) не должна считаться. Гейт должен
сканировать только живые пути и иметь список исключений, записанный в гейт, а не
в конфиг.

**Как откатой.** Гейт — аддитивный; красный гейт можно временно сузить списком
исключений, но не выключить.

### Схема зависимостей

```
Шаг 0 (пилот Pack)      — ни от чего не зависит; ничего не ломает
   │
Шаг 1 (весь build/_build) — блокируется #0
   │
Шаг 2 (CI + bootstrap)    — блокируется #1
   │
Шаг 3 (документация)      — независим от #1; можно параллельно
   │
Шаг 4 (удаление NUKE)     — блокируется #1, #2, #3
   │
Шаг 5 (гейт)              — блокируется #4 (иначе гейт красный с первого дня)
```

---

## 4. Смета по нашему коду, в числах

### 4.1 Объём кода

| Что | Число | Как получено |
|---|---:|---|
| файлов `.cs` в `build/_build/` | **32** | `find build/_build -name '*.cs'` |
| строк в них | **3 718** | `cat \| grep -c ''` |
| из них меняются (правка `using`-ов) | **19** | таблица шага 1 |
| из них не меняются | **13** | таблица шага 1 |
| таргетов | **13** | `grep "private Target"` в `Build.cs` |
| мета-команд (`List`, `Doctor`, `What`) | 3 | там же |
| файлов конфигурации сборщика | **5** | `_build.csproj`, `build/Directory.Build.props`, `build/build.sh`, `build/build.ps1`, `.nuke/` |
| строк в `_build.csproj` | 105 | `wc -l` |
| distinct NuKE-идентификаторов, которые мы используем | **7** | `grep -rho "Nuke\.[A-Za-z.]*"` |
| уникальных `using Nuke.*` | **7** | таблица шага 1 |
| пакетных пинов, которые надо пересмотреть | **4** | `NuGet.Packaging` (поднять), `Microsoft.Build*` (оставить), `System.Security.Cryptography.Xml` (оставить), `Nuke.Common` (заменить) |

### 4.2 Места в CI

| Что | Число |
|---|---:|
| workflow-файлов всего | **17** |
| суммарно строк в них | **9 691** |
| из них **исполняемых** вызовов `build/build.sh` | **2** (оба в `ci.yml`: строки 1354 и 1562) |
| комментариев, упоминающих сборщик | 3 (в `ci.yml`) |
| джобов всего (блоков `jobs:`) | **104** |
| `runs-on:` | 63 |
| `needs:` | 19 |
| блоков `permissions:` | 70 (157 строк вместе с телом) |
| ссылок на `secrets.` | 2 |
| ссылок на `upload-artifact` | 36 |
| строк в `run:`-блоках (то, что уезжает в C#) | **2 728** (28,1 % от 9 691) |
| шагов (`- name:`) | 302 |

### 4.3 Документация

| Что | Число |
|---|---:|
| документов в `docs/`, описывающих сборщик | **8** |
| упоминаний сборщика в них | 55 (`build.sh` / `build/_build` / `NUKE`) |
| ADR в `docs/adr/` про сборщик | **0** (не было) — будет ADR-023 |
| файлов конфигурации с упоминанием NUKE в комментариях | **3** (`Directory.Build.props`, `Directory.Packages.props`, `build/Directory.Build.props`) + `build/_build.csproj` |

### 4.4 Трудозатраты

**Это оценка по объёму правки, а не измерение.** Никто не выполнял этот переезд;
цифры ниже — это разложение объёма (файлы, строки, места вызова) на часы по
средней скорости правки.

| Шаг | Оценка | Что входит |
|---|---:|---|
| 0. Пилот `Pack` | **0,5-1 день** | второй build-проект, сравнение argv/состава/выхода, шаг в джобе `pack` |
| 1. Перенос `build/_build/` | **1-2 дня** | 19 файлов с правкой `using`, пересборка 13 таргетов, сверка каталога 1:1, переписывание `_build.csproj` и пинов |
| 2. CI и bootstrap | **0,5 дня** | 2 точки вызова, 3 комментария, 2 скрипта |
| 3. Документация | **0,5-1 день** | 8 документов, 3 файла конфигурации, прогон `check-md-links` и `md-lint` |
| 4. Удаление старого | **0,25 дня** | удаление каталога, чистка конфигурации, зелёная полоса |
| 5. Гейт | **1-2 дня** | Go-гейт, регистрация, тесты, self-test, пересборка бинаря, запись в `docs/CI_GATES.md` и `tools/README.md` |
| **Итого** | **3,5-7,5 дней** | плюс время на прогон CI между шагами |

Стоимость ошибки: **остановленная полоса CI**. Поэтому порядок шагов важнее
трудозатрат: пилот ничего не ломает, переключение — одна точка входа, удаление —
последнее.

---

## 5. Что НЕ переезжает: граница YAML

### 5.1 Что вообще невозможно выразить в C#

| Конструкция | Почему не переезжает |
|---|---|
| `on:` (событие, ветки, пути) | Решение **GitHub** о том, когда запускать workflow. Сервер решает, а не клиент. |
| `runs-on:` | Выбор раннера — тоже решение GitHub: пул, тип машины, self-hosted. |
| `permissions:` | Токен выдаёт GitHub, набор прав — его политика. |
| `secrets.` | Секреты живут в GitHub; передача — через окружение раннера. |
| публикация артефактов и их подпись | `actions/upload-artifact` + подпись — операция GitHub. |
| `needs:` как топология | Граф зависимостей джобов вычисляет планировщик GitHub. |
| required checks в ruleset | Настройка репозитория, вообще не файл. |

### 5.2 А может ли fallout сгенерировать workflow-файлы?

**Да — и это меняет план.** На странице
[docs.fallout.build/docs/cicd/github-actions](https://docs.fallout.build/docs/cicd/github-actions)
сказано дословно:

> **Configuration Generation**
>
> You can generate workflow files from your existing target definitions by
> adding the `GitHubActions` attribute. For instance, you can run the `Compile`
> target on every push with the latest Ubuntu image:
>
> ```csharp
> [GitHubActions(
>     "continuous",
>     GitHubActionsImage.UbuntuLatest,
>     On = new[] { GitHubActionsTrigger.Push },
>     InvokedTargets = new[] { nameof(Compile) })]
> class Build : FalloutBuild { /* ... */ }
> ```
>
> Generated output
>
> Whenever you make changes to the attribute, you have to run the build at least
> once to regenerate the workflow file.

Что именно пишет генератор — по исходникам
(`src/Fallout.Common/CI/GitHubActions/`):

| Что | Пишет генератор? | Источник |
|---|---|---|
| `name:` | да | `GitHubActionsConfiguration.Write` |
| `on:` (триггеры, ветки, пути, cron) | да | там же, строки 35-48 |
| `env:` | да | строка 49 |
| `permissions:` | да | строка 59-62 |
| `concurrency:` | да | строка 69 |
| `defaults:` | да | строка 93 |
| `jobs:` | да | строка 106 |
| `runs-on:` (из `Image` или `RunsOnLabels`) | да | `GitHubActionsJob.Write` |
| `timeout-minutes:`, `environment:`, `concurrency:` (на джоб) | да | `GitHubActionsJob.Write` |
| шаги: checkout, cache, setup-dotnet, `dotnet tool restore`, `dotnet fallout <targets>` | да | `GitHubActionsRunStep.Write`, `GitHubActionsCheckoutStep`, `GitHubActionsCacheStep`, `GitHubActionsArtifactStep` |
| артефакты (`.Produces("*.nupkg")`) | да | `GitHubActionsArtifactStep` |
| **`needs:` (топология джобов)** | **НЕТ** | grep по `src/Fallout.Common/CI/GitHubActions/` не нашёл ни `Needs`, ни `"needs"` |

**Вывод, важный для плана.** Генератор fallout'а умеет писать **полный** workflow
файл — включая `on:`, `permissions:`, `runs-on:`. То есть «сгенерировать workflow-файлы
через сборщик» технически возможно, и в этом — отдельный, более дальний горизонт
работы.

**Но для нашей задачи это не цель, а отдельное решение.** Причины:

1. У нас **104 джоба в 17 файлах**, из них `ci.yml` — 16 джобов с `needs`-топологией
   (9 строк `needs:` в `ci.yml` по замеру). Генератор `needs:` не пишет — топологию
   придётся дописывать руками, и тогда «сгенерированный» файл всё равно правится
   руками. Генерация становится источником расхождений.
2. Наши workflow'ы — это не «сборка», а **оркестрация 104 джобов** с матрицами,
   кэшами, шардингом, `if:`-условиями (102 строки `if:`), gate-запусками и
   требуемыми проверками. Генератор рассчитан на «цель → пара шагов», а не на
   «104 джоба с планированием».
3. Правило репозитория: гейт не даёт дереву разойтись с тем, что сгенерировано
   (как `gen-seam-graph` и его сверка). Если мы вводим генерацию workflow-файлов,
   нужен **сверяющий гейт** — это отдельная работа.

**Поэтому в объём этого переезда генерация workflow-файлов НЕ входит.** План трогает
**только точки вызова** в CI (шаг 2). Генерация — кандидат в отдельную задачу
после того, как сборщик переехал; в этом документе она зафиксирована как
известная возможность, а не как часть переезда.

### 5.3 Минимальная YAML-обвязка, которая останется

Считаем по замеру на `dev` = `82c7a1d0`:

| Конструкция | Строк | Из них генератор fallout умеет | Остаётся руками |
|---|---:|---:|---:|
| блоки `on:` (триггеры, ветки, пути, cron) | **484** | 484 | 0 |
| блоки `permissions:` (заголовок + тело) | **157** | 157 | 0 |
| `runs-on:` | **63** | 63 | 0 |
| `needs:` | **19** | **0** | **19** |
| заголовки джобов (`имя:` + `name:`) | **208** | 208 | 0 |
| **Итого «решётки»** | **931** | 912 | **19** |
| `run:`-блоки (содержимое шелл-скриптов) | **2 728** | 0 | уезжают в C# |
| остальное (шаги, env, кэши, комментарии) | 6 032 | — | — |

**Числа, которые надо помнить:**

- **Минимальная YAML-обвязка, которая не может быть выражена в C#** — это
  `needs:` (**19 строк**) плюс `on:`/`permissions:`/`runs-on:` (704 строки), если
  их генерировать. Итого **931 строка из 9 691** — **9,6 %**.
- Если генерировать workflow не будем (решение по п. 5.2), обвязка — это
  **целиком текущие 9 691 строка**, из которых **2 728** (28,1 %) — шелл-код, который
  логично унести в C#, а остальное — YAML, который остаётся YAML.
- Практический ориентир: **на один workflow-файл** минимальная обвязка — от 5
  строк (`goldens.yml`: 2 джоба, 0 `needs:`, 143 строки целиком) до
  ~30 строк (`ci.yml`: 16 джобов, 9 `needs:`, 13 `permissions:`, 13 `runs-on:`).

---

## 6. Риск-реестр

| # | Риск | Вероятность | Цена | Мера |
|---|---|---|---|---|
| R1 | **Полоса CI перестаёт стартовать** | средняя | критическая | Пилот (шаг 0) добавляет, ничего не заменяет. Переключение — одна точка входа. Удаление — последним. Каждый шаг имеет явную откатку. |
| R2 | NUKE-версия `10.4.0` fallout'а ведёт себя иначе в 13 таргетов | средняя | средняя | Шаг 0 проверяет `Pack`; шаг 1 проверяет все 13 через argv + exit code + `list` 1:1. |
| R3 | **`NuGet.Packaging 6.12.5` → NU1605, restore не проходит** | **высокая** (уже найдено) | средняя | Поднять пин до 6.14.3 в шаге 1. Проверка: restore `build/_build.csproj` должен пройти. |
| R4 | Другая модель кэша / другая нумерация версий | средняя | средняя | Версии fallout'а — semver, `10.4.0` — стабильная. Нумерация CI-артефактов (`build-<run>`) — наша, не fallout'а. Проверяется первым прогоном `pack`. |
| R5 | Другие пути к артефактам (`.nuke/` → `.fallout/`) | **высокая** (уже известно) | низкая | Переименование каталога — шаг 1. Пути к артефактам (`artifacts/publish/...`) задаёт наш `ArtifactPathResolver`, не fallout. |
| R6 | **Лицензия** | — | — | **Проверено: MIT.** `LICENSE` в репозитории fallout'а — MIT, с атрибуцией NUKE; `.nuspec` — `<license type="expression">MIT</license>`. Совместима с MIT-частью нашего дерева. Риска нет. |
| R7 | `.slnx` не поддерживается в выбранной версии | **низкая** | высокая | **Подтверждено:** `Fallout.Persistence.Solution` (10.4.0 и 11.0.18) содержит `SlnxFile`/`SlnxSerializerSettings`. Плюс NUKE issue #1586 (`.slnx` в NUKE) остался без ответа мейнтейнера — то есть fallout здесь ближе к нужному. |
| R8 | `.NET 10` не поддерживается | **низкая** | высокая | **Подтверждено:** `lib/net10.0/` — единственный TFM у `Fallout.Common`. |
| R9 | NativeAOT-публикация ломается | средняя | средняя | Наш код ставит `PublishAot` как MSBuild-свойство, а не типизированным сеттером, — поведение не зависит от API. Но **прямого подтверждения нет**: шаг 0 обязан проверить `Publish` с `PublishVariant.AOT` и сравнить бинарник. |
| R10 | `DotNetTasks` не переброшен в shim (issue #69) | **подтверждено** | средняя | Shim-путь не используем; идём через `fallout-migrate`. |
| R11 | Пространство имён разъезжается между 10.x и 11.x (`Fallout.Common.ProjectModel` vs `Fallout.Solutions`) | **подтверждено** | низкая | Фиксируем версию **10.4.0** в `build/_build.csproj`; гейт шага 5 фиксирует, что пин не поедет. |
| R12 | Fallout'а никто не использует (103 тыс. скачиваний против 39,9 млн) | **подтверждено** | высокая | Это настоящий риск. Меры: пинить версию; не полагаться на `develop`; иметь план отката (NUKE-дерево остаётся в git). Осознанное решение владельца, а не случайность. |
| R13 | `fallout-migrate` не справится с нашим кодом (32 файла, не шаблон NUKE) | средняя | низкая | Инструмент идемпотентен и имеет `--dry-run`. Пилот на 19 файлах с `using`-ами покажет охват. |
| R14 | Транзитивные зависимости fallout'а тянут новые пакеты (`Octokit 14.0.0`, `Azure.Identity 1.17.1`, `YamlDotNet 16.3.0`) | **подтверждено** | низкая | CPM выключен для `build/` — пин-каталог не пострадает. Время restore вырастет; замеряем на шаге 1. |
| R15 | Генерация workflow-файлов втягивает нас в отдельный проект | средняя | высокая | **Явно вне объёма** (п. 5.2). В план не входит, решается отдельной задачей. |

---

## 7. Альтернативы, которые были рассмотрены

| Альтернатива | Почему не выбрана |
|---|---|
| **Остаться на NUKE** | Отклонено фактами: 0 коммитов за 6 месяцев, 323 дня без коммита, ни одного ответа мейнтейнера, 65 открытых issue без разбора, сайт проекта не работает, CVE-отчёт без ответа. Плюс наш билд уже несёт ручные пины, закрывающие долг, который никто не будет чинить. |
| **Cake** | Активен, но это скриптовый DSL (C#-скрипты, не класс-библиотека). Наш `build/_build/` — 32 файла классов с разделением по SRP; переписывать его в Cake-скрипты — потеря типизации и структуры. Не изучался детально; выбран не по этому пункту, а по пункту NUKE. |
| **FAKE / Bullseye / MSBuild-native оркестрация** | Не изучались детально в этом исследовании (в объём не входило). Общий довод: у нас уже есть готовый, разложенный по SRP оркестратор на C#, и Fallout — прямой форк того же самого с сохранённой историей и миграционным инструментом. **Это не измерение, а довод; см. §9.** |
| **Свой оркестратор на MSBuild-таргетах** | MSBuild-таргеты не дают того, что даёт build-фреймворк: параметризации, каталога таргетов, `--dry-run`, разбора артефактов. Наш `BuildOutput`/`DoctorChecks`/`ImpactMapper`/`TargetCatalog` (1 688 строк) пришлось бы переписывать с нуля. |

---

## 8. Что записано в репозитории и какие номера issue

- Отчёт: `docs/BUILDSYSTEM_STUDY.md` (этот файл).
- ADR: [`docs/adr/ADR-023-build-orchestrator-moves-to-fallout.md`](adr/ADR-023-build-orchestrator-moves-to-fallout.md).
- Строка в индексе ADR: `docs/adr/DECISIONS.md`, раздел «ADR-023».
- Эпик: **[#2178](https://github.com/refusedguy/Harbor-Harness/issues/2178)** —
  «Переезд оркестратора сборки с NUKE на Fallout (ADR-023)». В комментарии к
  эпику зафиксировано решение владельца и схема зависимостей.
- Подзадачи по шагам плана:

| Шаг | Issue | Название |
|---|---|---|
| 0 | [#2179](https://github.com/refusedguy/Harbor-Harness/issues/2179) | пилот: один таргет (`Pack`) на fallout, тот же артефакт |
| 1 | [#2180](https://github.com/refusedguy/Harbor-Harness/issues/2180) | перенос всего `build/_build/` на API fallout |
| 2 | [#2181](https://github.com/refusedguy/Harbor-Harness/issues/2181) | точки вызова в CI и bootstrap-скрипты |
| 3 | [#2182](https://github.com/refusedguy/Harbor-Harness/issues/2182) | документация и комментарии в конфигурации |
| 4 | [#2183](https://github.com/refusedguy/Harbor-Harness/issues/2183) | удаление NUKE-пути |
| 5 | [#2184](https://github.com/refusedguy/Harbor-Harness/issues/2184) | гейт, который не даёт вернуться к NUKE |

---

## 9. Что осталось неизмеренным

1. **Время прогона fallout-сборки против NUKE-сборки.** Не измерено: локальный
   `dotnet` в этом окружении запрещён (OOM), сборка измеряется только CI. Сравнение
   будет получено на шаге 0.
2. **Побайтовое равенство `.nupkg`.** Не измерено: зависит от того, что NuGet
   пишет в `metadata`/`.nuspec`. Критерий приёмки сформулирован как «тот же состав
   + те же CRC», а не «тот же SHA-256»; решение примет шаг 0.
3. **Cake, FAKE, Bullseye, MSBuild-native** — не изучались детально. В §7 приведён
   общий довод, а не сравнение по численным критериям. Это сознательный объём:
   решение владельца принято по факту «NUKE заброшен», а не по результатам
   сравнения альтернатив.
4. **Сколько времени займёт CI-прогон каждого шага.** Не измерено; зависит от
   длины очереди и от нагрузки на пулы.
5. **Что именно ломается в `DoctorChecks` (481 строка) и `ImpactMapper` (441 строка)**,
   если они используют NUKE-API, который в fallout'е переименован. По статическому
   чтению — оба файла не содержат `using Nuke.*`, то есть собственного NUKE-API не
   трогают; но фактической компиляции не было.
6. **Поведение `fallout-migrate` на нашем дереве.** Не прогонялось: инструмент
   требует `dotnet` (локальный запрещён). Проверяется в CI на шаге 0.
7. **Полный перечень API-расхождений между `Nuke.Common 10.1.0` и
   `Fallout.Common 10.4.0`.** Сверялась только та поверхность, которую используем мы
   (7 идентификаторов, 19 файлов). Полное сравнение публичного API не делалось.
