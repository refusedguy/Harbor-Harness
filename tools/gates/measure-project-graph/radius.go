package measureprojectgraph

import (
	"fmt"
	"sort"
	"strings"

	"harbor.local/gates/internal/buildcost"
)

// radiusRow is one row of the radius table: what a change in one project
// costs everybody who depends on it.
type radiusRow struct {
	// Project is the owner: the project the change is in.
	Project string
	// Direct is the number of projects that reference the owner directly.
	Direct int
	// Consumers is the number of projects that depend on the owner
	// directly or through another project.
	Consumers int
	// Seconds is the price of the radius: the owner and its consumers,
	// together.
	Seconds float64
	// Priced says whether the price is known: it needs the recorded
	// measurement, because the graph alone does not know what a project
	// costs.
	Priced bool
}

// radiusRows computes the radius of every project of the graph.
//
// The radius is the owner and every project that depends on it, directly or
// through another project: the set a change in one file of the owner
// rebuilds. The price of that set is the sum of what the projects cost, and
// it needs the recorded measurement (the prices map); without it the counts
// are still the answer to "how many projects rebuild", and the price is
// marked as unknown rather than printed as zero.
func radiusRows(graph *buildcost.Graph, prices map[string]float64) []radiusRow {
	rows := make([]radiusRow, 0, len(graph.Projects))
	for _, project := range graph.SortedProjects() {
		radius := graph.RadiusOf(project)
		row := radiusRow{
			Project:   project,
			Direct:    radius.Direct,
			Consumers: len(radius.Consumers),
		}
		if len(prices) > 0 {
			row.Priced = true
			row.Seconds = prices[project]
			for _, consumer := range radius.Consumers {
				row.Seconds += prices[consumer]
			}
		}
		rows = append(rows, row)
	}
	sort.Slice(rows, func(i, j int) bool {
		if rows[i].Seconds != rows[j].Seconds {
			return rows[i].Seconds > rows[j].Seconds
		}
		return rows[i].Project < rows[j].Project
	})
	return rows
}

// printRadius prints the radius table.
func printRadius(graph *buildcost.Graph, prices map[string]float64, recorded map[string]float64) {
	rows := radiusRows(graph, prices)
	fmt.Println()
	fmt.Println("the radius of a change: the owner and everything that depends on it")
	if len(prices) == 0 {
		fmt.Println("(no baseline given, so the prices are unknown: the graph says how many")
		fmt.Println(" projects rebuild, the recorded measurement says what they cost)")
		fmt.Println("project                                       | direct | consumers")
		fmt.Println("----------------------------------------------+--------+-----------")
		for _, row := range rows {
			fmt.Printf("%-46s | %6d | %9d\n", row.Project, row.Direct, row.Consumers)
		}
		return
	}
	fmt.Println("project                                       | direct | consumers | seconds | recorded")
	fmt.Println("----------------------------------------------+--------+-----------+---------+----------")
	for _, row := range rows {
		recordedText := "-"
		if value, ok := recorded[row.Project]; ok {
			recordedText = fmt.Sprintf("%6.1f s", value)
		}
		fmt.Printf("%-46s | %6d | %9d | %7.1f | %s\n",
			row.Project, row.Direct, row.Consumers, row.Seconds, recordedText)
	}
}

// renderRadiiJSON renders the radius table as JSON.
func renderRadiiJSON(graph *buildcost.Graph, prices map[string]float64) string {
	rows := radiusRows(graph, prices)
	var text strings.Builder
	text.WriteString("[\n")
	for index, row := range rows {
		text.WriteString("    {\n")
		text.WriteString("      \"project\": ")
		text.WriteString(jsonString(row.Project))
		text.WriteString(",\n")
		text.WriteString(fmt.Sprintf("      \"direct_consumers\": %d,\n", row.Direct))
		text.WriteString(fmt.Sprintf("      \"transitive_consumers\": %d,\n", row.Consumers))
		text.WriteString(fmt.Sprintf("      \"radius_projects\": %d,\n", row.Consumers+1))
		text.WriteString(fmt.Sprintf("      \"seconds\": %.1f\n", row.Seconds))
		text.WriteString("    }")
		if index < len(rows)-1 {
			text.WriteString(",")
		}
		text.WriteString("\n")
	}
	text.WriteString("  ]")
	return text.String()
}
