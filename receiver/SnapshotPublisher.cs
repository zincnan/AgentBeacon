using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;
using AgentBeacon.Shared;

namespace AgentBeacon.Receiver;

/// <summary>
/// Server-side IPC: pushes full snapshot envelopes over Named Pipes to any
/// connected Indicator. HTTP request handling must NEVER wait on pipe I/O.
///
/// Per-client queue uses a bounded Channel with DropOldest, so a slow / stuck
/// Indicator cannot back-pressure POST /api/v1/status: when the queue is full,
/// the oldest not-yet-sent snapshot is discarded in favor of the newest.
///
/// Each connected client owns:
///   - one per-client Channel&lt;byte[]&gt; (capacity 8, DropOldest, single-writer
///     for the accept thread initial enqueue, multi-writer to also accept
///     Upsert-triggered enqueues from HTTP request threads; single reader)
///   - one subscription on the SessionStateStore, whose callback enqueues
///     ONLY to this client's channel
///
/// We deliberately do NOT have a single OnStoreChanged that broadcasts to
/// every client. With N clients that would issue N * N TryWrite calls per
/// Upsert (one fanout per subscription), and each fanout would re-enqueue
/// the same snapshot to every other client as well. The per-client
/// subscription model guarantees exactly one TryWrite per Upsert per
/// client.
///
/// Wire format (per message):
///   4-byte big-endian int32 length prefix
///   length bytes of UTF-8 JSON payload (a SnapshotEnvelope)
/// </summary>
public sealed class SnapshotPublisher : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    private readonly SessionStateStore _store;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly object _clientsLock = new();
    private readonly Dictionary<int, Channel<byte[]>> _clients = new();

    public SnapshotPublisher(SessionStateStore store, string pipeName)
    {
        _store = store;
        _pipeName = pipeName;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        var ct = _cts.Token;
        int clientId = 0;
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            int id;
            Channel<byte[]> channel;
            try
            {
                server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.Out,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                id = Interlocked.Increment(ref clientId);
                channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(capacity: 8)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    // SingleReader: ServeClientAsync is the only reader.
                    // SingleWriter = false is a conservative choice now
                    // that the enqueue paths for one client are actually
                    // serialized by SessionStateStore's lock (the
                    // per-client subscription's initialEnqueue and its
                    // onChange callback both run under that lock, in
                    // store-order). We keep SingleWriter=false because
                    // (a) future code paths may legitimately want to
                    // enqueue from outside the lock and (b) TryWrite is
                    // already lock-free and DropOldest.
                    SingleReader = true,
                    SingleWriter = false,
                });

                lock (_clientsLock)
                {
                    _clients[id] = channel;
                }

                // Per-client subscription. The onChange callback enqueues
                // ONLY to this client's channel — no cross-client
                // broadcast. This gives exactly one TryWrite per Upsert
                // per client, regardless of how many other clients are
                // connected.
                //
                // SubscribeWithInitial atomically registers the subscriber
                // AND enqueues the current snapshot for this specific
                // client, all under the store lock. Because the initial
                // enqueue completes before this method returns, any
                // concurrent Upsert that has not yet acquired the store
                // lock will fire onChange AFTER the initial enqueue; any
                // Upsert that has already acquired the lock either
                // completed before us (its update is reflected in the
                // initial snapshot we just enqueued) or will queue behind
                // us (its callback fires after we release).
                //
                // Net effect: this client's channel receives snapshots in
                // strict store-order, never ending on a snapshot older
                // than its initial.
                var subscription = _store.SubscribeWithInitial(
                    onChange: snap => EnqueueToClient(id, snap),
                    initialEnqueue: snap => EnqueueToClient(id, snap));
                // Keep subscription alive for the client's lifetime;
                // ServeClientAsync disposes it on disconnect.
                _ = Task.Run(() => ServeClientAsync(id, server, channel, subscription, ct));
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                break;
            }
            catch (Exception)
            {
                server?.Dispose();
                // Loop continues; the next WaitForConnectionAsync will retry.
            }
        }
    }

    /// <summary>
    /// Serialize + TryWrite to a single specific client's Channel. Called
    /// from (a) the accept thread's initialEnqueue under the store lock and
    /// (b) the per-client onChange callback running on whichever HTTP
    /// request thread drove the Upsert. Both are TryWrite (DropOldest,
    /// non-blocking). The channel's own lock-free TryWrite serializes
    /// correctly without us needing an additional _clientsLock here.
    /// </summary>
    private void EnqueueToClient(int id, IReadOnlyList<SessionSnapshot> sessions)
    {
        Channel<byte[]>? ch;
        lock (_clientsLock)
        {
            if (!_clients.TryGetValue(id, out ch)) return;
        }
        var envelope = new SnapshotEnvelope
        {
            Type = IpcConstants.SnapshotType,
            Sessions = sessions,
        };
        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOpts);
        // DropOldest: TryWrite always succeeds when full by evicting.
        if (!ch.Writer.TryWrite(payload))
        {
            // Channel closed mid-write: ignore; loop will exit.
        }
    }

    private async Task ServeClientAsync(int id, NamedPipeServerStream server, Channel<byte[]> channel, IDisposable subscription, CancellationToken ct)
    {
        try
        {
            // Send loop: drain the channel, write length-prefixed frames.
            // 4-byte length prefix is tiny — use a heap array to avoid CA2014
            // (stackalloc inside an await-foreach is flagged even though each
            // iteration is not a tight loop).
            var header = new byte[4];
            await foreach (var payload in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (payload.Length > int.MaxValue)
                {
                    throw new InvalidOperationException("payload too large");
                }
                int len = payload.Length;
                header[0] = (byte)((len >> 24) & 0xFF);
                header[1] = (byte)((len >> 16) & 0xFF);
                header[2] = (byte)((len >> 8) & 0xFF);
                header[3] = (byte)(len & 0xFF);

                await server.WriteAsync(header).ConfigureAwait(false);
                await server.WriteAsync(payload).ConfigureAwait(false);
                await server.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
        catch (IOException)
        {
            // client gone
        }
        catch
        {
            // best-effort; close
        }
        finally
        {
            // Unsubscribe FIRST so no further Upsert callback enqueues to
            // a channel that is about to be closed.
            try { subscription.Dispose(); } catch { }
            channel.Writer.TryComplete();
            try { server.Dispose(); } catch { }
            lock (_clientsLock)
            {
                _clients.Remove(id);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _acceptLoop.ConfigureAwait(false); } catch { }

        // Close all client channels so their send loops exit.
        Channel<byte[]>[] channels;
        lock (_clientsLock)
        {
            channels = _clients.Values.ToArray();
            _clients.Clear();
        }
        foreach (var c in channels)
        {
            c.Writer.TryComplete();
        }
        _cts.Dispose();
    }
}
