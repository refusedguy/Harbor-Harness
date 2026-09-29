# Probe for the docs gate (#509)

Throwaway file, deleted with its branch. It exists to prove two things at once:

1. a diff containing **only** markdown still starts `.github/workflows/docs.yml`
   (the `paths: ['**.md']` filter is the one the gate depends on), and
2. the link gate is red, not green, when a link does not resolve.

Broken on purpose: [this target does not exist](docs/NO-SUCH-FILE-509.md) and
[so does this anchor](docs/ROADMAP.md#no-such-anchor-509).
