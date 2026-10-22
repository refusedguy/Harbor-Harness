package mergeloss

// diffparse.go -- one diff, read as the answer for every file in it.
//
// The gate asks git what a pull request changed, one file at a time: 498
// `git diff` processes on this repository's history. The whole diff carries
// the same lines, and the file a line belongs to is the file the header
// names, so ONE diff answers for every file at once.
//
// THE LINES A FILE OWNS
//
// The rule is the one the per-file diff was read with, and it is applied to
// the same bytes:
//
//	"--- ..." and "+++ ..."   the file's own header: not content
//	"+..."                     a line the file added
//	"-..."                     a line the file removed
//	anything else             the mode, the hunk header: not content
//
// A file a pull request DELETED is not examined: the deletion is the pull
// request's decision, not a loss. The per-file diff was read from a list
// that said so, and this parser says the same thing: a file whose only
// section is a deletion is a deleted file.
//
// A file that changed KIND -- a symlink that became a regular file -- has
// TWO sections, one that deletes and one that adds, and it is not a deleted
// file. It is examined, and both of its sections are read, exactly as the
// per-file diff read them.

import (
	"strings"

	"harbor.local/gates/internal/pycompat"
)

// pathDiff is what one file's sections say.
type pathDiff struct {
	added    []string
	removed  []string
	sections int
	deleted  bool
}

// pullRequestDiff is the answer for every file of one diff.
type pullRequestDiff struct {
	order []string
	files map[string]*pathDiff
}

// file is what one file's diff says.
func (d *pullRequestDiff) file(path string) pathDiff {
	if d == nil {
		return pathDiff{}
	}
	entry, ok := d.files[path]
	if !ok {
		return pathDiff{}
	}
	return *entry
}

// examined is the files the pull request changed, in the order the diff
// names them.
//
// A file the pull request deleted is not in the list: the deletion is the
// pull request's decision, and the gate does not call it a loss.
func (d *pullRequestDiff) examined() []string {
	out := make([]string, 0, len(d.order))
	for _, path := range d.order {
		entry := d.files[path]
		if entry.sections == 1 && entry.deleted {
			continue
		}
		out = append(out, path)
	}
	return out
}

// parseDiff reads a unified diff.
//
// The name of the file a section belongs to is the name in the header, and
// the header is `diff --git a/<file> b/<file>`: the two halves are the same
// name, so the split is the place where they are equal.
func parseDiff(out string) *pullRequestDiff {
	result := &pullRequestDiff{files: map[string]*pathDiff{}}
	current := ""
	for _, raw := range pycompat.SplitLines(out) {
		if strings.HasPrefix(raw, "diff --git ") {
			path, ok := pathFromDiffHeader(raw)
			if !ok {
				current = ""
				continue
			}
			entry, seen := result.files[path]
			if !seen {
				entry = &pathDiff{}
				result.files[path] = entry
				result.order = append(result.order, path)
			}
			entry.sections++
			current = path
			continue
		}
		if current == "" {
			continue
		}
		entry := result.files[current]
		if strings.HasPrefix(raw, "deleted file mode ") {
			entry.deleted = true
			continue
		}
		if strings.HasPrefix(raw, "---") || strings.HasPrefix(raw, "+++") {
			continue
		}
		if strings.HasPrefix(raw, "+") {
			entry.added = append(entry.added, raw[1:])
			continue
		}
		if strings.HasPrefix(raw, "-") {
			entry.removed = append(entry.removed, raw[1:])
			continue
		}
	}
	return result
}

// pathFromDiffHeader is the file a `diff --git` header names.
//
// git writes a name the way it writes every name: quoted when it holds a
// character that needs quoting, and the same name on both sides of the
// header. The split is therefore the place where the two halves are equal,
// and a name that holds " b/" does not confuse it: no earlier place can
// split one name into two equal halves.
func pathFromDiffHeader(line string) (string, bool) {
	const prefix = "diff --git a/"
	if !strings.HasPrefix(line, prefix) {
		return "", false
	}
	rest := line[len(prefix):]
	for index := 0; index+3 <= len(rest); index++ {
		if rest[index] != ' ' || rest[index+1] != 'b' || rest[index+2] != '/' {
			continue
		}
		left, right := rest[:index], rest[index+3:]
		if left == right {
			return left, true
		}
	}
	return "", false
}

// deliberateRemovalsFor is the lines that NORMAL commits removed, for every
// path at once.
//
// A normal commit is one that did not come through this repository's local
// squash-merge procedure: a pull request merged through the GitHub API, or
// a direct commit. When such a commit removes a line, somebody asked for it
// to go, and the gate must not call that a loss.
//
// The local squash-merges are excluded on purpose: their removals are
// exactly what this gate is looking for.
//
// ONE `git log -p` answers for every path: the walk of the history is what
// costs, and the file a removed line belongs to is the file the header
// names.
//
// `--no-renames` is the flag that makes the answer the same answer the
// one-log-per-path way gave. With renames on, git pairs a renamed file with
// the file it was, and the pair hides the content the per-file walk showed:
// the file the gate asks about would read as "renamed away" instead of as
// "removed line by line".
func deliberateRemovalsFor(dir string, paths []string, tip string,
	squashMerges map[string]bool) map[string]map[string]bool {
	removed := map[string]map[string]bool{}
	if len(paths) == 0 {
		return removed
	}
	args := []string{"log", "--first-parent", "-p", "--no-renames", "--format=@@COMMIT %H", tip, "--"}
	args = append(args, paths...)
	out, ok := gitTextAt(dir, args...)
	if !ok {
		return removed
	}
	commit := ""
	path := ""
	for _, raw := range pycompat.SplitLines(out) {
		if strings.HasPrefix(raw, "@@COMMIT ") {
			commit = strings.TrimSpace(raw[len("@@COMMIT "):])
			continue
		}
		if commit != "" && squashMerges[commit] {
			// A local squash-merge: its removals are what the gate is
			// looking for, so they are not "somebody asked for it to go".
			continue
		}
		if strings.HasPrefix(raw, "diff --git ") {
			name, ok := pathFromDiffHeader(raw)
			if ok {
				path = name
			}
			continue
		}
		if strings.HasPrefix(raw, "---") || strings.HasPrefix(raw, "+++") {
			continue
		}
		if strings.HasPrefix(raw, "-") {
			lines, seen := removed[path]
			if !seen {
				lines = map[string]bool{}
				removed[path] = lines
			}
			lines[normalize(raw[1:])] = true
		}
	}
	return removed
}
