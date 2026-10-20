using System.Reflection;
using Harbor.Abstractions.Models;
using Harbor.Abstractions.Sessions;
using Harbor.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Storage.Tests;

/// <summary>
///     Guard for #1831 — the checkpoint/rewind axis of the SQLite backend moves
///     out of <see cref="SqliteSessionStore" /> into a
///     <c>SqliteCheckpointStore</c>, the mirror of the shipped JSONL slice
///     (#1513 / PR #1716). The move is a pure relocation, and this file pins
///     the two things it must not change.
///     <para>
///         <b>1. THE IMPL SITE.</b> <see cref="ISessionCheckpointStore" /> stays
///         implemented by <see cref="SqliteSessionStore" />, and the three
///         members stay DECLARED on it — the store keeps them as one-line
///         forwarders over the axis. If a later edit moves the implementation
///         onto the axis, or rewrites the forwarders as explicit interface
///         implementations, the impl site moves: every consumer that reaches
///         for the concrete store (<c>apps/Harbor.App.Cli/Commands/
///         SessionCheckpointRunner.cs</c> does it through the interface, but the
///         concrete type is what DI hands out) loses the surface.
///     </para>
///     <para>
///         <b>2. THE BEHAVIOUR.</b> The whole checkpoint surface stays
///         reachable through the interface and answers exactly as before: the
///         anchor's index, the recorded checkpoint, the truncated history, the
///         honest trail marker, and the shared "not found" failure texts.
///     </para>
///     <para>
///         Written BEFORE the move (the repo rule: guard first, rewrite
///         second), so it is a regression guard and not a description of the
///         new shape.
///     </para>
/// </summary>
[ParallelLimiter<SqliteStoreLimit>]
public class SqliteCheckpointStoreImplSiteTests
{
    private static SqliteSessionStore Create(out string dbPath)
    {
        dbPath = Path.Combine(Path.GetTempPath(), $"harbor-sqlite-ckpt-site-{Guid.NewGuid():N}.db");
        return new SqliteSessionStore(dbPath, NullLogger<SqliteSessionStore>.Instance);
    }

    private static void Cleanup(string dbPath)
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(dbPath)) File.Delete(dbPath);
    }

    private static UserMessage Msg(string sessionId, int i) => new(
        $"msg-{i:D2}-{Guid.NewGuid():N}",
        sessionId,
        DateTimeOffset.UtcNow.AddMilliseconds(i * 5), // distinct stamps: stable ordering
        $"message {i}",
        "code",
        "test-model");

    private static async Task<List<string>> SeedAsync(SqliteSessionStore store, string sessionId, int count)
    {
        var ids = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            var message = Msg(sessionId, i);
            await store.AppendMessageAsync(sessionId, message);
            ids.Add(message.Id);
        }

        return ids;
    }

    /// <summary>
    ///     The impl site stays on the store: it implements the checkpoint seam
    ///     and DECLARES all three members itself.
    /// </summary>
    [Test]
    public async Task Store_Declares_The_Checkpoint_Surface()
    {
        Type store = typeof(SqliteSessionStore);

        await Assert.That(store.GetInterfaces().Any(i => i == typeof(ISessionCheckpointStore))).IsTrue();

        string[] members =
        [
            nameof(ISessionCheckpointStore.CreateCheckpointAsync),
            nameof(ISessionCheckpointStore.ListCheckpointsAsync),
            nameof(ISessionCheckpointStore.RewindToCheckpointAsync),
        ];

        foreach (string name in members)
        {
            MethodInfo? declared = store.GetMethod(
                name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            await Assert.That(declared is not null).IsTrue();
            await Assert.That(declared!.DeclaringType == store).IsTrue();
        }
    }

    /// <summary>
    ///     The surface behaves the same through the interface and through the
    ///     concrete store: the anchor's index, the recorded checkpoint, the
    ///     truncation, the honest trail and the shared failure texts.
    /// </summary>
    [Test]
    public async Task Checkpoint_Surface_Behaves_The_Same_Through_The_Interface()
    {
        var store = Create(out string dbPath);
        try
        {
            ISessionCheckpointStore seam = store;
            var session = (await store.CreateAsync("/proj", "code", "test", "test-model")).Value;
            var ids = await SeedAsync(store, session.Id, 3);

            var marked = await seam.CreateCheckpointAsync(session.Id, ids[1], "before-move");
            await Assert.That(marked.IsSuccess).IsTrue();
            await Assert.That(marked.Value.MessageId).IsEqualTo(ids[1]);
            await Assert.That(marked.Value.MessageIndex).IsEqualTo(1);
            await Assert.That(marked.Value.Label).IsEqualTo("before-move");
            await Assert.That(marked.Value.IsRewindTrail).IsFalse();

            // The same call on the concrete store answers the same way.
            var viaStore = await store.CreateCheckpointAsync(session.Id, ids[2], "second");
            await Assert.That(viaStore.IsSuccess).IsTrue();
            await Assert.That(viaStore.Value.MessageIndex).IsEqualTo(2);

            var listed = await seam.ListCheckpointsAsync(session.Id);
            await Assert.That(listed.IsSuccess).IsTrue();
            await Assert.That(listed.Value.Count).IsEqualTo(2);
            await Assert.That(listed.Value[0].Id).IsEqualTo(marked.Value.Id);
            await Assert.That(listed.Value[1].Id).IsEqualTo(viaStore.Value.Id);

            // The rewind truncates to the anchor and leaves the honest trail.
            var rewound = await seam.RewindToCheckpointAsync(session.Id, marked.Value.Id);
            await Assert.That(rewound.IsSuccess).IsTrue();
            await Assert.That(rewound.Value.Removed).IsEqualTo(1);
            await Assert.That(rewound.Value.Remaining).IsEqualTo(2);
            await Assert.That(rewound.Value.MessageId).IsEqualTo(ids[1]);

            // The dropped future takes its own checkpoint with it, and the
            // trail marker is appended in its place.
            var after = await store.ListCheckpointsAsync(session.Id);
            await Assert.That(after.Value.Count).IsEqualTo(2);
            await Assert.That(after.Value[0].Id).IsEqualTo(marked.Value.Id);
            await Assert.That(after.Value[1].IsRewindTrail).IsTrue();
            await Assert.That(after.Value[1].RewindOf).IsEqualTo(marked.Value.Id);
            await Assert.That(after.Value[1].Removed).IsEqualTo(1);

            // The shared failure texts do not move either.
            var missing = await seam.ListCheckpointsAsync("no-such-session");
            await Assert.That(missing.IsFailure).IsTrue();
            await Assert.That(missing.Error).IsEqualTo("Session 'no-such-session' not found.");

            var missingCheckpoint = await seam.RewindToCheckpointAsync(session.Id, "no-such-checkpoint");
            await Assert.That(missingCheckpoint.IsFailure).IsTrue();
            await Assert.That(missingCheckpoint.Error)
                .IsEqualTo($"Checkpoint 'no-such-checkpoint' not found in session '{session.Id}'.");
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    /// <summary>
    ///     The store's forwarders really do route to the axis: the store holds
    ///     ONE <c>SqliteCheckpointStore</c>, and a write through the store's
    ///     forwarder is visible to the axis (and the other way round). The
    ///     forwarder is a hand-off, not a second implementation.
    /// </summary>
    [Test]
    public async Task Store_Forwards_The_Checkpoint_Calls_To_The_Axis()
    {
        var store = Create(out string dbPath);
        try
        {
            SqliteCheckpointStore? axis = ReadAxis(store);
            await Assert.That(axis is not null).IsTrue();
            await Assert.That(axis!.GetType().IsSealed).IsTrue();
            await Assert.That(axis.GetType().IsNotPublic).IsTrue();
            await Assert.That(axis is ISessionCheckpointStore).IsTrue();

            var session = (await store.CreateAsync("/proj", "code", "test", "test-model")).Value;
            var ids = await SeedAsync(store, session.Id, 3);

            // Written through the STORE's forwarder...
            var marked = await store.CreateCheckpointAsync(session.Id, ids[1], "through-store");
            await Assert.That(marked.IsSuccess).IsTrue();

            // ... and read back through the AXIS: the forwarder is not a copy.
            var listedOnAxis = await axis.ListCheckpointsAsync(session.Id);
            await Assert.That(listedOnAxis.IsSuccess).IsTrue();
            await Assert.That(listedOnAxis.Value.Count).IsEqualTo(1);
            await Assert.That(listedOnAxis.Value[0].Id).IsEqualTo(marked.Value.Id);

            // Written through the AXIS...
            var markedOnAxis = await axis.CreateCheckpointAsync(session.Id, ids[2], "through-axis");
            await Assert.That(markedOnAxis.IsSuccess).IsTrue();

            // ... and read back through the STORE's forwarder.
            var listedOnStore = await store.ListCheckpointsAsync(session.Id);
            await Assert.That(listedOnStore.IsSuccess).IsTrue();
            await Assert.That(listedOnStore.Value.Count).IsEqualTo(2);
            await Assert.That(listedOnStore.Value[1].Id).IsEqualTo(markedOnAxis.Value.Id);
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    /// <summary>
    ///     The axis the store delegates to: the store's single
    ///     <c>SqliteCheckpointStore</c> field.
    /// </summary>
    private static SqliteCheckpointStore? ReadAxis(SqliteSessionStore store)
    {
        FieldInfo? field = typeof(SqliteSessionStore)
            .GetField("_checkpoints", BindingFlags.NonPublic | BindingFlags.Instance);
        return field?.GetValue(store) as SqliteCheckpointStore;
    }
}
