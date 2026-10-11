using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

// Test-only encrypted byte relay. It cannot read credentials or modify TLS payloads.
// Chunk delay/fragmentation and connection cuts are NOT IP packet loss or WAN emulation.
internal sealed class TcpFaultProxy : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<int, (TcpClient Client, TcpClient Server)> connections = new();
    private readonly List<Task> workers = [];
    private readonly int upstreamPort;
    private readonly Task accept;
    private TaskCompletionSource? hold;
    private int nextId, delayed, blocked, cuts;
    private long bytes, writes;
    public volatile bool Impaired;
    public int DelayedWrites => Volatile.Read(ref delayed);
    public int BlockedPumps => Volatile.Read(ref blocked);
    public int CutConnections => Volatile.Read(ref cuts);
    public long ForwardedBytes => Interlocked.Read(ref bytes);
    public long Writes => Interlocked.Read(ref writes);
    public string Address { get; }

    public TcpFaultProxy(string upstream)
    {
        var uri = new Uri(upstream);
        if (uri.Scheme != "https" || uri.Host != "127.0.0.1" || uri.AbsolutePath != "/")
            throw new ArgumentException("The fault relay is restricted to loopback HTTPS.");
        upstreamPort = uri.Port;
        listener.Start();
        Address = "https://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
        accept = Accept();
    }

    public void HoldDownstream() => Volatile.Write(ref hold, new(TaskCreationOptions.RunContinuationsAsynchronously));
    public void ReleaseDownstream() => Interlocked.Exchange(ref hold, null)?.TrySetResult();
    public void Cut()
    {
        foreach (var pair in connections.Values)
        {
            Interlocked.Increment(ref cuts);
            pair.Client.Dispose(); pair.Server.Dispose();
        }
    }

    private async Task Accept()
    {
        try
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                // A broken test must not create an unbounded relay.
                if (workers.Count >= 64) { client.Dispose(); throw new InvalidOperationException("Relay connection budget exceeded."); }
                workers.Add(Relay(client));
            }
        }
        catch (Exception e) when (lifetime.IsCancellationRequested && e is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task Relay(TcpClient client)
    {
        using var server = new TcpClient();
        using (client)
        using (var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
        {
            int id = Interlocked.Increment(ref nextId);
            connections[id] = (client, server);
            Task[] pumps = [];
            try
            {
                await server.ConnectAsync(IPAddress.Loopback, upstreamPort, stop.Token);
                pumps = [Pump(client.GetStream(), server.GetStream(), false, stop.Token),
                    Pump(server.GetStream(), client.GetStream(), true, stop.Token)];
                await Task.WhenAny(pumps);
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
            finally
            {
                stop.Cancel(); client.Dispose(); server.Dispose();
                foreach (var pump in pumps)
                    try { await pump; }
                    catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
                connections.TryRemove(id, out _);
            }
        }
    }

    private async Task Pump(NetworkStream source, NetworkStream target, bool downstream, CancellationToken stop)
    {
        var buffer = new byte[4096];
        int count;
        while ((count = await source.ReadAsync(buffer, stop)) != 0)
        {
            for (int offset = 0; offset < count;)
            {
                if (downstream && Volatile.Read(ref hold) is { } gate)
                {
                    Interlocked.Increment(ref blocked);
                    await gate.Task.WaitAsync(stop);
                }
                int length = Math.Min(Impaired ? 1024 : buffer.Length, count - offset);
                if (Impaired)
                {
                    int sample = Interlocked.Increment(ref delayed);
                    await Task.Delay(5 + sample % 3 * 5, stop); // deterministic 5/10/15 ms per byte chunk
                }
                await target.WriteAsync(buffer.AsMemory(offset, length), stop);
                Interlocked.Add(ref bytes, length); Interlocked.Increment(ref writes);
                offset += length;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel(); listener.Stop(); ReleaseDownstream();
        foreach (var pair in connections.Values) { pair.Client.Dispose(); pair.Server.Dispose(); }
        try { await accept; }
        finally { await Task.WhenAll(workers); lifetime.Dispose(); }
    }
}
