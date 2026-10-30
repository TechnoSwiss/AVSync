using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace AVSync;

/// <summary>
/// Tiny UDP protocol. Every packet: "AVS1" magic, 1 type byte, payload.
///   Discover      (any -> broadcast)   sender instance id, sender start time
///   DiscoverReply (source -> sender)   source instance id, source start time, UTF-8 machine name
///   Ping          (sync -> source)     t1 (int64, sync monotonic ticks)
///   Pong          (source -> sync)     t1 echoed, t2 source receive time, t3 source send time
/// </summary>
public static class Protocol
{
    public const int DefaultPort = 47800;

    public const byte Discover = 1;
    public const byte DiscoverReply = 2;
    public const byte Ping = 3;
    public const byte Pong = 4;

    private static ReadOnlySpan<byte> Magic => "AVS1"u8;
    private const int HeaderLength = 5;

    public static int Write(Span<byte> buffer, byte type)
    {
        Magic.CopyTo(buffer);
        buffer[4] = type;
        return HeaderLength;
    }

    public static bool TryRead(ReadOnlySpan<byte> packet, out byte type, out ReadOnlySpan<byte> payload)
    {
        type = 0;
        payload = default;
        if (packet.Length < HeaderLength || !packet[..4].SequenceEqual(Magic)) return false;
        type = packet[4];
        payload = packet[HeaderLength..];
        return true;
    }

    public static int WritePing(Span<byte> buffer, long t1)
    {
        int n = Write(buffer, Ping);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[n..], t1);
        return n + 8;
    }

    public static int WritePong(Span<byte> buffer, long t1, long t2, long t3)
    {
        int n = Write(buffer, Pong);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[n..], t1);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[(n + 8)..], t2);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[(n + 16)..], t3);
        return n + 24;
    }

    /// <summary>Identifies this running instance; the oldest source wins if two end up running at once.</summary>
    public static readonly long InstanceId = Random.Shared.NextInt64();
    public static readonly long InstanceStart = DateTime.UtcNow.Ticks;

    public static int WriteDiscover(Span<byte> buffer)
    {
        int n = Write(buffer, Discover);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[n..], InstanceId);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[(n + 8)..], InstanceStart);
        return n + 16;
    }

    public static int WriteDiscoverReply(Span<byte> buffer, string name)
    {
        int n = WriteDiscover(buffer);
        buffer[4] = DiscoverReply;
        return n + Encoding.UTF8.GetBytes(name.AsSpan(0, Math.Min(name.Length, 60)), buffer[n..]);
    }

    public static bool TryReadDiscoverReply(ReadOnlySpan<byte> payload, out long id, out long start, out string name)
    {
        id = start = 0;
        name = "";
        if (payload.Length < 16) return false;
        id = BinaryPrimitives.ReadInt64LittleEndian(payload);
        start = BinaryPrimitives.ReadInt64LittleEndian(payload[8..]);
        name = Encoding.UTF8.GetString(payload[16..]);
        return true;
    }

    /// <summary>Creates a UDP socket that won't throw on ICMP "port unreachable" (a Windows quirk).</summary>
    public static Socket CreateUdpSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        const int SIO_UDP_CONNRESET = unchecked((int)0x9800000C);
        try { socket.IOControl(SIO_UDP_CONNRESET, new byte[4], null); } catch (SocketException) { }
        socket.EnableBroadcast = true;
        return socket;
    }

    /// <summary>Broadcast addresses for every active IPv4 interface, plus global broadcast and loopback.</summary>
    public static List<IPAddress> DiscoveryTargets()
    {
        var targets = new List<IPAddress> { IPAddress.Broadcast, IPAddress.Loopback };
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null) continue;
                byte[] ip = ua.Address.GetAddressBytes(), mask = ua.IPv4Mask.GetAddressBytes();
                for (int i = 0; i < 4; i++) ip[i] |= (byte)~mask[i];
                var bcast = new IPAddress(ip);
                if (!targets.Contains(bcast)) targets.Add(bcast);
            }
        }
        return targets;
    }

    public static string LocalAddresses()
    {
        var list = new List<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                    list.Add(ua.Address.ToString());
        }
        return list.Count == 0 ? "no network" : string.Join(", ", list);
    }
}
