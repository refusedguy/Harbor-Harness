// SourceCommentStripperTests.cs — the helper 29 rules hand their raw file text to,
// held to the two things it claims: a comment is gone, and nothing else is.
//
// THE PROBLEM THIS SOLVES
// -----------------------
// `SourceCommentStripper` is the only comment stripper in this suite that
// understands STRING LITERALS — `SourceScan.StripComments` is a regex pair and
// is literal-blind by design. It is the one rules reach for when the prose they
// are grading quotes the literals they match ("working", "MochaYellow").
//
// It has never been tested. All 29 of its call sites are architecture RULES,
// which are graded by whether they go red on the product tree — and no product
// file in any scanned tree currently contains a multi-line `/* … */`, so the
// defect below has had nothing to fire on. A helper with 29 callers and zero
// tests is a helper whose correctness is a coincidence, not a property.
//
// THE DEFECT (#919)
// -----------------
// `Strip` deletes comment characters and tracks block-comment state in a LOCAL
// that dies on return. `StripAll` then asks `OpensUnterminatedBlockComment`
// whether the line left it inside a block comment — by counting `/*` and `*/`
// in the ALREADY-STRIPPED string, which `Strip` has just emptied of both. The
// count is 0, the predicate is false for every input, `inBlockComment` is never
// set, and the `inBlockComment` branch of `StripAll` is unreachable. Every
// continuation line of a multi-line block comment is lexed as code and handed
// downstream as code.
//
// #899 is what this looks like from the outside: an interior line shaped
// `: ISessionForker` reads as a base list, so a guard guarding a guard went
// false-positive on a doc comment. That PR documented the defect from outside
// and worked around it in its own file, which is the right call for a PR that is
// not about the helper and exactly the wrong outcome for the helper.
//
// WHAT THESE TESTS ARE NOT
// -----------------------
// This is not a claim that the stripped text is byte-exact C#. It is a claim
// about the two properties 29 rules actually depend on:
//   1. the INTERIOR of a `/* … */` is not code, on any line of it;
//   2. the line count is preserved, because every caller indexes the result by
//      input line number to report a file:line.
// (2) is not decoration either: `ThemeAxisStaysDataRules`, `DesktopSharedTakesNoIoRules`
// and `DesignSystemLeafTakesNoIoRules` all walk `for (int i = 0; i < lines.Length; i++)`
// against the stripped array and report `i + 1` as the source line.
//
// Each behavioural test has a mirror that must still PASS after the fix, so the
// fix cannot be "strip more, break less": the one-line-block, the trailing
// comment, and the literal that contains `//` are all cases where an
// over-eager stripper would go quietly blind.

using TUnit.Assertions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Non-vacuity controls for <see cref="SourceCommentStripper" /> — the lexer 29
///     architecture rules hand raw file text to (#919).
/// </summary>
public sealed class SourceCommentStripperTests
{
    // ── The defect: a block comment that spans lines ─────────────────────────

    [Test]
    public async Task MultiLineBlockComment_InteriorIsNotHandedDownstreamAsCode()
    {
        // The shape #899 hit, verbatim in intent: prose inside a `/* … */` that
        // reads as a base list. A continuation line starting with `:` is not a
        // declaration, and a stripper that cannot say so will fail a rule on its
        // own documentation.
        string[] source =
        [
            "var port = new CoreSessionForker();",
            "/* the accepted shape is",
            "   : ISessionForker",
            "   the consumer holds it requiredly */",
            "services.AddSingleton<ISessionFactory, SessionFactory>();",
        ];

        string[] clean = SourceCommentStripper.StripAll(source);
        string text = string.Join("\n", clean);

        await Assert.That(text).DoesNotContain(": ISessionForker")
            .Because("the interior of a block comment is a comment, on every line of it");
        await Assert.That(clean[2]).IsEqualTo(string.Empty)
            .Because("`   : ISessionForker` is entirely comment and has no code to keep");
        await Assert.That(text).DoesNotContain("the accepted shape")
            .Because("prose that documents a rule must not be readable as the rule's subject");
    }

    [Test]
    public async Task MultiLineBlockComment_KeepsTheCodeOnEitherSide()
    {
        // The mirror of the test above, and the one that stops "strip the rest of
        // the file" from being a fix: the code BEFORE the opener and the code
        // AFTER the closer are real and must survive, on their own lines.
        string[] source =
        [
            "var before = 1; /* opens here",
            "   and the interior is prose, not code",
            "   and still prose */ var after = 2;",
        ];

        string[] clean = SourceCommentStripper.StripAll(source);

        // The trailing space is the space that stood BEFORE `/*`, and it is
        // spelled by concatenation so a reader (or a trim) does not "fix" it.
        // `Strip` deletes comment characters rather than blanking them, so
        // nothing stands in for them.
        await Assert.That(clean[0]).IsEqualTo("var before = 1;" + " ")
            .Because("`Strip` deletes rather than blanks, so the space before `/*` is all that is left");
        await Assert.That(clean[1]).IsEqualTo(string.Empty)
            .Because("this line is interior and nothing else");
        await Assert.That(clean[2]).IsEqualTo(" var after = 2;")
            .Because("the tail after `*/` is real code again and is re-lexed on its own");
        await Assert.That(string.Join("\n", clean)).Contains("var after = 2;")
            .Because("a stripper that ate the closer's tail would still pass the test above");
    }

    [Test]
    public async Task MultiLineBlockComment_DoesNotSwallowTheWholeFile()
    {
        // The degenerate form: an unterminated `/*` runs to end of file, and an
        // unterminated `@"` likewise. This is its own case because it is the one a
        // state-carrying fix could plausibly get wrong in the OTHER direction — a
        // "no closer found, start over" fallback at end of input would resume
        // stripping mid-comment and let the interior out, which is the same leak
        // with one more step in it.
        string[] source =
        [
            "var first = 1;",
            "/* never closed",
            "prose that looks like code: var smuggled = 2;",
        ];

        string[] clean = SourceCommentStripper.StripAll(source);

        await Assert.That(clean[0]).IsEqualTo("var first = 1;");
        await Assert.That(string.Join("\n", clean)).DoesNotContain("var smuggled = 2;")
            .Because("an unterminated comment is a comment to end of file, not a licence to resume");
    }

    // ── Line count, because every caller indexes by it ───────────────────────

    [Test]
    public async Task StripAll_ReturnsOneLinePerInputLine()
    {
        // Every caller that reports a file:line indexes the result against the
        // input index, so a stripper that dropped or merged lines would make
        // every diagnostic in the suite point at the wrong line — silently, and
        // in a way no rule can detect about itself.
        string[] source =
        [
            "// one",
            "/* two",
            "   still two */",
            "var three = 3; // three",
        ];

        string[] clean = SourceCommentStripper.StripAll(source);

        await Assert.That(clean.Length).IsEqualTo(source.Length)
            .Because("line numbers computed downstream must still point at the real source line");
    }

    // ── A second consequence of the same reset: multi-line verbatim strings ──

    [Test]
    public async Task MultiLineVerbatimString_IsNotReadAsCommentText()
    {
        // `@"…"` may legally span lines in C#, and the repo uses that. This is
        // the same defect wearing a different hat: `StripAll` restarts the lexer
        // in `Code` on every line, so a continuation line that happens to contain
        // `//` is truncated mid-literal. A rule about a rendered string is ABOUT
        // the literal, so this loses the exact thing the rule is looking for.
        string[] source =
        [
            "var banner = @\"line one",
            "// not a comment, it is inside the literal",
            "line three\";",
        ];

        string text = string.Join("\n", SourceCommentStripper.StripAll(source));

        await Assert.That(text).Contains("// not a comment")
            .Because("a `//` inside an open `@\"…\"` is a string, and the rule is about the string");
        await Assert.That(text).Contains("line three")
            .Because("the literal does not end until its closing quote");
    }

    // ── Mirrors: the cases a too-eager fix would break ───────────────────────

    [Test]
    public async Task SingleLineBlockComment_IsStillStripped()
    {
        // The anti-loophole for the fix. Carrying state across lines must not
        // change what happens to a comment that opens and closes on one line,
        // which is the overwhelmingly common case and the one every existing
        // caller actually depends on today.
        string[] source = ["var kept = 1; /* var dropped = 2; */ var alsoKept = 3;"];

        string text = string.Join("\n", SourceCommentStripper.StripAll(source));

        await Assert.That(text).DoesNotContain("var dropped = 2;")
            .Because("a one-line block comment is a comment too");
        await Assert.That(text).Contains("var kept = 1;")
            .Because("the code before it is real");
        await Assert.That(text).Contains("var alsoKept = 3;")
            .Because("and so is the code after it — `*/` returns the lexer to Code");
    }

    [Test]
    public async Task TrailingLineComment_KeepsTheCodeThatPrecedesIt()
    {
        string[] source = ["var config = Load(); // the trailing note"];

        string text = string.Join("\n", SourceCommentStripper.StripAll(source));

        await Assert.That(text).IsEqualTo("var config = Load();" + " ")
            .Because("blanking to end-of-line must not launder the code in front of it");
    }

    [Test]
    public async Task LineCommentInsideAStringLiteral_IsNotAComment()
    {
        // The property that distinguishes this helper from `SourceScan.StripComments`
        // and the reason the 29 callers use it: `//` inside a `"…"` is a string.
        // A rule that matches a quoted literal must still see the literal. If this
        // ever goes red the fix has traded one blindness for another.
        string[] source = ["_logger.LogError(\"corrupt at https://example.invalid/cfg.json\");"];

        string text = string.Join("\n", SourceCommentStripper.StripAll(source));

        await Assert.That(text).Contains("https://example.invalid/cfg.json")
            .Because("the URL is inside a string literal, not a comment");
    }

    [Test]
    public async Task ALineCommentDoesNotPoisonTheLineAfterIt()
    {
        // The trap in the fix itself, and the reason the end-of-line reset in
        // `Strip` is load-bearing rather than tidiness. `Strip`'s LineComment arm
        // only returns to Code on a `\n`, and NO caller supplies one — they hand
        // over `File.ReadAllLines` output, which has no newline in it. So a
        // carried state arrives at the next line still being LineComment, drops
        // that line, and stays LineComment for the line after that, forever.
        //
        // The trigger is not exotic: it is the first `///` line in any file. An
        // earlier version of this fix carried the state unconditionally and turned
        // 1,213 of 1,271 scanned files into mostly-empty ones, which would have
        // made every rule in the suite pass vacuously. `///` is the FIRST THING in
        // a C# file and the input here is a plain XML doc comment — the most
        // ordinary line in the repository.
        string[] source =
        [
            "/// <summary>",
            "///     A doc comment, which is a `//` comment.",
            "/// </summary>",
            "public sealed class Real { }",
        ];

        string[] clean = SourceCommentStripper.StripAll(source);

        await Assert.That(clean[3]).IsEqualTo("public sealed class Real { }")
            .Because("three `///` lines must not cost the file its first declaration");
        await Assert.That(clean[0]).IsEqualTo(string.Empty)
            .Because("and the doc comment itself is still a comment");
    }

    [Test]
    public async Task AStringContinuedWithABackslashKeepsItsLiteralAcrossTheLine()
    {
        // Coverage, not discrimination — this passes with or without the fix, and
        // is here because it is the OTHER state that legally spans a line and the
        // one with the best reason to be pinned. A `"…"` continued with a trailing
        // `\` is ordinary C#, and the continuation line may contain `//` without
        // that being a comment.
        //
        // (A `'…'` char literal cannot span a line in C# at all, so closing that
        // state at end of line in `Strip` is defensive only — a scanner handed a
        // truncated file, not a scanner handed source. It is reset there anyway,
        // because "cannot happen" is not a property worth depending on when the
        // cost of being wrong is the rest of the file.)
        string[] source =
        [
            "var s = \"first \\",
            "// still the literal, not a comment",
            "third\";",
        ];

        string text = string.Join("\n", SourceCommentStripper.StripAll(source));

        await Assert.That(text).Contains("// still the literal")
            .Because("the `\\` continuation keeps the literal open across the newline");
        await Assert.That(text).Contains("third")
            .Because("and the literal does not end until its closing quote");
    }

    // ── The other half of the inversion: a `/*` inside a STRING LITERAL ───────

    [Test]
    public async Task BlockMarkerInsideAStringLiteralIsNotACommentBoundary()
    {
        // This is the half of #919 the reported symptom does not describe, and it
        // is the half that was doing damage. `Strip` deletes real comments but
        // PRESERVES literals, so the old counter — which read the stripped line —
        // was 0 on every genuine block comment and 1 on every line whose `/*` was
        // ordinary string content. Exactly inverted.
        //
        // So `"src/*"` put 29 rules into "inside a block comment" at that line and
        // kept them there, and the measured cost in `src/` + `apps/` is 845 lines of
        // real code blanked across 5 files — 442 of them in `PermissionRuleset.cs`,
        // the file the contributor guide tells you to edit to add a permission rule.
        // Three of the rules that walk all of `src/` (`LogLevelMnemonicRule`,
        // `ExtensionAxisFreezeRule`, `FileLogSinkOwnershipRule`) have been grading
        // it that way.
        string[] source =
        [
            "new(\"write\", \"src/*\", PermissionAction.Allow),",
            "var real = 1;",
            "new(\"edit\", \"*.env\", PermissionAction.Deny),",
        ];

        string[] clean = SourceCommentStripper.StripAll(source);

        await Assert.That(clean[1]).IsEqualTo("var real = 1;")
            .Because("a `/*` inside a literal is a glob pattern, not a comment, and must not blank the file after it");
        await Assert.That(clean[2]).IsEqualTo("new(\"edit\", \"*.env\", PermissionAction.Deny),")
            .Because("and the line after that one is code too");
    }

    // ── Non-vacuity of the suite itself: Strip still works per line ───────────

    [Test]
    public async Task Strip_HandlesOneLineOnItsOwn()
    {
        // `Strip` is the per-line entry point its XML doc promises, and it is
        // correct on its own — the defect is entirely in `StripAll`'s use of it.
        // This is here so a future fix that rewrites the shared lexer cannot
        // quietly change `Strip`'s contract while fixing `StripAll`.
        await Assert.That(SourceCommentStripper.Strip("var a = 1; // note")).IsEqualTo("var a = 1; ");
        await Assert.That(SourceCommentStripper.Strip("var a = /* x */ 1;")).IsEqualTo("var a =  1;");
        await Assert.That(SourceCommentStripper.Strip("var a = \"// not a comment\";"))
            .IsEqualTo("var a = \"// not a comment\";");
    }
}
