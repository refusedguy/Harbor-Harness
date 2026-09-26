# SPRINT: gh-issue-16-skills

## Goal
Реализовать Skills system: SKILL.md discovery, skill tool, system-prompt injection.

## Context
Наследие из pstack-навыков. Нужно стандартизировать discovery и выполнение скиллов.

## Tasks
- Реализовать сканер SKILL.md
- Добавить skill tool в реестр
- System-prompt injection из SKILL.md
- Plugin-уровень для кастомных скиллов

## Acceptance
- Все скиллы из .kilo-docs/skills/ загружаются
- Skill tool исполняет скиллы по контракту
- System-prompt содержит заявленные скиллы
