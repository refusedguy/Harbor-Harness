using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Harbor.Architecture.Tests;

/// <summary>
///     #957 — a vestigial <c>Microsoft.Extensions.*</c> <c>PackageReference</c> is a
///     declaration the project does not honour: no type or member the project
///     names is declared by that package, and nothing the package carries is
///     reachable through it alone.
/// </summary>
/// <remarks>
///     <para>
///         WHY A TEXT RULE ON THE CSPROJ IS NOT ENOUGH, which is why #910 raised
///         no guard. <c>Harbor.Tui.Tests</c> looked vestigial and was — and its
///         package arrived by three other routes, so deleting the line changed
///         nothing. Conversely
///         <c>src/Harbor.Ui.Framework.Projection</c> named no Microsoft.Extensions
///         type in any of its 18 sources, and its
///         <c>Microsoft.Extensions.Logging.Abstractions</c> reference was still
///         load-bearing for a project two <c>ProjectReference</c> hops away. Both
///         facts are invisible to a rule that reads one file.
///     </para>
///     <para>
///         So this rule walks the same two artifacts a restore would: the
///         package's own <c>lib/net10.0/*.xml</c> (which types and members it
///         declares) and its <c>nuspec</c> <c>net10.0</c> dependency group (what
///         it drags in). That makes "is this reference honoured" a question about
///         the package graph rather than about a spelling.
///     </para>
///     <para>
///         NOT A COMPILE. A type reached only through a generic constraint, an
///         attribute, an <c>extern alias</c>, or a name that also exists in another
///         referenced assembly is out of reach here. The rule is therefore
///         one-sided on purpose: it reports a reference as vestigial only when it
///         can name the package's declared surface and find nothing in the
///         project's own sources that resolves to it. Anything ambiguous stays
///         silent. A false report costs a revert; a missed one costs nothing.
///     </para>
/// </remarks>
public sealed partial class VestigialExtensionsPackageReferenceRule
{
    private const string ExtPrefix = "Microsoft.Extensions.";

    private static readonly string[] SourceRoots = ["src", "apps", "tests", "samples"];

    [Test]
    public async Task No_project_declares_an_unhonoured_Microsoft_Extensions_package()
    {
        var root = RepositoryRoot();
        var findings = Scan(root);

        var report = new StringBuilder();
        foreach (var f in findings)
            report.AppendLine($"  {f.Project,-52} {f.Package}");

        await Assert.That(findings).IsEmpty().Because(
            "a PackageReference is vestigial when no type or member it declares is reachable "
            + "from the project, nothing it drags in is used, and no consumer was relying on it "
            + "arriving through here. Delete the line, or record why it is load-bearing. "
            + "Unhonoured:\n" + report);
    }

    // ---------------------------------------------------------------- the walk

    private static List<(string Project, string Package)> Scan(string root)
    {
        var packages = PackagesInPlay(root);
        var declared = new Dictionary<string, HashSet<string>?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in packages)
            declared[p] = PackageSurface.Read(p);
        var deps = packages.ToDictionary(p => p, p => NuspecDependencies.Read(p), StringComparer.OrdinalIgnoreCase);
        var findings = new List<(string, string)>();

        foreach (var project in AllProjects(root))
        {
            var sources = ProjectSources(project);
            if (sources.Count == 0)
                continue;

            foreach (var pkg in project.PackageReferences.Where(p => p.StartsWith(ExtPrefix, StringComparison.Ordinal)))
            {
                var surface = declared[pkg];
                if (surface is null || surface.Count == 0)
                    continue;                       // not resolvable here — stay silent

                var globals = ProjectGlobalUsings(project);

                // 1. does the project bind any name this package declares? The
                //    binding is resolved through each file's using-scopes, so a
                //    same-named type in another assembly does not count.
                if (NamesBoundTo(sources, declared, [pkg], globals).Count > 0)
                    continue;

                // 2. what does deleting the line actually take away? If the package
                //    still arrives by another route the deletion is cosmetic — real,
                //    and still a deletion worth reporting. If it does NOT, then every
                //    type the package carries leaves with it, and the next check is
                //    what decides whether anything needed those types.
                var (before, after) = Closures(project, pkg, deps);
                var lost = before.Except(after, StringComparer.OrdinalIgnoreCase).ToHashSet();

                // 3. does the project bind anything that was only reachable THROUGH
                //    this reference? (Harbor.Tui.Tests is the proof that a project can
                //    hold a package it never spells and lose nothing; this is the
                //    opposite direction, and it is the one that breaks a build.)
                if (lost.Any(d => declared.ContainsKey(d)
                                 && NamesBoundTo(sources, declared, [d], globals).Count > 0))
                    continue;

                // 4. a CONSUMER may be relying on the package arriving through here.
                var atRisk = false;
                foreach (var consumer in ProjectConsumers(project, root))
                {
                    // A consumer is at risk when it binds a name this package — or
                    // something only this reference supplied — provides, AND does not
                    // declare the package itself. That is #910's OpenAiCompatible half
                    // exactly: Harbor.Benchmarks, Harbor.LoadTests and
                    // Harbor.Providers.Tests never mentioned the package, and deleting
                    // the line emptied three projects. Skipping every project that HAS a
                    // consumer would silence the rule almost everywhere, since nearly
                    // every src/ project has one.
                    if (consumer.PackageReferences.Contains(pkg, StringComparer.OrdinalIgnoreCase))
                        continue;
                    var consumerSources = ProjectSources(consumer);
                    if (consumerSources.Count == 0)
                        continue;
                    var consumerGlobals = ProjectGlobalUsings(consumer);
                    if (NamesBoundTo(consumerSources, declared, [pkg], consumerGlobals).Count > 0
                        || lost.Any(d => declared.ContainsKey(d)
                                         && NamesBoundTo(consumerSources, declared, [d],
                                                         consumerGlobals).Count > 0))
                    {
                        atRisk = true;
                        break;
                    }
                }

                if (atRisk)
                    continue;

                findings.Add((project.Relative, pkg));
            }
        }

        return findings;
    }

    /// <summary>
    ///     Records one member name, normalized: generic arity suffix dropped, and a
    ///     "{T:Type, M:Type.Method}" overload list kept only as its first entry.
    /// </summary>
    private static void Add(HashSet<string> names, string raw)
    {
        var fq = raw.Trim();
        if (fq.StartsWith("{", StringComparison.Ordinal))
        {
            var close = fq.IndexOf('}');
            if (close < 0)
                return;
            fq = fq[1..close];
            var comma = fq.IndexOf(',');
            if (comma >= 0)
                fq = fq[..comma];
            fq = fq.Trim();
            if (fq.StartsWith("T:", StringComparison.Ordinal))
                fq = fq[2..];
        }

        var tick = fq.IndexOf('`');
        if (tick >= 0)
            fq = fq[..tick];
        var brace = fq.IndexOf('{');
        if (brace >= 0)
            fq = fq[..brace];

        if (fq.StartsWith(ExtPrefix, StringComparison.Ordinal))
            names.Add(fq);
    }

    private static HashSet<string> ProjectGlobalUsings(Project project)
    {
        var out_ = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in ProjectSources(project))
            foreach (Match m in GlobalUsing().Matches(Read(file)))
                out_.Add(m.Groups[1].Value);
        return out_;
    }

    /// <summary>
    ///     Short names the project's sources actually bind to something the given
    ///     packages declare — resolved THROUGH each file's using-scopes, never by
    ///     name alone.
    /// </summary>
    /// <remarks>
    ///     Name alone is the whole trap in this class, and two live cases sit in
    ///     this repository. <c>src/Harbor.Logging/LoggerSetup.cs</c> says
    ///     <c>ILogger</c> three times and its only relevant <c>using</c> is
    ///     <c>using Serilog;</c>, so every one of those binds to
    ///     <c>Serilog.ILogger</c> — a type that is not in the package at all. And
    ///     <c>src/Harbor.Ipc.Server</c> reaches <c>AddSimpleConsole()</c> through
    ///     a <c>global using Microsoft.Extensions.Logging;</c> in
    ///     <c>GlobalUsings.cs</c>, so a per-file scan of the two call sites
    ///     reports them as bare identifiers and misses the dependency entirely.
    ///     A short name counts only when a using-scope in the file covers
    ///     the namespace that declares it — or, for an extension or instance
    ///     member, the namespace its owning type lives in.
    /// </remarks>
    private static HashSet<string> NamesBoundTo(IReadOnlyList<string> sources,
                                                IReadOnlyDictionary<string, HashSet<string>?> declared,
                                                IEnumerable<string> packages,
                                                IEnumerable<string> extraScopes)
    {
        var hits = new HashSet<string>(StringComparer.Ordinal);
        var globals = new HashSet<string>(extraScopes, StringComparer.Ordinal);

        // short name -> fully-qualified names, for the packages under test
        var index = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var pkg in packages)
        {
            if (!declared.TryGetValue(pkg, out var surface) || surface is null)
                continue;
            foreach (var fq in surface)
            {
                var cut = fq.LastIndexOf('.');
                if (cut <= 0)
                    continue;
                var ns = fq[..cut];
                var simple = fq[(cut + 1)..];
                if (!index.TryGetValue(simple, out var set))
                    index[simple] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(ns);
            }
        }

        foreach (var file in sources)
        {
            var text = Read(file);
            var scopes = new HashSet<string>(globals, StringComparer.Ordinal);
            scopes.UnionWith(ProjectScopes(text));

            var body = Comment().Replace(text, " ");
            body = Using().Replace(body, " ");
            body = Verbatim().Replace(body, "\"\"");
            body = Literal().Replace(body, "\"\"");

            foreach (Match m in Name().Matches(body))
            {
                if (!index.TryGetValue(m.Value, out var owners))
                    continue;
                if (owners.Any(o => scopes.Contains(o)))
                {
                    hits.Add(m.Value);
                    continue;
                }
                // A MEMBER binds through the namespace its OWNER lives in, not through
                // a static using of the owner: LoggerExtensions.LogInformation and
                // ConsoleLoggerExtensions.AddSimpleConsole are called on an ILogger /
                // ILoggingBuilder with only `using Microsoft.Extensions.Logging;` — or a
                // global using — in scope. Without this the extension-method shape reads
                // as an unused package, which is exactly how the four
                // samples/plugins/Harbor.Plugin.* projects look deletable (they call
                // context.CreateLogger<T>().LogInformation(...)) and how
                // src/Harbor.Ipc.Server hides two AddSimpleConsole() call sites behind
                // GlobalUsings.cs. Those are load-bearing, and a probe that misses the
                // shape reports deleting them as a cleanup.
                if (owners.Any(o => scopes.Any(s => o.StartsWith(s + ".", StringComparison.Ordinal))))
                    hits.Add(m.Value);
            }
        }

        return hits;
    }

    /// <summary>
    ///     The SDK's implicit using set, in scope in every file because
    ///     <c>ImplicitUsings</c> is <c>enable</c> repo-wide
    ///     (Directory.Build.props:8) and no using directive points at them.
    /// </summary>
    /// <remarks>
    ///     <c>System.Net.Http</c> is the one that changes a verdict. It is where
    ///     <c>IHttpClientFactory</c> lives, it is declared by
    ///     <c>Microsoft.Extensions.Http</c>, and
    ///     <c>tests/Harbor.App.Cli.Tests/HostBuilderDiTests.cs:273</c> calls
    ///     <c>Services.GetService&lt;IHttpClientFactory&gt;()</c> while declaring only
    ///     <c>DependencyInjection</c> and <c>Hosting</c> — neither of which depends on
    ///     <c>Http</c>. That test reaches the package through
    ///     <c>apps/Harbor.App.Cli</c>, so deleting App.Cli's line breaks a test project
    ///     the <c>test</c> job runs on every PR. Leaving implicit usings out makes the
    ///     rule call that reference vestigial, which is the most expensive possible
    ///     error for it to make.
    /// </remarks>
    private static readonly string[] ImplicitUsings =
    [
        "System", "System.Collections.Generic", "System.IO", "System.Linq",
        "System.Net.Http", "System.Threading", "System.Threading.Tasks",
    ];

    /// <summary>The namespaces a single file brings into scope.</summary>
    private static HashSet<string> ProjectScopes(string text)
    {
        var scopes = new HashSet<string>(ImplicitUsings, StringComparer.Ordinal);
        foreach (Match m in GlobalUsing().Matches(text))
            scopes.Add(m.Groups[1].Value);
        foreach (Match m in Using().Matches(text))
            if (!m.Groups[1].Success)
                scopes.Add(m.Groups[2].Value);
        return scopes;
    }

    // -------------------------------------------------------------- artifacts

    private static HashSet<string> PackagesInPlay(string root)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in AllProjects(root))
        foreach (Match m in PackageReference().Matches(Read(project.File)))
            if (m.Groups[1].Value.StartsWith(ExtPrefix, StringComparison.Ordinal))
                set.Add(m.Groups[1].Value);
        return set;
    }

    /// <summary>Which declared types/members each package owns — read from the package.</summary>
    private static class PackageSurface
    {
        private static readonly Dictionary<string, HashSet<string>?> Cache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        ///     Every public type the package's net10.0 assembly declares, by full name.
        /// </summary>
        /// <remarks>
        ///     Loaded reflectively from the file on disk, not referenced: the guard has
        ///     to work for a package the test project does not reference. Only names
        ///     under <c>Microsoft.Extensions.</c> and <c>System.Net.Http</c> are kept —
        ///     the latter because that is where <c>IHttpClientFactory</c> lives, and it
        ///     is the type that decides App.Cli's Http reference.
        /// </remarks>
        private static HashSet<string> DeclaredTypes(string dir)
        {
            var found = new HashSet<string>(StringComparer.Ordinal);

            var dll = Directory.GetFiles(dir, "*.dll", SearchOption.AllDirectories)
                .FirstOrDefault(f => f.Contains($"{Path.DirectorySeparatorChar}net10.0{Path.DirectorySeparatorChar}",
                                                StringComparison.Ordinal)
                                && !Path.GetFileName(f).Contains("Resources.", StringComparison.Ordinal));
            if (dll is null)
                return found;

            try
            {
                using var reader = new PEReader(File.OpenRead(dll));
                if (!reader.HasMetadata)
                    return found;
                var md = reader.GetMetadataReader();
                foreach (var def in md.TypeDefinitions)
                {
                    if (!def.IsPublic)
                        continue;
                    var ns = md.GetString(def.Namespace);
                    if (ns.Length == 0)
                        continue;   // <Module> and the like
                    var full = ns + "." + md.GetString(def.Name);
                    if (full.StartsWith(ExtPrefix, StringComparison.Ordinal)
                        || full.StartsWith("System.Net.Http.", StringComparison.Ordinal))
                        found.Add(full);
                }
            }
            catch (BadImageFormatException)
            {
                return found;       // a satellite or native asset: nothing to read
            }
            catch (IOException)
            {
                return found;
            }

            return found;
        }

        internal static HashSet<string>? Read(string package)
        {
            lock (Cache)
            {
                if (Cache.TryGetValue(package, out var hit))
                    return hit;
            }

            var dir = PackageDir(package);
            HashSet<string>? result = null;
            if (dir is not null)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var xml in Directory.GetFiles(dir, "*.xml", SearchOption.AllDirectories))
                {
                    if (!xml.Contains($"{Path.DirectorySeparatorChar}net10.0{Path.DirectorySeparatorChar}"))
                        continue;
                    XDocument doc;
                    try { doc = XDocument.Parse(Read(xml)); }
                    catch (XmlException) { continue; }

                    foreach (var member in doc.Descendants("member"))
                    {
                        var name = member.Attribute("name")?.Value;
                        if (name is null)
                            continue;
                        // T: / M: -> kept FULLY QUALIFIED, so a name can be resolved
                        // against a using-scope instead of matched blindly.
                        // "NullLogger`1" and "{T:Type}" are normalized to their
                        // generic-free / undecorated form.
                        if (name.StartsWith("T:", StringComparison.Ordinal))
                        {
                            Add(names, name[2..]);
                        }
                        // M: is an extension or instance member. It binds to a
                        // package through its OWNER's namespace, so keeping the
                        // owner.member pair lets LogInformation and AddSimpleConsole
                        // resolve the way the compiler resolves them. Inherited BCL
                        // members (System.IDisposable.Dispose) are excluded by the same
                        // prefix test — attributing those to Logging.Console is what
                        // makes a probe call Dispose a dependency.
                        else if (name.StartsWith("M:", StringComparison.Ordinal))
                        {
                            Add(names, name[2..]);
                        }
                    }
                }

                // The XML doc file is NOT a complete surface, and the gap is
                // load-bearing. Microsoft.Extensions.Http mentions IHttpClientFactory
                // 64 times and gives it no T: entry at all, because the interface is
                // undocumented. A surface built from the XML alone cannot see the one
                // type tests/Harbor.App.Cli.Tests actually binds, so it would declare
                // App.Cli's Microsoft.Extensions.Http reference vestigial — and
                // deleting it breaks a project the test job runs on every PR. The
                // assembly's own TypeDef table is the ground truth, so it is unioned in.
                foreach (var declared in DeclaredTypes(dir))
                    names.Add(declared);

                result = names.Count > 0 ? names : null;
            }

            lock (Cache) { Cache[package] = result; }
            return result;
        }
    }

    /// <summary>The net10.0 dependency group — the transitive routes.</summary>
    private static class NuspecDependencies
    {
        private static readonly Dictionary<string, HashSet<string>> Cache = new(StringComparer.OrdinalIgnoreCase);

        internal static HashSet<string> Read(string package)
        {
            lock (Cache)
            {
                if (Cache.TryGetValue(package, out var hit))
                    return hit;
            }

            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dir = PackageDir(package);
            var nuspec = dir is null ? null : Directory.GetFiles(dir, "*.nuspec").FirstOrDefault();
            if (nuspec is not null)
            {
                XDocument? doc = null;
                try { doc = XDocument.Parse(Read(nuspec)); }
                catch (XmlException) { /* a malformed nuspec: no routes to report */ }

                if (doc is not null)
                {
                    foreach (var group in doc.Descendants("group"))
                    {
                        if (group.Attribute("targetFramework")?.Value is not { } tfm)
                            continue;
                        if (!tfm.Contains("net10.0", StringComparison.Ordinal))
                            continue;
                        foreach (var dep in group.Elements("dependency"))
                            if (dep.Attribute("id")?.Value is { } id)
                                result.Add(id.Value);
                        break;
                    }
                }
            }

            lock (Cache) { Cache[package] = result; }
            return result;
        }
    }

    /// <summary>What the project can see with the reference present, and without it.</summary>
    private static (HashSet<string> Before, HashSet<string> After) Closures(
        Project project, string without, Dictionary<string, HashSet<string>> deps)
    {
        var roots = new HashSet<string>(project.PackageReferences, StringComparer.OrdinalIgnoreCase);
        // Directory.Build.targets adds TUnit to every *.Tests — a visible reference is
        // not a real one, and a count that reads csproj text alone counts the wrong set.
        if (project.Name.EndsWith(".Tests", StringComparison.Ordinal))
            roots.Add("TUnit");

        var after = new HashSet<string>(roots, StringComparer.OrdinalIgnoreCase);
        after.Remove(without);
        return (Walk(roots, deps), Walk(after, deps));
    }

    private static HashSet<string> Walk(IEnumerable<string> roots,
                                        Dictionary<string, HashSet<string>> deps)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>(roots);
        while (stack.Count > 0)
        {
            var p = stack.Pop();
            if (!seen.Add(p))
                continue;
            if (deps.TryGetValue(p, out var next))
                foreach (var n in next)
                    stack.Push(n);
        }
        return seen;
    }

    // ---------------------------------------------------------------- projects

    // Named File/Dir rather than Path/Directory on purpose: a member called
    // `Directory` shadows System.IO.Directory for every expression inside the
    // type, which silently retargets bare `Directory.Exists(...)` calls. The
    // first CI run of this file failed exactly that way.
    private sealed record Project(string File, string Dir, string Name, string Relative,
                                  IReadOnlyList<string> PackageReferences);

    private static IEnumerable<Project> AllProjects(string root)
    {
        foreach (var r in SourceRoots)
        {
            var dir = Path.Combine(root, r);
            if (!Directory.Exists(dir))
                continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.csproj", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}contrib{Path.DirectorySeparatorChar}",
                                  StringComparison.Ordinal))
                    continue;
                yield return ReadProject(file, root);
            }
        }
    }

    private static Project ReadProject(string file, string root)
    {
        XDocument doc;
        try { doc = XDocument.Parse(Read(file)); }
        catch (XmlException) { doc = new XDocument(new XElement("Project")); }

        var dir = Path.GetDirectoryName(file)!;
        var packages = doc.Descendants("PackageReference")
            .Where(p => p.Attribute("Include") is not null)
            .Select(p => p.Attribute("Include")!.Value)
            .ToList();

        return new Project(file, dir,
                           Path.GetFileNameWithoutExtension(file),
                           Path.GetRelativePath(root, file).Replace('\\', '/'),
                           packages);
    }

    /// <summary>A project's own .cs, plus anything pulled in via Compile Include.</summary>
    private static List<string> ProjectSources(Project project)
    {
        var files = new List<string>();
        foreach (var f in Directory.EnumerateFiles(project.Dir, "*.cs", SearchOption.AllDirectories))
        {
            var parts = f.Split(Path.DirectorySeparatorChar);
            if (parts.Contains("bin") || parts.Contains("obj"))
                continue;
            files.Add(f);
        }

        XDocument doc;
        try { doc = XDocument.Parse(Read(project.File)); }
        catch (XmlException) { return files; }

        foreach (var c in doc.Descendants("Compile"))
        {
            if (c.Attribute("Include")?.Value is not { } inc)
                continue;
            var full = Path.GetFullPath(Path.Combine(project.Dir,
                                                    inc.Replace('\\', Path.DirectorySeparatorChar)));
            if (Directory.Exists(full))
                files.AddRange(Directory.EnumerateFiles(full, "*.cs", SearchOption.AllDirectories));
            else if (File.Exists(full))
                files.Add(full);
        }

        return files.Distinct().ToList();
    }

    private static List<Project> ProjectConsumers(Project project, string root)
    {
        var consumers = new List<Project>();
        foreach (var other in AllProjects(root))
            if (other.File != project.File && References(other, project))
                consumers.Add(other);
        return consumers;
    }

    private static bool References(Project from, Project to)
    {
        XDocument doc;
        try { doc = XDocument.Parse(Read(from.File)); }
        catch (XmlException) { return false; }

        foreach (var r in doc.Descendants("ProjectReference"))
        {
            if (r.Attribute("Include")?.Value is not { } inc)
                continue;
            var full = Path.GetFullPath(Path.Combine(from.Dir,
                                                    inc.Replace('\\', Path.DirectorySeparatorChar)));
            if (!full.EndsWith(".csproj", StringComparison.Ordinal))
                full += ".csproj";
            if (string.Equals(Path.GetFullPath(full), Path.GetFullPath(to.File),
                              StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string? PackageDir(string package)
    {
        var home = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (home is null)
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(profile))
                return null;
            home = Path.Combine(profile, ".nuget", "packages");
        }

        var pinned = ReadPinnedVersion(package);
        var dir = Path.Combine(home, package.ToLowerInvariant(), pinned);
        return Directory.Exists(dir) ? dir : null;
    }

    private static string ReadPinnedVersion(string package)
    {
        var props = Path.Combine(RepositoryRoot(), "Directory.Packages.props");
        if (!File.Exists(props))
            return "0.0.0";
        XDocument doc;
        try { doc = XDocument.Parse(Read(props)); }
        catch (XmlException) { return "0.0.0"; }

        foreach (var v in doc.Descendants("PackageVersion"))
            if (v.Attribute("Include")?.Value == package
                && v.Attribute("Version")?.Value is { } version)
                return version.Value;

        return "0.0.0";
    }

    private static string RepositoryRoot()
        => RepoPaths.RepoRoot ?? throw new InvalidOperationException(
            "Harbor.slnx not found above " + AppContext.BaseDirectory
            + " — this guard walks the repository and cannot run from a published test host.");

    private static string Read(string file) => File.ReadAllText(file, Encoding.UTF8);

    // ----------------------------------------------------------------- regexes

    [GeneratedRegex(@"(?m)^\s*global\s+using\s+(?!static\s)([A-Za-z_][\w.]*)\s*;")]
    private static partial Regex GlobalUsing();

    [GeneratedRegex(@"(?m)^\s*using\s+(static\s+)?([A-Za-z_][\w.]*)\s*;")]
    private static partial Regex Using();

    [GeneratedRegex(@"//[^\n]*")]
    private static partial Regex Comment();

    [GeneratedRegex(@"@""(?:[^""]|"")*""")]
    private static partial Regex Verbatim();

    [GeneratedRegex(@"""(?:\\.|[^""\\\n])*""")]
    private static partial Regex Literal();

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex Name();

    [GeneratedRegex(@"<PackageReference\s+Include=""([^""]+)""")]
    private static partial Regex PackageReference();
}