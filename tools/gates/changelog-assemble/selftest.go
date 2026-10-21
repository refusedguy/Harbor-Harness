package changelog

// selftest.go -- the gate's own proof that it can fail.
//
// The fixture is the real tree: a probe fragment is dropped into
// changelog/fragments/, the assembler must glue it into [Unreleased], a
// second run must change nothing, and the entry that is already in the file
// must be reported as a duplicate. The probe and the original CHANGELOG.md
// are restored afterwards.
//
// A assembler edited until it glues nothing is indistinguishable from one
// that glues nothing.

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"harbor.local/gates/internal/gate"
)

const probeText = "### #999999 — the self-test fragment\n\n" +
	"**Why.** The assembler must notice a diverged file.\n"

func (r *repo) selfTest() int {
	original, err := os.ReadFile(r.changelog)
	if err != nil {
		fmt.Fprintf(os.Stderr, "self-test FAILED: cannot read CHANGELOG.md: %v\n", err)
		return gate.Red
	}
	probe := filepath.Join(r.fragment, "999999-self-test.md")
	if err := os.MkdirAll(r.fragment, 0o755); err != nil {
		fmt.Fprintf(os.Stderr, "self-test FAILED: %v\n", err)
		return gate.Red
	}
	if err := os.WriteFile(probe, []byte(probeText), 0o644); err != nil {
		fmt.Fprintf(os.Stderr, "self-test FAILED: %v\n", err)
		return gate.Red
	}
	defer func() {
		os.WriteFile(r.changelog, original, 0o644)
		os.Remove(probe)
	}()

	// 1. The fragment is glued into [Unreleased].
	once, err := r.merged(string(original))
	if err != nil {
		fmt.Fprintf(os.Stderr, "self-test FAILED: %v\n", err)
		return gate.Red
	}
	if !strings.Contains(once, "the self-test fragment") {
		fmt.Println("FAIL: сборщик не вставил фрагмент в [Unreleased].")
		return gate.Red
	}
	if once == string(original) {
		fmt.Println("FAIL: сборщик ничего не изменил, хотя фрагмент был.")
		return gate.Red
	}

	// 2. The assembly is idempotent: a second run changes nothing.
	twice, err := r.merged(once)
	if err != nil {
		fmt.Fprintf(os.Stderr, "self-test FAILED: %v\n", err)
		return gate.Red
	}
	if twice != once {
		fmt.Println("FAIL: сборщик не идемпотентен: второй запуск меняет файл.")
		return gate.Red
	}

	// 3. An entry that is already in the file is not appended a second time.
	third, err := r.merged(once)
	if err != nil {
		fmt.Fprintf(os.Stderr, "self-test FAILED: %v\n", err)
		return gate.Red
	}
	if strings.Contains(strings.SplitN(third, "### #999999", 2)[0], "the self-test fragment") {
		fmt.Println("FAIL: запись фрагмента дописалась второй раз.")
		return gate.Red
	}

	// 4. The duplicate is caught: the same entry is in the file and in the
	//    fragment, so the gate has to say so.
	if err := os.WriteFile(r.changelog, []byte(once), 0o644); err != nil {
		fmt.Fprintf(os.Stderr, "self-test FAILED: %v\n", err)
		return gate.Red
	}
	dupes, err := r.duplicateEntries(once)
	if err != nil {
		fmt.Fprintf(os.Stderr, "self-test FAILED: %v\n", err)
		return gate.Red
	}
	if len(dupes) == 0 {
		fmt.Println("FAIL: гейт не заметил запись, которая лежит и в файле, и во фрагменте.")
		return gate.Red
	}

	fmt.Println("OK: сборщик склеивает фрагмент, идемпотентен и ловит дубль записи.")
	return gate.Green
}
