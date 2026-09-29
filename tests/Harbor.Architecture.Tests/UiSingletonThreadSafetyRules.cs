// UiSingletonThreadSafetyRules.cs — guard for #605 (GoF-A19).
//
// DefaultUiProjector documented itself as NOT thread-safe ("call Project from
// a single render loop — every built-in renderer already constructs its own
// instance") while the only shipped DI host registered it — and its only
// consumer — as singletons. apps/Harbor.App.Avalonia/Hosting/ServiceRegistration.cs
// registers both DefaultUiProjector and UiRenderEngine that way.
//
// Both halves of that comment were false, and the consequence is a crash
// rather than a stale frame: the projector's only mutable state is the field
// `private ProjectionCache? _cache;` (DefaultUiProjector.cs:61), published
// unsynchronised at the end of Project and read at the top. Without a
// release/acquire pair a second thread can observe the ProjectionCache
// *reference* while its fields are still at their defaults — which is how the
// #562 `null!` class would turn a publication race into a
// NullReferenceException at the reuse sites (Project lines 70, 112, 134).
//
// The fix made the singleton honest: `_cache` is `volatile`, so the cache is
// safely published and concurrent `Project` calls return a stale-but-non-null
// screen instead of a half-built one. This file stops the ORIGINAL defect —
// the doc/container contradiction — from coming back.
//
// It is a CORRESPONDENCE rule, not a blanket ban, and that is what keeps it
// non-vacuous (the failure mode EnforcerIntegrityTests exists to prevent). A
// type is flagged only when BOTH hold:
//
//   (a) its doc comment declares it not thread-safe, AND
//   (b) an apps/*/Hosting composition root registers it with AddSingleton.
//
// Neither half alone is a defect. A render-thread-only widget documented as
// such and constructed per renderer is correct; a singleton that documents
// itself as safe is correct. Only the pair is a lie — and it is precisely the
// shape #576 called out ("a doc comment promising a lifetime the container
// does not provide is a defect").
//
// SCOPE, deliberately narrow. The doc scan covers src/Harbor.Ui.Framework*
// (where the projector lives and where this defect class is owned); the
// registration scan covers apps/*/Hosting (the composition roots that do the
// registering). Both sides are scanned from the tree rather than a hard-coded
// name list, so a NEW render-thread-only singleton in the UI layer is caught
// without anyone editing this file. The other "not thread-safe" doc comments
// in the repo (CellForge render-thread widgets, ISessionStore,
// PluginLoadHostAdapter) are outside the scanned set on purpose: widening it
// would make the gate red on a tree this change is not touching.
//
// The second test is the anti-rot check. A two-sided correspondence can decay
// into a rule that matches nothing; DefaultUiProjectorIsStillASingleton proves
// the registration side is still live by naming the type the rule was written
// for, so a future change that moves the projector off the singleton path
// surfaces here instead of silently disarming the rule above.

using System.Text;
using System.Text.RegularExpressions;

namespace Harbor.Architecture.Tests;

/// <summary>
///     Asserts that no type under <c>src/Harbor.Ui.Framework*/</c> both
///     documents itself as not thread-safe and is registered as a DI singleton
///     by a shipped composition root.
/// </summary>
public class UiSingletonThreadSafetyRules
{
    /// <summary>
    ///     Doc-comment phrasings that assert a type is unsafe for concurrent
    ///     use. Case-insensitive and tolerant of <c>thread safe</c> /
    ///     <c>thread-safe</c> spacing, because both real forms in the layer
    ///     differ ("is NOT thread-safe", "Not thread-safe by design").
    /// </summary>
    private static readonly Regex NotThreadSafeDoc =
        new(@"not\s+thread[-\s]?safe", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    ///     A publicly reachable class declaration. <c>private</c>/<c>internal
    ///     nested</c> types are excluded on purpose — a DI singleton is always
    ///     public, and matching nested helper types would let one file's
    ///     private class taint the registration set.
    /// </summary>
    private static readonly Regex TypeDeclaration =
        new(@"^[ \t]*(?:public|internal)[^\n=;]*?\bclass[ \t]+(?<name>[A-Za-z_][A-Za-z0-9_]*)",
            RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Matches one <c>AddSingleton&lt;T&gt;</c> target.</summary>
    private static readonly Regex SingletonRegistration =
        new(@"AddSingleton<\s*(?<name>[A-Za-z_][A-Za-z0-9_.]*)", RegexOptions.Compiled);

    /// <summary>
    ///     The contiguous <c>///</c> block directly above a declaration. XML
    ///     docs must be adjacent to what they document, so the first blank or
    ///     non-doc line walking upwards ends the block.
    /// </summary>
    private static string DocCommentAbove(string[] lines, int declarationLine)
    {
        var doc = new StringBuilder();
        for (int i = declarationLine - 1; i >= 0; i--)
        {
            string trimmed = lines[i].Trim();
            if (!trimmed.StartsWith("///", StringComparison.Ordinal))
            {
                break;
            }

            // Prepend: the walk runs upwards, so the block is collected
            // bottom-up and would otherwise come out reversed.
            doc.Insert(0, trimmed).Append('\n');
        }

        return doc.ToString();
    }

    /// <summary>
    ///     Every <c>.cs</c> file under <c>src/Harbor.Ui.Framework*</c>, sorted
    ///     for a stable failure message.
    /// </summary>
    private static IReadOnlyList<string> EnumerateUiFrameworkSources()
    {
        if (RepoPaths.RepoRoot is not { } root)
        {
            return [];
        }

        string src = Path.Combine(root, "src");
        if (!Directory.Exists(src))
        {
            return [];
        }

        var found = new List<string>();
        foreach (string dir in Directory.GetDirectories(src, "Harbor.Ui.Framework*"))
        {
            found.AddRange(Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories));
        }

        return
        [
            .. found.Where(p => !IsBuildOutput(p))
                  .OrderBy(p => p, StringComparer.Ordinal)
        ];
    }

    /// <summary>
    ///     Every <c>AddSingleton</c> target named by a shipped composition root
    ///     under <c>apps/</c>, keyed by simple type name.
    /// </summary>
    private static HashSet<string> EnumerateAppSingletonRegistrations()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (RepoPaths.RepoRoot is not { } root)
        {
            return names;
        }

        string apps = Path.Combine(root, "apps");
        if (!Directory.Exists(apps))
        {
            return names;
        }

        // Only composition roots register services. Restricting the walk to
        // apps/*/Hosting keeps model classes that merely *consume* a
        // singleton out of the registration set.
        foreach (string hosting in Directory.GetDirectories(apps, "Hosting", SearchOption.AllDirectories))
        {
            foreach (string file in Directory.GetFiles(hosting, "*.cs", SearchOption.AllDirectories))
            {
                if (IsBuildOutput(file))
                {
                    continue;
                }

                foreach (Match match in SingletonRegistration.Matches(ReadText(file)))
                {
                    // Keep the simple name: registrations spell the type
                    // unqualified in every composition root, and taking the
                    // last segment keeps a fully-qualified registration from
                    // being missed.
                    string name = match.Groups["name"].Value;
                    int dot = name.LastIndexOf('.');
                    names.Add(dot >= 0 ? name[(dot + 1)..] : name);
                }
            }
        }

        return names;
    }

    private static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            // A published test host may not have the tree at all; degrade to
            // "nothing to check" rather than failing on an unrelated IO error.
            return string.Empty;
        }
    }

    /// <summary>
    ///     Strips inline XML markup so a <c>&lt;c&gt;…&lt;/c&gt;</c> wrapper
    ///     cannot split the phrase and hide it.
    /// </summary>
    private static string PlainText(string doc) => Regex.Replace(doc, @"<[^>]+>", " ");

    /// <summary>
    ///     No type under <c>src/Harbor.Ui.Framework*/</c> is both documented
    ///     as not thread-safe and registered as a DI singleton by a shipped
    ///     composition root under <c>apps/</c>.
    /// </summary>
    [Test]
    public async Task TypesDocumentedAsNotThreadSafe_AreNotRegisteredAsSingletons()
    {
        var singletons = EnumerateAppSingletonRegistrations();
        var violations = new List<string>();

        foreach (string file in EnumerateUiFrameworkSources())
        {
            string source = ReadText(file);
            if (source.Length == 0)
            {
                continue;
            }

            string[] lines = source.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                Match declaration = TypeDeclaration.Match(lines[i]);
                if (!declaration.Success)
                {
                    continue;
                }

                string name = declaration.Groups["name"].Value;
                if (!singletons.Contains(name))
                {
                    continue;
                }

                string doc = PlainText(DocCommentAbove(lines, i));
                if (doc.Length == 0)
                {
                    continue;
                }

                if (NotThreadSafeDoc.IsMatch(doc))
                {
                    violations.Add(
                        $"{Path.GetFileName(file)}: {name} documents itself as not thread-safe " +
                        $"and is registered AddSingleton<{name}>(). Either drop the singleton " +
                        "or make the documented contract true (synchronise the shared state).");
                }
            }
        }

        await Assert.That(violations).IsEmpty();
    }

    /// <summary>
    ///     Anti-rot: proves the correspondence above is still armed. The
    ///     projector stays a singleton of the desktop host — the #605 fix took
    ///     the synchronisation path, not the per-renderer lifetime — and its
    ///     doc no longer claims the container contradicts.
    /// </summary>
    /// <remarks>
    ///     If this fails, either the projector moved off the singleton path
    ///     (the correspondence has nothing left to guard for this type and
    ///     this assertion must be re-pointed deliberately) or the stale
    ///     "not thread-safe" wording crept back. Both are worth a hard stop
    ///     rather than a quietly weakened rule.
    /// </remarks>
    [Test]
    public async Task DefaultUiProjector_IsStillASingleton_AndNoLongerDocumentedAsUnsafe()
    {
        var singletons = EnumerateAppSingletonRegistrations();
        string projectorTypeName = typeof(Harbor.Ui.Framework.Projection.DefaultUiProjector).Name;

        await Assert.That(singletons.Contains(projectorTypeName)).IsTrue();

        string? projectorFile = EnumerateUiFrameworkSources()
            .FirstOrDefault(p => Path.GetFileName(p) == "DefaultUiProjector.cs");
        await Assert.That(projectorFile).IsNotNull();

        string[] lines = ReadText(projectorFile!).Split('\n');
        int declarationLine = Array.FindIndex(lines, l => l.Contains("class DefaultUiProjector", StringComparison.Ordinal));
        await Assert.That(declarationLine).IsGreaterThanOrEqualTo(0);

        string doc = PlainText(DocCommentAbove(lines, declarationLine));

        await Assert.That(NotThreadSafeDoc.IsMatch(doc)).IsFalse();
    }
}
