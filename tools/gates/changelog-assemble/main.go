// Package changelog is the compiled port of tools/changelog-assemble.py.
//
// It glues the per-pull-request changelog fragments into the `[Unreleased]`
// section of CHANGELOG.md. A pull request owns its own fragment and never
// edits CHANGELOG.md, so two pull requests never touch the same bytes.
//
// The text this prints is the text the python original printed, and the exit
// codes are the contract of the binary: 0 green, 1 red, 2 the gate could not
// run.
package changelog

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"

	"harbor.local/gates/internal/gate"
	"harbor.local/gates/internal/pycompat"
)

const header = "## [Unreleased]"

// fragment is one file under changelog/fragments.
type fragment struct {
	key  string
	text string
}

// repo is the tree the assembler works on.
//
// The python original resolved it from the script's own path, so a copy of
// the script in a throwaway tree worked on that tree. The binary is
// committed at tools/bin/harbor-gates, so the tree is two directories above
// it.
type repo struct {
	root      string
	fragment  string
	changelog string
}

func openRepo() (*repo, error) {
	root, err := gate.BinaryRoot()
	if err != nil {
		return nil, err
	}
	return &repo{
		root:      root,
		fragment:  filepath.Join(root, "changelog", "fragments"),
		changelog: filepath.Join(root, "CHANGELOG.md"),
	}, nil
}

// Run is the gate entry point.
func Run(args []string) int {
	check := false
	selfTest := false
	for _, arg := range args {
		switch arg {
		case "--check":
			check = true
		case "--self-test":
			selfTest = true
		default:
			fmt.Fprintf(os.Stderr, "unknown argument: %s\n", arg)
			return gate.Broken
		}
	}

	tree, err := openRepo()
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Broken
	}

	if selfTest {
		return tree.selfTest()
	}

	have, err := os.ReadFile(tree.changelog)
	if err != nil {
		fmt.Fprintf(os.Stderr, "CHANGELOG.md: %v\n", err)
		return gate.Red
	}
	want, err := tree.merged(string(have))
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Red
	}

	if check {
		// Between releases the fragments are NOT yet in the file — that is
		// normal: the assembler glues them at release time. Two things have
		// to hold:
		//   1) no fragment's entry is already in CHANGELOG.md — the release
		//      would glue it a second time;
		//   2) the assembly is idempotent: a second run changes nothing.
		dupes, err := tree.duplicateEntries(string(have))
		if err != nil {
			fmt.Fprintln(os.Stderr, err.Error())
			return gate.Red
		}
		if len(dupes) > 0 {
			fmt.Println("FAIL: запись фрагмента уже есть в CHANGELOG.md — на релизе она")
			fmt.Println("      повторится: " + strings.Join(dupes, ", "))
			return gate.Red
		}
		once, err := tree.merged(string(have))
		if err != nil {
			fmt.Fprintln(os.Stderr, err.Error())
			return gate.Red
		}
		twice, err := tree.merged(once)
		if err != nil {
			fmt.Fprintln(os.Stderr, err.Error())
			return gate.Red
		}
		if twice != once {
			fmt.Println("FAIL: сборщик не идемпотентен: второй запуск меняет файл.")
			return gate.Red
		}
		frags, err := tree.fragments()
		if err != nil {
			fmt.Fprintln(os.Stderr, err.Error())
			return gate.Red
		}
		fmt.Printf("OK: %d фрагмент(ов), дублей нет, сборка идемпотентна.\n", len(frags))
		return gate.Green
	}

	if want == string(have) {
		fmt.Println("нечего собирать: CHANGELOG.md уже совпадает с фрагментами.")
		return gate.Green
	}
	if err := os.WriteFile(tree.changelog, []byte(want), 0o644); err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Red
	}
	frags, err := tree.fragments()
	if err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		return gate.Red
	}
	fmt.Printf("собрано: %d фрагмент(ов) в CHANGELOG.md\n", len(frags))
	return gate.Green
}

var leadingDigits = regexp.MustCompile(`^(\d+)`)

// fragments lists the fragments in order: the pull request number, then the
// file name.
func (r *repo) fragments() ([]fragment, error) {
	entries, err := os.ReadDir(r.fragment)
	if err != nil {
		// No fragment directory: no fragments.
		return nil, nil
	}
	var out []fragment
	for _, entry := range entries {
		if entry.IsDir() || !strings.HasSuffix(entry.Name(), ".md") {
			continue
		}
		if strings.HasPrefix(strings.ToUpper(entry.Name()), "README") {
			continue
		}
		data, err := os.ReadFile(filepath.Join(r.fragment, entry.Name()))
		if err != nil {
			continue
		}
		stem := strings.TrimSuffix(entry.Name(), ".md")
		number := "999999"
		if match := leadingDigits.FindStringSubmatch(stem); match != nil {
			number = match[1]
		}
		out = append(out, fragment{key: number + "\x00" + entry.Name(), text: string(data)})
	}
	sort.SliceStable(out, func(i, j int) bool { return out[i].key < out[j].key })
	return out, nil
}

// assembled is the text of the fragments that are not in the section yet.
//
// A fragment whose heading is already in the section is skipped: without
// that, a second run of the assembler would append it a second time.
func (r *repo) assembled(already string) (string, error) {
	frags, err := r.fragments()
	if err != nil {
		return "", err
	}
	var parts []string
	for _, frag := range frags {
		body := strings.Trim(frag.text, "\n")
		if body == "" {
			continue
		}
		// A fragment may NOT carry the section's heading: after the glue it
		// would read as the start of another section and cut [Unreleased]
		// in half.
		var kept []string
		for _, line := range pycompat.SplitNL(body) {
			if strings.HasPrefix(line, "## [") {
				continue
			}
			kept = append(kept, line)
		}
		body = strings.Trim(strings.Join(kept, "\n"), "\n")
		if body == "" {
			continue
		}
		heading := ""
		for _, line := range pycompat.SplitNL(body) {
			if strings.HasPrefix(line, "### ") {
				heading = strings.TrimSpace(line)
				break
			}
		}
		if heading != "" && strings.Contains(already, heading) {
			continue
		}
		parts = append(parts, body)
	}
	return strings.Join(parts, "\n\n"), nil
}

// currentSection is the text between the [Unreleased] heading and the next
// section heading.
func (r *repo) currentSection(text string) (string, error) {
	lines := pycompat.SplitNL(text)
	start := -1
	for index, line := range lines {
		if strings.TrimSpace(line) == header {
			start = index
			break
		}
	}
	if start < 0 {
		return "", fmt.Errorf("CHANGELOG.md: секция [Unreleased] не найдена")
	}
	end := len(lines)
	for index := start + 1; index < len(lines); index++ {
		if strings.HasPrefix(lines[index], "## [") {
			end = index
			break
		}
	}
	return strings.Trim(strings.Join(lines[start+1:end], "\n"), "\n"), nil
}

// merged is the changelog with the fragments glued into [Unreleased].
func (r *repo) merged(text string) (string, error) {
	section, err := r.currentSection(text)
	if err != nil {
		return "", err
	}
	newSection, err := r.assembled(section)
	if err != nil {
		return "", err
	}
	if newSection == "" {
		return text, nil
	}
	var parts []string
	if strings.TrimSpace(section) != "" {
		parts = append(parts, section)
	}
	if strings.TrimSpace(newSection) != "" {
		parts = append(parts, newSection)
	}
	body := strings.Join(parts, "\n\n")

	lines := pycompat.SplitNL(text)
	start := -1
	for index, line := range lines {
		if strings.TrimSpace(line) == header {
			start = index
			break
		}
	}
	if start < 0 {
		return "", fmt.Errorf("CHANGELOG.md: секция [Unreleased] не найдена")
	}
	end := len(lines)
	for index := start + 1; index < len(lines); index++ {
		if strings.HasPrefix(lines[index], "## [") {
			end = index
			break
		}
	}
	out := make([]string, 0, len(lines)+3)
	out = append(out, lines[:start+1]...)
	out = append(out, "", body, "")
	out = append(out, lines[end:]...)
	return strings.TrimRight(strings.Join(out, "\n"), "\n") + "\n", nil
}

// duplicateEntries are the fragment headings that are already in the
// changelog.
func (r *repo) duplicateEntries(text string) ([]string, error) {
	section, err := r.currentSection(text)
	if err != nil {
		return nil, err
	}
	frags, err := r.fragments()
	if err != nil {
		return nil, err
	}
	var out []string
	for _, frag := range frags {
		for _, line := range pycompat.SplitNL(frag.text) {
			if strings.HasPrefix(line, "### ") {
				title := strings.TrimSpace(line)
				if strings.Contains(section, title) {
					out = append(out, title)
				}
				break
			}
		}
	}
	return out, nil
}
