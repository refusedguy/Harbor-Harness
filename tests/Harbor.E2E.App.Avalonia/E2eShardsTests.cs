using System.Reflection;
using Harbor.E2E.Framework;
using TUnit.Assertions;

namespace Harbor.E2E.App.Avalonia;

/// <summary>
///     Keeps the two-process split of this project honest (#1790).
/// </summary>
/// <remarks>
///     <para>
///         The split itself is described in <see cref="E2eShards" />; this
///         class is the guard that makes it impossible to add a test class to
///         this project and leave it out of both shards. A class that is in
///         neither shard would run in whichever process discovered it — which
///         is how a class would end up executed twice, or (worse) executed in
///         a shard that does not own it, next to a class that was expecting
///         the screen to be empty.
///     </para>
///     <para>
///         The guard is deliberately a STATIC reflection check: it touches no
///         Avalonia state, so it runs in the unkeyed bucket and does not take
///         a slot in the serialised <c>e2e-framework</c> chain.
///     </para>
/// </remarks>
public class E2eShardsTests
{
    /// <summary>
    ///     Every class in this project that shares the one Avalonia session is
    ///     in exactly one shard, and the two shards do not overlap.
    /// </summary>
    [Test]
    public async Task Every_Serialized_Class_Belongs_To_Exactly_One_Shard()
    {
        var expected = SerializedClassNames();
        expected.AddRange(FrameworkTestClassNames());

        var shard0 = E2eShards.Shard0;
        var shard1 = E2eShards.Shard1;
        var union = shard0.Concat(shard1).ToList();

        await Assert.That(union.Distinct().Count())
            .IsEqualTo(union.Count)
            .Because(
                "A class listed in BOTH shards would be discovered by two processes and run twice — "
                + "and, worse, two classes that share the one Avalonia window would land in the same "
                + "process, which is the race the e2e-framework key exists to prevent.");

        await Assert.That(string.Join(",", union.OrderBy(n => n, StringComparer.Ordinal)))
            .IsEqualTo(string.Join(",", expected.OrderBy(n => n, StringComparer.Ordinal)))
            .Because(
                "The shard table is the whole split: every class that holds the e2e-framework key must "
                + "appear in exactly one shard, every test class TUnit discovers in this process must "
                + "appear in exactly one shard (a class in neither is silently dropped from CI), and no "
                + "class that does not run here may appear in one. A new test class in this project has "
                + "to be assigned to a shard.");
    }

    /// <summary>
    ///     No class in this project holds the BARE form. The bare form means
    ///     "runs completely alone" — a global mutex over the whole assembly —
    ///     and this project's isolation is a NAMED group, not a global one.
    /// </summary>
    [Test]
    public async Task No_Class_In_This_Project_Holds_The_Bare_Global_Form()
    {
        var bare = new List<string>();

        foreach (Type type in typeof(E2eShards).Assembly.GetTypes())
        {
            NotInParallelAttribute? attribute = type.GetCustomAttribute<NotInParallelAttribute>();
            if (attribute is null)
            {
                continue;
            }

            if (attribute.ConstraintKeys.Length == 0)
            {
                bare.Add(type.Name);
            }
        }

        await Assert.That(string.Join(",", bare.OrderBy(n => n, StringComparer.Ordinal)))
            .IsEqualTo(string.Empty)
            .Because(
                "The bare [NotInParallel] is TUnit's global form: it holds the assembly's NotInParallel "
                + "lock, so the class runs COMPLETELY alone and every other test in the project waits for "
                + "it. This project already serialises itself with a named key ('e2e-framework'), which "
                + "is the right shape for a resource whose competitors are enumerable; a bare attribute "
                + "on top of it would add nothing but the whole-assembly stall.");
    }

    /// <summary>
    ///     Each shard's filter expression names exactly that shard's classes.
    /// </summary>
    [Test]
    public async Task Each_Shards_Filter_Names_Exactly_That_Shards_Classes()
    {
        foreach (IReadOnlyList<string> shard in E2eShards.All)
        {
            string filter = E2eShards.FilterFor(shard);

            await Assert.That(filter.StartsWith("/*/*/(", StringComparison.Ordinal)).IsTrue();
            await Assert.That(filter.EndsWith(")/*", StringComparison.Ordinal)).IsTrue();

            foreach (string className in shard)
            {
                await Assert.That(filter.Contains($"({className})", StringComparison.Ordinal))
                    .IsTrue()
                    .Because(
                        $"The filter for a shard must select that shard's classes and no others; "
                        + $"{className} is in the shard but not in its filter expression.");
            }
        }
    }

    /// <summary>
    ///     The classes in this project that hold the shared Avalonia session's
    ///     constraint key, by simple name.
    /// </summary>
    private static List<string> SerializedClassNames()
    {
        var names = new List<string>();

        foreach (Type type in typeof(E2eShards).Assembly.GetTypes())
        {
            NotInParallelAttribute? attribute = type.GetCustomAttribute<NotInParallelAttribute>();
            if (attribute is null)
            {
                continue;
            }

            if (attribute.ConstraintKeys.Contains(E2eShards.ConstraintKey, StringComparer.Ordinal))
            {
                names.Add(type.Name);
            }
        }

        return names;
    }

    /// <summary>
    ///     The test classes this process ALSO runs because they live in the
    ///     referenced <c>Harbor.E2E.Framework</c> test project. TUnit
    ///     discovers a referenced test project's classes in every project that
    ///     references it, so they are part of this run — and a shard filter
    ///     that did not name them would drop them from CI.
    /// </summary>
    private static List<string> FrameworkTestClassNames()
    {
        var names = new List<string>();

        Assembly framework = typeof(E2eTestBase).Assembly;

        foreach (Type type in framework.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface)
            {
                continue;
            }

            bool hasTest = type.GetMethods()
                .Any(method => method.GetCustomAttribute<TestAttribute>() is not null);

            if (hasTest)
            {
                names.Add(type.Name);
            }
        }

        return names;
    }
}
