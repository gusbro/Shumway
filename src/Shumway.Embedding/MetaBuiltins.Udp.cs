using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Shumway.Builtins;
using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Embedding;

/// <summary>The UDP internals of <c>library(sockets)</c>. A datagram is one message, not a
/// run of bytes, so these are not streams.</summary>
public static partial class MetaBuiltins
{
    private const string UdpSocketFunctor = "$udp_socket";
    private const int LargestDatagram = 65535;

    /// <summary>A UDP socket and the receive it may have left waiting: a timeout or a time
    /// limit must not lose the datagram it would have got.</summary>
    internal sealed class UdpSocketEntry
    {
        public UdpSocketEntry(UdpClient client) => Client = client;
        public UdpClient Client { get; }
        public Task<UdpReceiveResult>? PendingReceive { get; set; }
        public Task<UdpReceiveResult> Receiving() => PendingReceive ??= Client.ReceiveAsync();
    }

    private static UdpSocketEntry RequireUdpSocket(Activation engine, Term term)
    {
        if (term is VarTerm) throw new ShumwayPrologException(IsoError.InstantiationError());
        if (term is not CompoundTerm { Functor: UdpSocketFunctor, Args: [IntTerm id] })
            throw new ShumwayPrologException(IsoError.DomainError("udp_socket", term));
        return RequireHost(engine, "library(sockets)").NetObject((int)id.Value) as UdpSocketEntry
            ?? throw new ShumwayPrologException(IsoError.ExistenceError("udp_socket", term));
    }

    private static bool NewUdpSocket(Activation engine)
    {
        Term options = MaterializeRegister(engine, 1);
        bool broadcast = false, reuse = false;
        foreach (Term option in ListItems(options, options))
        {
            if (option is VarTerm) throw new ShumwayPrologException(IsoError.InstantiationError());
            switch (option)
            {
                case CompoundTerm { Functor: "broadcast", Args: [var v] }: broadcast = BoolOption(v, option); break;
                case CompoundTerm { Functor: "reuseaddr", Args: [var v] }: reuse = BoolOption(v, option); break;
                default: throw new ShumwayPrologException(IsoError.DomainError("socket_option", option));
            }
        }
        var client = new UdpClient(Socket.OSSupportsIPv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork);
        if (Socket.OSSupportsIPv6) client.Client.DualMode = true;
        client.EnableBroadcast = broadcast;
        if (reuse)
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        int id = RequireHost(engine, "library(sockets)").AddNetObject(new UdpSocketEntry(client));
        return engine.UnifyRegisterWithCell(0, Materializer.MaterializeAsCell(engine,
            new CompoundTerm(UdpSocketFunctor, new Term[] { new IntTerm(id) })));
    }

    /// <summary><c>'$udp_socket'(-Socket, +Options)</c></summary>
    public static bool UdpSocket(Activation engine) => NetCall(engine, NewUdpSocket);

    private static bool BindUdpSocket(Activation engine)
    {
        UdpSocketEntry udp = RequireUdpSocket(engine, MaterializeRegister(engine, 0));
        Term address = MaterializeRegister(engine, 1);
        var (host, portTerm, unixPath) = ParseSocketAddress(address);
        if (unixPath is not null)
            throw new ShumwayPrologException(IsoError.DomainError("socket_address", address));
        int port = portTerm is VarTerm ? 0 : ResolvePort(portTerm, false, "udp");
        IPAddress ip = host.Length == 0
            ? (Socket.OSSupportsIPv6 ? IPAddress.IPv6Any : IPAddress.Any)
            : ToSocketFamily(ResolveHost(engine.Streams!, host, new SocketOptions(), address)[0]);
        udp.Client.Client.Bind(new IPEndPoint(ip, port));
        if (portTerm is not VarTerm) return true;
        int chosen = ((IPEndPoint)udp.Client.Client.LocalEndPoint!).Port;
        if (address is VarTerm) return engine.UnifyRegisterWithCell(1, Cell.Int(chosen));
        Cell cell = ResolveLocal(engine, engine.GetRegister(1));
        int slot = engine.AllocateHeap(1);
        engine.SetHeap(slot, Cell.Int(chosen));
        return engine.Unify(cell.AsHeapIndex + 2, slot);
    }

    /// <summary><c>'$udp_bind'(+Socket, ?Address)</c></summary>
    public static bool UdpBind(Activation engine) => NetCall(engine, BindUdpSocket);

    /// <summary>The socket is dual mode where IPv6 exists, so an IPv4 address goes mapped.
    /// Not left to the socket: .NET Framework has refused the IPv4 form under load.</summary>
    private static IPAddress ToSocketFamily(IPAddress ip)
        => Socket.OSSupportsIPv6 && ip.AddressFamily == AddressFamily.InterNetwork ? ip.MapToIPv6() : ip;

    private sealed class DatagramOptions
    {
        public bool Binary;
        public string? Encoding;
        public string As = "string";
        public int TimeoutMs;
    }

    private static DatagramOptions ParseDatagramOptions(Term list, bool receiving)
    {
        var o = new DatagramOptions();
        foreach (Term option in ListItems(list, list))
        {
            if (option is VarTerm) throw new ShumwayPrologException(IsoError.InstantiationError());
            switch (option)
            {
                case CompoundTerm { Functor: "type", Args: [AtomTerm { Name: "binary" or "text" } t] }:
                    o.Binary = t.Name == "binary";
                    break;
                case CompoundTerm { Functor: "encoding", Args: [AtomTerm e] }
                        when TextEncodings.NameToEngineName(e.Name) is string name:
                    o.Encoding = name;
                    break;
                case CompoundTerm { Functor: "as", Args: [AtomTerm { Name: "atom" or "string" or "codes" or "chars" } a] } when receiving:
                    o.As = a.Name;
                    break;
                case CompoundTerm { Functor: "timeout", Args: [var v] } when receiving:
                    o.TimeoutMs = SecondsToMs(v, option);
                    break;
                default:
                    throw new ShumwayPrologException(IsoError.DomainError("socket_option", option));
            }
        }
        return o;
    }

    private static byte[] DatagramBytes(Activation engine, Term data, DatagramOptions o)
    {
        if (data is VarTerm) throw new ShumwayPrologException(IsoError.InstantiationError());
        if (o.Binary)
        {
            var bytes = new List<byte>();
            foreach (Term item in ListItems(data, data))
            {
                if (item is VarTerm) throw new ShumwayPrologException(IsoError.InstantiationError());
                if (item is not IntTerm { Value: >= 0 and <= 255 } b)
                    throw new ShumwayPrologException(IsoError.TypeError("byte", item));
                bytes.Add((byte)b.Value);
            }
            return bytes.ToArray();
        }
        if (!data.TryAsText(out string text))
            throw new ShumwayPrologException(IsoError.TypeError("text", data));
        string name = o.Encoding ?? engine.Streams!.DefaultEncodingName;
        if (TextEncodings.IsUnicode(name)) return TextEncodings.ByName(name)!.GetBytes(text);
        // What a narrow encoding cannot hold is an error, as on a stream.
        using var buffer = new MemoryStream();
        using (StreamWriter w = StreamBuiltins.TextWriterOver(buffer, name, leaveOpen: true)) w.Write(text);
        return buffer.ToArray();
    }

    private static bool SendDatagram(Activation engine)
    {
        UdpSocketEntry udp = RequireUdpSocket(engine, MaterializeRegister(engine, 0));
        DatagramOptions o = ParseDatagramOptions(MaterializeRegister(engine, 3), receiving: false);
        byte[] bytes = DatagramBytes(engine, MaterializeRegister(engine, 1), o);
        Term to = MaterializeRegister(engine, 2);
        if (to is VarTerm) throw new ShumwayPrologException(IsoError.InstantiationError());
        var (host, portTerm, unixPath) = ParseSocketAddress(to);
        if (unixPath is not null || host.Length == 0)
            throw new ShumwayPrologException(IsoError.DomainError("socket_address", to));
        int port = ResolvePort(portTerm, false, "udp");
        IPAddress ip = ToSocketFamily(ResolveHost(engine.Streams!, host, new SocketOptions(), to)[0]);
        engine.Streams!.Wait(udp.Client.SendAsync(bytes, bytes.Length, new IPEndPoint(ip, port)), 0, "write");
        return true;
    }

    /// <summary><c>'$udp_send'(+Socket, +Data, +To, +Options)</c></summary>
    public static bool UdpSend(Activation engine) => NetCall(engine, SendDatagram);

    private static bool ReceiveDatagram(Activation engine)
    {
        UdpSocketEntry udp = RequireUdpSocket(engine, MaterializeRegister(engine, 0));
        DatagramOptions o = ParseDatagramOptions(MaterializeRegister(engine, 3), receiving: true);
        Task<UdpReceiveResult> receiving = udp.Receiving();
        engine.Streams!.Wait(receiving, o.TimeoutMs, "read");
        udp.PendingReceive = null;
        UdpReceiveResult datagram = receiving.Result;
        IPAddress from = datagram.RemoteEndPoint.Address;
        if (from.IsIPv4MappedToIPv6) from = from.MapToIPv4();
        Term sender = new CompoundTerm(":", new Term[] { new AtomTerm(from.ToString()), new IntTerm(datagram.RemoteEndPoint.Port) });
        if (!engine.UnifyRegisterWithCell(2, Materializer.MaterializeAsCell(engine, sender))) return false;
        if (o.Binary)
        {
            var codes = new List<Cell>(datagram.Buffer.Length);
            foreach (byte b in datagram.Buffer) codes.Add(Cell.Int(b));
            return UnifyListOfCells(engine, 1, codes);
        }
        string encoding = o.Encoding ?? engine.Streams!.DefaultEncodingName;
        // As on a stream: nobody can know what a peer sends, unless an encoding was named.
        string text = new StrictTextReader(new MemoryStream(datagram.Buffer), encoding, tolerant: o.Encoding is null).ReadToEnd();
        return o.As switch
        {
            "atom" => engine.UnifyRegisterWithCell(1, Cell.Atom(AtomTable.Intern(text).Id)),
            "codes" => engine.UnifyRegisterWithHeapAt(1, engine.MakeTextList(text, TextKind.Codes)),
            _ => engine.UnifyRegisterWithHeapAt(1, engine.MakeTextList(text, TextKind.Chars)),
        };
    }

    /// <summary><c>'$udp_receive'(+Socket, -Data, -From, +Options)</c></summary>
    public static bool UdpReceive(Activation engine) => NetCall(engine, ReceiveDatagram);

    private static bool CloseUdpSocket(Activation engine)
    {
        Term term = MaterializeRegister(engine, 0);
        RequireUdpSocket(engine, term).Client.Dispose();
        RequireHost(engine, "library(sockets)").RemoveNetObject((int)((IntTerm)((CompoundTerm)term).Args[0]).Value);
        return true;
    }

    /// <summary><c>'$udp_close'(+Socket)</c></summary>
    public static bool UdpClose(Activation engine) => NetCall(engine, CloseUdpSocket);
}
