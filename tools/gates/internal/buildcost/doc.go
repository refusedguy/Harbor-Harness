// Package buildcost measures what the COMPILER costs, and what a change in
// one project costs everybody who depends on it.
//
// The question this package exists for is issue #2112: "how much does each
// project cost to compile", and the follow-up: "how many projects have to be
// recompiled when one file of project X changes". Before this package both
// numbers were counted by hand, so every decision to split a project was
// made by eye.
//
// THREE SOURCES, THREE KINDS OF FACT
//
//   - The BUILD LOG (MSBuild's Performance Summary, the `-clp:PerformanceSummary`
//     output). It carries the compiler's own totals: how many Csc calls the
//     build made and how many seconds they took. Those are measurements.
//
//   - The SOLUTION (Harbor.slnx and the ProjectReference edges of every
//     project). It carries the shape of the graph: who depends on whom, and
//     therefore how many projects a change in one of them rebuilds. Those
//     are measurements.
//
//   - The TREE (the .cs files under each project). It carries the size of
//     each project: how many files the compiler reads and how many lines
//     they hold. Those are measurements.
//
// WHY THE PER-PROJECT COST IS COMPUTED AND NOT READ
//
// The Performance Summary reports a project's time INCLUSIVE of the builds of
// the projects it references, and a project that many projects reference is
// built many times over (once per referencing context), so the row of a
// widely-referenced project is mostly negotiation, not compilation.
//
// The number this package computes instead is the project's OWN time: the
// row minus the own time of the projects it references, taken in
// topological order (the references first). In a build that runs one
// compiler at a time that is the project's compile time, because a project's
// build contains the builds of its references.
//
// A build that runs many compilers at once breaks that containment: the
// references are built in parallel, so a project waits for them without
// them being inside its own window, and the subtraction produces numbers
// that are dominated by waiting rather than by compilation. The ratio of the
// sum of the own times to the compiler's own total is the tell: it is about
// 1.1 for a serial build and about 12 for a parallel one. Checkable is the
// whole point, so the check is part of the instrument, not a note in a
// document.
package buildcost
