using System.IO.Pipes;
using System.Text.Json;
using AgentBeacon.Shared;

namespace AgentBeacon.Indicator.Core;

/// <summary>
/// Indicator-side pipe client. Connects to the Receiver's Named Pipe,
/// reads length-prefixed SnapshotEnvelope frames, and dispatches them via
/// OnSnapshot. Auto-reconnects with backoff if the pipe drops.
///
/// Wire format (matches SnapshotPublisher):
///   4-byte big-endian int32 length
///   length bytes UTF-8 JSON
/// </summary>
public sealed class PipeClient : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Read exactly <paramref name="count"/> bytes from <paramref name="stream"/>,
    /// looping over short reads. Returns <c>true</c> if exactly
    /// <paramref name="count"/> bytes were assembled; <c>false</c> if the
    /// stream reached EOF before any bytes were read (clean disconnect).
    /// Throws <see cref="IOException"/> if EOF was hit after at least one
    /// byte (truncated frame).
    /// </summary>
    public static async Task<bool> ReadExactlyOrEofAsync(
        Stream stream, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        int total = 0;
        while (total < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(offset + total, count - total), ct)
                .ConfigureAwait(false);
            if (n == 0)
            {
                if (total == 0) return false; // clean EOF
                throw new IOException(
                    $"truncated read: expected {count} bytes, got {total} before EOF");
            }
            total += n;
        }
        return true;
    }

    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public event Action<SnapshotEnvelope>? OnSnapshot;
    public event Action<Exception>? OnError;

    /// <summary>Number of consecutive failed connect attempts (for diagnostics).</summary>
    public int ReconnectAttempts { get; private set; }

    public PipeClient(string pipeName)
    {
        _pipeName = pipeName;
        _loop = Task.Run(ConnectLoopAsync);
    }

    private async Task ConnectLoopAsync()
    {
        var ct = _cts.Token;
        var backoff = TimeSpan.FromMilliseconds(200);
        var maxBackoff = TimeSpan.FromSeconds(5);

        while (!ct.IsCancellationRequested)
        {
            NamedPipeClientStream? pipe = null;
            try
            {
                pipe = new NamedPipeClientStream(
                    ".", _pipeName, PipeDirection.In, PipeOptions.Asynchronous);

                // NamedPipeClientStream.Connect throws TimeoutException on
                // hard timeout on Windows; on Linux, .NET 9+ also surfaces
                // TimeoutException for the timeout overload.
                await pipe.ConnectAsync((int)TimeSpan.FromSeconds(5).TotalMilliseconds, ct)
                    .ConfigureAwait(false);

                // Successful connect resets backoff and counters.
                ReconnectAttempts = 0;
                backoff = TimeSpan.FromMilliseconds(200);

                await ReadLoopAsync(pipe, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { pipe?.Dispose(); } catch { }
                break;
            }
            catch (Exception ex)
            {
                try { OnError?.Invoke(ex); } catch { }
                try { pipe?.Dispose(); } catch { }
                ReconnectAttempts++;
            }

            // Wait before reconnecting; cap at maxBackoff.
            try
            {
                await Task.Delay(backoff, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }

            var next = backoff.TotalMilliseconds * 2;
            backoff = TimeSpan.FromMilliseconds(Math.Min(maxBackoff.TotalMilliseconds, next));
        }
    }

    private async Task ReadLoopAsync(NamedPipeClientStream pipe, CancellationToken ct)
    {
        var lenBuf = new byte[4];
        while (!ct.IsCancellationRequested)
        {
            // Loop until we have exactly 4 bytes for the length prefix.
            // Stream.ReadAsync is permitted to return fewer bytes than
            // requested; a partial header followed by EOF is a truncated
            // frame, not a clean disconnect.
            bool gotHeader = await ReadExactlyOrEofAsync(pipe, lenBuf, 0, 4, ct)
                .ConfigureAwait(false);
            if (!gotHeader) break; // clean EOF / pipe closed by peer

            int len = (lenBuf[0] << 24) | (lenBuf[1] << 16) | (lenBuf[2] << 8) | lenBuf[3];
            if (len <= 0 || len > 1024 * 1024)
                throw new IOException($"bad frame length: {len}");

            var payload = new byte[len];
            // After a successful header that declares len > 0 bytes of
            // payload, an early EOF is a truncated frame, NOT a clean
            // disconnect — the peer promised bytes it never delivered.
            // ReadExactlyOrEofAsync returns false only on EOF before any
            // byte was read, which here is impossible; EOF mid-payload
            // already throws IOException.
            bool gotPayload = await ReadExactlyOrEofAsync(pipe, payload, 0, len, ct)
                .ConfigureAwait(false);
            if (!gotPayload)
            {
                throw new IOException(
                    $"truncated payload: header declared {len} bytes but EOF before any payload byte");
            }

            SnapshotEnvelope? env;
            try
            {
                env = JsonSerializer.Deserialize<SnapshotEnvelope>(payload);
            }
            catch (JsonException ex)
            {
                throw new IOException("malformed snapshot envelope", ex);
            }
            if (env is null) throw new IOException("null envelope");

            try { OnSnapshot?.Invoke(env); } catch { /* swallow */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _loop.ConfigureAwait(false); } catch { }
        _cts.Dispose();
    }

    public void Dispose()
    {
        // Trigger cancellation; don't wait for the loop. The caller can
        // await DisposeAsync() if synchronous shutdown matters.
        try { _cts.Cancel(); } catch { }
    }
}
