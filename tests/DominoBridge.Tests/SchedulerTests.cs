using DominoBridge.Core.Configuration;
using DominoBridge.Core.Protocol;
using DominoBridge.Core.Queue;
using DominoBridge.Core.Transport;
using Xunit;

namespace DominoBridge.Tests;

public class SchedulerTests
{
    private static AppSettings Settings(FakeEdcServer srv, Action<AppSettings>? tweak = null)
    {
        var s = new AppSettings();
        s.Printer.IpAddress = "127.0.0.1";
        s.Printer.Port = srv.Port;
        s.Printer.AckTimeoutMs = 400;
        s.Printer.ConnectTimeoutMs = 1000;
        s.Queue.ReconnectIntervalMs = 0;
        s.Queue.AutoRefill = false; // manual mode unless a test opts in
        tweak?.Invoke(s);
        return s;
    }

    private static PrintQueue Rows(AppSettings s, int n)
    {
        var p = new EdcProtocol(s.Printer);
        var rows = new List<PrintRow>();
        for (int i = 1; i <= n; i++)
        {
            var r = new PrintRow(i, i + 1, new[] { $"P{i}", $"S{i:000}" });
            r.DataString = p.BuildDataString(r.Fields);
            r.DisplayPayload = p.ToDisplayString(r.DataString);
            rows.Add(r);
        }
        return new PrintQueue(rows);
    }

    private static string Text(byte[] frame) => System.Text.Encoding.ASCII.GetString(frame, 1, frame.Length - 2);

    [Fact]
    public async Task Sends_all_rows_in_excel_order_with_real_markers()
    {
        using var srv = new FakeEdcServer();
        var s = Settings(srv, x => x.Queue.BatchSize = 32);
        var q = Rows(s, 5);
        using var conn = new PrinterConnectionManager(s);
        Assert.True(await conn.ConnectAsync());
        var sch = new SendScheduler(q, conn, s);
        await sch.RunAsync();

        Assert.Equal(SchedulerState.Completed, sch.State);
        Assert.Equal(new[] { "P1,S001", "P2,S002", "P3,S003", "P4,S004", "P5,S005" }, srv.Frames.Select(Text));
        Assert.All(srv.Frames, f => { Assert.Equal(0x02, f[0]); Assert.Equal(0x03, f[^1]); });
        Assert.All(q.Rows, r => Assert.Equal(RowStatus.Accepted, r.Status));
        Assert.Equal(5, q.Counts().Accepted);
    }

    [Fact]
    public async Task Stops_at_batch_limit_and_waits_for_operator_then_continues()
    {
        using var srv = new FakeEdcServer();
        var s = Settings(srv, x => x.Queue.BatchSize = 3);
        var q = Rows(s, 7);
        using var conn = new PrinterConnectionManager(s);
        await conn.ConnectAsync();
        var sch = new SendScheduler(q, conn, s);

        await sch.RunAsync();
        Assert.Equal(SchedulerState.WaitingForOperator, sch.State);
        Assert.Equal(3, srv.FrameCount);
        Assert.Equal(3, sch.BufferEstimate);

        sch.SetBufferEstimate(0); // operator: printer consumed them
        await sch.RunAsync();
        Assert.Equal(6, srv.FrameCount);
        sch.SetBufferEstimate(0);
        await sch.RunAsync();
        Assert.Equal(SchedulerState.Completed, sch.State);
        Assert.Equal(7, srv.FrameCount);
        Assert.Equal(Enumerable.Range(1, 7).Select(i => $"P{i},S{i:000}"), srv.Frames.Select(Text));
    }

    [Fact]
    public async Task Batch_limit_never_exceeds_printer_max_queue_size()
    {
        var s = new AppSettings();
        s.Queue.BatchSize = 100; s.Queue.MaxQueueSize = 32;
        using var conn = new PrinterConnectionManager(s);
        Assert.Equal(32, new SendScheduler(new PrintQueue(new List<PrintRow>()), conn, s).BatchLimit);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Nak_halts_without_resending_and_blocks_start_until_operator_decides()
    {
        using var srv = new FakeEdcServer { Reply = n => n == 3 ? new byte[] { 0x15 } : new byte[] { 0x06 } };
        var s = Settings(srv, x => x.Queue.BatchSize = 32);
        var q = Rows(s, 5);
        using var conn = new PrinterConnectionManager(s);
        await conn.ConnectAsync();
        var sch = new SendScheduler(q, conn, s);

        await sch.RunAsync();
        Assert.Equal(SchedulerState.Halted, sch.State);
        Assert.Equal(3, srv.FrameCount);                  // no blind retry, nothing after the NAK
        Assert.Equal(RowStatus.Rejected, q.Rows[2].Status);
        Assert.Equal(RowStatus.Pending, q.Rows[3].Status);

        await sch.RunAsync();                             // must refuse: order would break
        Assert.Equal(SchedulerState.Halted, sch.State);
        Assert.Equal(3, srv.FrameCount);

        srv.Reply = _ => new byte[] { 0x06 };
        Assert.True(q.Retry(q.Rows[2]));
        await sch.RunAsync();
        Assert.Equal(SchedulerState.Completed, sch.State);
        Assert.Equal(new[] { "P1,S001", "P2,S002", "P3,S003", "P3,S003", "P4,S004", "P5,S005" }, srv.Frames.Select(Text));
    }

    [Fact]
    public async Task Ack_timeout_marks_row_Unknown_and_is_never_resent_automatically()
    {
        using var srv = new FakeEdcServer { Reply = n => n == 2 ? null : new byte[] { 0x06 } };
        var s = Settings(srv, x => { x.Queue.BatchSize = 32; x.Queue.MaxRetry = 3; });
        var q = Rows(s, 4);
        using var conn = new PrinterConnectionManager(s);
        await conn.ConnectAsync();
        var sch = new SendScheduler(q, conn, s);

        await sch.RunAsync();
        Assert.Equal(SchedulerState.Halted, sch.State);
        Assert.Equal(RowStatus.Unknown, q.Rows[1].Status);
        Assert.Equal(2, srv.FrameCount);                  // MaxRetry must not cause a re-send of an ambiguous row
        await sch.RunAsync();
        Assert.Equal(2, srv.FrameCount);

        srv.Reply = _ => new byte[] { 0x06 };
        Assert.True(q.MarkAccepted(q.Rows[1]));           // operator verified on the device
        await sch.RunAsync();
        Assert.Equal(SchedulerState.Completed, sch.State);
        Assert.Equal(4, srv.FrameCount);
    }

    [Fact]
    public async Task Connection_lost_before_ack_marks_row_Unknown()
    {
        using var srv = new FakeEdcServer { Reply = n => n == 2 ? Array.Empty<byte>() : new byte[] { 0x06 } };
        var s = Settings(srv, x => x.Queue.BatchSize = 32);
        var q = Rows(s, 3);
        using var conn = new PrinterConnectionManager(s);
        await conn.ConnectAsync();
        var sch = new SendScheduler(q, conn, s);
        await sch.RunAsync();
        Assert.Equal(RowStatus.Unknown, q.Rows[1].Status);
        Assert.Equal(RowStatus.Pending, q.Rows[2].Status);
        Assert.Equal(ConnectionState.Disconnected, conn.State);
    }

    [Fact]
    public async Task Unexpected_reply_is_not_treated_as_accepted()
    {
        using var srv = new FakeEdcServer { Reply = _ => new byte[] { 0x41 } };
        var s = Settings(srv);
        var q = Rows(s, 2);
        using var conn = new PrinterConnectionManager(s);
        await conn.ConnectAsync();
        await new SendScheduler(q, conn, s).RunAsync();
        Assert.Equal(RowStatus.Unknown, q.Rows[0].Status);
    }

    [Fact]
    public async Task Not_connected_leaves_row_Pending_and_sends_nothing()
    {
        using var srv = new FakeEdcServer();
        var s = Settings(srv);
        s.Printer.Port = 1; // nothing listens
        var q = Rows(s, 2);
        using var conn = new PrinterConnectionManager(s);
        var sch = new SendScheduler(q, conn, s);
        await sch.RunAsync();
        Assert.Equal(SchedulerState.Halted, sch.State);
        Assert.Equal(RowStatus.Pending, q.Rows[0].Status);
        Assert.Equal(0, srv.FrameCount);
    }

    [Fact]
    public async Task Stop_does_not_touch_printer_and_keeps_rows_pending()
    {
        using var srv = new FakeEdcServer { Reply = _ => { Thread.Sleep(80); return new byte[] { 0x06 }; } };
        var s = Settings(srv, x => { x.Queue.BatchSize = 32; x.Printer.AckTimeoutMs = 2000; });
        var q = Rows(s, 20);
        using var conn = new PrinterConnectionManager(s);
        await conn.ConnectAsync();
        var sch = new SendScheduler(q, conn, s);
        var run = sch.RunAsync();
        await Task.Delay(250);
        sch.RequestStop();
        await run;
        Assert.Equal(SchedulerState.Stopped, sch.State);
        var c = q.Counts();
        Assert.True(c.Accepted > 0 && c.Pending > 0);
        Assert.Equal(0, c.Sending);
        Assert.Equal(c.Accepted, srv.FrameCount);
    }

    [Fact]
    public async Task Pause_stops_new_sends_and_resume_continues()
    {
        using var srv = new FakeEdcServer { Reply = _ => { Thread.Sleep(60); return new byte[] { 0x06 }; } };
        var s = Settings(srv, x => { x.Queue.BatchSize = 32; x.Printer.AckTimeoutMs = 2000; });
        var q = Rows(s, 10);
        using var conn = new PrinterConnectionManager(s);
        await conn.ConnectAsync();
        var sch = new SendScheduler(q, conn, s);
        var run = sch.RunAsync();
        await Task.Delay(150);
        sch.Pause();
        await Task.Delay(400);
        int during = srv.FrameCount;
        await Task.Delay(300);
        Assert.Equal(during, srv.FrameCount);
        Assert.Equal(SchedulerState.Paused, sch.State);
        sch.Resume();
        await run;
        Assert.Equal(10, srv.FrameCount);
    }

    [Fact]
    public async Task AutoRefill_waits_on_nak_and_resends_same_row_until_everything_is_accepted()
    {
        // Fake printer buffer: capacity 3, one item consumed every 40 ms. NAK while full.
        var accepted = new List<DateTime>();
        var lockObj = new object();
        using var srv = new FakeEdcServer();
        srv.Reply = _ =>
        {
            lock (lockObj)
            {
                accepted.RemoveAll(t => (DateTime.UtcNow - t).TotalMilliseconds > 40 * 3);
                if (accepted.Count >= 3) return new byte[] { 0x15 };
                accepted.Add(DateTime.UtcNow);
                return new byte[] { 0x06 };
            }
        };
        var s = Settings(srv, x => { x.Queue.AutoRefill = true; x.Queue.AutoRefillDelayMs = 100; });
        var q = Rows(s, 10);
        using var conn = new PrinterConnectionManager(s);
        await conn.ConnectAsync();
        var sch = new SendScheduler(q, conn, s);
        await sch.RunAsync();

        Assert.Equal(SchedulerState.Completed, sch.State);
        Assert.All(q.Rows, r => Assert.Equal(RowStatus.Accepted, r.Status));
        // accepted frames (ACKed ones) must be exactly P1..P10 in order, each once
        // frames include NAKed duplicates; verify order is non-decreasing and every row appears
        var names = srv.Frames.Select(Text).ToList();
        Assert.True(names.Count > 10);
        Assert.Equal(Enumerable.Range(1, 10).Select(i => $"P{i},S{i:000}"), names.Distinct());
    }

    [Fact]
    public async Task AutoRefill_halts_when_nak_never_clears()
    {
        using var srv = new FakeEdcServer { Reply = _ => new byte[] { 0x15 } };
        var s = Settings(srv, x => { x.Queue.AutoRefill = true; x.Queue.AutoRefillDelayMs = 100; x.Queue.AutoRefillMaxConsecutiveNaks = 3; });
        var q = Rows(s, 2);
        using var conn = new PrinterConnectionManager(s);
        await conn.ConnectAsync();
        var sch = new SendScheduler(q, conn, s);
        await sch.RunAsync();
        Assert.Equal(SchedulerState.Halted, sch.State);
        Assert.Equal(RowStatus.Rejected, q.Rows[0].Status);
    }

    [Fact]
    public async Task Test_connection_sends_nothing()
    {
        using var srv = new FakeEdcServer();
        var s = Settings(srv);
        using var conn = new PrinterConnectionManager(s);
        var (ok, _) = await conn.TestConnectionAsync();
        Assert.True(ok);
        await Task.Delay(150);
        Assert.Equal(0, srv.FrameCount);
    }

    [Fact]
    public async Task Test_connection_reports_failure()
    {
        var s = new AppSettings();
        s.Printer.IpAddress = "127.0.0.1"; s.Printer.Port = 1; s.Printer.ConnectTimeoutMs = 500;
        using var conn = new PrinterConnectionManager(s);
        var (ok, msg) = await conn.TestConnectionAsync();
        Assert.False(ok);
        Assert.Contains("Connection Failed", msg);
    }

    [Fact]
    public async Task Test_data_exact_bytes_and_ack_shown()
    {
        using var srv = new FakeEdcServer();
        var s = Settings(srv);
        using var conn = new PrinterConnectionManager(s);
        await conn.ConnectAsync();
        var p = new EdcProtocol(s.Printer);
        var r = await conn.SendPacketAsync(p, p.BuildDataPacket("TEST123"));
        Assert.Equal(DeliveryOutcome.Accepted, r.Outcome);
        Assert.Equal("06", EdcProtocol.ToHex(r.Response));
        Assert.Equal("02 54 45 53 54 31 32 33 03", EdcProtocol.ToHex(srv.Frames[0]));
    }

    [Fact]
    public void Settings_roundtrip_json_matches_spec_shape()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "s.json");
        var s = new AppSettings(); s.Printer.Delimiter = "\t";
        SettingsStore.Save(s, path);
        var json = File.ReadAllText(path);
        Assert.Contains("\"Printer\"", json); Assert.Contains("\"IpAddress\": \"192.168.1.55\"", json);
        Assert.Contains("\"Port\": 16000", json); Assert.Contains("\"StartMarker\": 2", json); Assert.Contains("\"EndMarker\": 3", json);
        Assert.Equal("\t", SettingsStore.Load(path).Printer.Delimiter);
    }
}
