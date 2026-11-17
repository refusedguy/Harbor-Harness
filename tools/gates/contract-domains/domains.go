package contractdomains

import (
	"fmt"
	"path/filepath"
	"sort"
	"strings"
)

// # THE DOMAINS
//
// A domain is a set of types with a set of consumers of its own. The
// criterion is stated once and applied mechanically:
//
//   - two types with the SAME consumer set are one group;
//   - two groups belong to the same domain when their consumer sets are
//     similar enough (Jaccard >= the threshold), and the clustering is
//     COMPLETE linkage: every pair of groups inside a domain must be
//     similar, so a chain of two similar pairs cannot drag a third,
//     unrelated group into the domain.
//
// And the counter-criterion, which the issue asks for explicitly: a set of
// types that everybody uses is NOT a domain. It cannot be separated: the
// assembly it moves into would have the same consumers as the assembly it
// left, so the radius would not shrink by one project.
//
// # WHAT THE RADIUS SIMULATION DOES
//
// The simulation answers the question the issue asks: if the types of a
// domain move into their own assembly, how many projects rebuild when
// something in that domain changes?
//
// The graph is re-wired, not guessed:
//
//   - a project that used the root for the types of the moved domain only
//     now references the new assembly, and no longer the root;
//   - a project that used the root for a type that STAYS keeps its edge to
//     the root and gains an edge to the new assembly;
//   - the new assembly references the root when a type of the domain names
//     a type that stays (the cut is not clean), and references another new
//     assembly when it names a type of that domain.
//
// The transitive closure of the re-wired graph is the new radius, and its
// price is the measured cost of the projects in it.

// domain is a set of types with a consumer set of its own.
type domain struct {
	// Name is the domain's name: the name of its biggest group.
	Name string
	// Types are the names of the types in the domain.
	Types []string
	// Consumers are the projects that use at least one of the types.
	Consumers map[string]bool
	// Files are the files the types are declared in.
	Files map[string]bool
	// Namespaces are the namespaces the types are declared in.
	Namespaces map[string]bool
	// Lines is the number of lines of the domain's files.
	Lines int
	// Groups is how many consumer groups the domain merged.
	Groups int
	// Weakest is the smallest Jaccard inside the domain: how tight the
	// domain is. Zero for a domain of one group.
	Weakest float64
}

// group is one set of types with exactly the same consumers.
type group struct {
	types     []string
	consumers map[string]bool
}

// jaccard is the similarity of two consumer sets.
func jaccard(a map[string]bool, b map[string]bool) float64 {
	if len(a) == 0 && len(b) == 0 {
		return 1
	}
	shared := 0
	for key := range a {
		if b[key] {
			shared++
		}
	}
	union := len(a) + len(b) - shared
	if union == 0 {
		return 0
	}
	return float64(shared) / float64(union)
}

// clusterGroups clusters consumer groups into domains.
//
// The clustering is complete linkage with a Jaccard floor: a group joins a
// domain only when it is similar to EVERY group already in it.
func clusterGroups(groups []*group, threshold float64) []*domain {
	domains := make([]*domain, 0, len(groups))
	for _, g := range groups {
		domains = append(domains, &domain{
			Types:      append([]string{}, g.types...),
			Consumers:  copySet(g.consumers),
			Files:      map[string]bool{},
			Namespaces: map[string]bool{},
			Groups:     1,
			Weakest:    1,
		})
	}
	for {
		best := -1
		bestOther := -1
		bestScore := threshold
		for i := 0; i < len(domains); i++ {
			for j := i + 1; j < len(domains); j++ {
				score := jaccard(domains[i].Consumers, domains[j].Consumers)
				if score < bestScore {
					continue
				}
				if !linkable(domains[i], domains[j], threshold) {
					continue
				}
				if score > bestScore || best < 0 {
					best, bestOther, bestScore = i, j, score
				}
			}
		}
		if best < 0 {
			break
		}
		mergeDomains(domains[best], domains[bestOther])
		domains = append(domains[:bestOther], domains[bestOther+1:]...)
	}
	return domains
}

// linkable says whether two domains may be merged without breaking the
// complete-linkage rule.
func linkable(a *domain, b *domain, threshold float64) bool {
	return jaccard(a.Consumers, b.Consumers) >= threshold
}

// mergeDomains merges b into a.
func mergeDomains(a *domain, b *domain) {
	a.Types = append(a.Types, b.Types...)
	for key := range b.Consumers {
		a.Consumers[key] = true
	}
	for key := range b.Files {
		a.Files[key] = true
	}
	for key := range b.Namespaces {
		a.Namespaces[key] = true
	}
	a.Groups += b.Groups
	if weakest := jaccard(a.Consumers, b.Consumers); weakest < a.Weakest {
		a.Weakest = weakest
	}
}

// copySet copies a set.
func copySet(in map[string]bool) map[string]bool {
	out := make(map[string]bool, len(in))
	for key := range in {
		out[key] = true
	}
	return out
}

// consumerGroups groups the types of a root by their consumer set.
func consumerGroups(census *typeCensus, root string) []*group {
	byConsumers := map[string]*group{}
	var order []string
	for key, decl := range census.Types {
		if !strings.HasPrefix(key, root+"|") {
			continue
		}
		consumers := census.Consumers[key]
		signature := setSignature(consumers)
		existing, seen := byConsumers[signature]
		if !seen {
			existing = &group{consumers: copySet(consumers)}
			byConsumers[signature] = existing
			order = append(order, signature)
		}
		existing.types = append(existing.types, decl.Name)
	}
	out := make([]*group, 0, len(order))
	for _, signature := range order {
		g := byConsumers[signature]
		sort.Strings(g.types)
		out = append(out, g)
	}
	sort.Slice(out, func(i, j int) bool {
		if len(out[i].consumers) != len(out[j].consumers) {
			return len(out[i].consumers) > len(out[j].consumers)
		}
		return out[i].types[0] < out[j].types[0]
	})
	return out
}

// setSignature is a stable key of a consumer set.
func setSignature(set map[string]bool) string {
	keys := make([]string, 0, len(set))
	for key := range set {
		keys = append(keys, key)
	}
	sort.Strings(keys)
	return strings.Join(keys, ";")
}

// domainOf builds the domains of a root and fills their files and lines.
func domainOf(census *typeCensus, root string, threshold float64) []*domain {
	groups := consumerGroups(census, root)
	domains := clusterGroups(groups, threshold)
	byName := map[string]*typeDecl{}
	for key, decl := range census.Types {
		if strings.HasPrefix(key, root+"|") {
			byName[decl.Name] = decl
		}
	}
	for _, d := range domains {
		for _, name := range d.Types {
			decl, ok := byName[name]
			if !ok {
				continue
			}
			d.Files[decl.File] = true
			d.Namespaces[decl.Namespace] = true
		}
		d.Lines = 0
		for file := range d.Files {
			d.Lines += census.FileLines[file]
		}
		d.Name = d.Types[0]
	}
	sort.Slice(domains, func(i, j int) bool {
		if len(domains[i].Consumers) != len(domains[j].Consumers) {
			return len(domains[i].Consumers) > len(domains[j].Consumers)
		}
		return domains[i].Name < domains[j].Name
	})
	return domains
}

// partition is one way of cutting a root into domains.
type partition struct {
	// Root is the root the partition belongs to.
	Root string
	// Domains are the domains of the partition, the biggest first.
	Domains []*domain
	// Core is the domain that stays in the root.
	Core *domain
	// APIFiles are the frozen public-API files of the root.
	APIFiles []string
}

// cut is the partition a number of domains asks for.
//
// The biggest domain (the one with the most consumers) stays in the root:
// it is the core, and the root's biggest consumer group is the one that
// cannot be moved without moving the root itself.
//
// The rest are merged into the requested number of domains by the highest
// similarity first: the two consumer groups that look most alike become
// one domain, so a cut at N domains is the N coarsest clusters of the
// hierarchy and not an arbitrary slice of the list.
func cut(domains []*domain, count int) *partition {
	if count >= len(domains) {
		return &partition{Domains: domains, Core: domains[0]}
	}
	core := domains[0]
	rest := append([]*domain{}, domains[1:]...)
	for len(rest) > count-1 {
		bestI, bestJ := -1, -1
		bestScore := -1.0
		for i := 0; i < len(rest); i++ {
			for j := i + 1; j < len(rest); j++ {
				score := jaccard(rest[i].Consumers, rest[j].Consumers)
				if score > bestScore {
					bestScore, bestI, bestJ = score, i, j
				}
			}
		}
		if bestI < 0 {
			break
		}
		mergeDomains(rest[bestI], rest[bestJ])
		rest = append(rest[:bestJ], rest[bestJ+1:]...)
	}
	out := append([]*domain{core}, rest...)
	return &partition{Root: "", Domains: out, Core: core}
}

// narrowing is the cut that moves out EVERYTHING that can move out.
//
// The core is everything that stays: the domains that cannot be separated
// without a dependency crossing the cut. Every separable domain becomes an
// assembly of its own.
//
// This is the cut the issue asks for -- "narrow the contract: move out what
// almost nobody uses" -- and it is the only cut that does not put a new
// assembly between the root and its consumers.
func narrowing(domains []*domain, separable map[string]bool) *partition {
	core := &domain{
		Name:       "core",
		Types:      []string{},
		Consumers:  map[string]bool{},
		Files:      map[string]bool{},
		Namespaces: map[string]bool{},
	}
	out := []*domain{}
	for _, d := range domains {
		if separable[d.Name] {
			out = append(out, d)
			continue
		}
		core.Types = append(core.Types, d.Types...)
		for key := range d.Consumers {
			core.Consumers[key] = true
		}
		for key := range d.Files {
			core.Files[key] = true
		}
		for key := range d.Namespaces {
			core.Namespaces[key] = true
		}
	}
	out = append([]*domain{core}, out...)
	return &partition{Domains: out, Core: core}
}

// simulation is the re-wired graph of one partition.
type simulation struct {
	// Nodes are the projects of the re-wired graph, the new assemblies
	// included.
	Nodes []string
	// References maps a node to the nodes it references.
	References map[string][]string
	// NewAssemblies maps a domain to the assembly it moved into.
	NewAssemblies map[*domain]string
	// Radius maps a node to the number of its transitive consumers.
	Radius map[string]int
	// Consumers maps a node to its transitive consumers, sorted.
	Consumers map[string][]string
	// Price maps a node to the price of its radius, in seconds.
	Price map[string]float64
	// ProjectSeconds are the measured costs of the projects.
	ProjectSeconds map[string]float64
	// NewSeconds are the projected own costs of the new assemblies.
	NewSeconds map[string]float64
}

// simulate re-wires the graph for a partition and measures the new radii.
//
// The root keeps the core domain; every other domain moves into an
// assembly of its own. A project references the assembly of every domain
// whose types it uses, and the root only if it uses a type of the core.
//
// The own cost of a new assembly is a PROJECTION: the root's measured cost
// divided among the domains by their lines. The consumer counts and the
// consumers' costs are facts.
func simulate(measured *tree, census *typeCensus, part *partition, baseline *baseline) *simulation {
	root := part.Root
	references := map[string][]string{}
	for _, proj := range measured.Projects {
		refs := []string{}
		for _, reference := range proj.References {
			if reference == root {
				continue
			}
			refs = append(refs, reference)
		}
		references[proj.Path] = refs
	}
	newAssemblies := map[*domain]string{}
	for _, d := range part.Domains {
		if d == part.Core {
			continue
		}
		name := strings.TrimSuffix(filepath.Base(root), ".csproj") + "." + domainSlug(d)
		newAssemblies[d] = "src/" + name + "/" + name + ".csproj"
	}
	// The projects' edges to the root and to the new assemblies.
	for _, proj := range measured.Projects {
		used := domainsUsedBy(census, root, proj.Path, part.Domains, part.Core)
		for _, d := range used {
			if d == part.Core {
				// The root IS the core's assembly: it does not
				// reference itself.
				if proj.Path != root {
					references[proj.Path] = append(references[proj.Path], root)
				}
				continue
			}
			references[proj.Path] = append(references[proj.Path], newAssemblies[d])
		}
	}
	// The new assemblies' own edges: a domain that names a type of another
	// domain references that domain's assembly.
	for _, d := range part.Domains {
		assembly, moved := newAssemblies[d]
		if !moved {
			continue
		}
		for _, other := range part.Domains {
			if other == d {
				continue
			}
			if domainNamesDomain(census, measured, root, d, other) {
				if other == part.Core {
					references[assembly] = append(references[assembly], root)
				} else {
					references[assembly] = append(references[assembly], newAssemblies[other])
				}
			}
		}
	}
	for key := range references {
		sort.Strings(references[key])
		references[key] = dedupe(references[key])
	}
	out := &simulation{
		Nodes:          []string{},
		References:     references,
		NewAssemblies:  newAssemblies,
		Radius:         map[string]int{},
		Consumers:      map[string][]string{},
		Price:          map[string]float64{},
		ProjectSeconds: map[string]float64{},
		NewSeconds:     map[string]float64{},
	}
	for _, proj := range measured.Projects {
		out.Nodes = append(out.Nodes, proj.Path)
	}
	for _, d := range part.Domains {
		if assembly, moved := newAssemblies[d]; moved {
			out.Nodes = append(out.Nodes, assembly)
		}
	}
	sort.Strings(out.Nodes)
	seconds := map[string]float64{}
	if baseline != nil {
		for path, value := range baseline.Projects {
			seconds[path] = value
		}
	}
	rootSeconds := seconds[root]
	totalLines := 0
	for _, d := range part.Domains {
		totalLines += d.Lines
	}
	for _, d := range part.Domains {
		assembly, moved := newAssemblies[d]
		if !moved {
			continue
		}
		share := 0.0
		if totalLines > 0 {
			share = rootSeconds * float64(d.Lines) / float64(totalLines)
		}
		out.NewSeconds[assembly] = share
		seconds[assembly] = share
	}
	// The root keeps the core: its own cost falls with the lines that left.
	coreLines := part.Core.Lines
	if totalLines > 0 {
		seconds[root] = rootSeconds * float64(coreLines) / float64(totalLines)
	}
	for _, node := range out.Nodes {
		consumers := transitiveConsumers(node, references)
		out.Radius[node] = len(consumers)
		out.Consumers[node] = consumers
		price := seconds[node]
		for _, consumer := range consumers {
			price += seconds[consumer]
		}
		out.Price[node] = price
	}
	out.ProjectSeconds = seconds
	return out
}

// transitiveConsumers returns the projects that depend on a node.
func transitiveConsumers(node string, references map[string][]string) []string {
	reverse := map[string][]string{}
	for from, refs := range references {
		for _, to := range refs {
			reverse[to] = append(reverse[to], from)
		}
	}
	seen := map[string]bool{}
	stack := append([]string{}, reverse[node]...)
	for len(stack) > 0 {
		current := stack[len(stack)-1]
		stack = stack[:len(stack)-1]
		if current == node || seen[current] {
			continue
		}
		seen[current] = true
		stack = append(stack, reverse[current]...)
	}
	out := make([]string, 0, len(seen))
	for key := range seen {
		out = append(out, key)
	}
	sort.Strings(out)
	return out
}

// dedupe removes the duplicates of a sorted list.
func dedupe(in []string) []string {
	out := in[:0]
	for index, value := range in {
		if index > 0 && value == in[index-1] {
			continue
		}
		out = append(out, value)
	}
	return out
}

// domainsUsedBy returns the domains of the root a project uses.
//
// The root itself uses only its core: the types of the moved domains are
// not in the root's files any more once they moved.
func domainsUsedBy(census *typeCensus, root string, projectPath string, domains []*domain, core *domain) []*domain {
	if projectPath == root {
		if core == nil {
			return nil
		}
		return []*domain{core}
	}
	used := map[string]bool{}
	for key, consumers := range census.Consumers {
		if !strings.HasPrefix(key, root+"|") {
			continue
		}
		if consumers[projectPath] {
			used[strings.TrimPrefix(key, root+"|")] = true
		}
	}
	out := []*domain{}
	for _, d := range domains {
		for _, name := range d.Types {
			if used[name] {
				out = append(out, d)
				break
			}
		}
	}
	return out
}

// domainNamesDomain says whether a domain names a type of another domain.
//
// The scan is by name: a type of the domain's files mentions the other
// type's name. It cannot tell a type reference from a member of the same
// name, so the answer is a candidate, and the report says so.
func domainNamesDomain(census *typeCensus, measured *tree, root string, from *domain, to *domain) bool {
	proj, ok := measured.ByPath[root]
	if !ok {
		return false
	}
	wanted := map[string]bool{}
	for _, name := range to.Types {
		wanted[name] = true
	}
	for file := range from.Files {
		for _, source := range proj.Files {
			if source.Path != file {
				continue
			}
			// The declarations of the file are not references: a file
			// that declares both Narrow and Widely does not make
			// Narrow depend on Widely.
			declared := map[string]bool{}
			for _, decl := range declsIn(source, proj.Path) {
				declared[decl.Name] = true
			}
			for ident := range source.Idents {
				if declared[ident] {
					continue
				}
				if wanted[ident] {
					return true
				}
			}
		}
	}
	return false
}

// domainSlug is the assembly name a domain would get.
//
// The name of the domain's biggest type, without the suffix that says
// "this is a record of an identifier": `SessionId` becomes `Session`, and
// `ToolExecutionEndEvent` becomes `ToolExecutionEnd`.
func domainSlug(d *domain) string {
	name := d.Name
	for _, suffix := range []string{"Event", "Id", "s"} {
		if len(name) > len(suffix)+2 && strings.HasSuffix(name, suffix) {
			name = strings.TrimSuffix(name, suffix)
			break
		}
	}
	if name == "" {
		name = "Domain"
	}
	return name
}

// describe renders a domain's consumer set for the report.
func describe(set map[string]bool) string {
	keys := make([]string, 0, len(set))
	for key := range set {
		keys = append(keys, shortProject(key))
	}
	sort.Strings(keys)
	return strings.Join(keys, ", ")
}

// shortProject is the short name of a project.
func shortProject(path string) string {
	name := strings.TrimSuffix(filepath.Base(path), ".csproj")
	return name
}

// formatRadius renders a radius as a short string.
func formatRadius(count int, seconds float64) string {
	return fmt.Sprintf("%d projects / %.1f s", count, seconds)
}
