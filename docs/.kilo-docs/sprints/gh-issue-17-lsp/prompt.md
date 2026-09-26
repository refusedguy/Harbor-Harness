# SPRINT: gh-issue-17-lsp

## Goal
Интегрировать LSP для диагностики, переходов по коду и definition tools.

## Context
LSP — стандарт для IDE-фич. Harbor требует нативной поддержки без потери производительности.

## Tasks
- Реализовать LSP diagnostics adapter
- Go-to-definition и references
- Definition tool в agent loop
- Тесты + документация

## Acceptance
- Диагностики отображаются в UI
- Go-to-definition работает по нативному коду
- Definition tool покрыт unit-тестами
