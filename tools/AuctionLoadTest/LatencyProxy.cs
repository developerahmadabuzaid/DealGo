using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace AuctionLoadTest;

/// <summary>
/// A TCP relay that holds every chunk of data for a fixed time before passing it on, in both
/// directions. Put between a local API and a local PostgreSQL, it makes the database behave as if
/// it were across a network: each round trip costs twice the delay, while pipelined data still
/// streams (chunks keep their order and are not delayed one behind another).
/// </summary>
static class LatencyProxy
{
    public static async Task<int> RunAsync(Args a)
    {
        var listen = a.Int("listen", 5463);
        var target = a.Get("target", "127.0.0.1:5462").Split(':');
        var delay = TimeSpan.FromMilliseconds(a.Double("delay-ms", 2));

        var listener = new TcpListener(IPAddress.Loopback, listen);
        listener.Start();
        Console.WriteLine($"relaying 127.0.0.1:{listen} → {target[0]}:{target[1]} with {delay.TotalMilliseconds} ms each way");

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };

        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop.Token); }
            catch (OperationCanceledException) { break; }
            _ = RelayAsync(client, target[0], int.Parse(target[1]), delay, stop.Token);
        }
        listener.Stop();
        return 0;
    }

    private static async Task RelayAsync(TcpClient client, string host, int port, TimeSpan delay, CancellationToken ct)
    {
        using (client)
        using (var upstream = new TcpClient { NoDelay = true })
        {
            client.NoDelay = true;
            try
            {
                await upstream.ConnectAsync(host, port, ct);
                var a = Pump(client.GetStream(), upstream.GetStream(), delay, ct);
                var b = Pump(upstream.GetStream(), client.GetStream(), delay, ct);
                await Task.WhenAny(a, b);
            }
            catch
            {
                // One side closed; the using blocks close the other.
            }
        }
    }

    private static async Task Pump(NetworkStream from, NetworkStream to, TimeSpan delay, CancellationToken ct)
    {
        var queue = Channel.CreateUnbounded<(byte[] Data, int Length, long Due)>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        var writer = Task.Run(async () =>
        {
            await foreach (var (data, length, due) in queue.Reader.ReadAllAsync(ct))
            {
                var wait = Clock.Ms(Clock.Ts, due);
                if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait), ct);
                await to.WriteAsync(data.AsMemory(0, length), ct);
            }
        }, ct);

        try
        {
            while (true)
            {
                var buffer = new byte[16384];
                var read = await from.ReadAsync(buffer, ct);
                if (read == 0) break;
                var due = Clock.Ts + (long)(delay.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
                await queue.Writer.WriteAsync((buffer, read, due), ct);
            }
        }
        finally
        {
            queue.Writer.TryComplete();
            try { await writer; } catch { }
        }
    }
}
