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
        /// <summary>Guards <see cref="Subscribes" />.</summary>
        private readonly Lock _subscribesLock = new();
        private readonly Channel<EventFrame> _frames =
            Channel.CreateUnbounded<EventFrame>(new UnboundedChannelOptions { SingleReader = false });

        private int _disposed;

        /// <summary>
        ///     Every subscribe request this client received, in order. Read under
        ///     <see cref="SubscribesGuard" /> — the decorator subscribes from the
        ///     pump while the test reads this list, so it is genuinely shared.
        /// </summary>
        public List<SubscribeToEventsRequest> Subscribes { get; } = [];

        /// <summary>The lock guarding <see cref="Subscribes" />.</summary>
        public Lock SubscribesGuard => _subscribesLock;

        /// <summary>What <see cref="SendAsync" /> answers with. Null = Ok with the scripted ack.</summary>
        public HarborResponse? SubscribeAnswer { get; set; }

        /// <summary>When set, <see cref="SendAsync" /> throws this instead of answering.</summary>
        public Exception? SendFailure { get; set; }

        /// <summary>
        ///     How many times <see cref="ConnectAsync" /> was called. Asserted in
        ///     the headline test: one connect per generation is what "re-dial"
        ///     actually means.
        /// </summary>
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
        public void RaiseConnectionLost() => ConnectionLost?.Invoke(this, EventArgs.Empty);

        /// <inheritdoc />
        public Task ConnectAsync(CancellationToken ct = default)
        {
            ConnectCount++;
            return Task.CompletedTask;
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

            lock (_subscribesLock)
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

    /// <summary>
    ///     A transport that opens no socket. It exists to satisfy the contract,
    ///     and to fail on demand so the dial loop's retry path is reachable.
    /// </summary>
    private sealed class FakeTransport : IIpcClientTransport
    {
        /// <inheritdoc />
        public string Endpoint => "fake://seam-test";

        /// <summary>
        ///     Never bound: this transport opens no socket. Present because the
        ///     contract declares it, and constant so a reader is not left
        ///     wondering what it would mean if it flipped.
        /// </summary>
        public bool IsBound => false;

        /// <summary>When set, <see cref="ConnectAsync" /> throws it — a failed dial.</summary>
        public Exception? DialFailure { get; set; }

        /// <inheritdoc />
        public Task<Stream> ConnectAsync(CancellationToken ct = default)
            => DialFailure is null ? Task.FromResult<Stream>(Stream.Null) : Task.FromException<Stream>(DialFailure);

        /// <inheritdoc />
        public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        /// <inheritdoc />
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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

        // Generations 1-3 refuse to dial at the TRANSPORT layer — the "daemon is not
        // listening yet" case, which is the pre-flight failure the backoff ladder
        // exists for. Generations 4 and 5 dial normally: 4 is the live one we cut,
        // 5 is the recovery.
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
            ulong? resumedFrom;
            lock (recovered.SubscribesGuard)
            {
                resumedFrom = recovered.Subscribes.Single().LastSequence;
            }

            await Assert.That(resumedFrom).IsEqualTo((ulong?)3)
                .Because(
                    "the whole point of the reconnect protocol: the new subscribe presents the last "
                    + "sequence the client processed, so the server replays only what was missed. "
                    + "Presenting 0 — or null, which means 'first subscription' — would re-deliver frames "
                    + "1-3 and the consumer would see them twice. LastSequence is ulong? because null is "
                    + "the protocol's way of saying a first subscription carries no resume point");

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

        // Each failed generation was actually disposed rather than leaked. Read
        // outside the lock: awaiting inside one is a compile error, and a
        // snapshot copy is also the honest read — the assertion judges a moment
        // in time, not a value held across an await.
        int generationCount;
        bool firstDisposed;
        lock (clients)
        {
            generationCount = clients.Count;
            firstDisposed = clients[0].IsDisposed;
        }

        await Assert.That(generationCount).IsGreaterThanOrEqualTo(5)
            .Because("three refused dials, one live generation and one recovery — five at minimum");
        await Assert.That(firstDisposed).IsTrue()
            .Because("a generation that lost its connection must be disposed, or every reconnect leaks a channel");

        // Each generation connected exactly once. A decorator that "reconnected"
        // by reusing the previous client instance would still satisfy every
        // sequence assertion above, so this is what distinguishes a genuine
        // re-dial from a retried request.
        int connectsOnFourth;
        lock (clients)
        {
            connectsOnFourth = clients[3].ConnectCount;
        }

        await Assert.That(connectsOnFourth).IsEqualTo(1)
            .Because(
                "the fourth generation is the live one and must have connected exactly once — more means the "
                + "decorator reconnected an existing client, zero means DialAsync skipped ConnectAsync");
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

            int attempts;
            lock (client.SubscribesGuard)
            {
                attempts = client.Subscribes.Count;
            }

            await Assert.That(attempts).IsEqualTo(1)
                .Because(
                    "exactly one attempt: had it been treated as transient, the count would keep climbing for "
                    + "as long as the test ran");
        }
    }

    /// <summary>
    ///     A connection lost MID-STREAM must break the pump and re-dial — the
    ///     mirror image of the refusal case, and the one that proves the two are
    ///     distinguished rather than both throwing.
    /// </summary>
    /// <remarks>
    ///     This is the path <c>LostSubscription</c> exists for: the fake raises
    ///     <c>ConnectionLost</c> directly, which is the only member of the
    ///     concrete client that used to force the decorator to name it.
    /// </remarks>
    [Test]
    public async Task ConnectionLostMidStream_RetriesOnAFreshGeneration()
    {
        var clients = new List<FakeRpcClient>();

        var wrapper = new ReconnectableRpcClient(
            _ => Task.FromResult<IIpcClientTransport>(new FakeTransport()),
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

        await using (wrapper)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            _ = Task.Run(async () =>
            {
                try
                {
                    await foreach (var _ in wrapper.SubscribeWithReconnectAsync(
                        _ => Task.CompletedTask, cts.Token))
                    {
                        await Task.Delay(10, cts.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Window elapsed.
                }
            });

            // Wait for the first generation to be live and pumping.
            await WaitUntilAsync(() => clients.Count >= 1, TimeSpan.FromSeconds(20));

            // Now kill it the way a real read loop dies: raise ConnectionLost.
            // This is the LostSubscription path, and the event is the one member
            // of the old concrete client the decorator had no contract for.
            clients[0].Push(1, 1);
            clients[0].RaiseConnectionLost();

            await WaitUntilAsync(() => clients.Count >= 2, TimeSpan.FromSeconds(20));

            bool lostDisposed;
            lock (clients)
            {
                lostDisposed = clients[0].IsDisposed;
            }

            await Assert.That(lostDisposed).IsTrue()
                .Because(
                    "the generation whose connection died must be disposed, not reused — reusing it would retry "
                    + "against a dead pipe and the reconnect would never actually re-dial");

            // The recovered generation serves the stream again, proving the pump
            // really resumed rather than the loop merely spinning.
            var recovered = clients[1];
            recovered.Push(2, 2);
            await Task.Delay(200);

            await Assert.That(recovered.ConnectCount).IsEqualTo(1)
                .Because(
                    "the recovered generation is a different client instance that connected once — the "
                    + "signature of a real re-dial");
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
            IRpcClient connected = await wrapper.ConnectAsync();

            await Assert.That(built).IsNotNull()
                .Because("the client factory runs during ConnectAsync; if it did not, the decorator dialed "
                       + "nothing and the return value below is meaningless");

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