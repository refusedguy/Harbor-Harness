// Package bannedapiexemptions is the gate for the banned-API exemptions.
//
// The banned-API list (BannedSymbols.txt) is a SECURITY control: it forbids
// the Newtonsoft serializer, the XML serializer, the Windows registry, the
// legacy async patterns and the sync-over-async calls, and the compiler
// enforces it through RS0030. A suppression -- a `#pragma warning disable
// RS0030` -- is the one way to switch that control off.
//
// The catalogue at the bottom of BannedSymbols.txt records every suppression
// the product tree carries, with the reason it is there. This gate compares
// the catalogue with the tree, in BOTH directions:
//
//   - a file that carries a suppression and the catalogue does not name is
//     an UNJUSTIFIED suppression: a hole in the ban with no record;
//   - a catalogue row that names a file which no longer carries a
//     suppression is a STALE row: a permission for a problem that is gone;
//   - a file whose number of suppressions differs from the number of rows
//     naming it is a COUNT mismatch: a second suppression appeared under a
//     row that justifies one.
//
// The comparison is per FILE and per ROW, never per LINE. A line number is
// a coordinate, and every line inserted above it moves the coordinate; the
// arch tree removed its line anchors for that reason (#1728), and this gate
// does not reintroduce one. A row names a file; the file may carry more
// than one suppression, and the row's description is what covers them.
//
// WHY THIS GATE EXISTS (issue #1730)
//
// The sweep asked which guards prove that they can fail. Every gate in
// tools/ has a self-test that plants a violation; the banned-API
// suppression catalogue had NO such check, and the tree had drifted:
//
//   - src/Harbor.Hosting/Modules/RegistriesModule.cs was named by a
//     catalogue row ("CS-plugin LoadAllAsync") and carries no RS0030
//     pragma at all: the sync-over-async it justified is gone, and the
//     row is a permission for a problem that no longer exists;
//   - apps/Harbor.App.Avalonia/ViewModels/SettingsViewModel.cs carries
//     TWO RS0030 suppressions (the ctor load and the auth probe) and the
//     catalogue named it once.
//
// Nothing in the repository noticed either. The ban is a compiler
// diagnostic, so a suppression is invisible to it by construction: the
// diagnostic is exactly what the suppression switches off.
//
// The contract is the one every compiled gate honours: exit 0 green, 1 red,
// 2 the gate could not run. "Could not run" is NOT "found nothing".
package bannedapiexemptions

import (
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"

	"harbor.local/gates/internal/gate"
)

// The inputs. Both are data, not inference: the catalogue is one section of
// one file, and the product tree is src/ plus apps/.
const (
	catalogueFile = "BannedSymbols.txt"
	sectionMarker = "ACCEPTED LEGACY EXEMPTIONS"
	diagnostic    = "RS0030"
)

// The product trees the suppression may live in. apps/ is a composition
// root and src/ is the product; contrib/ and tests/ are outside the ban's
// perimeter (the ban file is not an AdditionalFile of the test projects).
var productTrees = []string{"src/", "apps/"}

// catalogueRow is one row of the exemption catalogue.
//
// A row is a comment line of the shape
//
//	#   src/Harbor.Hosting/Modules/ConfigurationModule.cs  common+Harbor config eager load
//
// The path, an optional ":NNN" coordinate (NOT checked -- see the package
// comment), and a description of what the suppression is for.
type catalogueRow struct {
	path        string
	lineNo      int
	description string
}

// finding is one thing the gate found.
type finding struct {
	rule string
	path string
	msg  string
}

// rowPattern matches a catalogue row: a comment, three or more spaces, a
// repo path ending in .cs, an optional coordinate, and a description.
//
// The path must be ONE whitespace-free token, so a prose line of the
// catalogue ("#   Reason: ...") is not mistaken for a row: the first token
// after the comment mark has to be the path itself.
//
// The description is separated by ONE or more spaces, because the catalogue
// is not formatted to a column: two of its rows pad the path to a column
// and two do not.
var rowPattern = regexp.MustCompile(`^#\s{2,}(\S*/\S*\.cs)(:\d+)?(?:\s+(.*))?$`)

// pragmaPattern matches a `#pragma warning disable` line. The id list is
// read afterwards, because RS0030 may share the line with other ids.
var pragmaPattern = regexp.MustCompile(`(?m)^[ \t]*#pragma[ \t]+warning[ \t]+disable[ \t]+([^\n]*)$`)

// parseCatalogue reads the exemption rows out of the catalogue.
//
// The second return value says whether the SECTION was found at all. A
// catalogue without the section is not an empty catalogue: it is a gate
// that cannot see its subject, and it must not read as green.
func parseCatalogue(text string) ([]catalogueRow, bool) {
	lines := strings.Split(text, "\n")
	start := -1
	for i, line := range lines {
		if strings.Contains(line, sectionMarker) {
			start = i
			break
		}
	}
	if start < 0 {
		return nil, false
	}

	var rows []catalogueRow
	for i := start + 1; i < len(lines); i++ {
		m := rowPattern.FindStringSubmatch(lines[i])
		if m == nil {
			continue
		}
		rows = append(rows, catalogueRow{
			path:        m[1],
			lineNo:      i + 1,
			description: strings.TrimSpace(m[3]),
		})
	}
	return rows, true
}

// suppressionCount is the number of RS0030 suppressions in one source file.
//
// A line that disables RS0030 among other ids counts once: the question is
// "does this file switch the ban off", not "how many ids the line lists".
// A trailing "// reason" comment is cut off before the id list is read,
// because the repository's own suppressions carry their reason there.
func suppressionCount(source string) int {
	count := 0
	for _, m := range pragmaPattern.FindAllStringSubmatch(source, -1) {
		list := m[1]
		if i := strings.Index(list, "//"); i >= 0 {
			list = list[:i]
		}
		for _, id := range strings.Split(list, ",") {
			if strings.TrimSpace(id) == diagnostic {
				count++
				break
			}
		}
	}
	return count
}

// scan compares the catalogue with the product tree.
//
// files maps a repo-relative path to its source. The function is pure, so
// the self-test can hand it a synthetic tree.
func scan(catalogue string, files map[string]string) ([]finding, int, int, bool) {
	rows, sectionFound := parseCatalogue(catalogue)
	if !sectionFound {
		return nil, 0, 0, false
	}

	// The rows, counted per file.
	rowsPerFile := map[string]int{}
	for _, r := range rows {
		rowsPerFile[r.path]++
	}

	// The suppressions, counted per file. The count is the number of
	// SUPPRESSIONS, not a flag: a file with two of them needs two rows.
	pragmasPerFile := map[string]int{}
	for path, source := range files {
		if n := suppressionCount(source); n > 0 {
			pragmasPerFile[path] = n
		}
	}

	var findings []finding

	// 1. A suppression the catalogue does not justify.
	suppressedFiles := make([]string, 0, len(pragmasPerFile))
	for path := range pragmasPerFile {
		suppressedFiles = append(suppressedFiles, path)
	}
	sort.Strings(suppressedFiles)
	for _, path := range suppressedFiles {
		if rowsPerFile[path] == 0 {
			findings = append(findings, finding{
				rule: "EXEMPTION-UNJUSTIFIED",
				path: path,
				msg: fmt.Sprintf(
					"this file carries %d %s suppression(s) and the catalogue names no row for it: "+
						"the ban is switched off here with no recorded reason",
					pragmasPerFile[path], diagnostic),
			})
		}
	}

	// 2. A catalogue row the tree no longer backs.
	cataloguedFiles := make([]string, 0, len(rowsPerFile))
	for path := range rowsPerFile {
		cataloguedFiles = append(cataloguedFiles, path)
	}
	sort.Strings(cataloguedFiles)
	for _, path := range cataloguedFiles {
		if _, exists := files[path]; !exists {
			findings = append(findings, finding{
				rule: "EXEMPTION-STALE",
				path: path,
				msg:  "the catalogue names this file and the tree does not have it: a permission for a file that is gone",
			})
			continue
		}
		if pragmasPerFile[path] == 0 {
			findings = append(findings, finding{
				rule: "EXEMPTION-STALE",
				path: path,
				msg: fmt.Sprintf(
					"the catalogue names this file and it carries no %s pragma: a permission for a "+
						"problem that no longer exists", diagnostic),
			})
			continue
		}
		// 3. The counts disagree: a suppression appeared under a row that
		//    justifies a different number of them.
		if pragmasPerFile[path] != rowsPerFile[path] {
			findings = append(findings, finding{
				rule: "EXEMPTION-COUNT",
				path: path,
				msg: fmt.Sprintf(
					"the catalogue names this file %d time(s) and it carries %d %s suppression(s): "+
						"every suppression needs a row of its own",
					rowsPerFile[path], pragmasPerFile[path], diagnostic),
			})
		}
	}

	// 4. A row that says nothing about why the suppression is there. The
	//    catalogue's own header claims every exemption carries "a reason
	//    at its call site", and a row with no description is a permission
	//    with no argument.
	for _, r := range rows {
		if r.description == "" {
			findings = append(findings, finding{
				rule: "EXEMPTION-NO-REASON",
				path: r.path,
				msg: fmt.Sprintf(
					"the catalogue row for this file says nothing about why the %s suppression is there",
					diagnostic),
			})
		}
	}

	return findings, len(rows), len(suppressedFiles), true
}

// readTree collects the product tree: the tracked src/ and apps/ sources.
//
// The INDEX, not a glob: an untracked scratch file must not be able to
// change the verdict, and a tracked file must not be able to hide from it.
func readTree(root string) (map[string]string, error) {
	paths, err := gate.TrackedFiles(root, ".cs")
	if err != nil {
		return nil, err
	}
	files := map[string]string{}
	for _, p := range paths {
		if !inProductTree(p) {
			continue
		}
		data, err := os.ReadFile(filepath.Join(root, filepath.FromSlash(p)))
		if err != nil {
			return nil, fmt.Errorf("cannot read %s: %w", p, err)
		}
		files[p] = string(data)
	}
	return files, nil
}

func inProductTree(path string) bool {
	for _, prefix := range productTrees {
		if strings.HasPrefix(path, prefix) {
			return true
		}
	}
	return false
}

// Run is the gate.
func Run(args []string) int {
	minExemptions := 0
	selfTest := false
	for i := 0; i < len(args); i++ {
		switch args[i] {
		case "--min-exemptions":
			if i+1 >= len(args) {
				fmt.Fprintln(os.Stderr, "--min-exemptions needs a number")
				return gate.Broken
			}
			i++
			n := 0
			for _, r := range args[i] {
				if r < '0' || r > '9' {
					fmt.Fprintln(os.Stderr, "--min-exemptions needs a number")
					return gate.Broken
				}
				n = n*10 + int(r-'0')
			}
			minExemptions = n
		case "--self-test":
			selfTest = true
		case "--help", "-h":
			fmt.Println("usage: harbor-gates banned-api-exemptions [--min-exemptions N] [--self-test]")
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

	cataloguePath := filepath.Join(root, catalogueFile)
	catalogueBytes, err := os.ReadFile(cataloguePath)
	if err != nil {
		fmt.Fprintln(os.Stderr, "cannot read", catalogueFile, "-", err)
		return gate.Broken
	}

	files, err := readTree(root)
	if err != nil {
		fmt.Fprintln(os.Stderr, "cannot read the product tree:", err)
		return gate.Broken
	}

	findings, rows, suppressedFiles, sectionFound := scan(string(catalogueBytes), files)

	if !sectionFound {
		fmt.Printf("\nBLIND — %s has no %q section. The gate cannot see its subject, and a gate that "+
			"cannot see is not a gate that found nothing:\n", catalogueFile, sectionMarker)
		fmt.Println("  the exemption catalogue is missing [CATALOGUE-BLIND]")
		return gate.Red
	}

	fmt.Printf("compared %d exemption row(s) against the %s suppressions in %d product file(s)\n",
		rows, diagnostic, suppressedFiles)

	if len(findings) > 0 {
		fmt.Println()
		for _, f := range findings {
			fmt.Printf("%s [%s] %s\n", f.path, f.rule, f.msg)
		}
	}

	// The floor: the catalogue must hold the rows it holds today. An
	// emptied catalogue is not a clean tree, it is a gate with no subject.
	if minExemptions > 0 && rows < minExemptions {
		fmt.Printf("\nFLOOR — the catalogue holds %d row(s) and the floor is %d: an exemption was "+
			"removed without the floor being lowered on purpose\n", rows, minExemptions)
		return gate.Red
	}

	if len(findings) > 0 {
		return gate.Red
	}
	return gate.Green
}
