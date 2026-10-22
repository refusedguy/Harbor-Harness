package mergeloss

// gitbatch.go -- ONE `git cat-file --batch` process for the whole run.
//
// The gate asks git for the content of a file at a revision: the file as
// the pull request's parent had it, the file as the merge has it, the file
// as the tip has it. One `git show` per question is 944 processes on this
// repository's history, and a process costs more than the read does.
//
// `git cat-file --batch` takes a list of object names on stdin and answers
// each of them in turn, so the whole run is ONE process and the gate pays
// for one start-up instead of a thousand.
//
// THE PROTOCOL
//
//	<oid> SP <type> SP <size> LF <body> LF      the object
//	<name> SP missing LF                        there is no such object
//
// The body is the object's bytes and nothing else, so the answer for a blob
// is the file's content, byte for byte what `git show` prints.
//
// WHAT THE BATCH CANNOT ANSWER
//
// The batch takes OBJECT NAMES, not revision expressions: `HEAD:file.txt`
// is a name, and `HEAD^` and `HEAD^{commit}` are not -- the batch answers
// "missing" to both. So the parent of a commit is read out of the commit's
// own body, and the question "is this commit here" is answered by the
// batch's own type word.

import (
	"bufio"
	"bytes"
	"fmt"
	"io"
	"os/exec"
	"sort"
	"strconv"
	"strings"

	"harbor.local/gates/internal/pycompat"
)

// gitCalls counts the processes of git the run started.
//
// The gate asks git fewer questions as the history grows -- one process for
// every blob, one diff per pull request, one walk of the history -- and the
// count is what says so. A run that starts 67 processes is not a run that
// starts 1547, and the number belongs in the log where a reader sees it
// grow, and not hidden inside the gate.
var gitCalls = map[string]int{}

// gitCall is one process of git, counted under the name of its sub-command.
func gitCall(args []string) {
	name := "git"
	if len(args) > 0 && args[0] != "" {
		name = args[0]
	}
	gitCalls[name]++
}

// gitCallsSummary is the line the gate prints: how many processes of git
// the run started, and what they were.
func gitCallsSummary() string {
	names := make([]string, 0, len(gitCalls))
	total := 0
	for name, count := range gitCalls {
		names = append(names, name)
		total += count
	}
	sort.Strings(names)
	parts := make([]string, 0, len(names))
	for _, name := range names {
		parts = append(parts, fmt.Sprintf("%s %d", name, gitCalls[name]))
	}
	return fmt.Sprintf("git: %d processes (%s)", total, strings.Join(parts, ", "))
}

// objectBatch is the one process the run reads its objects through.
type objectBatch struct {
	dir     string
	command *exec.Cmd
	stdin   io.WriteCloser
	stdout  *bufio.Reader
	errors  bytes.Buffer
}

// batchObject is one answer from the batch process.
type batchObject struct {
	name string
	kind string
	body []byte
}

// newObjectBatch starts the batch process in a directory.
func newObjectBatch(dir string) (*objectBatch, error) {
	command := exec.Command("git", "cat-file", "--batch")
	command.Dir = dir
	command.Env = gitEnv()
	gitCall(command.Args[1:])
	// The errors of the batch process are not the gate's errors: a name
	// that does not resolve is an answer, not a failure. They are caught
	// so that a chatty process cannot block on a full pipe.
	batch := &objectBatch{dir: dir, command: command}
	command.Stderr = &batch.errors
	stdin, err := command.StdinPipe()
	if err != nil {
		return nil, err
	}
	stdout, err := command.StdoutPipe()
	if err != nil {
		return nil, err
	}
	if err := command.Start(); err != nil {
		return nil, err
	}
	batch.stdin = stdin
	batch.stdout = bufio.NewReader(stdout)
	return batch, nil
}

// read asks for one object and waits for the answer.
//
// The answer is a line: the object's id, its kind and its size. A name that
// does not resolve is answered with the word "missing" and has no body.
func (b *objectBatch) read(spec string) (batchObject, bool) {
	if b == nil {
		return batchObject{}, false
	}
	if _, err := io.WriteString(b.stdin, spec+"\n"); err != nil {
		return batchObject{}, false
	}
	line, err := b.stdout.ReadString('\n')
	if err != nil {
		return batchObject{}, false
	}
	fields := strings.Fields(strings.TrimRight(line, "\n"))
	if len(fields) < 3 {
		// "missing" and "ambiguous" land here: there is no such object.
		return batchObject{}, false
	}
	size, err := strconv.Atoi(fields[len(fields)-1])
	if err != nil {
		// The last field of an answer that is not an object is a word,
		// not a number: there is no such object.
		return batchObject{}, false
	}
	body := make([]byte, size+1)
	if _, err := io.ReadFull(b.stdout, body); err != nil {
		return batchObject{}, false
	}
	return batchObject{
		name: fields[0],
		kind: fields[len(fields)-2],
		body: body[:size],
	}, true
}

// fileLines is the lines of `path` at `rev`, or nothing when the file is
// not there.
//
// A file that does not exist at that revision is not an error: a pull
// request that adds a file has no "before" version of it.
//
// A path that is a DIRECTORY there is a different thing: `git show` prints
// the directory listing, and the batch answers with the tree object. The
// paths this gate works on come from a diff of files, so a directory never
// happens; the listing is read the old way, one process, so that the answer
// is the same either way.
func (b *objectBatch) fileLines(rev, path string) ([]string, bool) {
	spec := rev + ":" + path
	found, ok := b.read(spec)
	if !ok {
		return nil, false
	}
	if found.kind != "blob" {
		out, ok := gitTextAt(b.dir, "show", spec)
		if !ok {
			return nil, false
		}
		return pycompat.SplitLines(out), true
	}
	return pycompat.SplitLines(string(found.body)), true
}

// fileLinesOrEmpty is the lines of `path` at `rev`, or nothing.
func (b *objectBatch) fileLinesOrEmpty(rev, path string) []string {
	lines, _ := b.fileLines(rev, path)
	return lines
}

// commitInfo is what the batch says about a commit.
type commitInfo struct {
	oid     string
	parents []string
}

// commit reads a commit out of the object store.
//
// The parents are read out of the commit's own header: the body of a commit
// starts with its tree, then its parents, then the author, and the message
// comes after a blank line. A message line that starts with "parent " is
// not a parent, and the blank line is where the header ends.
func (b *objectBatch) commit(sha string) (commitInfo, bool) {
	found, ok := b.read(sha)
	if !ok || found.kind != "commit" {
		return commitInfo{}, false
	}
	info := commitInfo{oid: found.name}
	for _, raw := range pycompat.SplitLines(string(found.body)) {
		if raw == "" {
			break
		}
		if strings.HasPrefix(raw, "parent ") {
			info.parents = append(info.parents, strings.TrimSpace(raw[len("parent "):]))
		}
	}
	return info, true
}

// firstParent is the parent `git rev-parse <sha>^` would name, or nothing.
//
// A commit with no parent has none: the answer is the empty string, the way
// `git rev-parse` answers it.
func (b *objectBatch) firstParent(sha string) string {
	info, ok := b.commit(sha)
	if !ok || len(info.parents) == 0 {
		return ""
	}
	return info.parents[0]
}

// hasCommit reports whether the commit is in the object store.
//
// The question is the one `git cat-file -e <sha>^{commit}` asks: a sha that
// names a commit, or a tag that peels to one. The batch does not peel, so
// a tag is asked the old way -- a pull request head is a commit, and this
// is a formality.
func (b *objectBatch) hasCommit(sha string) bool {
	found, ok := b.read(sha)
	if !ok {
		return false
	}
	if found.kind == "commit" {
		return true
	}
	if found.kind == "tag" {
		return gitOKAt(b.dir, "cat-file", "-e", sha+"^{commit}")
	}
	return false
}

// close ends the batch process.
func (b *objectBatch) close() {
	if b == nil {
		return
	}
	b.stdin.Close()
	b.command.Wait()
}
