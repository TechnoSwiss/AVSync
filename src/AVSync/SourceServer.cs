using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace AVSync;

/// <summary>
/// Answers discovery requests and time pings on a dedicated thread for minimal reply latency.
/// Also probes the network every 2 s for another source (e.g. two PCs started at the same moment);
/// if an older one is found, <see cref="OlderSourceFound"/> fires so this instance can become a sync app.
/// </summary>
public sealed class SourceServer : IDisposable
{
    private readonly Socket _socket;
    private readonly IClock _clock;
    private readonly Thread _thread, _prober;
    private readonly ConcurrentDictionary<IPEndPoint, long> _clients = new();
    private volatile bool _running = true;

    public int Port { get; }

    /// <summary>Raised (once, on a background thread) with the endpoint and name of an older source.</summary>
    public event Action<IPEndPoint, string>? OlderSourceFound;
    private int _conflictRaised;

    private SourceServer(Socket socket, IClock clock, int port)
    {
        _socket = socket;
        _clock = clock;
        Port = port;
        _thread = new Thread(Run) { IsBackground = true, Name = "AVSync source", Priority = ThreadPriority.Highest };
        _thread.Start();
        _prober = new Thread(Probe) { IsBackground = true, Name = "AVSync probe" };
        _prober.Start();
    }

    /// <summary>Returns null if the port is already in use (e.g. another source on this PC).</summary>
    public static SourceServer? TryStart(int port, IClock clock)
    {
        var socket = Protocol.CreateUdpSocket();
        try
        {
            socket.ExclusiveAddressUse = true;
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            return new SourceServer(socket, clock, port);
        }
        catch (SocketException)
        {
            socket.Dispose();
            return null;
        }
    }

    /// <summary>Number of sync apps that pinged in the last 3 seconds.</summary>
    public int ActiveClients
    {
        get
        {
            long cutoff = Mono.NowTicks - 3 * TimeSpan.TicksPerSecond;
            int count = 0;
            foreach (var kv in _clients)
            {
                if (kv.Value >= cutoff) count++;
                else _clients.TryRemove(kv.Key, out _);
            }
            return count;
        }
    }

    private void Run()
    {
        var buffer = new byte[512];
        var reply = new byte[512];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        while (_running)
        {
            int n;
            try
            {
                n = _socket.ReceiveFrom(buffer, ref remote);
            }
            catch (SocketException) { continue; }
            catch (ObjectDisposedException) { return; }

            long t2 = _clock.NowTicks;
            if (!Protocol.TryRead(buffer.AsSpan(0, n), out byte type, out var payload)) continue;

            try
            {
                switch (type)
                {
                    case Protocol.Discover:
                        _socket.SendTo(reply, 0, Protocol.WriteDiscoverReply(reply, Environment.MachineName), SocketFlags.None, remote);
                        break;
                    case Protocol.DiscoverReply
                        when Protocol.TryReadDiscoverReply(payload, out long id, out long start, out string name) &&
                             id != Protocol.InstanceId &&
                             (start < Protocol.InstanceStart || (start == Protocol.InstanceStart && id < Protocol.InstanceId)) &&
                             Interlocked.Exchange(ref _conflictRaised, 1) == 0:
                        var other = (IPEndPoint)remote;
                        OlderSourceFound?.Invoke(new IPEndPoint(other.Address, other.Port), name);
                        break;
                    case Protocol.Ping when payload.Length >= 8:
                        long t1 = BinaryPrimitives.ReadInt64LittleEndian(payload);
                        int len = Protocol.WritePong(reply, t1, t2, _clock.NowTicks);
                        _socket.SendTo(reply, 0, len, SocketFlags.None, remote);
                        var ep = (IPEndPoint)remote;
                        _clients[new IPEndPoint(ep.Address, ep.Port)] = Mono.NowTicks;
                        break;
                }
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { return; }
        }
    }

    private void Probe()
    {
        var packet = new byte[32];
        int length = Protocol.WriteDiscover(packet);
        while (_running)
        {
            Thread.Sleep(2000);
            foreach (var target in Protocol.DiscoveryTargets())
            {
                try { _socket.SendTo(packet, 0, length, SocketFlags.None, new IPEndPoint(target, Port)); }
                catch (SocketException) { }
                catch (ObjectDisposedException) { return; }
            }
        }
    }

    public void Dispose()
    {
        _running = false;
        _socket.Dispose();
    }
}
