package contractdomains

import (
	"fmt"
	"sort"
	"strings"
)

// # THE LEVER
//
// The radius of a root is held by its DIRECT referencers: every project
// that reaches the root through one of them inherits the root's radius.
//
// The measurement asks the question the issue asks -- "if a group of types
// moves out, how many projects stop depending on the root" -- and answers
// it by taking each direct referencer away and measuring what is left:
//
//	lever: the edge that holds the radius
//
// This is the number that decides the whole issue. If ONE referencer holds
// the entire radius of the root, then moving types out of the root does not
// shrink the root's radius at all: the referencer still references it, and
// everything the referencer serves still rebuilds.
//
// What the cut buys is then not a smaller root, but CHEAPER FILES: the
// types of the narrow domains stop costing the whole tree.

// leverRow is one referencer's hold on the root's radius.
type leverRow struct {
	// Referencer is the project that references the root.
	Referencer string `json:"referencer"`
	// Direct is the number of the root's direct referencers.
	Direct int `json:"direct"`
	// RadiusWithout is the root's radius with this edge taken away.
	RadiusWithout int `json:"radius_without"`
	// Dropped is how many projects leave the radius with this edge gone.
	Dropped int `json:"dropped"`
	// TypesUsed is how many of the root's types the referencer names.
	TypesUsed int `json:"types_used"`
	// DomainsUsed is how many of the root's domains the referencer names.
	DomainsUsed int `json:"domains_used"`
}

// lever is the analysis of the root's radius.
type lever struct {
	// Root is the root the analysis belongs to.
	Root string `json:"root"`
	// Radius is the root's radius today.
	Radius int `json:"radius"`
	// Rows are the direct referencers, the one that holds the most
	// first.
	Rows []leverRow `json:"rows"`
}

// measureLever measures which referencer holds the root's radius.
func measureLever(root string, measured *tree, census *typeCensus, domains []*domain) lever {
	out := lever{Root: root}
	references := map[string][]string{}
	for _, proj := range measured.Projects {
		references[proj.Path] = proj.References
	}
	out.Radius = len(transitiveConsumers(root, references))
	for _, proj := range measured.Projects {
		referencesToRoot := false
		for _, reference := range proj.References {
			if reference == root {
				referencesToRoot = true
			}
		}
		if !referencesToRoot {
			continue
		}
		without := map[string][]string{}
		for path, refs := range references {
			if path != proj.Path {
				without[path] = refs
				continue
			}
			kept := []string{}
			for _, reference := range refs {
				if reference != root {
					kept = append(kept, reference)
				}
			}
			without[path] = kept
		}
		radiusWithout := len(transitiveConsumers(root, without))
		typesUsed := 0
		for key, consumers := range census.Consumers {
			if !strings.HasPrefix(key, root+"|") {
				continue
			}
			if consumers[proj.Path] {
				typesUsed++
			}
		}
		domainsUsed := 0
		for _, d := range domains {
			if domainUsedBy(census, &partition{Root: root}, d, proj.Path) {
				domainsUsed++
			}
		}
		out.Rows = append(out.Rows, leverRow{
			Referencer:    proj.Path,
			Direct:        out.Radius,
			RadiusWithout: radiusWithout,
			Dropped:       out.Radius - radiusWithout,
			TypesUsed:     typesUsed,
			DomainsUsed:   domainsUsed,
		})
	}
	sort.Slice(out.Rows, func(i, j int) bool {
		if out.Rows[i].Dropped != out.Rows[j].Dropped {
			return out.Rows[i].Dropped > out.Rows[j].Dropped
		}
		return out.Rows[i].Referencer < out.Rows[j].Referencer
	})
	return out
}

// printLever prints the lever analysis.
func printLever(l lever) {
	fmt.Printf("\n[lever] %s: the radius is %d projects\n", l.Root, l.Radius)
	fmt.Printf("  the radius is held by the root's direct referencers: every project that reaches the root\n")
	fmt.Printf("  through one of them inherits the radius. The row is what is left when that one edge is gone.\n")
	fmt.Printf("  %-56s %8s %10s %8s %6s %8s\n", "referencer", "types", "radius w/o", "dropped", "domains", "direct")
	for _, row := range l.Rows {
		fmt.Printf("  %-56s %8d %10d %8d %6d %8d\n",
			shortProject(row.Referencer), row.TypesUsed, row.RadiusWithout, row.Dropped, row.DomainsUsed, row.Direct)
	}
	if len(l.Rows) > 0 && l.Rows[0].Dropped == l.Radius {
		fmt.Printf("  ONE referencer holds the whole radius: %s.\n", shortProject(l.Rows[0].Referencer))
		fmt.Printf("  Moving types OUT of the root does not shrink the root's radius while that edge stays:\n")
		fmt.Printf("  the referencer still references the root, and everything it serves still rebuilds.\n")
		fmt.Printf("  What the cut buys is CHEAPER FILES -- the types of the narrow domains stop costing\n")
		fmt.Printf("  the whole tree -- and the root's own size shrinks.\n")
	}
}
