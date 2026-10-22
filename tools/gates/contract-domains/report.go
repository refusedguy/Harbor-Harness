package contractdomains

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
)

// # THE REPORT
//
// The report has five sections, and every number in it says what it is:
//
//	[census]   the type -> consumers map of both roots, and what is
//	           ambiguous in it
//	[domains]  the domains the map falls into, and the verdict on each:
//	           a domain, or NOT a domain (everybody uses it)
//	[radius]   the radius of every root today, and the radius of every
//	           domain after the cut, on the re-wired graph
//	[cost]     what one more assembly costs
//	[plan]     the table "N domains (3 / 5 / 8 / 12) | radius | cost |
//	           win against N-1"
//
// A number that was measured from the tree is printed as a plain number. A
// number that was PROJECTED (the own cost of an assembly that does not
// exist yet) is printed with a `~` in front of it, and the section says so.

// report is the whole measurement.
type report struct {
	// Projects is the number of projects of the solution.
	Projects int `json:"projects"`
	// Roots are the contract roots and what was measured about them.
	Roots []rootReport `json:"roots"`
	// Plan is the table of the cuts at 3, 5, 8 and 12 domains.
	Plan []planRow `json:"plan"`
	// Cost is the price of one more assembly.
	Cost costReport `json:"cost"`
	// Ambiguous are the names declared in two places and the files whose
	// mention could not be resolved.
	Ambiguous []ambiguityReport `json:"ambiguous"`
	// Levers are the analyses of which referencer holds each root's
	// radius.
	Levers []lever `json:"levers"`
	// Cuts are the analyses of which domains can move out, per root.
	Cuts map[string][]dependencyCheck `json:"cuts"`
	// Folders are the divisions by directory, and their spread.
	Folders map[string][]folderGroup `json:"folders"`
	// Options are the settings the measurement ran with.
	Options map[string]any `json:"options"`
	// HasBaseline says whether the compile-cost baseline was in the tree:
	// without it the prices are unavailable and the counts are not.
	HasBaseline bool `json:"has_baseline"`
}

// rootReport is what was measured about one root.
type rootReport struct {
	// Root is the project file of the root.
	Root string `json:"root"`
	// Types is the number of types the root declares.
	Types int `json:"types"`
	// Files and Lines are the size of the root.
	Files int `json:"files"`
	Lines int `json:"lines"`
	// DirectConsumers is the number of projects that reference the root
	// directly.
	DirectConsumers int `json:"direct_consumers"`
	// TransitiveConsumers is the number of projects that depend on the
	// root, directly or through another project.
	TransitiveConsumers int `json:"transitive_consumers"`
	// RadiusSeconds is the measured price of that radius.
	RadiusSeconds float64 `json:"radius_seconds"`
	// Domains are the domains the root's types fall into.
	Domains []domainReport `json:"domains"`
}

// domainReport is one domain.
type domainReport struct {
	// Name is the domain's name.
	Name string `json:"name"`
	// Types are the types in the domain.
	Types []string `json:"types"`
	// Consumers are the projects that use the domain.
	Consumers []string `json:"consumers"`
	// ConsumerCount is the number of those projects.
	ConsumerCount int `json:"consumer_count"`
	// Files and Lines are the size of the domain.
	Files int `json:"files"`
	Lines int `json:"lines"`
	// Namespaces are the namespaces the domain's types live in.
	Namespaces []string `json:"namespaces"`
	// Groups is how many consumer groups the domain merged.
	Groups int `json:"groups"`
	// WeakestJaccard is the smallest similarity inside the domain.
	WeakestJaccard float64 `json:"weakest_jaccard"`
	// Verdict is "domain" or "not a domain", and why.
	Verdict string `json:"verdict"`
	// Radius is the number of projects a change in the domain rebuilds,
	// after the domain moves into an assembly of its own.
	Radius int `json:"radius"`
	// RadiusSeconds is the price of that radius.
	RadiusSeconds float64 `json:"radius_seconds"`
	// OwnSeconds is the projected own cost of the new assembly.
	OwnSeconds float64 `json:"own_seconds"`
	// NamesTheCore says whether a type of the domain names a type that
	// stays in the root: the cut is not clean, and the new assembly must
	// reference the root.
	NamesTheCore bool `json:"names_the_core"`
	// ConsumerFiles is the number of files outside the root that name a
	// type of the domain: the upper bound of the `using` churn if the
	// domain got a namespace of its own.
	ConsumerFiles int `json:"consumer_files"`
}

// planRow is one row of the "N domains" table.
type planRow struct {
	// N is the number of domains the cut has.
	N int `json:"n"`
	// Kind says what the row is: a cut at N domains, or the narrowing
	// cut that moves out everything that can move out.
	Kind string `json:"kind"`
	// Root is the root the cut belongs to.
	Root string `json:"root"`
	// NewAssemblies is how many assemblies the cut adds.
	NewAssemblies int `json:"new_assemblies"`
	// RootRadiusBefore is the root's radius today.
	RootRadiusBefore int `json:"root_radius_before"`
	// RootRadiusAfter is the root's radius after the cut.
	RootRadiusAfter int `json:"root_radius_after"`
	// WorstRadius is the largest radius of the domains: the price of a
	// change in the worst file of the root.
	WorstRadius int `json:"worst_radius"`
	// MedianRadius is the median of the domains' radii.
	MedianRadius int `json:"median_radius"`
	// RadiusSeconds is the price of the worst radius.
	RadiusSeconds float64 `json:"radius_seconds"`
	// CheapTypes is how many of the root's types end up in a domain whose
	// radius is below the root's radius today.
	CheapTypes int `json:"cheap_types"`
	// TotalTypes is how many types the root declares.
	TotalTypes int `json:"total_types"`
	// NewProjectReferences is how many ProjectReference edges the cut adds.
	NewProjectReferences int `json:"new_project_references"`
	// LayerMatrixRows, LayerRuleRows, PublicApiRows and UsingFiles are the
	// price of the cut in the rows and the files it touches.
	LayerMatrixRows int `json:"layer_matrix_rows"`
	LayerRuleRows   int `json:"layer_rule_rows"`
	PublicApiRows   int `json:"public_api_rows"`
	UsingFiles      int `json:"using_files"`
	// SweepCompilations is how many more compilings the architecture
	// sweep makes.
	SweepCompilations int `json:"sweep_compilations"`
	// SweepSeconds is what those compilings cost, derived from the
	// measured sweep.
	SweepSeconds float64 `json:"sweep_seconds"`
	// Gained are the projects that the cut adds to the root's radius.
	Gained []string `json:"gained"`
}

// costReport is the price of one more assembly.
type costReport struct {
	// LayerMatrixLines is the number of lines the layer matrix in the
	// document has.
	LayerMatrixLines int `json:"layer_matrix_lines"`
	// LayerMatrixRows is the number of rows the table of the layer rules
	// has.
	LayerMatrixRows int `json:"layer_matrix_rows"`
	// LayerRuleLists is the number of assembly lists the layer rules
	// keep, and LayerRuleEntries how many entries they hold.
	LayerRuleLists   int `json:"layer_rule_lists"`
	LayerRuleEntries int `json:"layer_rule_entries"`
	// CIListProjects is the number of projects the CI project lists name.
	CIListProjects int `json:"ci_list_projects"`
	// CIListHoldsProduct tells whether the CI lists name product
	// assemblies at all.
	CIListHoldsProduct bool `json:"ci_list_holds_product"`
	// PublicApiRows is the number of frozen public-API rows of the roots.
	PublicApiRows int `json:"public_api_rows"`
	// ProductAssemblies is the number of product assemblies the tree has.
	ProductAssemblies int `json:"product_assemblies"`
	// SweepCompilations, SweepSeconds and SweepPasses are the cost of
	// the architecture sweep.
	SweepCompilations int     `json:"sweep_compilations"`
	SweepSeconds      float64 `json:"sweep_seconds"`
	SweepPasses       int     `json:"sweep_passes"`
}

// ambiguityReport is a name that the tree declares in two places.
type ambiguityReport struct {
	// Name is the type name.
	Name string `json:"name"`
	// Sites are the places it is declared.
	Sites []string `json:"sites"`
	// Files are the files whose mention could not be resolved.
	Files []string `json:"files"`
}

// buildReport measures the tree and assembles the report.
func buildReport(root string, measured *tree, census *typeCensus, baseline *baseline,
	opts options, roots []string) *report {
	if baseline == nil {
		baseline = newBaseline()
	}
	out := &report{
		Projects:    len(measured.Projects),
		Folders:     map[string][]folderGroup{},
		Cuts:        map[string][]dependencyCheck{},
		HasBaseline: len(baseline.Projects) > 0,
		Options: map[string]any{
			"solution":  opts.solution,
			"jaccard":   opts.jaccard,
			"universal": opts.universal,
		},
	}
	for name, files := range census.Ambiguous {
		if len(files) == 0 {
			continue
		}
		sites := []string{}
		for _, site := range census.Sites[name] {
			sites = append(sites, site.Project+" :: "+site.Namespace)
		}
		sort.Strings(sites)
		out.Ambiguous = append(out.Ambiguous, ambiguityReport{
			Name:  name,
			Sites: sites,
			Files: files,
		})
	}
	sort.Slice(out.Ambiguous, func(i, j int) bool { return out.Ambiguous[i].Name < out.Ambiguous[j].Name })

	out.Cost = measureCost(root, measured, opts)

	for _, rootPath := range roots {
		proj, ok := measured.ByPath[rootPath]
		if !ok {
			continue
		}
		domains := domainOf(census, rootPath, opts.jaccard)
		entry := rootReport{
			Root:  rootPath,
			Types: countTypes(census, rootPath),
		}
		for _, source := range proj.Files {
			entry.Files++
			entry.Lines += source.Lines
		}
		direct := 0
		for _, other := range measured.Projects {
			for _, reference := range other.References {
				if reference == rootPath {
					direct++
					break
				}
			}
		}
		entry.DirectConsumers = direct
		references := map[string][]string{}
		for _, other := range measured.Projects {
			references[other.Path] = other.References
		}
		consumers := transitiveConsumers(rootPath, references)
		entry.TransitiveConsumers = len(consumers)
		if baseline != nil {
			entry.RadiusSeconds = baseline.Radius[rootPath]
		}
		for _, d := range domains {
			row := domainReport{
				Name:           d.Name,
				Types:          d.Types,
				Consumers:      sortedKeys(d.Consumers),
				ConsumerCount:  len(d.Consumers),
				Files:          len(d.Files),
				Lines:          d.Lines,
				Namespaces:     sortedKeys(d.Namespaces),
				Groups:         d.Groups,
				WeakestJaccard: d.Weakest,
			}
			row.ConsumerFiles = consumerFiles(measured, rootPath, d)
			if float64(len(d.Consumers)) >= opts.universal*float64(len(measured.Projects)) {
				row.Verdict = fmt.Sprintf(
					"NOT a domain: %d of %d projects use it, so the assembly it would move into has the same consumers as the one it left",
					len(d.Consumers), len(measured.Projects))
			} else {
				row.Verdict = fmt.Sprintf(
					"domain: %d of %d projects use it, and they are its own", len(d.Consumers), len(measured.Projects))
			}
			entry.Domains = append(entry.Domains, row)
		}
		out.Roots = append(out.Roots, entry)
		out.Levers = append(out.Levers, measureLever(rootPath, measured, census, domains))
		out.Cuts[rootPath] = checkCuts(census, measured, rootPath, domains)
		out.Folders[rootPath] = folders(census, rootPath)
	}
	for _, rootPath := range roots {
		for _, count := range opts.plan {
			row, ok := planFor(measured, census, baseline, opts, rootPath, count, out.Cost)
			if ok {
				out.Plan = append(out.Plan, row)
			}
		}
		row, ok := narrowingPlan(measured, census, baseline, opts, rootPath, out.Cost, out.Cuts[rootPath])
		if ok {
			out.Plan = append(out.Plan, row)
		}
	}
	return out
}

// narrowingPlan is the row of the plan table that moves out everything
// that can move out.
func narrowingPlan(measured *tree, census *typeCensus, baseline *baseline,
	opts options, rootPath string, cost costReport, checks []dependencyCheck) (planRow, bool) {
	domains := domainOf(census, rootPath, opts.jaccard)
	if len(domains) == 0 {
		return planRow{}, false
	}
	separable := map[string]bool{}
	for _, check := range checks {
		if check.Separable {
			separable[check.Domain] = true
		}
	}
	part := narrowing(domains, separable)
	part.Root = rootPath
	part.APIFiles = apiFilesOf(rootPath)
	sim := simulate(measured, census, part, baseline)
	row := planRow{
		N:                 len(part.Domains) - 1,
		Kind:              "narrowing: everything that can move out",
		Root:              rootPath,
		NewAssemblies:     len(part.Domains) - 1,
		RootRadiusBefore:  graphRadius(measured, rootPath),
		RootRadiusAfter:   sim.Radius[rootPath],
		TotalTypes:        countTypes(census, rootPath),
		LayerMatrixRows:   cost.LayerMatrixRows + cost.LayerRuleEntries,
		LayerRuleRows:     cost.LayerRuleEntries,
		PublicApiRows:     publicAPIRows(rootPath, part),
		SweepCompilations: len(part.Domains) - 1,
		SweepSeconds:      float64(len(part.Domains)-1) * cost.SweepSeconds / float64(max(1, cost.SweepCompilations)),
	}
	radii := []int{}
	cheap := 0
	for _, d := range part.Domains {
		assembly := rootPath
		if d != part.Core {
			assembly = sim.NewAssemblies[d]
		}
		radius := sim.Radius[assembly]
		radii = append(radii, radius)
		price := sim.Price[assembly]
		if price > row.RadiusSeconds {
			row.RadiusSeconds = price
		}
		if radius < row.RootRadiusBefore {
			cheap += len(d.Types)
		}
	}
	sort.Ints(radii)
	row.WorstRadius = radii[len(radii)-1]
	row.MedianRadius = radii[len(radii)/2]
	row.CheapTypes = cheap
	row.NewProjectReferences = newReferenceEdges(measured, census, part)
	row.UsingFiles = usingFiles(measured, part)
	row.Gained = gainedConsumers(measured, rootPath, sim)
	return row, true
}

// gainedConsumers are the projects that are in the root's radius after the
// cut and were not in it before.
//
// A cut that moves a domain out can ADD projects to the root's radius: the
// new assembly has to reference the root (because the moved types name the
// types that stay), and then the root is in the new assembly's radius too.
func gainedConsumers(measured *tree, root string, sim *simulation) []string {
	before := map[string]bool{}
	references := map[string][]string{}
	for _, proj := range measured.Projects {
		references[proj.Path] = proj.References
	}
	for _, consumer := range transitiveConsumers(root, references) {
		before[consumer] = true
	}
	out := []string{}
	for _, consumer := range sim.Consumers[root] {
		if !before[consumer] {
			out = append(out, shortProject(consumer))
		}
	}
	sort.Strings(out)
	return out
}

// countTypes counts the types of a root.
func countTypes(census *typeCensus, root string) int {
	total := 0
	for key := range census.Types {
		if strings.HasPrefix(key, root+"|") {
			total++
		}
	}
	return total
}

// sortedKeys returns the sorted keys of a set.
func sortedKeys(set map[string]bool) []string {
	out := make([]string, 0, len(set))
	for key := range set {
		out = append(out, key)
	}
	sort.Strings(out)
	return out
}

// consumerFiles counts the files outside the root that name a type of the
// domain: the upper bound of the `using` churn if the domain got a
// namespace of its own.
func consumerFiles(measured *tree, root string, d *domain) int {
	names := map[string]bool{}
	for _, name := range d.Types {
		names[name] = true
	}
	count := 0
	for _, proj := range measured.Projects {
		if proj.Path == root {
			continue
		}
		for _, source := range proj.Files {
			for name := range names {
				if source.Idents[name] {
					count++
					break
				}
			}
		}
	}
	return count
}

// planFor computes the row of the "N domains" table for one root.
//
// The cut is made on the domains the clustering found: the biggest domain
// (the one everybody needs) stays in the root, and the narrow ones are
// merged until the partition has the requested number of domains.
func planFor(measured *tree, census *typeCensus, baseline *baseline,
	opts options, rootPath string, count int, cost costReport) (planRow, bool) {
	domains := domainOf(census, rootPath, opts.jaccard)
	if len(domains) == 0 {
		return planRow{}, false
	}
	part := cut(domains, count)
	part.Root = rootPath
	part.APIFiles = apiFilesOf(rootPath)
	sim := simulate(measured, census, part, baseline)
	row := planRow{
		N:                 count,
		Kind:              fmt.Sprintf("cut at %d domains", count),
		Root:              rootPath,
		NewAssemblies:     len(part.Domains) - 1,
		RootRadiusBefore:  graphRadius(measured, rootPath),
		RootRadiusAfter:   sim.Radius[rootPath],
		TotalTypes:        countTypes(census, rootPath),
		LayerMatrixRows:   cost.LayerMatrixRows + cost.LayerRuleEntries,
		LayerRuleRows:     cost.LayerRuleEntries,
		PublicApiRows:     publicAPIRows(rootPath, part),
		SweepCompilations: len(part.Domains) - 1,
		SweepSeconds:      float64(len(part.Domains)-1) * cost.SweepSeconds / float64(max(1, cost.SweepCompilations)),
	}
	// The domains' radii, and the worst of them.
	radii := []int{}
	cheap := 0
	for _, d := range part.Domains {
		assembly := rootPath
		if d != part.Core {
			assembly = sim.NewAssemblies[d]
		}
		radius := sim.Radius[assembly]
		radii = append(radii, radius)
		if radius < row.RootRadiusBefore {
			cheap += len(d.Types)
		}
	}
	sort.Ints(radii)
	row.WorstRadius = radii[len(radii)-1]
	row.MedianRadius = radii[len(radii)/2]
	row.RadiusSeconds = 0
	for _, d := range part.Domains {
		assembly := rootPath
		if d != part.Core {
			assembly = sim.NewAssemblies[d]
		}
		price := sim.Price[assembly]
		if price > row.RadiusSeconds {
			row.RadiusSeconds = price
		}
	}
	row.CheapTypes = cheap
	row.NewProjectReferences = newReferenceEdges(measured, census, part)
	row.UsingFiles = usingFiles(measured, part)
	row.Gained = gainedConsumers(measured, rootPath, sim)
	return row, true
}

// apiFilesOf are the frozen public-API files of a root.
func apiFilesOf(root string) []string {
	dir := filepath.Dir(root)
	return []string{
		filepath.Join(dir, "PublicAPI.Shipped.txt"),
		filepath.Join(dir, "PublicAPI.Unshipped.txt"),
	}
}

// graphRadius is the radius of a project in the tree as it stands.
//
// It is a FACT of the graph, not a number read from a baseline: the number
// of projects that depend on the project, directly or through another
// project. The recorded radius of the compile-cost baseline is the same
// number, measured on the day the baseline was taken; the graph is the
// current truth, and the gate must not need a baseline to say it.
func graphRadius(measured *tree, project string) int {
	references := map[string][]string{}
	for _, proj := range measured.Projects {
		references[proj.Path] = proj.References
	}
	return len(transitiveConsumers(project, references))
}

// newReferenceEdges counts the ProjectReference edges the cut adds.
func newReferenceEdges(measured *tree, census *typeCensus, part *partition) int {
	count := 0
	for _, proj := range measured.Projects {
		used := 0
		for _, d := range part.Domains {
			if domainUsedBy(census, part, d, proj.Path) {
				used++
			}
		}
		// One edge to the root before, one edge per domain after.
		had := false
		for _, reference := range proj.References {
			if reference == part.Root {
				had = true
			}
		}
		if !had {
			count += used
			continue
		}
		if used > 1 {
			count += used - 1
		}
	}
	return count
}

// domainUsedBy says whether a project uses a type of a domain.
func domainUsedBy(census *typeCensus, part *partition, d *domain, projectPath string) bool {
	for _, name := range d.Types {
		key := part.Root + "|" + name
		consumers, ok := census.Consumers[key]
		if !ok {
			continue
		}
		if consumers[projectPath] {
			return true
		}
	}
	return false
}

// usingFiles counts the consumer files that would need a new `using` if
// the domains of the cut got namespaces of their own.
//
// The namespaces of the moved types stay where they are in this tree (that
// is what the contract split did in ADR-007), so the churn is ZERO -- and
// this number is the price of NOT doing that.
func usingFiles(measured *tree, part *partition) int {
	count := 0
	for _, proj := range measured.Projects {
		if proj.Path == part.Root {
			continue
		}
		for _, source := range proj.Files {
			for _, d := range part.Domains {
				if d == part.Core {
					continue
				}
				for _, name := range d.Types {
					if source.Idents[name] {
						count++
						break
					}
				}
			}
		}
	}
	return count
}

// censusFor is gone: the helpers take the census they need.

// publicAPIRows counts the frozen public-API rows of the types that move
// out of the root.
//
// The rows are read from the root's own PublicAPI.Shipped.txt: a row names
// the type it belongs to, and a moved type takes its row with it.
func publicAPIRows(root string, part *partition) int {
	moved := map[string]bool{}
	for _, d := range part.Domains {
		if d == part.Core {
			continue
		}
		for _, name := range d.Types {
			moved[name] = true
		}
	}
	if len(moved) == 0 {
		return 0
	}
	pattern := regexp.MustCompile(`\b(` + strings.Join(sortedKeys(moved), "|") + `)\b`)
	count := 0
	for _, file := range part.APIFiles {
		data, err := os.ReadFile(file)
		if err != nil {
			continue
		}
		for _, line := range strings.Split(string(data), "\n") {
			if pattern.MatchString(line) {
				count++
			}
		}
	}
	return count
}

// max returns the bigger of two ints.
func max(a int, b int) int {
	if a > b {
		return a
	}
	return b
}

// isProductAssembly says whether a name the CI lists carry is a product
// assembly rather than a test project.
//
// The lists carry the ASSEMBLY name ("Harbor.Architecture.Tests"), and the
// answer needs the project the name belongs to: the solution says where it
// lives, and a project under src/ or apps/ is a product assembly.
func isProductAssembly(measured *tree, name string) bool {
	for _, proj := range measured.Projects {
		if strings.TrimSuffix(filepath.Base(proj.Path), ".csproj") != name {
			continue
		}
		return strings.HasPrefix(proj.Path, "src/") || strings.HasPrefix(proj.Path, "apps/")
	}
	return false
}

// measureCost measures what one more assembly costs.
func measureCost(root string, measured *tree, opts options) costReport {
	out := costReport{
		SweepCompilations: opts.sweepCompilations,
		SweepSeconds:      opts.sweepSeconds,
		SweepPasses:       opts.sweepPasses,
	}
	// The layer matrix in the document: one line per assembly.
	doc := filepath.Join(root, "docs", "ARCHITECTURE_LAYERS.md")
	if data, err := os.ReadFile(doc); err == nil {
		for _, line := range strings.Split(string(data), "\n") {
			if strings.HasPrefix(strings.TrimSpace(line), "│  - ") {
				out.LayerMatrixLines++
			}
		}
	}
	// The table of the layer rules: one row per src assembly.
	rules := filepath.Join(root, "tests", "Harbor.Architecture.Tests", "FullLayerMatrixTests.cs")
	if data, err := os.ReadFile(rules); err == nil {
		out.LayerMatrixRows = strings.Count(string(data), `["Harbor.`)
	}
	// The assembly lists the layer rules keep.
	split := filepath.Join(root, "tests", "Harbor.Architecture.Tests", "AbstractionsSplitLayerRules.cs")
	if data, err := os.ReadFile(split); err == nil {
		out.LayerRuleLists = strings.Count(string(data), "internal static readonly string[]")
		out.LayerRuleEntries = strings.Count(string(data), `"Harbor.`)
	}
	// The CI project lists: they name TEST projects, not product
	// assemblies. That is the fact that decides what a new assembly costs
	// there.
	lists := filepath.Join(root, "tools", "ci-project-lists.json")
	if data, err := os.ReadFile(lists); err == nil {
		var parsed struct {
			Shards map[string]struct {
				Projects []string `json:"projects"`
			} `json:"shards"`
		}
		if json.Unmarshal(data, &parsed) == nil {
			for _, shard := range parsed.Shards {
				for _, name := range shard.Projects {
					out.CIListProjects++
					if isProductAssembly(measured, name) {
						out.CIListHoldsProduct = true
					}
				}
			}
		}
	}
	// The frozen public-API rows of the two roots.
	for _, rootPath := range []string{contractsRoot, abstractionsRoot} {
		dir := filepath.Join(root, filepath.Dir(rootPath))
		data, err := os.ReadFile(filepath.Join(dir, "PublicAPI.Shipped.txt"))
		if err != nil {
			continue
		}
		out.PublicApiRows += len(strings.Split(strings.TrimRight(string(data), "\n"), "\n"))
	}
	// The product assemblies of the tree: what the architecture sweep
	// compiles.
	for _, proj := range measured.Projects {
		if strings.HasPrefix(proj.Path, "src/") || strings.HasPrefix(proj.Path, "apps/") {
			out.ProductAssemblies++
		}
	}
	return out
}
