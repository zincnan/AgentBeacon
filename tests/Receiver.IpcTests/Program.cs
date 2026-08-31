using System.Diagnostics;
using System.IO.Pipes;
using AgentBeacon.Shared;

namespace AgentBeacon.Receiver.IpcTests;

public static class Program
{
    private static int _failed;

    public static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("Ipc_NoPipeFlag_PostStillWorks",                NoPipe_PostStillWorks),
            ("Ipc_Connect_GetsInitialSnapshot",              Connect_GetsInitialSnapshot),
            ("Ipc_Update_IsPushed",                          Update_IsPushed),
            ("Ipc_Disconnect_ReceiverKeepsServing",         Disconnect_ReceiverKeepsServing),
            ("Ipc_Reconnect_GetsCurrentSnapshot",            Reconnect_GetsCurrentSnapshot),
            ("Ipc_MultiSession_AllVisible",                  MultiSession_AllVisible),
            ("Ipc_SlowConsumer_DoesNotBlockPost",            SlowConsumer_DoesNotBlockPost),
            ("Ipc_Burst_50Events_AllDelivered",              Burst_50Events_AllDelivered),
            ("Ipc_Cli_UnknownFlagRejected",                  Cli_UnknownFlagRejected),
            ("Ipc_Cli_PipeRequiresValue",                    Cli_PipeRequiresValue),
            ("Ipc_Cli_NoPipeDisablesIpc",                    Cli_NoPipeDisablesIpc),
            ("Store_LastReceivedWins_ReplacesPrior",         Store_LastReceivedWins_ReplacesPrior),
            ("Ipc_ConcurrentPosts_DoesNotCorrupt",           Ipc_ConcurrentPosts_DoesNotCorrupt),
            // Round 2 close-out: race coverage — connect pipe BEFORE the
            // concurrent POST storm, then verify the final received frame
            // matches /debug/sessions exactly (no out-of-order snapshot
            // can leave the client ending on a stale view).
            ("Ipc_ConnectBeforeConcurrentPosts_FinalMatchesStore", Ipc_ConnectBeforeConcurrentPosts_FinalMatchesStore),

            // Round 2 close-out: multi-client fanout must deliver exactly
            // one snapshot per Upsert per client, not N*N broadcasts.
            ("Ipc_MultiClient_OneSnapshotPerUpsertPerClient",     Ipc_MultiClient_OneSnapshotPerUpsertPerClient),
        };

        var stopwatch = Stopwatch.StartNew();
        foreach (var (name, run) in tests)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await run();
                sw.Stop();
                Console.WriteLine($"  PASS  {name}  ({sw.ElapsedMilliseconds}ms)");
            }
            catch (Exception ex)
            {
                sw.Stop();
                _failed++;
                Console.WriteLine($"  FAIL  {name}  ({sw.ElapsedMilliseconds}ms)");
                Console.WriteLine($"        {ex.GetType().Name}: {ex.Message}");
                if (ex.StackTrace is not null)
                {
                    var first = ex.StackTrace.Split('\n').FirstOrDefault()?.Trim();
                    if (first is not null) Console.WriteLine($"        at {first}");
                }
            }
        }
        stopwatch.Stop();

        Console.WriteLine();
        Console.WriteLine($"  {tests.Length - _failed}/{tests.Length} passed in {stopwatch.Elapsed.TotalSeconds:F1}s");
        return _failed == 0 ? 0 : 1;
    }

    // ---- scenarios ----

    private static async Task NoPipe_PostStillWorks()
    {
        await using var h = await IpcTestHarness.StartAsync(
            token: "tok", debug: false, withPipe: false);
        var r = await h.PostStatusAsync(new
        {
            session_id = "s1", agent = "x", status = "running",
        });
        Assert(r.IsSuccessStatusCode, $"POST returned {(int)r.StatusCode}");
    }

    private static async Task Connect_GetsInitialSnapshot()
    {
        await using var h = await IpcTestHarness.StartAsync(token: "tok");

        // Pre-seed state via HTTP so the initial snapshot is non-empty.
        var post = await h.PostStatusAsync(new
        {
            session_id = "alpha", agent = "claude-code", status = "running",
            host = "host-a", message = "hello",
        });
        Assert(post.IsSuccessStatusCode, $"seed POST failed: {(int)post.StatusCode}");

        await using var pipe = h.ConnectPipe();
        var env = await h.ReadNextEnvelopeAsync(pipe);
        Assert(env.Type == IpcConstants.SnapshotType, $"type={env.Type}");
        Assert(env.Sessions.Count == 1, $"sessions={env.Sessions.Count}");
        var s = env.Sessions[0];
        Assert(s.SessionId == "alpha", $"sid={s.SessionId}");
        Assert(s.Agent == "claude-code", $"agent={s.Agent}");
        Assert(s.Host == "host-a", $"host={s.Host}");
        Assert(s.Message == "hello", $"message={s.Message}");
        Assert(s.Status == "running", $"status={s.Status}");
    }

    private static async Task Update_IsPushed()
    {
        await using var h = await IpcTestHarness.StartAsync(token: "tok");
        var post1 = await h.PostStatusAsync(new
        {
            session_id = "s1", agent = "x", status = "running",
        });
        Assert(post1.IsSuccessStatusCode, "post1 failed");

        await using var pipe = h.ConnectPipe();
        // Drain initial.
        var initial = await h.ReadNextEnvelopeAsync(pipe);
        Assert(initial.Sessions.Count == 1, $"initial.sessions={initial.Sessions.Count}");

        var post2 = await h.PostStatusAsync(new
        {
            session_id = "s1", agent = "x", status = "completed",
        });
        Assert(post2.IsSuccessStatusCode, "post2 failed");

        var next = await h.ReadNextEnvelopeAsync(pipe, TimeSpan.FromSeconds(8));
        Assert(next.Sessions.Count == 1, $"next.sessions={next.Sessions.Count}");
        Assert(next.Sessions[0].Status == "completed", $"next.status={next.Sessions[0].Status}");
        Assert(next.Sessions[0].UpdatedAt >= initial.Sessions[0].UpdatedAt,
            "updated_at should advance");
    }

    private static async Task Disconnect_ReceiverKeepsServing()
    {
        await using var h = await IpcTestHarness.StartAsync(token: "tok");

        var pipe1 = h.ConnectPipe();
        var env1 = await h.ReadNextEnvelopeAsync(pipe1);
        Assert(env1.Sessions.Count == 0, "expected empty initial snapshot");

        // Close client; receiver must not crash.
        pipe1.Close();

        // POST should still succeed.
        var post = await h.PostStatusAsync(new
        {
            session_id = "after-disc", agent = "x", status = "running",
        });
        Assert(post.IsSuccessStatusCode, "POST after disconnect failed");

        // New client should get the new state.
        await using var pipe2 = h.ConnectPipe();
        var env2 = await h.ReadNextEnvelopeAsync(pipe2);
        Assert(env2.Sessions.Count == 1, $"after reconnect sessions={env2.Sessions.Count}");
        Assert(env2.Sessions[0].SessionId == "after-disc", $"sid={env2.Sessions[0].SessionId}");
    }

    private static async Task Reconnect_GetsCurrentSnapshot()
    {
        await using var h = await IpcTestHarness.StartAsync(token: "tok");

        var r = await h.PostStatusAsync(new
        {
            session_id = "p", agent = "y", status = "approval",
        });
        Assert(r.IsSuccessStatusCode, "post failed");

        await using var pipe = h.ConnectPipe();
        var env = await h.ReadNextEnvelopeAsync(pipe);
        Assert(env.Sessions.Count == 1, "expected 1 session in initial snapshot");
        Assert(env.Sessions[0].Status == "approval", "expected approval status");
    }

    private static async Task MultiSession_AllVisible()
    {
        await using var h = await IpcTestHarness.StartAsync(token: "tok");
        for (int i = 0; i < 5; i++)
        {
            var r = await h.PostStatusAsync(new
            {
                session_id = $"s{i}", agent = "agent-x", status = "running",
            });
            Assert(r.IsSuccessStatusCode, $"post s{i} failed");
        }

        await using var pipe = h.ConnectPipe();
        var env = await h.ReadNextEnvelopeAsync(pipe);
        Assert(env.Sessions.Count == 5, $"expected 5 sessions, got {env.Sessions.Count}");
        var ids = env.Sessions.Select(s => s.SessionId).OrderBy(x => x).ToList();
        Assert(ids.SequenceEqual(new[] { "s0", "s1", "s2", "s3", "s4" }),
            $"ids=[{string.Join(",", ids)}]");
    }

    private static async Task SlowConsumer_DoesNotBlockPost()
    {
        await using var h = await IpcTestHarness.StartAsync(token: "tok");

        // Connect a pipe client and NEVER read from it.
        var pipe = h.ConnectPipe();

        // Burst many updates; each POST should complete quickly even though
        // the client is not draining its Channel<>.
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++)
        {
            var r = await h.PostStatusAsync(new
            {
                session_id = "slow", agent = "x", status = "running",
            });
            Assert(r.IsSuccessStatusCode, $"post {i} failed");
        }
        sw.Stop();
        Assert(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"20 POSTs took {sw.Elapsed.TotalSeconds:F1}s; should be < 10s");
        // Average per POST should be much smaller; this catches a regression
        // where the publisher accidentally blocks HTTP.
        Assert(sw.ElapsedMilliseconds / 20 < 1000,
            $"avg per POST = {sw.ElapsedMilliseconds / 20}ms");

        // Cleanup the unread pipe.
        try { pipe.Close(); } catch { }
    }

    private static async Task Burst_50Events_AllDelivered()
    {
        await using var h = await IpcTestHarness.StartAsync(token: "tok");
        await using var pipe = h.ConnectPipe();

        // Drain the initial empty snapshot.
        var init = await h.ReadNextEnvelopeAsync(pipe);
        Assert(init.Sessions.Count == 0, "expected empty initial");

        for (int i = 0; i < 50; i++)
        {
            var r = await h.PostStatusAsync(new
            {
                session_id = "burst", agent = "x", status = i % 2 == 0 ? "running" : "approval",
            });
            Assert(r.IsSuccessStatusCode, $"post {i} failed");
        }

        // Read frames until we see status=approval (the last event).
        // DropOldest means we may not see every single intermediate, but the
        // final state must be present and consistent.
        SnapshotEnvelope? last = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                last = await h.ReadNextEnvelopeAsync(pipe, TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException) { break; }
            if (last.Sessions.Count == 1 && last.Sessions[0].Status == "approval") break;
        }
        Assert(last is not null, "no frame received");
        Assert(last.Sessions.Count == 1, $"last.sessions={last.Sessions.Count}");
        Assert(last.Sessions[0].SessionId == "burst", "last.sid");
        Assert(last.Sessions[0].Status == "approval", $"last.status={last.Sessions[0].Status}");
    }

    private static async Task Cli_UnknownFlagRejected()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var dll = IpcTestHarness.FindReceiverDllForCli();
        Assert(dll is not null, "could not find agentbeacon-receiver.dll");
        psi.ArgumentList.Add(dll);
        psi.ArgumentList.Add("--definitely-not-a-flag");

        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync();
        Assert(p.ExitCode == 4, $"expected exit 4, got {p.ExitCode}");
    }

    private static async Task Cli_PipeRequiresValue()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var dll = IpcTestHarness.FindReceiverDllForCli();
        Assert(dll is not null, "could not find agentbeacon-receiver.dll");
        psi.ArgumentList.Add(dll);
        psi.ArgumentList.Add("--pipe");

        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync();
        Assert(p.ExitCode == 4, $"expected exit 4, got {p.ExitCode}");
    }

    private static async Task Cli_NoPipeDisablesIpc()
    {
        // With --no-pipe, the receiver should still serve HTTP, but we can't
        // easily test "no pipe" except by trying to connect and failing.
        await using var h = await IpcTestHarness.StartAsync(
            token: "tok", withPipe: false);
        var r = await h.PostStatusAsync(new
        {
            session_id = "x", agent = "y", status = "running",
        });
        Assert(r.IsSuccessStatusCode, "POST failed");

        // Connecting to the pipe name should fail. We attempt with a short
        // timeout; on Linux NamedPipeClientStream.Connect times out, on
        // Windows it throws TimeoutException. Either way, the call returns
        // without a successful connection.
        var threw = false;
        try
        {
            using var c = new NamedPipeClientStream(".", h.PipeName, PipeDirection.In);
            c.Connect(500);
            Assert(!c.IsConnected, "pipe unexpectedly connected when disabled");
        }
        catch (TimeoutException)
        {
            threw = true;
        }
        catch (IOException)
        {
            threw = true;
        }
        Assert(threw, "expected pipe connection to fail when IPC is disabled");
    }

    private static Task Store_LastReceivedWins_ReplacesPrior()
    {
        // Direct in-process check on SessionStateStore + SnapshotPublisher
        // semantics: no per-field merging; status replaces prior in full.
        var store = new SessionStateStore();
        store.Upsert(new SessionSnapshot
        {
            SessionId = "a", Agent = "x", Status = "running", UpdatedAt = DateTimeOffset.UtcNow,
        });
        store.Upsert(new SessionSnapshot
        {
            SessionId = "a", Agent = "x", Status = "completed", Message = "done",
            UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(1),
        });
        var snap = store.CurrentSnapshot();
        Assert(snap.Count == 1, "expected 1 session");
        Assert(snap[0].Status == "completed", $"status={snap[0].Status}");
        Assert(snap[0].Message == "done", $"message={snap[0].Message}");

        int fired = 0;
        var sub = store.SubscribeWithInitial(_ => fired++, _ => { });
        store.Upsert(new SessionSnapshot
        {
            SessionId = "b", Agent = "y", Status = "running", UpdatedAt = DateTimeOffset.UtcNow,
        });
        Assert(fired == 1, $"subscriber fired {fired} times");
        sub.Dispose();
        return Task.CompletedTask;
    }

    private static async Task Ipc_ConcurrentPosts_DoesNotCorrupt()
    {
        // Many parallel POSTs across multiple sessions / statuses land
        // without crashing the Receiver, and the final snapshot the
        // pipe delivers is internally consistent. This is the
        // post-storm-only scenario — the pipe is connected AFTER all
        // POSTs. For the race between connect-time and concurrent
        // updates, see Ipc_ConnectBeforeConcurrentPosts_FinalMatchesStore.
        await using var h = await IpcTestHarness.StartAsync(token: "tok");

        const int sessionCount = 8;
        const int postsPerSession = 25;

        var tasks = new List<Task>();
        for (int s = 0; s < sessionCount; s++)
        {
            int sid = s;
            tasks.Add(Task.Run(async () =>
            {
                for (int i = 0; i < postsPerSession; i++)
                {
                    var status = (i % 4) switch
                    {
                        0 => "running",
                        1 => "approval",
                        2 => "completed",
                        _ => "failed",
                    };
                    var r = await h.PostStatusAsync(new
                    {
                        session_id = $"s{sid}",
                        agent = "load",
                        status,
                    });
                    if (!r.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException(
                            $"post failed: {(int)r.StatusCode} s{sid} i{i} {status}");
                    }
                }
            }));
        }
        await Task.WhenAll(tasks);

        // Verify the store reflects 8 valid entries.
        var debug = await h.DebugSessionsAsync();
        Assert(debug.Length == sessionCount, $"expected {sessionCount} sessions, got {debug.Length}");
        foreach (var entry in debug)
        {
            var status = entry.GetProperty("status").GetString();
            var validStatuses = new[] { "running", "approval", "completed", "failed" };
            Assert(validStatuses.Contains(status),
                $"session {entry} has invalid status '{status}'");
        }

        // And the pipe can still deliver a final snapshot.
        await using var pipe = h.ConnectPipe();
        var env = await h.ReadNextEnvelopeAsync(pipe);
        Assert(env.Sessions.Count == sessionCount, $"snapshot count={env.Sessions.Count}");
        Assert(env.Sessions.All(s => s.Status is "running" or "approval" or "completed" or "failed"),
            "all statuses valid");
    }

    private static async Task Ipc_ConnectBeforeConcurrentPosts_FinalMatchesStore()
    {
        // Race coverage: connect the pipe BEFORE the concurrent POST
        // storm, drain frames as they arrive, then verify that the
        // LAST received frame's session set and statuses match
        // /debug/sessions exactly. This catches the bug where the
        // initial-snapshot enqueue could be overtaken by a concurrent
        // Upsert's enqueue, leaving the client on an older snapshot.
        await using var h = await IpcTestHarness.StartAsync(token: "tok");

        await using var pipe = h.ConnectPipe();
        // Drain the initial empty snapshot.
        var init = await h.ReadNextEnvelopeAsync(pipe);
        Assert(init.Sessions.Count == 0, "expected empty initial snapshot");

        const int sessionCount = 8;
        const int postsPerSession = 20;

        var tasks = new List<Task>();
        for (int s = 0; s < sessionCount; s++)
        {
            int sid = s;
            tasks.Add(Task.Run(async () =>
            {
                for (int i = 0; i < postsPerSession; i++)
                {
                    var status = (i % 4) switch
                    {
                        0 => "running",
                        1 => "approval",
                        2 => "completed",
                        _ => "failed",
                    };
                    var r = await h.PostStatusAsync(new
                    {
                        session_id = $"s{sid}",
                        agent = "race",
                        status,
                    });
                    if (!r.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException(
                            $"post failed: {(int)r.StatusCode} s{sid} i{i} {status}");
                    }
                }
            }));
        }

        // Concurrently drain frames while POSTs are running.
        var frames = new List<SnapshotEnvelope>();
        var drainer = Task.Run(async () =>
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    var env = await h.ReadNextEnvelopeAsync(pipe, TimeSpan.FromSeconds(2));
                    frames.Add(env);
                    // Stop when we've seen at least one frame for every
                    // session (i.e. the post-storm snapshot has been
                    // delivered at least once).
                    if (frames.Count > 0 && frames[^1].Sessions.Count == sessionCount) { }
                }
                catch (TimeoutException) { break; }
            }
        });

        await Task.WhenAll(tasks);
        await drainer;

        Assert(frames.Count > 0, "no frames received at all");

        // The /debug/sessions view is the ground truth for the final store.
        var debug = await h.DebugSessionsAsync();
        Assert(debug.Length == sessionCount, $"debug count={debug.Length}");

        // The LAST frame received must match /debug/sessions exactly:
        //   - same session_ids
        //   - same statuses
        //   - same agents
        var last = frames[^1];
        Assert(last.Sessions.Count == debug.Length,
            $"last frame count {last.Sessions.Count} != debug count {debug.Length}");

        var bySid = last.Sessions.ToDictionary(s => s.SessionId, StringComparer.Ordinal);
        foreach (var entry in debug)
        {
            var sid = entry.GetProperty("session_id").GetString()!;
            Assert(bySid.ContainsKey(sid), $"last frame missing session_id '{sid}'");
            var ls = bySid[sid];
            var debugStatus = entry.GetProperty("status").GetString()!;
            var debugAgent = entry.GetProperty("agent").GetString()!;
            Assert(ls.Status == debugStatus,
                $"session {sid}: last frame status '{ls.Status}' != debug status '{debugStatus}'");
            Assert(ls.Agent == debugAgent,
                $"session {sid}: last frame agent '{ls.Agent}' != debug agent '{debugAgent}'");
        }

        // Also assert that the final frame is "monotone" in the sense
        // that its updated_at is at least as large as the debug
        // updated_at for every session. (DropOldest can drop
        // intermediate frames but never an UPDATE after the matching
        // POST returned, because the publisher enqueues under the
        // store lock.)
        var updatedAtBySid = debug.ToDictionary(
            e => e.GetProperty("session_id").GetString()!,
            e => e.GetProperty("updated_at").GetDateTimeOffset());
        foreach (var s in last.Sessions)
        {
            Assert(s.UpdatedAt >= updatedAtBySid[s.SessionId],
                $"session {s.SessionId}: last frame updated_at {s.UpdatedAt:o} < debug updated_at {updatedAtBySid[s.SessionId]:o}");
        }
    }

    private static void Assert(bool cond, string msg)
    {
        if (!cond) throw new InvalidOperationException("assertion failed: " + msg);
    }

    private static async Task Ipc_MultiClient_OneSnapshotPerUpsertPerClient()
    {
        // Round 2 close-out: with N clients connected, one POST must
        // deliver EXACTLY one new snapshot per client, not N broadcasts
        // to all clients (which would be the N*N fanout bug).
        await using var h = await IpcTestHarness.StartAsync(token: "tok");

        // Connect two pipe clients. Drain each's initial empty snapshot
        // BEFORE any POST, so the only frames left in their channels are
        // the ones produced by our single POST below.
        await using var pipeA = h.ConnectPipe();
        var initA = await h.ReadNextEnvelopeAsync(pipeA);
        Assert(initA.Sessions.Count == 0, "A initial empty");
        await using var pipeB = h.ConnectPipe();
        var initB = await h.ReadNextEnvelopeAsync(pipeB);
        Assert(initB.Sessions.Count == 0, "B initial empty");

        // One POST.
        var post = await h.PostStatusAsync(new
        {
            session_id = "x", agent = "agent-x", status = "running",
        });
        Assert(post.IsSuccessStatusCode, "post failed");

        // Drain one frame from each side; both must contain the same
        // single session.
        var envA = await h.ReadNextEnvelopeAsync(pipeA, TimeSpan.FromSeconds(5));
        var envB = await h.ReadNextEnvelopeAsync(pipeB, TimeSpan.FromSeconds(5));
        Assert(envA.Sessions.Count == 1, $"A.count={envA.Sessions.Count}");
        Assert(envB.Sessions.Count == 1, $"B.count={envB.Sessions.Count}");
        Assert(envA.Sessions[0].SessionId == "x", "A sid");
        Assert(envB.Sessions[0].SessionId == "x", "B sid");
        Assert(envA.Sessions[0].Status == "running", "A status");
        Assert(envB.Sessions[0].Status == "running", "B status");
        Assert(envA.Sessions[0].UpdatedAt == envB.Sessions[0].UpdatedAt,
            "A and B must see the same updated_at for this single Upsert");

        // Critical: there must be NO duplicate frame delivered for this
        // single POST. With a 1-second short timeout, any second frame on
        // either side (from the N*N fanout bug) would arrive.
        var dupA = false;
        var dupB = false;
        try
        {
            await h.ReadNextEnvelopeAsync(pipeA, TimeSpan.FromSeconds(1));
            dupA = true;
        }
        catch (TimeoutException) { }
        try
        {
            await h.ReadNextEnvelopeAsync(pipeB, TimeSpan.FromSeconds(1));
            dupB = true;
        }
        catch (TimeoutException) { }
        Assert(!dupA, $"client A received a duplicate snapshot for one POST");
        Assert(!dupB, $"client B received a duplicate snapshot for one POST");
    }
}
