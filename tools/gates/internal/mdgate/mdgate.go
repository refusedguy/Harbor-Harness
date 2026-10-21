// Package mdgate holds what the two markdown gates share: the tracked-file
// list, and the anti-vacuity guard.
//
// The python original was tools/md_gate.py, a module the two gates imported.
// It is here for the same reason: the two gates must not each reinvent the
// tracked-file list and the guard, and a guard that exists twice is a guard
// that can be fixed in one place and forgotten in the other.
//
// # WHAT THE GUARD IS FOR
//
// A checker that scans nothing (or nearly nothing) and exits 0 is
// indistinguishable from a checker that found nothing, and that is the
// failure mode this repository has already paid for twice: a link gate in a
// workflow whose `paths-ignore` made it unreachable, and an allow-list that
// "verified zero files". So every gate reports how many files and how many
// units it looked at, and a floor (`--min-files` / `--min-refs`,
// `--min-lines`) turns a silently narrowed scan into a red build.
package mdgate

import (
	"fmt"
	"sort"
	"strings"

	"harbor.local/gates/internal/gate"
)

// TrackedMD returns every tracked `*.md` path, relative to the root,
// sorted.
//
// `git ls-files` (the index) rather than a glob: an untracked scratch file
// must not be able to break the build, and a tracked file must not be able
// to hide from it.
func TrackedMD(root string) ([]string, error) {
	return gate.TrackedFiles(root, ".md")
}

// TrackedCS returns every tracked `*.cs` path, relative to the root,
// sorted.
//
// TrackedMD's sibling, and the same reason: the INDEX, not a glob.
//
// The pathspec is `*.cs` and not `.cs` — a git pathspec without a leading
// `*` is a LITERAL path, which #944's own commit message records as having
// silently produced a one-entry index.
func TrackedCS(root string) ([]string, error) {
	return gate.TrackedFiles(root, ".cs")
}

// RequireNonVacuous returns guard violations for a scan; an empty slice
// means the scan was real.
//
// `files` is how many documents were read, `units` how many things were
// examined inside them (link references, checked lines). The floors are a
// ratchet: they are set in .github/workflows/docs.yml to today's numbers,
// and lowering one is a deliberate act that a reviewer can see in the diff.
func RequireNonVacuous(
	label string,
	files int,
	units int,
	unitName string,
	minFiles int,
	minUnits int,
) []string {
	var problems []string
	if files == 0 {
		problems = append(problems, fmt.Sprintf(
			"%s: scanned ZERO markdown files — a gate that examined nothing "+
				"cannot report that everything is fine", label))
		return problems
	}
	if files < minFiles {
		problems = append(problems, fmt.Sprintf(
			"%s: scanned %d markdown files, floor is %d — the file set shrank "+
				"or the path filter is wrong; lower the floor in "+
				".github/workflows/docs.yml only if the removal was intended",
			label, files, minFiles))
	}
	if units == 0 {
		problems = append(problems, fmt.Sprintf(
			"%s: examined ZERO %s in %d files — the parser stopped matching, "+
				"not the documents", label, unitName, files))
	} else if units < minUnits {
		problems = append(problems, fmt.Sprintf(
			"%s: examined %d %s, floor is %d — same cause as the file count: "+
				"the scan got narrower", label, units, unitName, minUnits))
	}
	return problems
}

// SortedKeys returns the map's keys, sorted. The gates print their
// findings in that order, and the order is part of the output a reader
// (and a diff) sees.
func SortedKeys[V any](m map[string]V) []string {
	out := make([]string, 0, len(m))
	for key := range m {
		out = append(out, key)
	}
	sort.Strings(out)
	return out
}

// SortedStrings returns the sorted copy of the list.
func SortedStrings(list []string) []string {
	out := append([]string{}, list...)
	sort.Strings(out)
	return out
}

// JoinLines joins the lines with a newline, the way python's
// "\n".join(list) does.
func JoinLines(lines []string) string {
	return strings.Join(lines, "\n")
}
