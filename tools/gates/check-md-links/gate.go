// Package mdlinks is the compiled port of tools/check-md-links.py.
//
// The gate checks the internal markdown links of the tracked `.md` files:
// does a link target resolve, does a `#anchor` exist in the document it
// points at, does a `path/File.cs:123` reference name a file that is still
// there.
//
// It is the sibling of md-lint: that one answers "is this file shaped the
// way the repo says it is?", this one answers "does this link resolve?".
// The two must not overlap.
//
// The text this prints is the text the python original printed, and the
// exit codes are the contract of the binary: 0 green, 1 red, 2 the gate
// could not run.
package mdlinks

import (
	"fmt"
	"os"
	"path"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"unicode/utf8"

	"harbor.local/gates/internal/gate"
	"harbor.local/gates/internal/mdgate"
)

// inlineLink is a `[text](target)` link. The target is the first non-space,
// non-'>' run inside the parens.
//
// The python original writes the pattern as `!?\[[^\]]*\]\(...`, with the
// "!" of an image link INSIDE the match. The port leaves the "!" out, and
// that is not a cosmetic change: RE2 uses the first literal of a pattern as
// a prefix to skip ahead, and the optional "!" hides the "[" from it. The
// pattern with the "!" costs 446 ms over the tracked markdown; the pattern
// without it costs 35 ms, for the SAME 1427 matches.
//
// The match therefore starts at the "[" instead of at the "!". The gate
// reads the LINE of a match and the TARGET of a match, and the "!" is on
// the same line as the "[" — so the report is the same.
var inlineLink = regexp.MustCompile(`\[[^\]]*\]\(\s*<?([^)>\s]+)>?(?:\s+(?:"[^"]*"|'[^']*'|\([^)]*\)))?\s*\)`)

// refDef is a reference-style link definition, `[id]: target`.
var refDef = regexp.MustCompile(`(?m)^[\t\n\v\f\r ]{0,3}\[[^\]]+\]:\s*<?([^\s>]+)>?`)

// htmlHref is an `<a href="target">`, used by the generated HTML reports.
var htmlHref = regexp.MustCompile(`(?i)<a\s[^>]*href="([^"]+)"`)

// fence is a code fence: three backticks or three tildes.
var fence = regexp.MustCompile(`^[\t\n\v\f\r ]{0,3}(` + "`" + `{3}|~{3})`)

// heading is an ATX heading, with the trailing `#`s of a closed heading
// removed.
var heading = regexp.MustCompile(`^[\t\n\v\f\r ]{0,3}#{1,6}\s+(.*?)\s*#*\s*$`)

// lineAnchor is a `path/File.cs:123` reference: the `:123` is a GitHub
// line anchor, not part of the path.
var lineAnchor = regexp.MustCompile(`^(.*):\d+$`)

// external is a scheme: `https:`, `mailto:`, and everything like them.
var external = regexp.MustCompile(`^[a-zA-Z][a-zA-Z0-9+.\-]*:`)

// The three substitutions the GitHub slug algorithm makes on a heading
// title before the slug is computed.
var (
	inlineCodeTitle = regexp.MustCompile("`([^`]*)`")
	linkTitle       = regexp.MustCompile(`\[([^\]]*)\]\([^)]*\)`)
	emphasisTitle   = regexp.MustCompile(`[*_~]`)
	// The character class python's `\w` matches: a letter, a number or an
	// underscore, in ANY script. Go's `\w` is ASCII-only, and the tracked
	// markdown has Russian headings, so the class is spelled out.
	notSlugChar = regexp.MustCompile(`[^\p{L}\p{N}_\- ]+`)
)

// Run is the gate entry point.
func Run(args []string) int {
	verbose := false
	minFiles := 0
	minRefs := 0
	selfTest := false
	for i := 0; i < len(args); i++ {
		switch args[i] {
		case "--verbose":
			verbose = true
		case "--min-files":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--min-files needs a number")
				return gate.Broken
			}
			i++
			n, ok := parseInt(args[i])
			if !ok {
				fmt.Fprintln(os.Stderr, "--min-files needs a number")
				return gate.Broken
			}
			minFiles = n
		case "--min-refs":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--min-refs needs a number")
				return gate.Broken
			}
			i++
			n, ok := parseInt(args[i])
			if !ok {
				fmt.Fprintln(os.Stderr, "--min-refs needs a number")
				return gate.Broken
			}
			minRefs = n
		case "--self-test":
			selfTest = true
		case "--help", "-h":
			fmt.Println("usage: harbor-gates check-md-links [--verbose] [--min-files N] [--min-refs N] [--self-test]")
			return gate.Green
		default:
			fmt.Fprintf(os.Stderr, "unknown argument: %s\n", args[i])
			return gate.Broken
		}
	}

	if selfTest {
		return selfTestRun()
	}

	root, err := gate.RepoRoot()
	if err != nil {
		fmt.Fprintln(os.Stderr, "cannot determine the repository root:", err)
		return gate.Broken
	}

	listed, err := mdgate.TrackedMD(root)
	if err != nil {
		fmt.Fprintln(os.Stderr, "cannot list the tracked markdown:", err)
		return gate.Broken
	}
	if len(listed) == 0 {
		fmt.Fprintln(os.Stderr, "no tracked markdown files found")
		return gate.Broken
	}

	missing := map[string]map[locKey]bool{}
	badAnchor := map[string]map[locKey]bool{}
	total := 0
	var unreadable []string
	anchors := newAnchorCache()

	for _, rel := range listed {
		raw, err := gate.ReadText(filepath.Join(root, rel))
		if err != nil {
			unreadable = append(unreadable, rel)
			continue
		}
		text := stripCode(raw)
		// A changelog fragment is assembled INTO the root-level CHANGELOG.md,
		// so its relative links are written against the repository root: the
		// link has to work in the document the fragment ends up in, not in
		// the folder the fragment waits in.
		base := ""
		if !strings.HasPrefix(rel, "changelog/fragments/") {
			base = path.Dir(rel)
			if base == "." {
				base = ""
			}
		}

		hits := collectHits(text)
		if len(hits) == 0 {
			continue
		}
		if verbose {
			fmt.Printf("  %s: %d refs\n", rel, len(hits))
		}

		for _, hit := range hits {
			total++
			ok, kind, frag := resolve(root, base, hit.target)
			line := rawLine(raw, text, hit.offset)
			if !ok {
				addLoc(missing, rel, line, hit.target)
				continue
			}
			if frag == "" || (kind != "file" && kind != "self") {
				continue
			}
			dest := normPath(joinPath(base, targetPath(hit.target)))
			if strings.HasSuffix(dest, ".md") && exists(root, dest) {
				if !containsFold(anchors.anchorsOf(root, dest), strings.ToLower(frag)) {
					addLoc(badAnchor, rel, line, hit.target)
				}
			}
		}
	}

	nMissing := countLocs(missing)
	nAnchor := countLocs(badAnchor)
	fmt.Printf("checked %d link refs across %d markdown files\n", total, len(listed))

	for _, section := range []struct {
		title string
		data  map[string]map[locKey]bool
	}{
		{"MISSING TARGETS", missing},
		{"MISSING ANCHORS", badAnchor},
	} {
		fmt.Printf("\n=== %s (%d) ===\n", section.title, countLocs(section.data))
		for _, rel := range mdgate.SortedKeys(section.data) {
			fmt.Printf("\n%s\n", rel)
			locs := sortedLocs(section.data[rel])
			if len(locs) > 20 {
				locs = locs[:20]
			}
			for _, loc := range locs {
				fmt.Printf("  L%d: %s\n", loc.line, loc.target)
			}
		}
	}

	if len(unreadable) > 0 {
		// A file the gate could not open is a hole in the scan, never a pass.
		fmt.Printf("\n=== UNREADABLE (%d) ===\n", len(unreadable))
		for _, rel := range mdgate.SortedStrings(unreadable) {
			fmt.Printf("  %s\n", rel)
		}
	}

	guard := mdgate.RequireNonVacuous(
		"check-md-links", len(listed), total, "link refs", minFiles, minRefs)
	if len(guard) > 0 {
		fmt.Print("\n=== VACUITY GUARD ===\n")
		for _, problem := range guard {
			fmt.Printf("  %s\n", problem)
		}
	}

	if len(guard) > 0 || len(unreadable) > 0 || nMissing > 0 || nAnchor > 0 {
		fmt.Printf(
			"\nFAIL: %d missing target(s), %d missing anchor(s), %d unreadable, "+
				"%d vacuity-guard problem(s).\n",
			nMissing, nAnchor, len(unreadable), len(guard))
		return gate.Red
	}
	fmt.Print("\nOK: no broken internal links.\n")
	return gate.Green
}

// parseInt reads a non-negative integer.
func parseInt(s string) (int, bool) {
	if s == "" {
		return 0, false
	}
	n := 0
	for _, r := range s {
		if r < '0' || r > '9' {
			return 0, false
		}
		n = n*10 + int(r-'0')
	}
	return n, true
}

// locKey is a (line, target) pair, the key of a finding.
type locKey struct {
	line   int
	target string
}

// hit is one link reference and where it starts.
type hit struct {
	target string
	offset int
}

// collectHits returns every link reference of the text, with the offset of
// the WHOLE match (the python original reported the line of the match's
// start, not of the target).
func collectHits(text string) []hit {
	var out []hit
	for _, re := range []*regexp.Regexp{inlineLink, refDef, htmlHref} {
		for _, m := range re.FindAllStringSubmatchIndex(text, -1) {
			out = append(out, hit{target: text[m[2]:m[3]], offset: m[0]})
		}
	}
	return out
}

// rawLine is the 1-based line a byte offset of the STRIPPED text falls on,
// counted against the RAW text.
//
// The offset is the python original's: it counted the newlines of the RAW
// text up to an offset of the STRIPPED text. The two texts have different
// lengths (a fenced block contributes nothing to the stripped text), so the
// count is not the line of the match in the stripped text — and the port
// reproduces the python original's number, because that number is what the
// report says.
func rawLine(raw string, text string, byteOffset int) int {
	if byteOffset > len(text) {
		byteOffset = len(text)
	}
	runeOffset := utf8.RuneCountInString(text[:byteOffset])
	prefix := []rune(raw)
	if runeOffset > len(prefix) {
		runeOffset = len(prefix)
	}
	newlines := 0
	for _, r := range prefix[:runeOffset] {
		if r == '\n' {
			newlines++
		}
	}
	return newlines + 1
}

// stripCode blanks out fenced blocks and inline code.
//
// The offsets must survive because they are used to report line numbers
// against the original file. The python original's shape is kept exactly,
// including the part that looks like a mistake: a line INSIDE a fence
// contributes NOTHING, not even its newline. That is what makes a fenced
// sample invisible to the parser, and it is why the reported line of a
// link after a fenced block is counted against the raw text.
func stripCode(text string) string {
	var b strings.Builder
	fenceChar := ""
	for _, line := range gate.SplitLinesKeepEnds(text) {
		if mark := fence.FindStringSubmatch(line); mark != nil {
			if fenceChar == "" {
				fenceChar = mark[1]
			} else if mark[1] == fenceChar {
				fenceChar = ""
			}
			if strings.HasSuffix(line, "\n") {
				b.WriteString("\n")
			}
			continue
		}
		if fenceChar != "" {
			continue
		}
		b.WriteString(withoutInlineCode(line))
	}
	return b.String()
}

// withoutInlineCode replaces every inline code span of a line with "“".
//
// The python original does it with a regular expression, one per line. The
// pattern is a backtick, then anything that is not a backtick and not a
// newline, then a backtick — so the span is the text BETWEEN two backticks
// on one line, and the replacement is the same length as the two backticks.
//
// A scan is the same rule without the regex engine: over 398 documents and
// 1400 links, the regex was the largest single cost of the gate.
func withoutInlineCode(line string) string {
	if !strings.ContainsRune(line, '`') {
		return line
	}
	var b strings.Builder
	b.Grow(len(line))
	for i := 0; i < len(line); {
		if line[i] != '`' {
			b.WriteByte(line[i])
			i++
			continue
		}
		end := i + 1
		for end < len(line) && line[end] != '`' && line[end] != '\n' {
			end++
		}
		if end < len(line) && line[end] == '`' {
			b.WriteString("``")
			i = end + 1
			continue
		}
		b.WriteByte('`')
		i++
	}
	return b.String()
}

// resolve reports whether a link target resolves, and what kind of target
// it is.
func resolve(root string, baseDir string, target string) (bool, string, string) {
	linkPath, frag := splitFragment(target)
	if linkPath == "" {
		return true, "self", frag
	}
	if external.MatchString(linkPath) {
		return true, "external", frag
	}
	resolved := normPath(joinPath(baseDir, linkPath))
	full := filepath.Join(root, resolved)
	if info, err := os.Stat(full); err == nil && info.IsDir() {
		// A directory is a valid target; GitHub renders its README.
		return true, "dir", frag
	}
	if _, err := os.Stat(full); err == nil {
		return true, "file", frag
	}
	if m := lineAnchor.FindStringSubmatch(resolved); m != nil {
		if _, err := os.Stat(filepath.Join(root, m[1])); err == nil {
			return true, "file-line", frag
		}
	}
	return false, "missing", frag
}

// splitFragment splits a target at the first '#'.
func splitFragment(target string) (string, string) {
	if i := strings.Index(target, "#"); i >= 0 {
		return target[:i], target[i+1:]
	}
	return target, ""
}

// targetPath is the path part of a target.
func targetPath(target string) string {
	linkPath, _ := splitFragment(target)
	return linkPath
}

// joinPath is python's `os.path.join(base, path)`: an absolute path
// replaces the base.
func joinPath(base string, p string) string {
	if strings.HasPrefix(p, "/") {
		return p
	}
	if base == "" {
		return p
	}
	return base + "/" + p
}

// normPath is python's `os.path.normpath`.
func normPath(p string) string {
	if p == "" {
		return "."
	}
	initialSlashes := strings.HasPrefix(p, "//")
	if initialSlashes && strings.HasPrefix(p, "///") {
		initialSlashes = false
	}
	parts := strings.Split(p, "/")
	var comps []string
	for _, comp := range parts {
		if comp == "" || comp == "." {
			continue
		}
		if comp != ".." || (!initialSlashes && len(comps) == 0) ||
			(len(comps) > 0 && comps[len(comps)-1] == "..") {
			comps = append(comps, comp)
		} else if len(comps) > 0 {
			comps = comps[:len(comps)-1]
		}
	}
	joined := strings.Join(comps, "/")
	if initialSlashes {
		joined = "/" + joined
	}
	if joined == "" {
		return "."
	}
	return joined
}

// exists reports whether the path exists, relative to the root.
func exists(root string, rel string) bool {
	_, err := os.Stat(filepath.Join(root, rel))
	return err == nil
}

// anchorCache remembers the heading slugs of the files a run has already
// read.
//
// The python original re-reads the file for EVERY link that points at it,
// and a document with twenty links to the same file pays twenty reads. The
// slugs are a pure function of the file's content, which does not change
// during a run, so the cache changes the cost and not the verdict.
type anchorCache struct {
	slugs map[string]map[string]bool
}

// newAnchorCache is an empty cache.
func newAnchorCache() *anchorCache {
	return &anchorCache{slugs: map[string]map[string]bool{}}
}

// anchorsOf returns the heading slugs of a markdown file.
func (c *anchorCache) anchorsOf(root string, rel string) map[string]bool {
	if slugs, ok := c.slugs[rel]; ok {
		return slugs
	}
	slugs := headingAnchors(root, rel)
	c.slugs[rel] = slugs
	return slugs
}

// headingAnchors returns the GitHub heading slugs of a markdown file.
//
// GitHub strips non-word/non-space/non-dash characters, then turns spaces
// into dashes — without trimming, so a leading emoji leaves a leading dash
// ("📊 Metrics" -> "-metrics"). Duplicate slugs get a -1, -2 suffix.
func headingAnchors(root string, rel string) map[string]bool {
	anchors := map[string]bool{}
	text, err := gate.ReadText(filepath.Join(root, rel))
	if err != nil {
		return anchors
	}
	seen := map[string]int{}
	for _, line := range gate.SplitLines(text) {
		m := heading.FindStringSubmatch(line)
		if m == nil {
			continue
		}
		title := strings.TrimSpace(m[1])
		title = inlineCodeTitle.ReplaceAllString(title, "$1")
		title = linkTitle.ReplaceAllString(title, "$1")
		title = emphasisTitle.ReplaceAllString(title, "")
		slug := notSlugChar.ReplaceAllString(title, "")
		slug = strings.ReplaceAll(slug, " ", "-")
		slug = strings.ToLower(slug)
		n := seen[slug]
		seen[slug] = n + 1
		if n == 0 {
			anchors[slug] = true
		} else {
			anchors[fmt.Sprintf("%s-%d", slug, n)] = true
		}
	}
	return anchors
}

// containsFold reports whether the set holds the value.
func containsFold(set map[string]bool, value string) bool {
	return set[value]
}

// addLoc records a finding.
func addLoc(data map[string]map[locKey]bool, rel string, line int, target string) {
	set, ok := data[rel]
	if !ok {
		set = map[locKey]bool{}
		data[rel] = set
	}
	set[locKey{line: line, target: target}] = true
}

// countLocs is the number of findings.
func countLocs(data map[string]map[locKey]bool) int {
	n := 0
	for _, set := range data {
		n += len(set)
	}
	return n
}

// sortedLocs returns the findings of one file, sorted by line and target.
func sortedLocs(set map[locKey]bool) []locKey {
	out := make([]locKey, 0, len(set))
	for loc := range set {
		out = append(out, loc)
	}
	sort.Slice(out, func(i, j int) bool {
		if out[i].line != out[j].line {
			return out[i].line < out[j].line
		}
		return out[i].target < out[j].target
	})
	return out
}
