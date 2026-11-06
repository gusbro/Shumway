using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>The UDP part of library(sockets), on the loopback interface.</summary>
public sealed class UdpLibraryTests
{
    private const string Helpers = @"
:- use_module(library(sockets)).

caught(Goal, Error) :- catch((Goal, fail), error(Error, _), true).

pair(A, P, B) :-
    udp_socket(A), udp_bind(A, '127.0.0.1':P),
    udp_socket(B).
";

    private static PrologEngine Engine()
    {
        var e = new PrologEngine();
        e.ConsultString(Helpers);
        return e;
    }

    private static T WithinSeconds<T>(int seconds, Func<T> work)
    {
        var task = OwnThread.Run(work);
        Assert.True(task.Wait(TimeSpan.FromSeconds(seconds)), "it hung");
        return task.Result;
    }

    [Fact]
    public void ADatagram_GoesAndTheAnswerComesBack()
    {
        Assert.True(WithinSeconds(20, () => Engine().Query(@"
            pair(A, P, B),
            udp_send(B, hello, '127.0.0.1':P, []),
            udp_receive(A, Got, From, [as(atom)]),
            Got == hello,
            From = '127.0.0.1':Port, integer(Port),
            udp_send(A, there, From, []),
            udp_receive(B, Back, _, [as(atom)]),
            Back == there,
            udp_close(A), udp_close(B).")).Success);
    }

    [Theory]
    [InlineData(@"[as(atom)]", @"Got == 'aé'")]
    [InlineData(@"[as(codes)]", @"Got == [0x61, 0xE9]")]
    [InlineData(@"[as(chars)]", @"Got == [a, 'é']")]
    [InlineData(@"[as(string)]", @"Got == [a, 'é']")]
    [InlineData(@"[]", @"Got == [a, 'é']")]
    [InlineData(@"[type(binary)]", @"Got == [0x61, 0xC3, 0xA9]")]
    public void AReceive_GivesTheDataAsAsked(string options, string check)
    {
        Assert.True(WithinSeconds(20, () => Engine().Query($@"
            pair(A, P, B),
            udp_send(B, 'aé', '127.0.0.1':P, []),
            udp_receive(A, Got, _, {options}),
            {check},
            udp_close(A), udp_close(B).")).Success, options);
    }

    [Theory]
    [InlineData(@"[0x61, 0xE9]")]
    [InlineData(@"[a, 'é']")]
    [InlineData(@"'aé'")]
    public void ASend_TakesAnyText(string data)
    {
        Assert.True(WithinSeconds(20, () => Engine().Query($@"
            pair(A, P, B),
            udp_send(B, {data}, '127.0.0.1':P, []),
            udp_receive(A, Got, _, [as(atom)]),
            Got == 'aé',
            udp_close(A), udp_close(B).")).Success, data);
    }

    [Fact]
    public void Bytes_GoAsTheyAre()
    {
        Assert.True(WithinSeconds(20, () => Engine().Query(@"
            pair(A, P, B),
            udp_send(B, [0, 200, 255], '127.0.0.1':P, [type(binary)]),
            udp_receive(A, [0, 200, 255], _, [type(binary)]),
            udp_close(A), udp_close(B).")).Success);
    }

    [Fact]
    public void AnEncoding_RulesBothEnds()
    {
        Assert.True(WithinSeconds(20, () => Engine().Query(@"
            pair(A, P, B),
            udp_send(B, 'é', '127.0.0.1':P, [encoding('UTF-16LE')]),
            udp_receive(A, [0xE9, 0x00], _, [type(binary)]),
            udp_send(B, 'é', '127.0.0.1':P, [encoding('ISO-8859-1')]),
            udp_receive(A, 'é', _, [as(atom), encoding('ISO-8859-1')]),
            udp_close(A), udp_close(B).")).Success);
    }

    [Fact]
    public void ACharacterTheEncodingCannotHold_IsAnError()
    {
        Assert.True(WithinSeconds(20, () => Engine().Query(@"
            pair(A, P, B),
            char_code(C, 0x3042),
            caught(udp_send(B, C, '127.0.0.1':P, [encoding('ISO-8859-1')]), representation_error(character)),
            udp_close(A), udp_close(B).")).Success);
    }

    [Fact]
    public void Text_NeverRaisesWithNoEncodingNamed_AndDoesInANamedOne()
    {
        Assert.True(WithinSeconds(20, () => Engine().Query(@"
            pair(A, P, B),
            udp_send(B, [0x61, 0x82], '127.0.0.1':P, [type(binary)]),
            udp_receive(A, Got, _, [as(codes)]),
            Got == [0x61, 0xFFFD],
            udp_send(B, [0x61, 0x82], '127.0.0.1':P, [type(binary)]),
            caught(udp_receive(A, _, _, [encoding('UTF-8')]), representation_error(character)),
            udp_close(A), udp_close(B).")).Success);
    }

    [Fact]
    public void AnEmptyDatagram_IsEmpty()
    {
        Assert.True(WithinSeconds(20, () => Engine().Query(@"
            pair(A, P, B),
            udp_send(B, '', '127.0.0.1':P, []),
            udp_receive(A, Got, _, [as(atom)]),
            Got == '',
            udp_close(A), udp_close(B).")).Success);
    }

    [Fact]
    public void AChosenPort_IsAnInteger()
    {
        var sol = WithinSeconds(20, () => Engine().Query(@"
            udp_socket(B), udp_bind(B, '127.0.0.1':Q), udp_close(B),
            udp_socket(C), udp_bind(C, inet('127.0.0.1', R)), udp_close(C)."));
        foreach (string v in new[] { "Q", "R" })
            Assert.True(((IntTerm)sol[v]!).Value > 0, v);
    }

    [Theory]
    [InlineData("[broadcast(true)]", true)]
    [InlineData("[broadcast(false)]", false)]
    [InlineData("[]", false)]
    public void Broadcast_IsAnOption(string options, bool expected)
    {
        var e = Engine();
        var sol = WithinSeconds(20, () => e.Query($"udp_socket(S, {options}), S = '$udp_socket'(Id)."));
        var entry = (MetaBuiltins.UdpSocketEntry)e.NetObject((int)((IntTerm)sol["Id"]!).Value)!;
        Assert.Equal(expected, entry.Client.EnableBroadcast);
        entry.Client.Dispose();
    }

    [Fact]
    public void ABoundPort_TakesIPv4FromAnywhere()
    {
        if (!EveryInterface.MayListen) return;
        Assert.True(WithinSeconds(20, () => Engine().Query(@"
            udp_socket(A), udp_bind(A, P), integer(P),
            udp_socket(B),
            udp_send(B, x, '127.0.0.1':P, []),
            udp_receive(A, x, '127.0.0.1':_, [as(atom)]),
            udp_close(A), udp_close(B).")).Success);
    }

    [Fact]
    public void IPv6_Works()
    {
        if (!Socket.OSSupportsIPv6) return;
        Assert.True(WithinSeconds(20, () => Engine().Query(@"
            udp_socket(A), udp_bind(A, '::1':P),
            udp_socket(B),
            udp_send(B, x, '::1':P, []),
            udp_receive(A, x, '::1':_, [as(atom)]),
            udp_close(A), udp_close(B).")).Success);
    }

    [Fact]
    public void Options_AreTaken()
    {
        Assert.True(WithinSeconds(20, () => Engine().Query(@"
            udp_socket(A, [broadcast(true), reuseaddr(true)]),
            udp_close(A).")).Success);
    }

    // ---------- waiting

    [Fact]
    public void AReceiveWithATimeout_RaisesAndLosesNoDatagram()
    {
        Assert.True(WithinSeconds(20, () => Engine().Query(@"
            pair(A, P, B),
            caught(udp_receive(A, _, _, [timeout(0.1)]), timeout_error(read)),
            udp_send(B, x, '127.0.0.1':P, []),
            udp_receive(A, x, _, [as(atom), timeout(5)]),
            udp_close(A), udp_close(B).")).Success);
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void ASend_DoesNotMindACollection()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var to = new IPEndPoint(IPAddress.Loopback.MapToIPv6(), ((IPEndPoint)server.Client.LocalEndPoint!).Port);
        int errors = 0;
        string? first = null;
        bool stop = false;
        var collectors = new Thread[2];
        for (int i = 0; i < collectors.Length; i++)
        {
            collectors[i] = new Thread(() =>
            {
                var keep = new object[64];
                for (int n = 0; !Volatile.Read(ref stop); n++)
                {
                    // Garbage between live objects, for a compaction to have something to move.
                    keep[n % keep.Length] = new byte[1024];
                    for (int k = 0; k < 200; k++) GC.KeepAlive(new byte[256]);
                    GC.Collect(0, GCCollectionMode.Forced, blocking: true, compacting: true);
                }
            });
            collectors[i].Start();
        }
        var senders = new Thread[4];
        for (int i = 0; i < senders.Length; i++)
        {
            senders[i] = new Thread(() =>
            {
                var data = new byte[] { 1 };
                for (int n = 0; n < 3000; n++)
                {
                    using var client = new UdpClient(AddressFamily.InterNetworkV6);
                    client.Client.DualMode = true;
                    try { MetaBuiltins.Send(client, data, to); }
                    catch (SocketException ex)
                    {
                        Interlocked.Increment(ref errors);
                        Interlocked.CompareExchange(ref first, ex.Message, null);
                    }
                }
            });
            senders[i].Start();
        }
        foreach (Thread s in senders) s.Join();
        Volatile.Write(ref stop, true);
        foreach (Thread c in collectors) c.Join();
        Assert.True(errors == 0, $"{errors} sends failed, the first with: {first}");
    }

    [Fact]
    public void AReceive_WithTheTokenAlreadyCancelled_IsCancelled()
    {
        var e = Engine();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ex = Assert.ThrowsAny<AggregateException>(() => WithinSeconds(20, () => e.QueryAll(@"
            udp_socket(A), udp_bind(A, '127.0.0.1':_), udp_receive(A, _, _, []).", cts.Token).ToList()));
        Assert.IsAssignableFrom<OperationCanceledException>(ex.InnerException);
    }

    [Fact]
    public void AReceiveThatWaits_IsCancelled()
    {
        var e = Engine();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var ex = Assert.ThrowsAny<AggregateException>(() => WithinSeconds(20, () => e.QueryAll(@"
            udp_socket(A), udp_bind(A, '127.0.0.1':_), udp_receive(A, _, _, []).", cts.Token).ToList()));
        Assert.IsAssignableFrom<OperationCanceledException>(ex.InnerException);
    }

    [Fact]
    public void Select_FindsADatagram_AndLosesIt_Not()
    {
        Assert.True(WithinSeconds(20, () => Engine().Query(@"
            pair(A, P, B),
            socket_select([], [], [A], [], [], [], 0.1),
            udp_send(B, x, '127.0.0.1':P, []),
            socket_select([], [], [A], [A], [], [], off),
            udp_receive(A, x, _, [as(atom)]),
            udp_close(A), udp_close(B).")).Success);
    }

    // ---------- bad arguments

    [Theory]
    [InlineData(@"caught(udp_socket(_, [nosuch(1)]), domain_error(socket_option, nosuch(1)))")]
    [InlineData(@"caught(udp_socket(_, [_]), instantiation_error)")]
    [InlineData(@"caught(udp_socket(_, foo), type_error(list, foo))")]
    [InlineData(@"caught(udp_bind(_, 1), instantiation_error)")]
    [InlineData(@"caught(udp_bind(foo, 1), domain_error(udp_socket, foo))")]
    [InlineData(@"caught(udp_close('$udp_socket'(999)), existence_error(udp_socket, _))")]
    [InlineData(@"udp_socket(S), caught(udp_bind(S, unix(x)), domain_error(socket_address, unix(x))), udp_close(S)")]
    [InlineData(@"udp_socket(S), caught(udp_send(S, _, 'h':1, []), instantiation_error), udp_close(S)")]
    [InlineData(@"udp_socket(S), caught(udp_send(S, x, _, []), instantiation_error), udp_close(S)")]
    [InlineData(@"udp_socket(S), caught(udp_send(S, x, 123, []), domain_error(socket_address, 123)), udp_close(S)")]
    [InlineData(@"udp_socket(S), caught(udp_send(S, f(x), 'h':1, []), type_error(text, f(x))), udp_close(S)")]
    [InlineData(@"udp_socket(S), caught(udp_send(S, [300], 'h':1, [type(binary)]), type_error(byte, 300)), udp_close(S)")]
    [InlineData(@"udp_socket(S), caught(udp_send(S, x, 'h':1, [as(atom)]), domain_error(socket_option, as(atom))), udp_close(S)")]
    [InlineData(@"udp_socket(S), caught(udp_receive(S, _, _, [as(other)]), domain_error(socket_option, as(other))), udp_close(S)")]
    [InlineData(@"udp_socket(S), udp_close(S), caught(udp_close(S), existence_error(udp_socket, _))")]
    public void BadArguments_AreErrors(string goal)
    {
        Assert.True(WithinSeconds(20, () => Engine().Query(goal + ".")).Success, goal);
    }
}
