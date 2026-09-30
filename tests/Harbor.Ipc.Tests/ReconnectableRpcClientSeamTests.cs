using System.Threading.Channels;
using Harbor.Ipc.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbor.Ipc.Tests;

/// <summary>
///     Issue #494, "Done looks like", first bullet: the reconnect test drives
///     fail → backoff → reconnect → success cycles with a FAKE transport and a
///     FAKE inner client. No MessagePack types, no named pipe, no
///     <c>HARBOR_IPC_EVENTSTREAM=1</c>, no server, and no 30-second
///     <c>WaitAsync</c> to keep CI honest.
/// </summary>
/// <remarks>
///     <para>
///         This file is only possible because the decorator names
///         <see cref="IRpcClient" /> instead of <c>MessagePackRpcClient</c>. The
///         backoff ladder, the subscribe-before-snapshot ordering and the
///         sequence dedup are the interesting logic, and before the seam was
///         closed every one of them was reachable only by standing up a real
///         Harbor daemon and cutting a real pipe under it. What was being tested
///         was the pipe; what needed testing was the state machine.
///     </para>
///     <para>
///         <b>Why it runs in CI when the old end-to-end test could not.</b> The
///         IPC named-pipe event-stream class self-skips on Linux behind an env
///         gate (see <c>EventStreamingTests</c> — "timing-flaky under TUnit
///         scheduling"), so on CI the reconnect paths below were unverified.
///         There is no pipe here and nothing to schedule, so these run
///         everywhere. The pipe-based suite stays as the integration complement;
///         this is the unit layer it was standing in for.
///     </para>
/// </remarks>
public class ReconnectableRpcClientSeamTests
{
    // ── The fake inner client ──────────────────────────────────────────────

    /// <summary>
    ///     An <see cref="IRpcClient" /> with no stream under it. Answers
    ///     subscribe requests from a scripted ack and publishes frames from a
    ///     channel the test writes to directly.
    /// </summary>
    private sealed class FakeRpcClient : IRpcClient
    {
        private readonly Channel<EventFrame> _frames =
            Channel.CreateUnbounded<EventFrame>(new UnboundedChannelOptions { SingleReader = false });

        private int _disposed;

        /// <summary>Every subscribe request this client received, in order.</summary>
        public List<SubscribeToEventsRequest> Subscribes { get; } = [];

        /// <summary>What <see cref="SendAsync" /> answers with. Null = Ok with the scripted ack.</summary>
        public HarborResponse? SubscribeAnswer { get; set; }

        /// <summary>When set, <see cref="ConnectAsync" /> throws this instead of connecting.</summary>
        public Exception? ConnectFailure { get; set; }

        /// <summary>When set, <see cref="SendAsync" /> throws this instead of answering.</summary>
        public Exception? SendFailure { get; set; }

        /// <summary>How many times <see cref="ConnectAsync" /> was called.</summary>
        public int ConnectCount { get; private set; }

        /// <summary>True once <see cref="DisposeAsync" /> has run.</summary>
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        /// <inheritdoc />
        public ChannelReader<EventFrame> EventFrames => _frames.Reader;

        /// <inheritdoc />
        public event EventHandler ConnectionLost = delegate { };

        /// <summary>Publish a frame as if the server had sent it.</summary>
        public void Push(ulong sequence, int turn) => _frames.Writer.TryWrite(new EventFrame(sequence, new HarborEvent.TurnStart(turn)));

        /// <summary>Simulate the read loop dying — the "dial again" trigger.</summary>
        public void RaiseConnectionLost() => ConnectionLost.Invoke(this, EventArgs.Empty);

        /// <inheritdoc />
        public Task ConnectAsync(CancellationToken ct = default)
        {
            ConnectCount++;
            return ConnectFailure is null ? Task.CompletedTask : Task.FromException(ConnectFailure);
        }

        /// <inheritdoc />
        public Task<HarborResponse> SendAsync(HarborRequest request, CancellationToken ct = default)
        {
            if (SendFailure is not null)
            {
                return Task.FromException<HarborResponse>(SendFailure);
            }

            if (request is not SubscribeToEventsRequest subscribe)
            {
                return Task.FromResult<HarborResponse>(new ErrorResponse { Message = $"unexpected {request.GetType().Name}" });
            }

            lock (Subscribes)
            {
                Subscribes.Add(subscribe);
            }

            if (SubscribeAnswer is not null)
            {
                return Task.FromResult(SubscribeAnswer);
            }

            // Default: a clean ack with no resync. ServerSequence 0 with
            // ResyncRequired=false is the "you are caught up, go live" answer
            // the decorator already handles (it only loads a snapshot when
            // !_hasSeen || ack.ResyncRequired).
            var ack = new SubscriptionAck(ServerSequence: 0, ResyncRequired: false);
            return Task.FromResult<HarborResponse>(
                new OkResponse { Payload = WireCodec.SerializeDomain(ack) });
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _disposed, 1);
            _frames.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A transport that opens no socket. It exists to satisfy the contract.</summary>
    private sealed class FakeTransport : IIpcClientTransport
    {
        private int _disposed;

        /// <inheritdoc />
        public string Endpoint => "fake://seam-test";

        /// <inheritdoc />
        public bool IsBound { get; private set; }

        /// <summary>When set, <see cref="ConnectAsync" /> throws it — a failed dial.</summary>
        public Exception? DialFailure { get; set; }

        /// <inheritdoc />
        public Task<Stream> ConnectAsync(CancellationToken ct = default)
            => DialFailure is null ? Task.FromResult<Stream>(Stream.Null) : Task.FromException<Stream>(DialFailure);

        /// <inheritdoc />
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _disposed, 1);
            IsBound = false;
            return ValueTask.CompletedTask;
        }
    }

    // ── The tests ──────────────────────────────────────────────────────────

    /// <summary>
    ///     THE HEADLINE. Three fail → backoff → reconnect → success cycles, then
    ///     a clean run: every dial is a fresh transport and a fresh inner client,
    ///     and the stream resumes from <c>lastSeen</c> rather than from zero.
    /// </summary>
    [Test]
    public async Task ThreeFailuresThenSuccess_ReconnectCleanly_AndResumeFromLastSeen()
    {
        var transports = new List<FakeTransport>();
        var clients = new List<FakeRpcClient>();

        // Generations 1-3 refuse to dial at the TRANSPORT layer (the "server is
        // not there yet" case); generation 4 dials, generation 5 dials too so the
        // third failure can be an in-flight one rather than only a pre-flight.
        var wrapper = new ReconnectableRpcClient(
            _ =>
            {
                var transport = new FakeTransport();
                lock (transports)
                {
                    transports.Add(transport);
                    if (transports.Count <= 3)
                    {
                        transport.DialFailure = new IOException("daemon not listening");
                    }
                }

                return Task.FromResult<IIpcClientTransport>(transport);
            },
            _ =>
            {
                var client = new FakeRpcClient();
                lock (clients)
                {
                    clients.Add(client);
                }

                return client;
            },
            NullLogger.Instance);

        var received = new List<EventFrame>();
        var snapshotCalls = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await using (wrapper)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await foreach (var frame in wrapper.SubscribeWithReconnectAsync(
                        _ => { Interlocked.Increment(ref snapshotCalls); return Task.CompletedTask; },
                        cts.Token))
                    {
                        lock (received)
                        {
                            received.Add(frame);
                            if (received.Count >= 3)
                            {
                                break;
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Window elapsed; the assertions judge the outcome.
                }
            });

            // Wait until generation 4 is the live one (three failed dials first).
            await WaitUntilAsync(() => clients.Count >= 4, TimeSpan.FromSeconds(20));

            // Generation 4 is the live connection: publish three ordered frames.
            var live = clients[3];
            live.Push(1, 1);
            live.Push(2, 2);
            live.Push(3, 3);

            await WaitUntilAsync(
                () => { lock (received) { return received.Count >= 3; } },
                TimeSpan.FromSeconds(20));

            // Cut the connection mid-stream. The pump must re-dial (generation 5)
            // and re-subscribe PRESENDING the sequence it already saw.
            await wrapper.CutCurrentConnectionForTestAsync();
            await WaitUntilAsync(() => clients.Count >= 5, TimeSpan.FromSeconds(20));

            var recovered = clients[4];
            lock (recovered.Subscribes)
            {
                var resume = recovered.Subscribes.Single();
                await Assert.That(resume.LastSequence).IsEqualTo(3)
                    .Because(
                        "the whole point of the reconnect protocol: the new subscribe presents the last "
                        + "sequence the client processed, so the server replays only what was missed. "
                        + "Presenting 0 instead would re-deliver frames 1-3 and the consumer would see them "
                        + "twice");
            }

            // The recovered generation continues the sequence rather than
            // restarting it.
            recovered.Push(4, 4);
            await WaitUntilAsync(
                () => { lock (received) { return received.Count >= 4; } },
                TimeSpan.FromSeconds(20));
        }

        // Exactly-once and strictly ordered across the cut.
        List<EventFrame> got;
        lock (received)
        {
            got = [.. received];
        }

        await Assert.That(got.Count).IsEqualTo(4);
        for (int i = 1; i < got.Count; i++)
        {
            await Assert.That(got[i].Sequence).IsGreaterThan(got[i - 1].Sequence)
                .Because($"frame {i} must not be a duplicate or a regression — the dedup is by monotonic sequence");
        }

        // Snapshot exactly once: the first subscribe only. Re-subscribing after
        // a cut must NOT reload it, or every reconnect would be a full reload.
        await Assert.That(Volatile.Read(ref snapshotCalls)).IsEqualTo(1)
            .Because(
                "the replay path covers the gap. Reloading the snapshot on each reconnect would make the "
                + "protocol's replay machinery dead code and would double-apply every event it re-reads");

        // Each failed generation was actually disposed rather than leaked.
        lock (clients)
        {
            await Assert.That(clients.Count).IsGreaterThanOrEqualTo(5);
            await Assert.That(clients[0].IsDisposed).IsTrue()
                .Because("a generation that lost its connection must be disposed, or every reconnect leaks a channel");
        }
    }

    /// <summary>
    ///     A refused SUBSCRIBE is protocol-level, not transient: the decorator
    ///     must fail loudly instead of spinning on the backoff ladder forever.
    /// </summary>
    [Test]
    public async Task RefusedSubscribe_ThrowsInsteadOfSpinning()
    {
        var client = new FakeRpcClient
        {
            SubscribeAnswer = new ErrorResponse { Message = "psk gate" },
        };

        var wrapper = new ReconnectableRpcClient(
            _ => Task.FromResult<IIpcClientTransport>(new FakeTransport()),
            _ => client,
            NullLogger.Instance);

        await using (wrapper)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await foreach (var _ in wrapper.SubscribeWithReconnectAsync(_ => Task.CompletedTask, cts.Token))
                {
                    // No frames: the fake never publishes.
                    await Task.Delay(10, cts.Token);
                }
            });

            await Assert.That(thrown.Message).Contains("psk gate")
                .Because(
                    "an ErrorResponse to a subscribe is a refusal (bad PSK, unknown request), not a dropped "
                    + "connection. Retrying it forever would hide the real cause behind a reconnect loop");

            await Assert.That(client.Subscribes.Count).IsEqualTo(1)
                .Because(
                    "exactly one attempt: had it been treated as transient, the count would keep climbing for "
                    + "as long as the test ran");
        }
    }

    /// <summary>
    ///     A failure AFTER the subscribe must be treated as transient and
    ///     retried — the mirror image of the refusal case, and the one that
    ///     proves the two are distinguished rather than both throwing.
    /// </summary>
    [Test]
    public async Task TransientSubscribeFailure_RetriesOnAFreshGeneration()
    {
        var clients = new List<FakeRpcClient>();

        var wrapper = new ReconnectableRpcClient(
            _ => Task.FromResult<IIpcClientTransport>(new FakeTransport()),
            _ =>
            {
                var client = new FakeRpcClient();
                // Only the FIRST generation's send fails; later ones are clean.
                if (clients.Count == 0)
                {
                    client.SendFailure = new IOException("pipe reset mid-handshake");
                }

                lock (clients)
                {
                    clients.Add(client);
                }

                return client;
            },
            NullLogger.Instance);

        await using (wrapper)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var snapshotCalls = 0;

            _ = Task.Run(async () =>
            {
                try
                {
                    await foreach (var _ in wrapper.SubscribeWithReconnectAsync(
                        _ => { Interlocked.Increment(ref snapshotCalls); return Task.CompletedTask; },
                        cts.Token))
                    {
                        await Task.Delay(10, cts.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Window elapsed.
                }
            });

            await WaitUntilAsync(() => clients.Count >= 2, TimeSpan.FromSeconds(20));

            lock (clients)
            {
                await Assert.That(clients[0].IsDisposed).IsTrue()
                    .Because("the failed generation is dropped, not reused — reusing it would retry against a dead pipe");
                await Assert.That(clients[1].SendFailure).IsNull()
                    .Because("the second generation was constructed clean, which is what 're-dial' has to mean");
            }

            // The clean generation now serves frames.
            clients[1].Push(1, 1);
            await WaitUntilAsync(() => Volatile.Read(ref snapshotCalls) >= 1, TimeSpan.FromSeconds(20));
        }
    }

    /// <summary>
    ///     <see cref="ReconnectableRpcClient.ConnectAsync" /> hands back the
    ///     CONTRACT, so a caller holding it cannot reach past the seam. This is
    ///     the type-level swap the issue's "Done looks like" asks for, asserted
    ///     as a signature rather than as prose.
    /// </summary>
    [Test]
    public async Task ConnectAsync_ReturnsTheContractNotTheImplementation()
    {
        FakeRpcClient? built = null;
        var wrapper = new ReconnectableRpcClient(
            _ => Task.FromResult<IIpcClientTransport>(new FakeTransport()),
            _ => built = new FakeRpcClient(),
            NullLogger.Instance);

        await using (wrapper)
        {
            var connected = await wrapper.ConnectAsync();
            await Assert.That(connected).IsNotNull();
            await Assert.That(built).IsNotNull();
            await Assert.That(connected).IsSameReferenceAs(built)
                .Because("the decorator must return the injected client itself, not a copy or a re-dial");
        }

        // Declared return type, read from IL rather than from source text: this
        // is the property the guard (#494) pins structurally.
        var returnType = typeof(ReconnectableRpcClient)
            .GetMethod(nameof(ReconnectableRpcClient.ConnectAsync))!
            .ReturnType;

        await Assert.That(returnType).IsEqualTo(typeof(Task<IRpcClient>))
            .Because(
                "ConnectAsync returned Task<MessagePackRpcClient> before #494, which handed every caller the "
                + "implementation and made the decorator impossible to swap in. The return type is the seam");
    }

    // ── Plumbing ───────────────────────────────────────────────────────────

    /// <summary>
    ///     Poll until <paramref name="condition" /> holds. Bounded, so a broken
    ///     reconnect loop fails the test instead of hanging the run — the failure
    ///     mode this file exists to eliminate.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException(
            $"reconnect condition not met within {timeout.TotalSeconds:0.#}s — the decorator did not "
            + "recover, which is the failure this file exists to make visible instead of hanging");
    }
}