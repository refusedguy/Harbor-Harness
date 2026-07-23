# Plan — Fix build/_build.csproj compilation

**Branch:** `desktop-apps`
**Created:** 2026-07-23

## Original Request

посмотри что нужно починить в @build/ чтобы он начал работать
прсото исправь чтобы билдилось

## Settings

- **Testing:** no
- **Logging:** verbose (DEBUG)
- **Docs:** yes — mandatory docs checkpoint at completion
- **Roadmap Linkage:** none (no `.ai-factory/ROADMAP.md`)

## Goal

Восстановить компиляцию `build/_build.csproj` (NUKE bootstrap). Сейчас сборка падает с 48 ошибками `CS0246`/`CS0234` из-за отсутствующего namespace `Harbor.Build.Configuration`. После исправлений `./build.sh Compile` должен успешно собирать решение.

## Constraints

- Не менять solution-wide `Directory.Build.props` / `Directory.Packages.props`
- Не добавлять новые внешние зависимости в `_build.csproj`
- Не ломать существующие targets (`Clean`, `Restore`, `Compile`, `Test`, `Publish`, `Release`)
- `build/Directory.Build.props` уже отключает analyzers для NUKE — оставить как есть

## Implementation Progress

✅ Task 1.1: Восстановить `Harbor.Build.Configuration` — создан `build/_build/Configuration/` с 5 типами
✅ Task 2.1: Проверить сборку `build/_build.csproj` — 0 ошибок, сборка прошла
✅ Task 2.2: Smoke-test `./build.sh Compile` — `Build succeeded` (2:00, Restore + Compile targets)

## Implementation Complete

All 3 tasks completed.

Plan file: `.ai-factory/plans/fix-build-system.md`
Files created:
- `build/_build/Configuration/BuildConfiguration.cs`
- `build/_build/Configuration/BuildSettings.cs`
- `build/_build/Configuration/PublishVariant.cs`
- `build/_build/Configuration/FeatureFlags.cs`
- `build/_build/Configuration/ArchiveFormat.cs`
Documentation: warn-only (skipped by user)

What's next?

1. 🔍 /aif:verify — Verify the change
2. 💾 /aif:commit — Commit directly

### Phase 1: Восстановить конфигурационные типы

#### Task 1.1: Восстановить `Harbor.Build.Configuration` ✅

**Deliverable:** Пакет типов, покрывающих все Usage в `Build.cs` и `Targets/*.cs`.

**Files to create:**
- `build/_build/Configuration/BuildConfiguration.cs`
- `build/_build/Configuration/BuildSettings.cs`
- `build/_build/Configuration/PublishVariant.cs`
- `build/_build/Configuration/FeatureFlags.cs`
- `build/_build/Configuration/ArchiveFormat.cs`

**Requirements:**
- Все типы в namespace `Harbor.Build.Configuration`
- `BuildConfiguration` — enum (`Debug`, `Release`)
- `BuildSettings` — record с полями `Configuration`, `TargetFramework`, `Runtime`
- `PublishVariant` — enum со значениями: `FrameworkDependent`, `SelfContained`, `SingleFile`, `SingleFileSelfContained`, `Trimmed`, `AOT`
- `FeatureFlags` — record с полями: `WithPlugins`, `WithScripting`, `WithSpectreTui`, `WithAllProviders`, `WithAllTools`, `Minimal`
- `ArchiveFormat` — enum (`None`, `TarGz`, `Zip`)
- Типы должны покрывать Usage: `Build.cs`, `Targets/AllTargets.cs`, `Targets/ArchiveTarget.cs`, `Targets/ReleaseTarget.cs`, `Targets/IpcPublishTarget.cs`, `Targets/PublishTarget.cs`, `Components/ArtifactPathResolver.cs`, `Components/PublishVariantBuilder.cs`, `Components/CliBuildConfigurator.cs`

**Logging:**
- `INFO [aif-plan] BuildConfiguration types restored`

**Dependencies:** нет.

---

### Phase 2: Верификация сборки

#### Task 2.1: Проверить сборку `build/_build.csproj`

**Deliverable:** `dotnet build build/_build.csproj` завершается с exit code 0, 0 ошибок компиляции.

**Files to change:** нет (исправления из Task 1.1 должны покрыть все CS0246/CS0234).

**Logging:**
- `INFO [aif-plan] _build.csproj builds successfully`

**Dependencies:** Task 1.1.

---

#### Task 2.2: Smoke-test `./build.sh Compile`

**Deliverable:** `./build.sh Compile` запускается, собирает `_build.dll`, выполняет NUKE target `Compile` и доходит до сборки `Harbor.slnx` без падения.

**Files to change:** нет.

**Logging:**
- `INFO [aif-plan] build.sh Compile smoke-test passed`

**Dependencies:** Task 2.1.

## Commit Plan

```
fix(build): restore Harbor.Build.Configuration types and verify NUKE bootstrap

- Task 1.1: add BuildConfiguration, BuildSettings, PublishVariant,
  FeatureFlags, ArchiveFormat under build/_build/Configuration/
- Task 2.1: verify dotnet build build/_build.csproj
- Task 2.2: smoke-test ./build.sh Compile
```
