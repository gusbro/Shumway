using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Shumway.Builtins;
using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Embedding;

/// <summary>The internals of <c>library(sockets)</c>. The library's module gives them
/// their public names and their error context.</summary>
public static partial class MetaBuiltins
{
    /// <summary>A listening socket and the accept it may have left waiting: a timeout or
    /// a time limit must not lose the connection it would have got.</summary>
    internal sealed class ServerSocketEntry
    {
        public ServerSocketEntry(Socket socket) => Socket = socket;
        public Socket Socket { get; }
        public Task<Socket>? PendingAccept { get; set; }

        public Task<Socket> Accepting() => PendingAccept ??=
            Task<Socket>.Factory.FromAsync(Socket.BeginAccept, Socket.EndAccept, null);
    }

    private const string ServerSocketFunctor = "$socket";

    private static void RegisterNetworkAndProcesses()
    {
        if (!RuntimeCaps.SupportsNetworkAndProcesses)
        {
            foreach ((string name, int arity) in new[] { ("$socket_client_open", 3), ("$socket_server_open", 3), ("$socket_server_accept", 4), ("$socket_server_close", 1), ("$socket_select", 7), ("$socket_shutdown", 2), ("$current_host", 1), ("$udp_socket", 2), ("$udp_bind", 2), ("$udp_send", 4), ("$udp_receive", 4), ("$udp_close", 1), ("$process_start", 7), ("$process_wait", 3), ("$process_kill", 2), ("$process_group_kill", 1), ("$process_release", 1), ("$process_id", 2), ("$process_id", 1), ("$process_which", 2), ("$tls_client_context", 2), ("$tls_client_negotiate", 3), ("$tls_server_context", 2), ("$tls_server_negotiate", 3) })
                BuiltinsRegistry.Register(name, arity, RefusedInABrowser);
            BuiltinsRegistry.Register("$is_process", 1, static _ => false);
            return;
        }
        BuiltinsRegistry.Register("$socket_client_open", 3, SocketClientOpen);
        BuiltinsRegistry.Register("$socket_server_open", 3, SocketServerOpen);
        BuiltinsRegistry.Register("$socket_server_accept", 4, SocketServerAccept);
        BuiltinsRegistry.Register("$socket_server_close", 1, SocketServerClose);
        BuiltinsRegistry.Register("$socket_select", 7, SocketSelect);
        BuiltinsRegistry.Register("$socket_shutdown", 2, SocketShutdown);
        BuiltinsRegistry.Register("$current_host", 1, CurrentHost);
        BuiltinsRegistry.Register("$udp_socket", 2, UdpSocket);
        BuiltinsRegistry.Register("$udp_bind", 2, UdpBind);
        BuiltinsRegistry.Register("$udp_send", 4, UdpSend);
        BuiltinsRegistry.Register("$udp_receive", 4, UdpReceive);
        BuiltinsRegistry.Register("$udp_close", 1, UdpClose);
        BuiltinsRegistry.Register("$process_start", 7, ProcessStart);
        BuiltinsRegistry.Register("$process_wait", 3, ProcessWait);
        BuiltinsRegistry.Register("$process_kill", 2, ProcessKill);
        BuiltinsRegistry.Register("$process_group_kill", 1, ProcessGroupKill);
        BuiltinsRegistry.Register("$process_release", 1, ProcessRelease);
        BuiltinsRegistry.Register("$process_id", 2, ProcessId2);
        BuiltinsRegistry.Register("$process_id", 1, ProcessId1);
        BuiltinsRegistry.Register("$is_process", 1, IsProcess);
        BuiltinsRegistry.Register("$process_which", 2, ProcessWhich);
        BuiltinsRegistry.Register("$tls_client_context", 2, TlsClientContextOpen);
        BuiltinsRegistry.Register("$tls_client_negotiate", 3, TlsClientNegotiate);
        BuiltinsRegistry.Register("$tls_server_context", 2, TlsServerContextOpen);
        BuiltinsRegistry.Register("$tls_server_negotiate", 3, TlsServerNegotiate);
    }

    private static bool RefusedInABrowser(Activation engine)
        => throw new PrologRuntimeException("system_error", "a browser has no sockets and cannot start programs");

    /// <summary>Runs a builtin of these libraries: what the network or the platform
    /// refuses arrives as <c>system_error</c>.</summary>
    private static bool NetCall(Activation engine, Func<Activation, bool> builtin)
    {
        try { return builtin(engine); }
        catch (Exception ex) when (ex is SocketException or PlatformNotSupportedException
            or System.Security.Authentication.AuthenticationException)
        {
            throw new PrologRuntimeException("system_error", ex.Message);
        }
    }

    private const int LargestBuffer = 1 << 30;

    private sealed class SocketOptions
    {
        public bool Binary = true;
        public bool TypeGiven;
        public string? Encoding;
        public string? Alias;
        public string? EofAction;
        public int TimeoutMs;
        public bool? NoDelay;
        public bool? KeepAlive;
        public int Backlog = 128;
        public int BufferSize = LiveStream.DefaultBufferSize;
        public bool ReuseAddress;
        public bool Loopback;
        public bool NumericNodename;
        public bool NumericServname;
    }

    private static IEnumerable<Term> ListItems(Term list, Term whole)
    {
        Term cur = list;
        while (cur is CompoundTerm { Functor: ".", Args: [var head, var tail] })
        {
            yield return head;
            cur = tail;
        }
        if (cur is VarTerm) throw new ShumwayPrologException(IsoError.InstantiationError());
        if (cur is not AtomTerm { Name: "[]" })
            throw new ShumwayPrologException(IsoError.TypeError("list", whole));
    }

    private static bool BoolOption(Term value, Term option)
    {
        if (value is VarTerm) throw new ShumwayPrologException(IsoError.InstantiationError());
        return value is AtomTerm { Name: "true" } || (value is AtomTerm { Name: "false" }
            ? false
            : throw new ShumwayPrologException(IsoError.DomainError("socket_option", option)));
    }

    private static int SecondsToMs(Term value, Term option) => value switch
    {
        VarTerm => throw new ShumwayPrologException(IsoError.InstantiationError()),
        IntTerm { Value: >= 0 and <= int.MaxValue / 1000 } i => (int)i.Value * 1000,
        FloatTerm { Value: >= 0 and <= int.MaxValue / 1000 } f => (int)Math.Ceiling(f.Value * 1000),
        _ => throw new ShumwayPrologException(IsoError.DomainError("socket_option", option)),
    };

    private static SocketOptions ParseSocketOptions(Term list, bool server)
    {
        var o = new SocketOptions();
        foreach (Term option in ListItems(list, list))
        {
            if (option is VarTerm) throw new ShumwayPrologException(IsoError.InstantiationError());
            if (option is not CompoundTerm { Args: [var v] } c)
                throw new ShumwayPrologException(IsoError.DomainError("socket_option", option));
            switch (c.Functor)
            {
                case "reuseaddr" when server: o.ReuseAddress = BoolOption(v, option); break;
                case "loopback" when server: o.Loopback = BoolOption(v, option); break;
                case "numeric_nodename": o.NumericNodename = BoolOption(v, option); break;
                case "numeric_servname": o.NumericServname = BoolOption(v, option); break;
                case "backlog" when server:
                    o.Backlog = v is IntTerm { Value: > 0 and <= int.MaxValue } b ? (int)b.Value
                        : throw new ShumwayPrologException(IsoError.DomainError("socket_option", option));
                    break;
                case "type" when !server:
                    o.TypeGiven = true;
                    o.Binary = v is AtomTerm { Name: "binary" } || (v is AtomTerm { Name: "text" }
                        ? false
                        : throw new ShumwayPrologException(IsoError.DomainError("socket_option", option)));
                    break;
                case "encoding" when !server:
                    if (v is not AtomTerm enc || TextEncodings.NameToEngineName(enc.Name) is not string name)
                        throw new ShumwayPrologException(IsoError.DomainError("socket_option", option));
                    o.Encoding = name;
                    break;
                case "alias" when !server:
                    o.Alias = v is AtomTerm a ? a.Name
                        : throw new ShumwayPrologException(IsoError.DomainError("socket_option", option));
                    break;
                case "eof_action" when !server:
                    o.EofAction = v is AtomTerm { Name: "error" or "eof_code" or "reset" } e ? e.Name
                        : throw new ShumwayPrologException(IsoError.DomainError("socket_option", option));
                    break;
                case "reposition" when !server:
                    if (v is not AtomTerm { Name: "false" })
                        throw new ShumwayPrologException(IsoError.DomainError("socket_option", option));
                    break;
                case "timeout" when !server: o.TimeoutMs = SecondsToMs(v, option); break;
                case "buffer_size" when !server:
                    if (v is VarTerm) throw new ShumwayPrologException(IsoError.InstantiationError());
                    o.BufferSize = v is IntTerm { Value: >= 0 and <= LargestBuffer } size ? (int)size.Value
                        : throw new ShumwayPrologException(IsoError.DomainError("socket_option", option));
                    break;
                case "nodelay" when !server: o.NoDelay = BoolOption(v, option); break;
                case "keepalive" when !server: o.KeepAlive = BoolOption(v, option); break;
                default:
                    throw new ShumwayPrologException(IsoError.DomainError("socket_option", option));
            }
        }
        // encoding/1 asks for text, unless type(binary) says otherwise (bytes ignore it).
        if (o.Encoding is not null && !o.TypeGiven) o.Binary = false;
        return o;
    }

    /// <summary>The port an address names: a number, an atom of digits, or a service name
    /// from the system's services file.</summary>
    private static int ResolvePort(Term port, bool numericOnly, string protocol = "tcp")
    {
        switch (port)
        {
            case VarTerm:
                throw new ShumwayPrologException(IsoError.InstantiationError());
            case IntTerm { Value: >= 0 and <= 65535 } i:
                return (int)i.Value;
            case AtomTerm a when int.TryParse(a.Name, NumberStyles.None, CultureInfo.InvariantCulture, out int n)
                                 && n <= 65535:
                return n;
            case AtomTerm a when !numericOnly:
                return ServiceNames.Port(a.Name, protocol)
                    ?? throw new ShumwayPrologException(IsoError.ExistenceError("socket_service", port));
            default:
                throw new ShumwayPrologException(IsoError.DomainError("socket_port", port));
        }
    }

    /// <summary>Splits an address into host and port: <c>inet(Host, Port)</c>,
    /// <c>Host:Port</c> or just a port. Null for <c>unix(Path)</c>.</summary>
    private static (string Host, Term Port, string? UnixPath) ParseSocketAddress(Term address)
    {
        switch (address)
        {
            case CompoundTerm { Functor: "unix", Args: [var path] }:
                return ("", address, path is AtomTerm p ? p.Name
                    : path is VarTerm ? throw new ShumwayPrologException(IsoError.InstantiationError())
                    : throw new ShumwayPrologException(IsoError.DomainError("socket_address", address)));
            case CompoundTerm { Functor: ":" or "inet", Args: [var host, var port] }:
                if (host is VarTerm) throw new ShumwayPrologException(IsoError.InstantiationError());
                if (host is not AtomTerm h)
                    throw new ShumwayPrologException(IsoError.DomainError("socket_address", address));
                return (h.Name, port, null);
            case VarTerm or IntTerm or AtomTerm:
                return ("", address, null);
            default:
                throw new ShumwayPrologException(IsoError.DomainError("socket_address", address));
        }
    }

    private static IPAddress[] ResolveHost(StreamRegistry registry, string host, SocketOptions o, Term address)
    {
        if (IPAddress.TryParse(host, out IPAddress? literal)) return new[] { literal };
        if (o.NumericNodename)
            throw new ShumwayPrologException(IsoError.DomainError("socket_address", address));
        Task<IPAddress[]> lookup = Dns.GetHostAddressesAsync(host);
        registry.Wait(lookup, o.TimeoutMs, "connect");
        return lookup.Result;
    }

    /// <summary><c>'$socket_client_open'(+Address, -Stream, +Options)</c></summary>
    public static bool SocketClientOpen(Activation engine) => NetCall(engine, ClientOpen);

    private static bool ClientOpen(Activation engine)
    {
        Term address = MaterializeRegister(engine, 0);
        SocketOptions o = ParseSocketOptions(MaterializeRegister(engine, 2), server: false);
        StreamRegistry registry = engine.Streams!;
        CheckStreamAlias(engine, registry, o.Alias);
        if (address is VarTerm) throw new ShumwayPrologException(IsoError.InstantiationError());
        var (host, portTerm, unixPath) = ParseSocketAddress(address);
        Socket socket = unixPath is not null
            ? ConnectUnix(registry, unixPath, o)
            : ConnectInet(registry, host, ResolvePort(portTerm, o.NumericServname), o, address);
        return UnifyNewSocketStream(engine, 1, socket, o, host);
    }

    private static Socket ConnectInet(StreamRegistry registry, string host, int port, SocketOptions o, Term address)
    {
        IPAddress[] addresses = host.Length == 0
            ? new[] { IPAddress.Loopback, IPAddress.IPv6Loopback }
            : ResolveHost(registry, host, o, address);
        Exception? last = null;
        foreach (IPAddress ip in addresses)
        {
            if (ip.AddressFamily == AddressFamily.InterNetworkV6 && !Socket.OSSupportsIPv6) continue;
            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                registry.Wait(ConnectAsync(socket, new IPEndPoint(ip, port)), o.TimeoutMs, "connect");
                return socket;
            }
            catch (Exception ex) when (ex is PrologRuntimeException { Kind: "system_error" })
            {
                socket.Dispose();
                last = ex;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
        throw last ?? new PrologRuntimeException("system_error", $"no address for {host}");
    }

    private static Task ConnectAsync(Socket socket, EndPoint endPoint)
        => Task.Factory.FromAsync<EndPoint>(socket.BeginConnect, socket.EndConnect, endPoint, null);

    private static Socket ConnectUnix(StreamRegistry registry, string path, SocketOptions o)
    {
#if NETFRAMEWORK
        throw new PrologRuntimeException("system_error", "unix domain sockets need .NET 5 or later");
#else
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            registry.Wait(ConnectAsync(socket, new UnixDomainSocketEndPoint(path)), o.TimeoutMs, "connect");
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
#endif
    }

    private static void CheckStreamAlias(Activation engine, StreamRegistry registry, string? alias)
    {
        if (alias is not null && registry.IsAliasTaken(alias))
            throw new ShumwayPrologException(IsoError.PermissionError("open", "source_sink",
                new CompoundTerm("alias", new Term[] { new AtomTerm(alias) }), engine));
    }

    private static bool UnifyNewSocketStream(Activation engine, int register, Socket socket, SocketOptions o, string? peerHost)
    {
        if (socket.ProtocolType == ProtocolType.Tcp)
        {
            if (o.NoDelay is bool noDelay) socket.NoDelay = noDelay;
            if (o.KeepAlive is bool keepAlive)
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, keepAlive);
        }
        // No newline translation: the bytes of a network protocol do not depend on
        // the platform of either end.
        StreamHandle handle = StreamBuiltins.OpenLiveStream(engine.Streams!,
            new NetworkStream(socket, ownsSocket: true), "readwrite", o.Binary,
            o.Encoding, o.Alias, o.EofAction, translateNewlines: false, o.BufferSize, asynchronous: true);
        handle.Socket = socket;
        handle.PeerHost = peerHost;
        handle.LiveConnection!.TimeoutMs = o.TimeoutMs;
        return engine.UnifyRegisterWithCell(register, StreamBuiltins.MakeStreamTerm(engine, handle));
    }

    /// <summary><c>'$socket_server_open'(?Address, -ServerSocket, +Options)</c></summary>
    public static bool SocketServerOpen(Activation engine) => NetCall(engine, ServerOpen);

    private static bool ServerOpen(Activation engine)
    {
        Term address = MaterializeRegister(engine, 0);
        SocketOptions o = ParseSocketOptions(MaterializeRegister(engine, 2), server: true);
        var (host, portTerm, unixPath) = ParseSocketAddress(address);
        PrologEngine owner = RequireHost(engine, "socket_server_open/3");
        Socket socket;
        if (unixPath is not null)
        {
            socket = ListenUnix(unixPath, o);
        }
        else
        {
            int port = portTerm is VarTerm ? 0 : ResolvePort(portTerm, o.NumericServname);
            socket = ListenInet(engine.Streams!, host, port, o, address);
            if (portTerm is VarTerm && !BindChosenPort(engine, address, ((IPEndPoint)socket.LocalEndPoint!).Port))
            {
                socket.Dispose();
                return false;
            }
        }
        int id = owner.AddNetObject(new ServerSocketEntry(socket));
        return engine.UnifyRegisterWithCell(1, Materializer.MaterializeAsCell(engine,
            new CompoundTerm(ServerSocketFunctor, new Term[] { new IntTerm(id) })));
    }

    /// <summary>Binds the port variable of an address, in the heap term the caller wrote:
    /// a bare variable or the second argument of <c>Host:Port</c>.</summary>
    private static bool BindChosenPort(Activation engine, Term address, int port)
    {
        if (address is VarTerm) return engine.UnifyRegisterWithCell(0, Cell.Int(port));
        Cell cell = ResolveLocal(engine, engine.GetRegister(0));
        int chosen = engine.AllocateHeap(1);
        engine.SetHeap(chosen, Cell.Int(port));
        return engine.Unify(cell.AsHeapIndex + 2, chosen);
    }

    private static Socket ListenInet(StreamRegistry registry, string host, int port, SocketOptions o, Term address)
    {
        IPAddress ip;
        if (o.Loopback) ip = IPAddress.Loopback;
        else if (host.Length == 0) ip = Socket.OSSupportsIPv6 ? IPAddress.IPv6Any : IPAddress.Any;
        else ip = ResolveHost(registry, host, o, address)[0];
        var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            if (ip.Equals(IPAddress.IPv6Any)) socket.DualMode = true;
            if (o.ReuseAddress)
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(ip, port));
            socket.Listen(o.Backlog);
            return socket;
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new PrologRuntimeException("system_error", ex.Message);
        }
    }

    private static Socket ListenUnix(string path, SocketOptions o)
    {
#if NETFRAMEWORK
        throw new PrologRuntimeException("system_error", "unix domain sockets need .NET 5 or later");
#else
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            if (o.ReuseAddress && File.Exists(path)) File.Delete(path);
            socket.Bind(new UnixDomainSocketEndPoint(path));
            socket.Listen(o.Backlog);
            return socket;
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new PrologRuntimeException("system_error", ex.Message);
        }
#endif
    }

    private static ServerSocketEntry RequireServerSocket(Activation engine, Term term)
    {
        if (term is VarTerm) throw new ShumwayPrologException(IsoError.InstantiationError());
        if (term is not CompoundTerm { Functor: ServerSocketFunctor, Args: [IntTerm id] })
            throw new ShumwayPrologException(IsoError.DomainError("server_socket", term));
        return RequireHost(engine, "library(sockets)").NetObject((int)id.Value) as ServerSocketEntry
            ?? throw new ShumwayPrologException(IsoError.ExistenceError("server_socket", term));
    }

    /// <summary><c>'$socket_server_accept'(+ServerSocket, -Client, -Stream, +Options)</c></summary>
    public static bool SocketServerAccept(Activation engine) => NetCall(engine, ServerAccept);

    private static bool ServerAccept(Activation engine)
    {
        ServerSocketEntry server = RequireServerSocket(engine, MaterializeRegister(engine, 0));
        SocketOptions o = ParseSocketOptions(MaterializeRegister(engine, 3), server: false);
        StreamRegistry registry = engine.Streams!;
        CheckStreamAlias(engine, registry, o.Alias);
        Task<Socket> accepting = server.Accepting();
        registry.Wait(accepting, o.TimeoutMs, "accept");
        server.PendingAccept = null;
        Socket socket = accepting.Result;
        string client = socket.RemoteEndPoint is IPEndPoint remote
            ? (remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address).ToString()
            : "";
        if (!engine.UnifyRegisterWithCell(1, Cell.Atom(AtomTable.Intern(client).Id)))
        {
            socket.Dispose();
            return false;
        }
        return UnifyNewSocketStream(engine, 2, socket, o, null);
    }

    /// <summary><c>'$socket_server_close'(+ServerSocket)</c></summary>
    public static bool SocketServerClose(Activation engine) => NetCall(engine, ServerClose);

    private static bool ServerClose(Activation engine)
    {
        Term term = MaterializeRegister(engine, 0);
        RequireServerSocket(engine, term).Socket.Dispose();
        RequireHost(engine, "library(sockets)").RemoveNetObject((int)((IntTerm)((CompoundTerm)term).Args[0]).Value);
        return true;
    }

    /// <summary><c>'$socket_shutdown'(+Stream, +How)</c>: ends one direction of a connection
    /// and leaves the other open.</summary>
    public static bool SocketShutdown(Activation engine) => NetCall(engine, ShutdownDirection);

    private static bool ShutdownDirection(Activation engine)
    {
        StreamHandle h = StreamBuiltins.ResolveStream(engine, engine.GetRegister(0));
        Term how = MaterializeRegister(engine, 1);
        if (how is VarTerm) throw new ShumwayPrologException(IsoError.InstantiationError());
        System.Net.Sockets.SocketShutdown direction = how switch
        {
            AtomTerm { Name: "read" } => System.Net.Sockets.SocketShutdown.Receive,
            AtomTerm { Name: "write" } => System.Net.Sockets.SocketShutdown.Send,
            AtomTerm { Name: "read_write" } => System.Net.Sockets.SocketShutdown.Both,
            _ => throw new ShumwayPrologException(IsoError.DomainError("socket_shutdown", how)),
        };
        Socket socket = h.Socket ?? throw new ShumwayPrologException(
            IsoError.DomainError("socket_stream", MaterializeRegister(engine, 0)));
        if (direction != System.Net.Sockets.SocketShutdown.Receive)
        {
            if (h.Writer is not null) h.Writer.Flush();
            else h.BinaryStream?.Flush();
        }
        try { socket.Shutdown(direction); }
        catch (SocketException ex) { throw new PrologRuntimeException("system_error", ex.Message); }
        return true;
    }

    /// <summary><c>'$current_host'(?Host)</c></summary>
    public static bool CurrentHost(Activation engine)
        => NetCall(engine, static e => e.UnifyRegisterWithCell(0, Cell.Atom(AtomTable.Intern(Dns.GetHostName()).Id)));

    /// <summary>An entry of a <c>socket_select/7</c> list, as the caller wrote it: the
    /// stream or server socket, or <c>Term-Entry</c>.</summary>
    private readonly record struct SelectEntry(Cell Written, Func<Task?> Waiting);

    private static List<SelectEntry> SelectEntries(Activation engine, int register, Func<Activation, Cell, Func<Task?>> readiness)
    {
        var entries = new List<SelectEntry>();
        Cell cur = engine.NormalizeListCell(ResolveLocal(engine, engine.GetRegister(register)));
        int steps = engine.HeapTop + 2;
        while (engine.TryUnconsListLike(cur, out Cell raw, out Cell tail))
        {
            if (--steps < 0)
                throw new ShumwayPrologException(IsoError.TypeError("list", MaterializeRegister(engine, register)));
            Cell written = ResolveLocal(engine, raw);
            if (written.Tag is Tag.Ref or Tag.AttVar)
                throw new ShumwayPrologException(IsoError.InstantiationError());
            Cell entry = written;
            if (written.Tag == Tag.Str
                && engine.GetHeap(written.AsHeapIndex).AsFunctorId == FunctorTable.Intern(AtomTable.Intern("-", permanent: true).Id, 2))
                entry = ResolveLocal(engine, engine.GetHeap(written.AsHeapIndex + 2));
            entries.Add(new SelectEntry(written, readiness(engine, entry)));
            cur = engine.NormalizeListCell(ResolveLocal(engine, tail));
        }
        if (cur.Tag is Tag.Ref or Tag.AttVar) throw new ShumwayPrologException(IsoError.InstantiationError());
        if (cur.Tag != Tag.Atom || cur.AsAtomId != AtomTable.EmptyListId)
            throw new ShumwayPrologException(IsoError.TypeError("list", MaterializeRegister(engine, register)));
        return entries;
    }

    private static Func<Task?> ServerReadiness(Activation engine, Cell entry)
    {
        ServerSocketEntry server = RequireServerSocket(engine, CellTerm(engine, entry));
        return () =>
        {
            Task accepting = server.Accepting();
            return accepting.IsCompleted ? null : accepting;
        };
    }

    private static Func<Task?> InputReadiness(Activation engine, Cell entry)
    {
        if (entry.Tag == Tag.Str && CellTerm(engine, entry) is CompoundTerm { Functor: UdpSocketFunctor } udpTerm)
        {
            UdpSocketEntry udp = RequireUdpSocket(engine, udpTerm);
            return () =>
            {
                Task receiving = udp.Receiving();
                return receiving.IsCompleted ? null : receiving;
            };
        }
        StreamHandle h = StreamBuiltins.ResolveStream(engine, entry);
        if (!h.IsReader)
            throw new ShumwayPrologException(IsoError.PermissionError("input", "stream", CellTerm(engine, entry), engine));
        return () => h.HasBufferedInput || h.LiveConnection is null ? null : h.LiveConnection.InputPending();
    }

    private static Func<Task?> OutputReadiness(Activation engine, Cell entry)
    {
        StreamHandle h = StreamBuiltins.ResolveStream(engine, entry);
        if (!h.IsWriter)
            throw new ShumwayPrologException(IsoError.PermissionError("output", "stream", CellTerm(engine, entry), engine));
        return () => null;
    }

    private static Term CellTerm(Activation engine, Cell cell)
    {
        int slot = engine.AllocateHeap(1);
        engine.SetHeap(slot, cell);
        return TermReader.Materialize(engine, slot);
    }

    private static bool UnifyListOfCells(Activation engine, int register, List<Cell> items)
    {
        if (items.Count == 0)
            return engine.UnifyRegisterWithCell(register, Cell.Atom(AtomTable.EmptyListId));
        int spine = engine.AllocateHeap(2 * items.Count + 1);
        for (int i = 0; i < items.Count; i++)
        {
            engine.SetHeap(spine + 2 * i, Cell.Lis(spine + 2 * i + 1));
            engine.SetHeap(spine + 2 * i + 1, items[i]);
        }
        engine.SetHeap(spine + 2 * items.Count, Cell.Atom(AtomTable.EmptyListId));
        return engine.UnifyRegisterWithHeapAt(register, spine);
    }

    /// <summary><c>'$socket_select'(+Servers, -SReady, +Reads, -RReady, +Writes, -WReady, +Timeout)</c>.
    /// What makes an entry ready is the read or accept this leaves waiting having finished,
    /// so a pipe takes part as a socket does, and nothing it gets is lost.</summary>
    public static bool SocketSelect(Activation engine) => NetCall(engine, Select);

    private static bool Select(Activation engine)
    {
        var servers = SelectEntries(engine, 0, ServerReadiness);
        var reads = SelectEntries(engine, 2, InputReadiness);
        var writes = SelectEntries(engine, 4, OutputReadiness);
        Term timeout = MaterializeRegister(engine, 6);
        int timeoutMs = timeout switch
        {
            VarTerm => throw new ShumwayPrologException(IsoError.InstantiationError()),
            AtomTerm { Name: "off" } => -1,
            IntTerm or FloatTerm => SecondsToMs(timeout, timeout),
            _ => throw new ShumwayPrologException(IsoError.DomainError("socket_select_timeout", timeout)),
        };
        var all = servers.Concat(reads).Concat(writes).ToList();
        List<Task> waiting = all.Select(e => e.Waiting()).OfType<Task>().ToList();
        if (waiting.Count == all.Count && all.Count > 0 && timeoutMs != 0)
        {
            try { engine.Streams!.Wait(Task.WhenAny(waiting), Math.Max(timeoutMs, 0), "select"); }
            catch (PrologRuntimeException ex) when (ex.Kind == "timeout_error") { }
        }
        else if (all.Count == 0 && timeoutMs != 0)
        {
            // Nothing to watch: it only waits, for the time asked or until cancelled.
            try { engine.Streams!.Wait(new TaskCompletionSource<bool>().Task, Math.Max(timeoutMs, 0), "select"); }
            catch (PrologRuntimeException ex) when (ex.Kind == "timeout_error") { }
        }
        return UnifyReady(engine, 1, servers) && UnifyReady(engine, 3, reads) && UnifyReady(engine, 5, writes);
    }

    private static bool UnifyReady(Activation engine, int register, List<SelectEntry> entries)
    {
        var ready = new List<Cell>();
        foreach (SelectEntry entry in entries)
            if (entry.Waiting() is null) ready.Add(entry.Written);
        return UnifyListOfCells(engine, register, ready);
    }
}

/// <summary>Service names from the system's services file. .NET has no call for this, and
/// reading the file gives what <c>getservbyname</c> gives, with no native layout to get
/// right on each system.</summary>
internal static class ServiceNames
{
    private static string FilePath => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "services")
        : "/etc/services";

    /// <summary>The port of a service name or alias, or null when there is none.</summary>
    public static int? Port(string name, string protocol = "tcp")
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            foreach (string line in File.ReadLines(FilePath))
            {
                int hash = line.IndexOf('#');
                string[] fields = (hash >= 0 ? line.Substring(0, hash) : line)
                    .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 2) continue;
                string[] portAndProtocol = fields[1].Split('/');
                if (portAndProtocol.Length != 2 || portAndProtocol[1] != protocol) continue;
                if (!int.TryParse(portAndProtocol[0], NumberStyles.None, CultureInfo.InvariantCulture, out int port)) continue;
                if (fields[0] == name || fields.Skip(2).Contains(name)) return port;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }
}
