---
name: Bug report
about: Report reproducible Harbor behavior with the data needed to investigate it
title: "[bug]: "
labels: bug
assignees: ""
---

# Bug report

Read the [support guide](../../docs/SUPPORT.md) and the
[troubleshooting index](../../docs/TROUBLESHOOTING.md) first. Many failures
already have a check that resolves them, and an issue that repeats one of those
symptoms gets closed as a duplicate.

Search existing issues, including closed ones, before opening this report.

## Expected behavior

What did you expect Harbor to do?

## Actual behavior

What happened instead? Quote the exact error text. Do not paraphrase it:
searching by the exact text also finds existing answers.

## Steps to reproduce

The smallest sequence that triggers the problem. Include the command you ran,
the prompt or configuration involved, and the step where the behavior changed.

## Harbor version and install path

Paste the output of `harbor --version`, and state whether you installed a
release binary, built from source, or use a daily build.

## Environment

- Operating system and CPU architecture:
- .NET version:
- `HARBOR_TUI` value, if you set one:

## Provider and model

The ids from `harbor providers` and `harbor models <provider>`.

Do not paste API keys or tokens.

## Logs

The relevant lines from `harbor logs --last`. Remove tokens, keys, and file
paths you do not want to share.

## What you already tried

Name the docs and issues you read, and what you tried before opening this
report. One problem per issue, please.
