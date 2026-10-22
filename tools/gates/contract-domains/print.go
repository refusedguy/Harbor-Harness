package contractdomains

import (
	"fmt"
	"os"
	"sort"
	"strings"
)

// printReport prints the measurement.
//
// The sections are the ones the issue asks for, in the order a reader
// needs them: the map, the domains, the radius, the price, the table.
func printReport(r *report) {
	fmt.Printf("contract-domains -- who uses the contract roots, and what cutting them costs\n")
	fmt.Printf("solution: %v | projects: %d | jaccard floor: %.2f | universal threshold: %.2f\n",
		r.Options["solution"], r.Projects, r.Options["jaccard"], r.Options["universal"])
	if !r.HasBaseline {
		fmt.Printf("NOTE: the compile-cost baseline is not in this tree, so the PRICES are not measured\n")
		fmt.Printf("      here: the consumer COUNTS are. The baseline lands with the compile-cost\n")
		fmt.Printf("      instrument (#2112); re-run this gate on a tree that has it for the prices.\n")
	}
	for _, root := range r.Roots {
		printRoot(root)
	}
	for _, l := range r.Levers {
		printLever(l)
	}
	for _, root := range r.Roots {
		printCuts(root.Root, r.Cuts[root.Root])
		printFolders(root.Root, r.Folders[root.Root])
	}
	if len(r.Ambiguous) > 0 {
		fmt.Printf("\n[ambiguous]\n")
		fmt.Printf("  %d names are declared in two places; the mention is resolved per file by the\n", len(r.Ambiguous))
		fmt.Printf("  namespaces the file imports, and a file that imports both is reported, not counted.\n")
		for _, item := range r.Ambiguous {
			fmt.Printf("  %-22s %s\n", item.Name, strings.Join(item.Sites, " | "))
			for _, file := range item.Files {
				fmt.Printf("      unresolved mention: %s\n", file)
			}
		}
	}
	printCost(r.Cost)
	printPlan(r.Plan, r.HasBaseline)
}

// printRoot prints one root: its census, its domains and its radius.
func printRoot(root rootReport) {
	fmt.Printf("\n============================================================\n")
	fmt.Printf("[census] %s\n", root.Root)
	fmt.Printf("  types: %d | files: %d | lines: %d\n", root.Types, root.Files, root.Lines)
	fmt.Printf("  direct consumers: %d | transitive consumers: %d | radius price: %.1f s\n",
		root.DirectConsumers, root.TransitiveConsumers, root.RadiusSeconds)
	fmt.Printf("\n[domains] %s\n", root.Root)
	fmt.Printf("  %-34s %5s %6s %8s %9s  %s\n", "domain", "types", "users", "groups", "jaccard", "verdict")
	for _, d := range root.Domains {
		fmt.Printf("  %-34s %5d %6d %8d %9.2f  %s\n",
			truncate(d.Name, 34), len(d.Types), d.ConsumerCount, d.Groups, d.WeakestJaccard, d.Verdict)
	}
	fmt.Printf("\n  the types of every domain:\n")
	for _, d := range root.Domains {
		fmt.Printf("  %s (%d types, %d projects, %d files, %d lines)\n",
			d.Name, len(d.Types), d.ConsumerCount, d.Files, d.Lines)
		fmt.Printf("    namespaces: %s\n", strings.Join(d.Namespaces, ", "))
		fmt.Printf("    types: %s\n", strings.Join(d.Types, ", "))
	}
}

// printCost prints the price of one more assembly.
func printCost(c costReport) {
	fmt.Printf("\n[cost] what one more assembly costs\n")
	fmt.Printf("  layer matrix in docs/ARCHITECTURE_LAYERS.md : %d lines, +1 line per assembly\n", c.LayerMatrixLines)
	fmt.Printf("  layer table in FullLayerMatrixTests.cs      : %d rows, +1 row per assembly\n", c.LayerMatrixRows)
	fmt.Printf("  assembly lists in the layer rules            : %d lists, %d entries, +1 entry per list\n",
		c.LayerRuleLists, c.LayerRuleEntries)
	fmt.Printf("  tools/ci-project-lists.json                  : %d projects, and it names %s\n",
		c.CIListProjects, ciListVerdict(c))
	fmt.Printf("  frozen public-API rows of the two roots      : %d rows, and the moved types take theirs with them\n",
		c.PublicApiRows)
	fmt.Printf("  product assemblies the tree has              : %d\n", c.ProductAssemblies)
	fmt.Printf("  architecture sweep                           : %d compilations, %.1f s a pass, %d passes\n",
		c.SweepCompilations, c.SweepSeconds, c.SweepPasses)
	fmt.Printf("  one more assembly                            : +1 compilation in the sweep = %.2f s a pass, %.2f s over %d passes\n",
		c.SweepSeconds/float64(max(1, c.SweepCompilations)), c.SweepSeconds/float64(max(1, c.SweepCompilations))*float64(c.SweepPasses), c.SweepPasses)
}

// ciListVerdict says what the CI project lists would need.
func ciListVerdict(c costReport) string {
	if c.CIListHoldsProduct {
		return "product assemblies too, so +1 row"
	}
	return "TEST projects only, so a new product assembly needs NO row (a new TEST project for it would)"
}

// printPlan prints the "N domains" table.
func printPlan(rows []planRow, hasBaseline bool) {
	fmt.Printf("\n[plan] N domains | radius | price | win against N-1\n")
	last := map[string]int{}
	for _, row := range rows {
		previous, seen := last[row.Root]
		win := "-"
		if seen {
			win = fmt.Sprintf("%+d projects (worst radius)", row.WorstRadius-previous)
		}
		last[row.Root] = row.WorstRadius
		price := "n/a"
		if hasBaseline {
			price = fmt.Sprintf("~%.1f s", row.RadiusSeconds)
		}
		fmt.Printf("  %-28s %-42s N=%2d | new assemblies %2d | root radius %3d -> %3d | worst radius %3d | median %3d | %s\n",
			shortProject(row.Root), row.Kind, row.N, row.NewAssemblies, row.RootRadiusBefore, row.RootRadiusAfter,
			row.WorstRadius, row.MedianRadius, price)
		fmt.Printf("      cheap types %d of %d | +%d ProjectReference edges | layer rows +%d | public-API rows moved %d | consumer files %d | sweep +%d compilations (~%.2f s) | win %s\n",
			row.CheapTypes, row.TotalTypes, row.NewProjectReferences, row.LayerMatrixRows, row.PublicApiRows,
			row.UsingFiles, row.SweepCompilations, row.SweepSeconds, win)
		if len(row.Gained) > 0 {
			fmt.Printf("      the cut ADDS %d projects to the root's radius: %s\n",
				len(row.Gained), strings.Join(row.Gained, ", "))
		}
	}
	fmt.Printf("\n  'cheap types' are the types whose change rebuilds FEWER projects than the root's\n")
	fmt.Printf("  radius today. 'consumer files' is the number of files that name a moved type: it is\n")
	fmt.Printf("  the churn if a moved domain gets a namespace of its own, and ZERO if the\n")
	fmt.Printf("  namespaces stay where they are (which is what the contract split did, ADR-007).\n")
	fmt.Printf("  The radius prices are PROJECTIONS for the own cost of an assembly that does not\n")
	fmt.Printf("  exist yet (the root's measured seconds divided among the domains by their\n")
	fmt.Printf("  lines); the consumer counts and the consumers' seconds are measured.\n")
}

// snapshot renders the census as a stable text: the "before" of a split
// and the "after" of it, comparable.
func (r *report) snapshot() string {
	var out strings.Builder
	out.WriteString("# contract-domains snapshot -- the map of who uses the contract roots.\n")
	out.WriteString("# Generated by harbor-gates contract-domains. Re-runnable: the same command\n")
	out.WriteString("# before a split and after it says what the split bought.\n")
	fmt.Fprintf(&out, "projects %d\n", r.Projects)
	for _, root := range r.Roots {
		fmt.Fprintf(&out, "root %s types %d files %d lines %d direct %d transitive %d\n",
			root.Root, root.Types, root.Files, root.Lines, root.DirectConsumers, root.TransitiveConsumers)
		rows := make([]domainReport, len(root.Domains))
		copy(rows, root.Domains)
		sort.Slice(rows, func(i, j int) bool { return rows[i].Name < rows[j].Name })
		for _, d := range rows {
			fmt.Fprintf(&out, "domain %s types %d consumers %d files %d lines %d namespaces %s\n",
				d.Name, len(d.Types), d.ConsumerCount, d.Files, d.Lines, strings.Join(d.Namespaces, ","))
			types := append([]string{}, d.Types...)
			sort.Strings(types)
			for _, name := range types {
				fmt.Fprintf(&out, "  type %s\n", name)
			}
		}
	}
	return out.String()
}

// truncate shortens a string to a width.
func truncate(text string, width int) string {
	if len(text) <= width {
		return text
	}
	return text[:width-1] + "…"
}

// fail prints a failure and returns the broken exit code.
func fail(err error) int {
	fmt.Fprintln(os.Stderr, err)
	return 2
}
