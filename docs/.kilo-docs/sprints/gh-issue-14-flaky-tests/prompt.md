# SPRINT: gh-issue-14-flaky-tests

## Goal
Исправить известные нестабильные и падающие тесты (Avalonia headless + IPC на Linux).

## Context
Avalonia headless тесты флапают на Linux. Нужно стабилизировать CI и повысить покрытие.

## Tasks
- Диагностика флапов Avalonia headless
- Исправление IPC тестов на Linux
- Добавление component/VLM/load тестов
- Настройка Microsoft.Testing.Platform для полноценных прогонов

## Acceptance
- CI green на Linux runners
- Zero flaky tests за 3 последовательных запуска
- E2E coverage улучшено
