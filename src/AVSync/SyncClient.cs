using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace AVSync;

public readonly record struct SyncStats(
    bool Connected, bool Locked, double LastRttMs, double MinRttMs, double JitterMs, double LossPercent);

/// <summary>
/// Pings the source 10x per second and estimates its clock NTP-style:
///   RTT    = (t4 - t1) - (t3 - t2)
///   offset = ((t2 - t1) + (t3 - t4)) / 2
/// The offset from the lowest-RTT sample in a sliding window is used, since that sample suffered the least
/// queuing delay and so has the most symmetric path (the main error source in this estimate).
/// </summary>
public sealed class SyncClient : IDisposable
{
    private const int PingIntervalMs = 100;
    private const int Window = 40; // 4 seconds of samples

    private readonly Socket _socket;
    private readonly int _port;
    private volatile IPEndPoint _source;
    private readonly Thread _sender, _receiver;
    private readonly object _lock = new();
    private readonly (long Rtt, long Offset)[] _samples = new (long, long)[Window];
    private int _sampleCount, _sampleNext;
    private long _lastRtt, _lastReply;
    private int _sent, _received;
    private double _lossPercent;
    private volatile bool _running = true;

    public SyncedClock Clock { get; } = new();
    public string SourceName { get; private set; }
    public IPEndPoint SourceEndPoint => _source;

    public SyncClient(Socket socket, IPEndPoint source, string sourceName)
    {
        _socket = socket;
        _source = source;
        _port = source.Port;
        SourceName = sourceName;
        _socket.ReceiveTimeout = 500;
        _receiver = new Thread(Receive) { IsBackground = true, Name = "AVSync receive", Priority = ThreadPriority.Highest };
        _sender = new Thread(Send) { IsBackground = true, Name = "AVSync ping" };
        _receiver.Start();
        _sender.Start();
    }

    /// <summary>Broadcasts a discovery request and waits for a source to answer.</summary>
    public static SyncClient? Discover(int port, TimeSpan timeout)
    {
        var socket = Protocol.CreateUdpSocket();
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        var targets = Protocol.DiscoveryTargets();
        var request = new byte[32];
        int requestLength = Protocol.WriteDiscover(request);
        var buffer = new byte[512];
        long deadline = Mono.NowTicks + timeout.Ticks, nextSend = 0;

        while (Mono.NowTicks < deadline)
        {
            if (Mono.NowTicks >= nextSend)
            {
                foreach (var target in targets)
                {
                    try { socket.SendTo(request, 0, requestLength, SocketFlags.None, new IPEndPoint(target, port)); }
                    catch (SocketException) { } // e.g. an interface that can't broadcast
                }
                nextSend = Mono.NowTicks + 300 * TimeSpan.TicksPerMillisecond;
            }

            if (!socket.Poll(50_000, SelectMode.SelectRead)) continue;
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            int n;
            try { n = socket.ReceiveFrom(buffer, ref remote); }
            catch (SocketException) { continue; }
            if (Protocol.TryRead(buffer.AsSpan(0, n), out byte type, out var payload) && type == Protocol.DiscoverReply &&
                Protocol.TryReadDiscoverReply(payload, out _, out _, out string name))
                return new SyncClient(socket, (IPEndPoint)remote, name);
        }

        socket.Dispose();
        return null;
    }

    /// <summary>Connects directly to a known source (no discovery).</summary>
    public static SyncClient Connect(string host, int port, string? name = null)
    {
        var address = IPAddress.TryParse(host, out var ip)
            ? ip
            : Dns.GetHostAddresses(host).First(a => a.AddressFamily == AddressFamily.InterNetwork);
        var socket = Protocol.CreateUdpSocket();
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        return new SyncClient(socket, new IPEndPoint(address, port), name ?? host);
    }

    public SyncStats Stats
    {
        get
        {
            lock (_lock)
            {
                bool connected = _lastReply != 0 && Mono.NowTicks - _lastReply < 2 * TimeSpan.TicksPerSecond;
                if (_sampleCount == 0) return new SyncStats(connected, Clock.Locked, 0, 0, 0, _lossPercent);

                long min = long.MaxValue;
                double sum = 0, sumSq = 0;
                for (int i = 0; i < _sampleCount; i++)
                {
                    long rtt = _samples[i].Rtt;
                    min = Math.Min(min, rtt);
                    sum += rtt;
                    sumSq += (double)rtt * rtt;
                }
                double mean = sum / _sampleCount;
                double stdDev = Math.Sqrt(Math.Max(0, sumSq / _sampleCount - mean * mean));
                return new SyncStats(connected, Clock.Locked, Ms(_lastRtt), Ms(min), Ms((long)stdDev), _lossPercent);
            }
        }
    }

    private static double Ms(long ticks) => ticks / (double)TimeSpan.TicksPerMillisecond;

    private void Send()
    {
        var packet = new byte[32];
        var discover = new byte[32];
        int discoverLength = Protocol.WriteDiscover(discover);
        long started = Mono.NowTicks, nextDiscover = 0;
        while (_running)
        {
            try
            {
                int n = Protocol.WritePing(packet, Mono.NowTicks);
                _socket.SendTo(packet, 0, n, SocketFlags.None, _source);
                lock (_lock) _sent++;

                // Source silent for 5 s? Look for one again (it may have restarted elsewhere).
                long now = Mono.NowTicks, last;
                lock (_lock) last = _lastReply == 0 ? started : _lastReply;
                if (now - last > 5 * TimeSpan.TicksPerSecond && now >= nextDiscover)
                {
                    nextDiscover = now + TimeSpan.TicksPerSecond;
                    foreach (var target in Protocol.DiscoveryTargets())
                        try { _socket.SendTo(discover, 0, discoverLength, SocketFlags.None, new IPEndPoint(target, _port)); }
                        catch (SocketException) { }
                }
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { return; }
            Thread.Sleep(PingIntervalMs);
        }
    }

    private void Receive()
    {
        var buffer = new byte[512];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
        while (_running)
        {
            int n;
            try { n = _socket.ReceiveFrom(buffer, ref remote); }
            catch (SocketException) { continue; } // timeout
            catch (ObjectDisposedException) { return; }

            long t4 = Mono.NowTicks;
            if (!Protocol.TryRead(buffer.AsSpan(0, n), out byte type, out var p)) continue;
            if (type == Protocol.DiscoverReply && Protocol.TryReadDiscoverReply(p, out _, out _, out string name))
            {
                var ep = (IPEndPoint)remote;
                if (!ep.Equals(_source))
                {
                    lock (_lock) _sampleCount = _sampleNext = 0; // old samples describe a different clock
                    SourceName = name;
                    _source = new IPEndPoint(ep.Address, ep.Port);
                }
                continue;
            }
            if (type != Protocol.Pong || p.Length < 24 || !remote.Equals(_source)) continue;

            long t1 = BinaryPrimitives.ReadInt64LittleEndian(p);
            long t2 = BinaryPrimitives.ReadInt64LittleEndian(p[8..]);
            long t3 = BinaryPrimitives.ReadInt64LittleEndian(p[16..]);
            long rtt = (t4 - t1) - (t3 - t2);
            if (rtt < 0 || rtt > 2 * TimeSpan.TicksPerSecond) continue;
            long offset = ((t2 - t1) + (t3 - t4)) / 2;

            long best;
            lock (_lock)
            {
                _samples[_sampleNext] = (rtt, offset);
                _sampleNext = (_sampleNext + 1) % Window;
                _sampleCount = Math.Min(_sampleCount + 1, Window);
                _lastRtt = rtt;
                _lastReply = t4;
                _received++;
                if (_sent >= 50)
                {
                    _lossPercent = Math.Clamp(100.0 * (_sent - _received) / _sent, 0, 100);
                    _sent = _received = 0;
                }

                int bestIndex = 0;
                for (int i = 1; i < _sampleCount; i++)
                    if (_samples[i].Rtt < _samples[bestIndex].Rtt) bestIndex = i;
                best = _samples[bestIndex].Offset;
            }
            Clock.Steer(best);
        }
    }

    public void Dispose()
    {
        _running = false;
        _socket.Dispose();
    }
}
