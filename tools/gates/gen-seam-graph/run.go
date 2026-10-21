package genseamgraph

// run.go -- the CLI of the seam-graph gate, and its self-test.
//
// The flags are the ones tools/gen-seam-graph.py had, with the same names
// and the same meaning, because the workflows pass them:
//
//	--repo PATH          the repository root
//	--out PATH           the document to check or write
//	--write              regenerate the document
//	--check              fail if the document is stale
//	--report             print the numbers
//	--self-test          prove the checker can fail
//	--min-files N        floor on the number of source files scanned
//	--min-interfaces N   floor on the number of interfaces found under src/
//
// The text this prints is the text the python original printed. The exit
// codes are the contract of the binary: 0 green, 1 red, 2 the gate could
// not run.

import (
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"

	"harbor.local/gates/internal/gate"
)

const defaultOut = "docs/SEAM_GRAPH.md"

// Run is the gate entry point. The dispatcher hands it the arguments that
// follow the gate name.
func Run(args []string) int {
	repo := ""
	out := ""
	write := false
	check := false
	report := false
	selfTest := false
	minFiles := 0
	minInterfaces := 0

	for i := 0; i < len(args); i++ {
		switch args[i] {
		case "--repo":
			v, ok := nextValue(args, &i)
			if !ok {
				return broken("--repo needs a path")
			}
			repo = v
		case "--out":
			v, ok := nextValue(args, &i)
			if !ok {
				return broken("--out needs a path")
			}
			out = v
		case "--write":
			write = true
		case "--check":
			check = true
		case "--report":
			report = true
		case "--self-test":
			selfTest = true
		case "--min-files":
			v, ok := nextValue(args, &i)
			if !ok {
				return broken("--min-files needs a number")
			}
			n, err := parseInt(v)
			if err != nil {
				return broken("--min-files needs a number")
			}
			minFiles = n
		case "--min-interfaces":
			v, ok := nextValue(args, &i)
			if !ok {
				return broken("--min-interfaces needs a number")
			}
			n, err := parseInt(v)
			if err != nil {
				return broken("--min-interfaces needs a number")
			}
			minInterfaces = n
		case "--help", "-h":
			usage()
			return gate.Green
		default:
			fmt.Fprintf(os.Stderr, "unknown argument: %s\n", args[i])
			return gate.Broken
		}
	}

	root, err := gate.RepoRoot()
	if err != nil {
		fmt.Fprintln(os.Stderr, "cannot determine the repository root:", err)
		return gate.Broken
	}
	if repo != "" {
		root, err = filepath.Abs(repo)
		if err != nil {
			fmt.Fprintln(os.Stderr, err)
			return gate.Broken
		}
	}
	outPath := filepath.Join(root, defaultOut)
	if out != "" {
		outPath, err = filepath.Abs(out)
		if err != nil {
			fmt.Fprintln(os.Stderr, err)
			return gate.Broken
		}
	}

	switch {
	case selfTest:
		return selfTestRun(root)
	case write:
		return writeOutput(root, outPath)
	case check:
		return checkOutput(root, outPath, minFiles, minInterfaces)
	case report:
		return reportNumbers(root)
	default:
		usage()
		return gate.Broken
	}
}

func usage() {
	fmt.Println("usage: harbor-gates gen-seam-graph [--check | --write | --report | --self-test]")
	fmt.Println("       [--repo PATH] [--out PATH] [--min-files N] [--min-interfaces N]")
	fmt.Println()
	fmt.Println("  --check              fail if the committed seam graph is stale")
	fmt.Println("  --write              regenerate the committed seam graph")
	fmt.Println("  --report             print the numbers")
	fmt.Println("  --self-test          prove the checker can fail")
	fmt.Println("  --min-files N        floor on the number of source files scanned")
	fmt.Println("  --min-interfaces N   floor on the number of interfaces found under src/")
}

func nextValue(args []string, i *int) (string, bool) {
	if *i+1 >= len(args) {
		return "", false
	}
	*i++
	return args[*i], true
}

func parseInt(s string) (int, error) {
	if s == "" {
		return 0, fmt.Errorf("empty number")
	}
	n := 0
	for _, r := range s {
		if r < '0' || r > '9' {
			return 0, fmt.Errorf("not a number: %s", s)
		}
		n = n*10 + int(r-'0')
	}
	return n, nil
}

func broken(message string) int {
	fmt.Fprintln(os.Stderr, message)
	return gate.Broken
}

// writeOutput regenerates the document.
func writeOutput(repo, out string) int {
	g := newGraph(repo)
	text := render(g)
	if err := os.MkdirAll(filepath.Dir(out), 0o755); err != nil {
		fmt.Fprintln(os.Stderr, err)
		return gate.Broken
	}
	if err := os.WriteFile(out, []byte(text), 0o644); err != nil {
		fmt.Fprintln(os.Stderr, err)
		return gate.Broken
	}
	rel := out
	if r, err := filepath.Rel(repo, out); err == nil {
		rel = r
	}
	fmt.Println("wrote", rel)
	return gate.Green
}

// checkOutput is the gate the workflows run.
func checkOutput(repo, out string, minFiles, minInterfaces int) int {
	if _, err := os.Stat(out); err != nil {
		fmt.Printf("FAIL: %s does not exist; run --write\n", out)
		return gate.Red
	}
	g := newGraph(repo)
	var interfaces []*typeDecl
	for _, d := range g.decls {
		if d.kind == "interface" && strings.HasPrefix(d.rel(), "src/") {
			interfaces = append(interfaces, d)
		}
	}
	if len(g.consumerFiles) < minFiles {
		fmt.Printf(
			"FAIL: only %d source files were scanned, floor is %d. A scan that "+
				"stopped covering the tree would produce a document that agrees "+
				"with itself and says nothing, the #509 shape.\n",
			len(g.consumerFiles), minFiles)
		return gate.Red
	}
	if len(interfaces) < minInterfaces {
		fmt.Printf(
			"FAIL: only %d interfaces were found under src/, floor is %d. A "+
				"scanner that stopped matching declarations would produce a "+
				"document that agrees with itself and says nothing.\n",
			len(interfaces), minInterfaces)
		return gate.Red
	}
	expected := render(g)
	actualBytes, err := os.ReadFile(out)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		return gate.Broken
	}
	actual := string(actualBytes)
	if expected == actual {
		fmt.Printf("OK: %s matches the tree (%d files, %d seams)\n",
			filepath.Base(out), len(g.consumerFiles), len(interfaces))
		return gate.Green
	}
	fmt.Printf("FAIL: %s is stale: the tree changed since it was generated.\n", filepath.Base(out))
	expLines := strings.Split(expected, "\n")
	actLines := strings.Split(actual, "\n")
	for i := 0; i < len(expLines) && i < len(actLines); i++ {
		if expLines[i] != actLines[i] {
			fmt.Printf("  first difference at line %d:\n", i+1)
			fmt.Printf("    committed: %s\n", actLines[i])
			fmt.Printf("    generated: %s\n", expLines[i])
			break
		}
	}
	if len(expLines) != len(actLines) {
		fmt.Printf("  line count differs: committed %d, generated %d\n", len(actLines), len(expLines))
	}
	fmt.Println("Run ./tools/build-gates.sh and commit the regenerated graph.")
	return gate.Red
}

// reportNumbers prints the census.
func reportNumbers(repo string) int {
	g := newGraph(repo)
	axes := g.axes()
	var interfaces []*typeDecl
	for _, d := range g.decls {
		if d.kind == "interface" && strings.HasPrefix(d.rel(), "src/") {
			interfaces = append(interfaces, d)
		}
	}
	implementsEdges, extendsEdges := 0, 0
	for _, e := range g.edges {
		if e.kind == "implements" {
			implementsEdges++
		} else {
			extendsEdges++
		}
	}
	fmt.Printf("files scanned      : %d\n", len(g.consumerFiles))
	fmt.Printf("types declared     : %d\n", len(g.decls))
	fmt.Printf("interfaces in src/ : %d\n", len(interfaces))
	fmt.Printf("implements edges   : %d\n", implementsEdges)
	fmt.Printf("extends edges      : %d\n", extendsEdges)
	fmt.Printf("DI registrations   : %d\n", len(g.registrations))
	fmt.Printf("bypass sites       : %d\n", len(g.bypasses))
	fmt.Printf("axes               : %d\n", len(axes))
	for _, ax := range axes {
		fmt.Printf("  %-22s %-22s\n", ax.marker, ax.door)
	}
	fmt.Println("")
	fmt.Println("declared route carriage (the product's declaration vs. the door body):")
	for _, row := range g.routeRows {
		mark := "ok"
		if !row.agrees() {
			mark = "MISMATCH"
		}
		fmt.Printf("  %-12s %-24s declared=%-11s observed=%-10s %s\n",
			row.route, row.door, row.declared, row.observed, mark)
	}
	fmt.Println("")
	fmt.Println("every seam in src/, with its in-tree implementations and consumers:")
	type seamCount struct {
		score     int
		name      string
		product   int
		consumers int
	}
	var rows []seamCount
	for _, decl := range interfaces {
		impls := g.implementers(decl.name)
		consumers := g.consumers(decl.name)
		product := 0
		for _, i := range impls {
			if isProduct(i.rel()) {
				product++
			}
		}
		rows = append(rows, seamCount{product + len(consumers), decl.name, product, len(consumers)})
	}
	sort.SliceStable(rows, func(i, j int) bool {
		if rows[i].score != rows[j].score {
			return rows[i].score > rows[j].score
		}
		return rows[i].name < rows[j].name
	})
	for _, r := range rows {
		fmt.Printf("  %-40s impls=%-4d consumers=%d\n", r.name, r.product, r.consumers)
	}
	return gate.Green
}
