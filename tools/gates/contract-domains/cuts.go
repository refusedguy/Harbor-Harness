package contractdomains

import (
	"fmt"
	"sort"
	"strings"
)

// # THE CUT
//
// A domain can only move into an assembly of its own if the cut is CLEAN.
// A cut is clean when neither side depends on the other:
//
//   - the domain's types must not name a type that stays in the root, or
//     the new assembly has to reference the root -- and then the new
//     assembly's radius is AT LEAST the root's radius, so the move buys
//     nothing;
//   - the types that stay in the root must not name a type of the domain,
//     or the root has to reference the new assembly -- and then the ROOT's
//     radius grows by the domain's consumers.
//
// A cut that is not clean moves the cost around; it does not remove it.
// This is the measurement that decides the issue, and it is why "split the
// root into N projects" is not the answer the numbers give.
//
// The dependency is a NAME in a file: the scan cannot tell a type
// reference from a member of the same name, so every dependency below is a
// CANDIDATE. The verdict is stated in those terms.

// dependencyCheck is what a domain's cut looks like.
type dependencyCheck struct {
	// Domain is the domain's name.
	Domain string `json:"domain"`
	// Types is how many types the domain holds.
	Types int `json:"types"`
	// NamesCore is the number of the types that stay in the root which
	// the domain's files name.
	NamesCore int `json:"names_core"`
	// NamedByCore is the number of the domain's types which the files
	// that stay in the root name.
	NamedByCore int `json:"named_by_core"`
	// Verdict is what the numbers mean.
	Verdict string `json:"verdict"`
	// Separable says whether the domain can move out as it is.
	Separable bool `json:"separable"`
}

// checkCuts measures, for every domain, whether it can move out.
//
// The domain moves out ALONE: everything else stays in the root. So the
// test is against the rest of the root, not against one other domain.
func checkCuts(census *typeCensus, measured *tree, root string, domains []*domain) []dependencyCheck {
	out := []dependencyCheck{}
	if len(domains) == 0 {
		return out
	}
	proj, ok := measured.ByPath[root]
	if !ok {
		return out
	}
	allTypes := map[string]bool{}
	for key := range census.Types {
		if strings.HasPrefix(key, root+"|") {
			allTypes[strings.TrimPrefix(key, root+"|")] = true
		}
	}
	for _, d := range domains {
		rest := map[string]bool{}
		for name := range allTypes {
			rest[name] = true
		}
		for _, name := range d.Types {
			delete(rest, name)
		}
		namesRest := 0
		for file := range d.Files {
			for _, source := range proj.Files {
				if source.Path != file {
					continue
				}
				declared := map[string]bool{}
				for _, decl := range declsIn(source, proj.Path) {
					declared[decl.Name] = true
				}
				for ident := range source.Idents {
					if declared[ident] {
						continue
					}
					if rest[ident] {
						namesRest++
					}
				}
			}
		}
		namedByRest := 0
		domainTypes := map[string]bool{}
		for _, name := range d.Types {
			domainTypes[name] = true
		}
		for _, source := range proj.Files {
			if domainTypes[source.Path] {
				continue
			}
			declared := map[string]bool{}
			for _, decl := range declsIn(source, proj.Path) {
				declared[decl.Name] = true
			}
			for ident := range source.Idents {
				if declared[ident] {
					continue
				}
				if domainTypes[ident] {
					namedByRest++
				}
			}
		}
		check := dependencyCheck{
			Domain:      d.Name,
			Types:       len(d.Types),
			NamesCore:   namesRest,
			NamedByCore: namedByRest,
		}
		switch {
		case namesRest > 0:
			check.Verdict = fmt.Sprintf(
				"NOT separable: the domain names %d type(s) that stay in the root, so its assembly would reference the root and inherit the root's radius",
				namesRest)
		case namedByRest > 0:
			check.Verdict = fmt.Sprintf(
				"NOT separable: %d type(s) that stay in the root name the domain, so the root would reference the new assembly and the root's radius would grow",
				namedByRest)
		default:
			check.Verdict = "separable: neither side names the other, so the new assembly is a leaf with a radius of its own"
			check.Separable = true
		}
		out = append(out, check)
	}
	return out
}

// printCuts prints the cut analysis.
func printCuts(root string, checks []dependencyCheck) {
	fmt.Printf("\n[cuts] %s: can the domains move out?\n", root)
	fmt.Printf("  a cut is clean when neither side names the other. A domain that names the core\n")
	fmt.Printf("  would sit BELOW the root and inherit its radius; a domain the core names would\n")
	fmt.Printf("  sit ABOVE it and grow the root's radius. Both move the cost; neither removes it.\n")
	fmt.Printf("  %-34s %6s %10s %12s  %s\n", "domain", "types", "names rest", "named by rest", "verdict")
	separable := 0
	separableTypes := 0
	for _, check := range checks {
		fmt.Printf("  %-34s %6d %10d %12d  %s\n",
			truncate(check.Domain, 34), check.Types, check.NamesCore, check.NamedByCore, check.Verdict)
		if check.Separable {
			separable++
			separableTypes += check.Types
		}
	}
	fmt.Printf("  separable domains: %d of %d, holding %d types\n", separable, len(checks), separableTypes)
}

// folderGroup is one group of types by the directory they live in.
type folderGroup struct {
	// Folder is the directory.
	Folder string `json:"folder"`
	// Types are the types in it.
	Types []string `json:"types"`
	// Consumers are the projects that use at least one of them.
	Consumers map[string]bool `json:"consumers"`
	// Spread is how many distinct consumer sets the types of the folder
	// have: a folder whose types have many different consumer sets is not
	// a domain, whatever its name says.
	Spread int `json:"spread"`
	// Widest is the largest consumer set inside the folder.
	Widest int `json:"widest"`
	// Narrowest is the smallest one.
	Narrowest int `json:"narrowest"`
}

// folders groups the types of a root by the directory they are declared in.
//
// The issue's hypothesis is that the root divides into 3-4 projects "by
// its directories". This measures what those directories hold: a folder
// whose types have many different consumer sets is not a domain, and
// cutting it into an assembly does not narrow anything.
func folders(census *typeCensus, root string) []folderGroup {
	byFolder := map[string]map[string]bool{}
	for key, decl := range census.Types {
		if !strings.HasPrefix(key, root+"|") {
			continue
		}
		folder := folderOf(decl.File)
		if byFolder[folder] == nil {
			byFolder[folder] = map[string]bool{}
		}
		byFolder[folder][decl.Name] = true
	}
	out := []folderGroup{}
	for folder, types := range byFolder {
		group := folderGroup{Folder: folder, Types: sortedKeys(types)}
		sets := map[string]bool{}
		for name := range types {
			for consumer := range census.Consumers[root+"|"+name] {
				sets[consumer] = true
			}
		}
		group.Consumers = sets
		group.Spread = len(distinctConsumerSets(census, root, types))
		group.Widest = 0
		group.Narrowest = 1 << 30
		for _, set := range distinctConsumerSets(census, root, types) {
			if len(set) > group.Widest {
				group.Widest = len(set)
			}
			if len(set) < group.Narrowest {
				group.Narrowest = len(set)
			}
		}
		out = append(out, group)
	}
	sort.Slice(out, func(i, j int) bool {
		if len(out[i].Consumers) != len(out[j].Consumers) {
			return len(out[i].Consumers) > len(out[j].Consumers)
		}
		return out[i].Folder < out[j].Folder
	})
	return out
}

// folderOf is the directory of a file, relative to its project's directory.
func folderOf(path string) string {
	index := strings.LastIndex(path, "/")
	if index < 0 {
		return "."
	}
	return path[:index]
}

// distinctConsumerSets returns the consumer sets of a set of types.
func distinctConsumerSets(census *typeCensus, root string, types map[string]bool) []map[string]bool {
	seen := map[string]bool{}
	out := []map[string]bool{}
	for name := range types {
		consumers := census.Consumers[root+"|"+name]
		key := setSignature(consumers)
		if seen[key] {
			continue
		}
		seen[key] = true
		out = append(out, consumers)
	}
	sort.Slice(out, func(i, j int) bool { return len(out[i]) > len(out[j]) })
	return out
}

// printFolders prints the folder division and the verdict on it.
func printFolders(root string, groups []folderGroup) {
	fmt.Printf("\n[folders] %s: the division by directory, and why it is not a division by domain\n", root)
	fmt.Printf("  %-40s %6s %8s %8s %8s\n", "folder", "types", "users", "widest", "spread")
	for _, group := range groups {
		fmt.Printf("  %-40s %6d %8d %8d %8d\n",
			truncate(group.Folder, 40), len(group.Types), len(group.Consumers), group.Widest, group.Spread)
	}
	fmt.Printf("  'spread' is how many DIFFERENT consumer sets the types of the folder have.\n")
	fmt.Printf("  A folder with a spread of 1 is a domain; a folder with a spread of many is a\n")
	fmt.Printf("  DIRECTORY, and cutting it into an assembly moves the cost without removing it.\n")
}
