# changelog/fragments/

One file per pull request, one entry inside it. The assembler
(`tools/changelog-assemble.py`) glues them into the `[Unreleased]` section of
`CHANGELOG.md` at release time.

## Why a directory instead of one file

Every pull request used to append its entry to the end of the same
`[Unreleased]` section of `CHANGELOG.md`. Two pull requests appending to the
same place always conflict, and that conflict is expensive: the branch
diverges from `dev`, it needs a re-merge, the branch changes, and the whole
CI lane restarts. The queue stopped on that circle, once per pull request.

A fragment cannot conflict: each pull request owns its own file, so two of
them never touch the same bytes. The entry itself stays what it was, prose
with the measured facts in it.

## How to write one

The file name is the pull request number: `changelog/fragments/2063.md`. The
content is the same text the entry used to have, starting with a `### ...`
heading:

```markdown
### #1763 — a tool's second exit: the deferred context is a typed field

**What.** ...
```

Number the heading with the issue or pull request it closes, the way the
entries in `CHANGELOG.md` do.

## Rules

- one fragment per pull request, named by its number;
- a pull request does NOT edit `CHANGELOG.md` itself: the assembler owns that
  file, and two pull requests editing it is exactly the conflict this
  directory exists to remove;
- a link inside a fragment is written against the REPOSITORY ROOT (`./docs/...`),
  because the fragment ends up inside the root-level `CHANGELOG.md`; the link
  checker knows this and resolves a fragment's links the same way;
- the assembler is idempotent: running it twice changes nothing;
- `python3 tools/changelog-assemble.py --self-test` proves the assembler
  still notices a diverged `CHANGELOG.md`.
