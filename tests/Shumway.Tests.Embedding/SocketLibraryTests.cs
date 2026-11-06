using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>library(sockets), over real connections on the loopback interface. Prolog holds
/// both ends in one thread, which works because a connection waits in the backlog until it
/// is accepted.</summary>
public sealed class SocketLibraryTests
{
    private const string Helpers = @"
:- use_module(library(sockets)).

pair(Options, C, A) :-
    socket_server_open('127.0.0.1':P, Srv),
    socket_client_open('127.0.0.1':P, C, Options),
    socket_server_accept(Srv, _, A, Options),
    socket_server_close(Srv).

caught(Goal, Error) :- catch((Goal, fail), error(Error, _), true).

all_codes(S, Cs) :-
    get_code(S, C),
    ( C =:= -1 -> Cs = [] ; Cs = [C|T], all_codes(S, T) ).
";

    private static PrologEngine Engine()
    {
        var e = new PrologEngine();
        e.ConsultString(Helpers);
        return e;
    }

    private static T WithinSeconds<T>(int seconds, Func<T> work)
    {
        return Finished(OwnThread.Run(work), seconds);
    }

    private static T Finished<T>(Task<T> task, int seconds)
    {
        Assert.True(task.Wait(TimeSpan.FromSeconds(seconds)), "it hung");
        return task.Result;
    }

    private static string Name(Solution s, string v) => ((AtomTerm)s[v]!).Name;

    // ---------- the conversation

    [Fact]
    public void AClientAndAServer_ExchangeBytes()
    {
        Assert.True(WithinSeconds(30, () => Engine().Query(@"
            pair([], C, A),
            put_byte(C, 97), put_byte(C, 200), flush_output(C),
            peek_byte(A, 97), get_byte(A, 97), get_byte(A, 200),
            put_byte(A, 98), flush_output(A),
            get_byte(C, 98),
            close(C), close(A).")).Success);
    }

    [Fact]
    public void AClientAndAServer_ExchangeTerms()
    {
        Assert.True(WithinSeconds(30, () => Engine().Query(@"
            pair([type(text)], C, A),
            char_code(E, 0xE9), char_code(G, 0x1D11E),
            writeq(C, f(E, G)), put_char(C, '.'), nl(C), flush_output(C),
            read(A, f(E2, G2)),
            E2 == E, G2 == G,
            writeq(A, ok), put_char(A, '.'), nl(A), flush_output(A),
            read(C, ok),
            close(C), close(A).")).Success);
    }

    [Fact]
    public void Closing_IsTheEndOfStreamForTheOtherSide()
    {
        Assert.True(WithinSeconds(30, () => Engine().Query(@"
            pair([], C, A),
            put_byte(C, 97), close(C),
            get_byte(A, 97), peek_byte(A, -1), at_end_of_stream(A), get_byte(A, -1),
            caught(get_byte(A, _), permission_error(input, past_end_of_stream, _)),
            close(A).")).Success);
    }

    [Fact]
    public void Closing_DeliversWhatIsBuffered()
    {
        Assert.True(WithinSeconds(30, () => Engine().Query(@"
            pair([type(text)], C, A),
            put_char(C, x), close(C),
            get_char(A, x),
            close(A).")).Success);
    }

    [Fact]
    public void Shutdown_EndsOneDirection_AndTheOtherStillWorks()
    {
        Assert.True(WithinSeconds(30, () => Engine().Query(@"
            pair([], C, A),
            put_byte(C, 97), socket_shutdown(C, write),
            get_byte(A, 97), get_byte(A, -1),
            put_byte(A, 98), flush_output(A),
            get_byte(C, 98),
            close(C), close(A).")).Success);
    }

    private const string Text = "type(text)";
    private const string Bytes = "type(binary)";
    private const string PutText = "put_char(C, a)";
    private const string PutByte = "put_byte(C, 97)";
    private const string GetText = "get_char(A, a)";
    private const string GetByte = "get_byte(A, 97)";

    [Theory]
    [InlineData(Text, PutText, GetText)]
    [InlineData(Bytes, PutByte, GetByte)]
    public void AShortWrite_WaitsForAFlush(string type, string put, string get)
    {
        Assert.True(WithinSeconds(30, () => Engine().Query($@"
            pair([{type}, timeout(5)], C, A),
            {put},
            socket_select([], [], [A], [], [], [], 0.2),
            flush_output(C),
            {get},
            close(C), close(A).")).Success);
    }

    [Theory]
    [InlineData(Text, PutText, GetText)]
    [InlineData(Bytes, PutByte, GetByte)]
    public void Closing_SendsWhatTheStreamHolds(string type, string put, string get)
    {
        Assert.True(WithinSeconds(30, () => Engine().Query($@"
            pair([{type}, timeout(5)], C, A),
            {put},
            close(C),
            {get},
            close(A).")).Success);
    }

    [Theory]
    [InlineData(Text, PutText, GetText)]
    [InlineData(Bytes, PutByte, GetByte)]
    public void ALongWrite_GoesOutWithoutAFlush(string type, string put, string get)
    {
        Assert.True(WithinSeconds(30, () => Engine().Query($@"
            pair([{type}, timeout(5)], C, A),
            forall(between(1, 5000, _), {put}),
            {get},
            close(C), close(A).")).Success);
    }

    [Theory]
    [InlineData(Text, PutText, GetText)]
    [InlineData(Bytes, PutByte, GetByte)]
    public void ABufferOfNothing_SendsAtOnce(string type, string put, string get)
    {
        Assert.True(WithinSeconds(30, () => Engine().Query($@"
            pair([{type}, buffer_size(0), timeout(5)], C, A),
            {put},
            {get},
            close(C), close(A).")).Success);
    }

    [Theory]
    [InlineData(Text, PutText, "forall(between(1, 200, _), put_char(C, a))", GetText)]
    [InlineData(Bytes, PutByte, PutByte, GetByte)]
    public void ABuffer_SendsWhenItFills(string type, string put, string fill, string get)
    {
        Assert.True(WithinSeconds(30, () => Engine().Query($@"
            pair([{type}, buffer_size(10), timeout(5)], C, A),
            forall(between(1, 9, _), {put}),
            socket_select([], [], [A], [], [], [], 0.2),
            {fill},
            {get},
            close(C), close(A).")).Success);
    }

    [Theory]
    [InlineData(Text, PutText)]
    [InlineData(Bytes, PutByte)]
    public void ALargerBuffer_HoldsMore(string type, string put)
    {
        Assert.True(WithinSeconds(30, () => Engine().Query($@"
            pair([{type}, buffer_size(100000), timeout(5)], C, A),
            forall(between(1, 5000, _), {put}),
            socket_select([], [], [A], [], [], [], 0.2),
            stream_property(C, buffer_size(100000)),
            close(C), close(A).")).Success);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(200)]
    [InlineData(4096)]
    public void AText_ArrivesWhole_WhateverTheBuffer(int size)
    {
        Assert.True(WithinSeconds(30, () => Engine().Query($@"
            pair([type(text), buffer_size({size}), timeout(5)], C, A),
            findall(X, (between(1, 1000, I), X is 97 + I mod 26), Codes),
            atom_codes(Text, Codes),
            write(C, Text),
            close(C),
            all_codes(A, Codes),
            close(A).")).Success);
    }

    [Fact]
    public void TheBufferSize_IsAProperty()
    {
        Assert.True(WithinSeconds(30, () => Engine().Query(@"
            pair([], C, A),
            stream_property(C, buffer_size(4096)),
            close(C), close(A).")).Success);
    }

    [Fact]
    public void ASelectAboutToWait_SendsWhatTheStreamHolds()
    {
        Assert.True(WithinSeconds(30, () => Engine().Query(@"
            pair([timeout(5)], C, A),
            put_byte(C, 97),
            socket_select([], [], [C], [], [], [], 0.1),
            get_byte(A, 97),
            close(C), close(A).")).Success);
    }

    [Theory]
    [InlineData(Text, "put_char(S, a)", "get_char(S, b)")]
    [InlineData(Bytes, "put_byte(S, 97)", "get_byte(S, 98)")]
    public void AReadAboutToWait_SendsWhatTheStreamHolds(string type, string put, string get)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = Task.Factory.StartNew(() =>
            {
                using TcpClient c = listener.AcceptTcpClient();
                NetworkStream s = c.GetStream();
                int b = s.ReadByte();
                s.WriteByte((byte)(b + 1));
            }, TaskCreationOptions.LongRunning);
            Assert.True(WithinSeconds(30, () => Engine().Query($@"
                socket_client_open('127.0.0.1':{port}, S, [{type}, timeout(5)]),
                {put},
                {get},
                close(S).")).Success);
            WithinSeconds(10, () => { server.GetAwaiter().GetResult(); return true; });
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public void Shutdown_SendsWhatTheStreamHolds()
    {
        Assert.True(WithinSeconds(30, () => Engine().Query(@"
            pair([type(text)], C, A),
            put_char(C, x), socket_shutdown(C, write),
            get_char(A, x), get_char(A, end_of_file),
            close(C), close(A).")).Success);
    }

    // ---------- what a stream reports

    [Fact]
    public void ASocketStream_IsBinaryByDefault_AndGoesBothWays()
    {
        Assert.True(WithinSeconds(30, () => Engine().Query(@"
            pair([], C, A),
            stream_property(C, type(binary)),
            stream_property(C, mode(readwrite)),
            stream_property(C, input), stream_property(C, output),
            stream_property(C, reposition(false)),
            stream_property(C, eof_action(error)),
            \+ stream_property(C, encoding(_)),
            close(C), close(A).")).Success);
    }

    [Theory]
    [InlineData(@"[type(text)]", "UTF-8")]
    [InlineData(@"[encoding('ISO-8859-1')]", "ISO-8859-1")]
    [InlineData(@"[type(text), encoding(utf16le)]", "UTF-16LE")]
    public void AnEncoding_MakesItText(string options, string official)
    {
        var sol = WithinSeconds(30, () => Engine().Query($@"
            pair({options}, C, A),
            stream_property(C, type(text)),
            stream_property(C, encoding(E)),
            close(C), close(A)."));
        Assert.True(sol.Success);
        Assert.Equal(official, Name(sol, "E"));
    }

    [Fact]
    public void BinaryIgnoresAnEncoding()
    {
        Assert.True(WithinSeconds(30, () => Engine().Query(@"
            pair([type(binary), encoding('UTF-16')], C, A),
            stream_property(C, type(binary)),
            close(C), close(A).")).Success);
    }

    [Fact]
    public void Options_ReachTheStream()
    {
        Assert.True(WithinSeconds(30, () => Engine().Query(@"
            socket_server_open('127.0.0.1':P, Srv),
            socket_client_open('127.0.0.1':P, C, [alias(sock), eof_action(eof_code), nodelay(true), keepalive(true), reposition(false)]),
            socket_server_accept(Srv, _, A, []),
            stream_property(C, alias(sock)),
            stream_property(C, eof_action(eof_code)),
            close(A),
            get_byte(sock, -1), get_byte(sock, -1),
            close(C), socket_server_close(Srv).")).Success);
    }

    [Fact]
    public void ATakenAlias_IsAnError()
    {
        Assert.True(WithinSeconds(30, () => Engine().Query(@"
            socket_server_open('127.0.0.1':P, Srv),
            socket_client_open('127.0.0.1':P, C, [alias(sock)]),
            caught(socket_client_open('127.0.0.1':P, _, [alias(sock)]),
                   permission_error(open, source_sink, alias(sock))),
            close(C), socket_server_close(Srv).")).Success);
    }

    [Fact]
    public void Text_FromThePeer_NeverRaisesWithNoEncodingNamed()
    {
        Assert.True(WithinSeconds(30, () => Engine().Query(@"
            pair([type(text)], C, A),
            close(A),
            close(C).")).Success);
        Assert.True(WithinSeconds(30, () => Engine().Query(@"
            socket_server_open('127.0.0.1':P, Srv),
            socket_client_open('127.0.0.1':P, C, [type(binary)]),
            socket_server_accept(Srv, _, A, [type(text)]),
            put_byte(C, 0x61), put_byte(C, 0x82), put_byte(C, 0x62), flush_output(C),
            get_code(A, 0x61), get_code(A, 0xFFFD), get_code(A, 0x62),
            close(C), close(A), socket_server_close(Srv).")).Success);
    }

    [Fact]
    public void Text_InANamedEncoding_RaisesWhatDoesNotFit()
    {
        Assert.True(WithinSeconds(30, () => Engine().Query(@"
            socket_server_open('127.0.0.1':P, Srv),
            socket_client_open('127.0.0.1':P, C, [type(binary)]),
            socket_server_accept(Srv, _, A, [encoding('UTF-8')]),
            put_byte(C, 0x82), put_byte(C, 0x62), flush_output(C),
            caught(get_code(A, _), representation_error(character)),
            get_code(A, 0x62),
            close(C), close(A), socket_server_close(Srv).")).Success);
    }

    [Fact]
    public void ANewline_ArrivesAsItWasSent()
    {
        Assert.True(WithinSeconds(30, () => Engine().Query(@"
            socket_server_open('127.0.0.1':P, Srv),
            socket_client_open('127.0.0.1':P, C, [type(binary)]),
            socket_server_accept(Srv, _, A, [type(text)]),
            put_byte(C, 13), put_byte(C, 10), flush_output(C),
            get_code(A, 13), get_code(A, 10),
            close(C), close(A), socket_server_close(Srv).")).Success);
    }

    // ---------- addresses

    [Theory]
    [InlineData(@"'127.0.0.1':P", @"'127.0.0.1':P")]
    [InlineData(@"inet('127.0.0.1', P)", @"inet('127.0.0.1', P)")]
    [InlineData(@"'127.0.0.1':P", @"P")]
    [InlineData(@"'127.0.0.1':P", @"localhost:P")]
    [InlineData(@"'127.0.0.1':P", @"'':P")]
    public void EveryFormOfAddress_Connects(string server, string client)
    {
        Assert.True(WithinSeconds(30, () => Engine().Query($@"
            socket_server_open({server}, Srv),
            integer(P),
            socket_client_open({client}, C, []),
            socket_server_accept(Srv, Client, A, []),
            atom(Client),
            close(C), close(A), socket_server_close(Srv).")).Success);
    }

    [Fact]
    public void AnIPv6Literal_Connects()
    {
        if (!Socket.OSSupportsIPv6) return;
        Assert.True(WithinSeconds(30, () => Engine().Query(@"
            socket_server_open('::1':P, Srv),
            socket_client_open('::1':P, C, []),
            socket_server_accept(Srv, '::1', A, []),
            close(C), close(A), socket_server_close(Srv).")).Success);
    }

    [Theory]
    [InlineData(@"P")]
    [InlineData(@"inet('', P)")]
    [InlineData(@"'':P")]
    public void AServerOnAllInterfaces_TakesIPv4(string address)
    {
        if (!EveryInterface.MayListen) return;
        var sol = WithinSeconds(30, () => Engine().Query($@"
            socket_server_open({address}, Srv),
            integer(P),
            socket_client_open('127.0.0.1':P, C, []),
            socket_server_accept(Srv, Client, A, []),
            close(C), close(A), socket_server_close(Srv)."));
        Assert.True(sol.Success);
        Assert.Equal("127.0.0.1", Name(sol, "Client"));
    }

    [Fact]
    public void ALoopbackServer_Listens()
    {
        Assert.True(WithinSeconds(30, () => Engine().Query(@"
            socket_server_open(P, Srv, [loopback(true), reuseaddr(true), backlog(2)]),
            socket_client_open('127.0.0.1':P, C, []),
            close(C), socket_server_close(Srv).")).Success);
    }

    [Fact]
    public void AChosenPort_IsAnInteger()
    {
        var sol = WithinSeconds(30, () => Engine().Query(@"
            socket_server_open('127.0.0.1':P, Srv), socket_server_close(Srv)."));
        Assert.True(((IntTerm)sol["P"]!).Value > 0);
    }

    [Fact]
    public void APort_MayBeAnAtomOfDigits()
    {
        Assert.True(WithinSeconds(30, () => Engine().Query(@"
            socket_server_open('127.0.0.1':P, Srv0), socket_server_close(Srv0),
            number_codes(P, Cs), atom_codes(Atom, Cs),
            socket_server_open('127.0.0.1':Atom, Srv),
            socket_client_open('127.0.0.1':Atom, C, []),
            close(C), socket_server_close(Srv).")).Success);
    }

    [Theory]
    [InlineData("daytime", 13)]
    public void APort_MayBeAServiceName(string name, int port)
    {
        // A port under 1024 may need privileges to listen on, so this tests the name
        // alone: the connection is refused or not, but never for not knowing the name.
        var error = WithinSeconds(30, () => Engine().Query($@"
            catch((socket_client_open('127.0.0.1':{name}, C, [timeout(2)]), close(C), E = none),
                  error(E, _), true)."))["E"]!;
        Assert.DoesNotContain("socket_service", error.ToString());
        Assert.DoesNotContain("socket_port", error.ToString());
        Assert.Equal(port, ServiceNames.Port(name));
    }

    [Fact]
    public void AServiceName_MayBeAnAlias()
    {
        Assert.Equal(80, ServiceNames.Port("www"));
    }

    [Fact]
    public void AnUnknownServiceName_IsAnError()
    {
        Assert.True(WithinSeconds(30, () => Engine().Query(@"
            caught(socket_client_open('127.0.0.1':no_such_service_x, _, []),
                   existence_error(socket_service, no_such_service_x)).")).Success);
    }

    [Fact]
    public void NumericServname_RefusesAName()
    {
        Assert.True(WithinSeconds(30, () => Engine().Query(@"
            caught(socket_client_open('127.0.0.1':daytime, _, [numeric_servname(true)]),
                   domain_error(socket_port, daytime)).")).Success);
    }

    [Fact]
    public void NumericNodename_RefusesAName()
    {
        Assert.True(WithinSeconds(30, () => Engine().Query(@"
            caught(socket_client_open(localhost:80, _, [numeric_nodename(true)]),
                   domain_error(socket_address, localhost:80)).")).Success);
    }

    [Theory]
    [InlineData(@"caught(socket_client_open(_, _, []), instantiation_error)")]
    [InlineData(@"caught(socket_client_open(_:80, _, []), instantiation_error)")]
    [InlineData(@"caught(socket_client_open(localhost:_, _, []), instantiation_error)")]
    [InlineData(@"caught(socket_client_open(f(x), _, []), domain_error(socket_address, f(x)))")]
    [InlineData(@"caught(socket_client_open(1:2, _, []), domain_error(socket_address, 1:2))")]
    [InlineData(@"caught(socket_client_open(localhost:70000, _, []), domain_error(socket_port, 70000))")]
    [InlineData(@"caught(socket_client_open(localhost:(-1), _, []), domain_error(socket_port, -1))")]
    [InlineData(@"caught(socket_client_open(localhost:80, _, [nosuch(1)]), domain_error(socket_option, nosuch(1)))")]
    [InlineData(@"caught(socket_client_open(localhost:80, _, [type(other)]), domain_error(socket_option, type(other)))")]
    [InlineData(@"caught(socket_client_open(localhost:80, _, [encoding(klingon)]), domain_error(socket_option, encoding(klingon)))")]
    [InlineData(@"caught(socket_client_open(localhost:80, _, [eof_action(x)]), domain_error(socket_option, eof_action(x)))")]
    [InlineData(@"caught(socket_client_open(localhost:80, _, [timeout(-1)]), domain_error(socket_option, timeout(-1)))")]
    [InlineData(@"caught(socket_client_open(localhost:80, _, [reposition(true)]), domain_error(socket_option, reposition(true)))")]
    [InlineData(@"caught(socket_client_open(localhost:80, _, foo), type_error(list, foo))")]
    [InlineData(@"caught(socket_client_open(localhost:80, _, [_]), instantiation_error)")]
    [InlineData(@"caught(socket_server_open(1, _, [type(text)]), domain_error(socket_option, type(text)))")]
    [InlineData(@"caught(socket_server_open(1, _, [backlog(0)]), domain_error(socket_option, backlog(0)))")]
    [InlineData(@"caught(socket_server_open(1, _, [buffer_size(1)]), domain_error(socket_option, buffer_size(1)))")]
    [InlineData(@"caught(socket_client_open(1, _, [buffer_size(-1)]), domain_error(socket_option, buffer_size(-1)))")]
    [InlineData(@"caught(socket_client_open(1, _, [buffer_size(a)]), domain_error(socket_option, buffer_size(a)))")]
    [InlineData(@"caught(socket_client_open(1, _, [buffer_size(_)]), instantiation_error)")]
    [InlineData(@"caught(socket_server_accept(_, _, _, []), instantiation_error)")]
    [InlineData(@"caught(socket_server_accept(foo, _, _, []), domain_error(server_socket, foo))")]
    [InlineData(@"caught(socket_server_close('$socket'(999)), existence_error(server_socket, '$socket'(999)))")]
    [InlineData(@"caught(socket_shutdown(user_output, write), domain_error(socket_stream, user_output))")]
    public void BadArguments_AreErrors(string goal)
    {
        Assert.True(WithinSeconds(30, () => Engine().Query(goal + ".")).Success, goal);
    }

    [Fact]
    public void AnError_NamesThePredicateOfTheLibrary()
    {
        Assert.True(WithinSeconds(30, () => Engine().Query(@"
            catch(socket_client_open(f(x), _, []), error(_, Context), true),
            Context == socket_client_open/3.")).Success);
    }

    [Fact]
    public void AClosedServer_IsAnError_AndItsPortIsFree()
    {
        Assert.True(WithinSeconds(30, () => Engine().Query(@"
            socket_server_open('127.0.0.1':P, Srv),
            socket_server_close(Srv),
            caught(socket_server_close(Srv), existence_error(server_socket, _)),
            caught(socket_server_accept(Srv, _, _, []), existence_error(server_socket, _)),
            socket_server_open('127.0.0.1':P, Srv2),
            socket_server_close(Srv2).")).Success);
    }

    [Fact]
    public void ARefusedConnection_IsASystemError()
    {
        // Bound and not listening: refused, and no other process can take the port.
        using var held = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        held.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)held.LocalEndPoint!).Port;
        Assert.True(WithinSeconds(60, () => Engine().Query($@"
            caught(socket_client_open('127.0.0.1':{port}, _, []), system_error(_)).")).Success);
    }

    [Fact]
    public void ABusyPort_IsASystemError()
    {
        Assert.True(WithinSeconds(30, () => Engine().Query(@"
            socket_server_open('127.0.0.1':P, Srv),
            caught(socket_server_open('127.0.0.1':P, _), system_error(_)),
            socket_server_close(Srv).")).Success);
    }

    [Fact]
    public void AnUnknownHost_IsASystemError()
    {
        Assert.True(WithinSeconds(60, () => Engine().Query(@"
            caught(socket_client_open('no.such.host.invalid':80, _, []), system_error(_)).")).Success);
    }

    [Fact]
    public void CurrentHost_IsAnAtom()
    {
        Assert.Equal(Dns.GetHostName(), Name(Engine().Query("current_host(H)."), "H"));
        Assert.Equal(Dns.GetHostName(), Name(Engine().Query("current_hostname(H)."), "H"));
    }

    // ---------- waiting

    [Fact]
    public void AnAcceptWithATimeout_RaisesAndLosesNoConnection()
    {
        Assert.True(WithinSeconds(30, () => Engine().Query(@"
            socket_server_open('127.0.0.1':P, Srv),
            caught(socket_server_accept(Srv, _, _, [timeout(0.1)]), timeout_error(accept)),
            socket_client_open('127.0.0.1':P, C, []),
            socket_server_accept(Srv, _, A, [timeout(5)]),
            close(C), close(A), socket_server_close(Srv).")).Success);
    }

    [Fact]
    public void AReadWithATimeout_RaisesAndLosesNoBytes()
    {
        Assert.True(WithinSeconds(30, () => Engine().Query(@"
            pair([timeout(1)], C, A),
            caught(get_byte(A, _), timeout_error(read)),
            put_byte(C, 97), flush_output(C),
            get_byte(A, 97),
            close(C), close(A).")).Success);
    }

    [Fact]
    public void AnAcceptThatWaits_IsCancelled()
    {
        var e = Engine();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var ex = Assert.ThrowsAny<AggregateException>(() => WithinSeconds(20, () => e.QueryAll(@"
            socket_server_open('127.0.0.1':_, Srv), socket_server_accept(Srv, _, _, []).", cts.Token).ToList()));
        Assert.IsAssignableFrom<OperationCanceledException>(ex.InnerException);
    }

    [Fact]
    public void AnAcceptThatRunsOutOfTime_GivesTimeOut()
    {
        var sol = WithinSeconds(30, () => Engine().Query(@"
            socket_server_open('127.0.0.1':_, Srv),
            time_out(socket_server_accept(Srv, _, _, []), 200, R),
            socket_server_close(Srv)."));
        Assert.Equal("time_out", Name(sol, "R"));
    }

    // ---------- socket_select/7

    [Fact]
    public void Select_WithNothingReady_GivesEmptyListsAfterTheTimeout()
    {
        var watch = Stopwatch.StartNew();
        Assert.True(WithinSeconds(30, () => Engine().Query(@"
            pair([], C, A),
            socket_server_open('127.0.0.1':_, Srv),
            socket_select([Srv], [], [A], [], [], [], 0.2),
            close(C), close(A), socket_server_close(Srv).")).Success);
        Assert.True(watch.ElapsedMilliseconds >= 150, watch.ElapsedMilliseconds.ToString());
    }

    [Fact]
    public void Select_FindsAPendingConnection()
    {
        Assert.True(WithinSeconds(30, () => Engine().Query(@"
            socket_server_open('127.0.0.1':P, Srv),
            socket_client_open('127.0.0.1':P, C, []),
            socket_select([Srv], [Srv], [], [], [], [], off),
            socket_server_accept(Srv, _, A, []),
            close(C), close(A), socket_server_close(Srv).")).Success);
    }

    [Fact]
    public void Select_FindsAStreamWithData_AndLosesNoBytes()
    {
        Assert.True(WithinSeconds(30, () => Engine().Query(@"
            pair([], C1, A1), pair([], C2, A2),
            put_byte(C2, 97), flush_output(C2),
            socket_select([], [], [A1, A2], Ready, [], [], off),
            Ready == [A2],
            get_byte(A2, 97),
            close(C1), close(A1), close(C2), close(A2).")).Success);
    }

    [Fact]
    public void Select_SeesTheEndOfAStream()
    {
        Assert.True(WithinSeconds(30, () => Engine().Query(@"
            pair([], C, A),
            close(C),
            socket_select([], [], [A], [A], [], [], off),
            get_byte(A, -1),
            close(A).")).Success);
    }

    [Fact]
    public void Select_CountsWhatAPeekLeftBuffered()
    {
        Assert.True(WithinSeconds(30, () => Engine().Query(@"
            pair([], C, A),
            put_byte(C, 97), flush_output(C),
            peek_byte(A, 97),
            socket_select([], [], [A], [A], [], [], 0),
            get_byte(A, 97),
            close(C), close(A).")).Success);
    }

    [Fact]
    public void Select_KeepsTheTermOfAnEntry()
    {
        var sol = WithinSeconds(30, () => Engine().Query(@"
            pair([], C, A),
            put_byte(C, 97), flush_output(C),
            socket_select([], [], [client(X)-A], [Y-A], [], [], off),
            X = 1, Y == client(1),
            socket_select([], [], [], [], [out-C], [out-C], off),
            close(C), close(A)."));
        Assert.True(sol.Success);
    }

    [Fact]
    public void Select_SeesWhatAPeekLeftInTheReader()
    {
        Assert.True(WithinSeconds(30, () => Engine().Query(@"
            pair([type(text)], C, A),
            put_char(C, x), flush_output(C),
            peek_char(A, x),
            socket_select([], [], [A], [A], [], [], 0),
            close(C), close(A).")).Success);
    }

    [Fact]
    public void Select_OutputIsReady()
    {
        Assert.True(WithinSeconds(30, () => Engine().Query(@"
            pair([], C, A),
            socket_select([], [], [], [], [C], [C], off),
            close(C), close(A).")).Success);
    }

    [Fact]
    public void Select_TakesAFile()
    {
        string file = Path.Combine(Path.GetTempPath(), "shumway-select-" + Guid.NewGuid().ToString("N")).Replace('\\', '/');
        File.WriteAllText(file, "a");
        try
        {
            Assert.True(WithinSeconds(30, () => Engine().Query($@"
                open('{file}', read, S),
                socket_select([], [], [S], [S], [], [], 0),
                close(S).")).Success);
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData(@"caught(socket_select(_, _, [], _, [], _, 0), instantiation_error)")]
    [InlineData(@"caught(socket_select([foo], _, [], _, [], _, 0), domain_error(server_socket, foo))")]
    [InlineData(@"caught(socket_select([], _, [], _, [], _, foo), domain_error(socket_select_timeout, foo))")]
    [InlineData(@"caught(socket_select([], _, [user_output], _, [], _, 0), permission_error(input, stream, _))")]
    [InlineData(@"caught(socket_select([], _, [], _, [user_input], _, 0), permission_error(output, stream, _))")]
    public void Select_BadArguments_AreErrors(string goal)
    {
        Assert.True(WithinSeconds(30, () => Engine().Query(goal + ".")).Success, goal);
    }

    [Fact]
    public void ASingleThreadedServer_ServesSeveralClients()
    {
        Assert.True(WithinSeconds(60, () => Engine().Query(@"
            socket_server_open('127.0.0.1':P, Srv),
            socket_client_open('127.0.0.1':P, C1, []),
            socket_client_open('127.0.0.1':P, C2, []),
            socket_select([Srv], [Srv], [], [], [], [], off),
            socket_server_accept(Srv, _, A1, []),
            socket_select([Srv], [Srv], [], [], [], [], off),
            socket_server_accept(Srv, _, A2, []),
            put_byte(C2, 2), flush_output(C2),
            socket_select([], [], [A1, A2], [A2], [], [], off),
            get_byte(A2, 2),
            put_byte(C1, 1), flush_output(C1),
            socket_select([], [], [A1, A2], [A1], [], [], off),
            get_byte(A1, 1),
            close(C1), close(C2), close(A1), close(A2),
            socket_server_close(Srv).")).Success);
    }

    // ---------- with a peer outside Prolog

    [Fact]
    public void ACSharpServer_AnswersAPrologClient()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = OwnThread.Run(() =>
            {
                using TcpClient c = listener.AcceptTcpClient();
                NetworkStream s = c.GetStream();
                int b = s.ReadByte();
                s.WriteByte((byte)(b + 1));
            });
            Assert.True(WithinSeconds(30, () => Engine().Query($@"
                socket_client_open('127.0.0.1':{port}, S, [nodelay(true)]),
                put_byte(S, 7), flush_output(S),
                get_byte(S, 8),
                close(S).")).Success);
            WithinSeconds(10, () => { server.GetAwaiter().GetResult(); return true; });
        }
        finally { listener.Stop(); }
    }

#if !NETFRAMEWORK
    [Fact]
    public void AUnixSocket_Connects()
    {
        string path = Path.Combine(Path.GetTempPath(), "shum" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".sock").Replace('\\', '/');
        try
        {
            Assert.True(WithinSeconds(30, () => Engine().Query($@"
                socket_server_open(unix('{path}'), Srv),
                socket_client_open(unix('{path}'), C, []),
                socket_server_accept(Srv, _, A, []),
                put_byte(C, 97), flush_output(C),
                get_byte(A, 97),
                close(C), close(A), socket_server_close(Srv).")).Success);
        }
        finally { File.Delete(path); }
    }
#endif

    // ---------- the library

    [Fact]
    public void TheLibrary_FillsNoGlobalName()
    {
        var e = new PrologEngine();
        Assert.False(e.Query("catch(socket_client_open(x, _, []), error(existence_error(procedure, _), _), fail).").Success);
        e.ConsultString(":- use_module(library(sockets)).");
        var other = new PrologEngine();
        other.ConsultString(@"
:- module(m, [p/0]).
:- use_module(library(sockets)).
p :- socket_server_open('127.0.0.1':_, S), socket_server_close(S).
");
        Assert.True(other.Query("m:p.").Success);
        Assert.True(other.Query("catch(socket_server_close(x), error(existence_error(procedure, _), _), true).").Success);
    }

    [Fact]
    public void ALibraryOfThatNameOnThePath_DoesNotReplaceIt()
    {
        string dir = Path.Combine(Path.GetTempPath(), "shumway-sockets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "sockets.pl"), ":- module(sockets, [current_host/1]).\ncurrent_host(fake).\n");
            var e = new PrologEngine();
            e.AddLibraryDirectory(dir);
            e.ConsultString(":- use_module(library(sockets)).");
            Assert.Equal(Dns.GetHostName(), Name(e.Query("current_host(H)."), "H"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
