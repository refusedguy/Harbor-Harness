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
// # WHY THE PER-PROJECT COST IS THE ROW AS REPORTED
//
// The Performance Summary reports, per project, the time MSBuild spent on
// that project, summed over the instances of it. A project that many other
// projects reference is built many times over, so its row holds the
// negotiation of the target frameworks as well as the compilation.
//
// The number this package uses is that row, with NOTHING subtracted from it,
// and that is a measurement rather than a simplification. In a build that runs
// one compiler at a time the build of a referenced project is NOT inside the
// window of the project that references it — the graph builds the references
// first — so subtracting the references' times would take away time that was
// never inside the row. The sign of the error is visible in the data: a
// project that references a 7.5-second project and whose own row reads 0.3
// seconds cannot contain it.
//
// A build that runs many compilers at once breaks that: the references are
// built in parallel, so a project waits for them without them being inside
// its own window, and the rows come to hold waiting rather than compilation.
// The ratio of the sum of the rows over the compiler's own total is the tell:
// it is about 3 for a build that ran one compiler at a time and 25 and more
// for a build that ran four at once, and a log on the wrong side of the tell
// is REFUSED by the instrument rather than priced.
//
// The ratio is also the tell in the other direction, and the instrument
// refuses that one too: a log whose rows add up to LESS than the compiler's
// own total has compiler time that is in no row at all, and apportioning that
// time over the rows that are there would scale the projects UP past what the
// log measured.
//
// Checkable is the whole point, so the check is part of the instrument, not a
// note in a document.
package buildcost
