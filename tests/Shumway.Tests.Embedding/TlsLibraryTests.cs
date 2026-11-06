using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>library(tls), against a peer written in C#: a handshake needs both ends moving
/// at once, which one Prolog thread cannot do. The certificate is made here, self-signed
/// for localhost, so the tests need no files and no network.</summary>
public sealed class TlsLibraryTests : IDisposable
{
    private const string Password = "secret";

    private const string Helpers = @"
:- use_module(library(sockets)).
:- use_module(library(tls)).

caught(Goal, Error) :- catch((Goal, fail), error(Error, _), true).

bytes_of(File, Bytes) :-
    open(File, read, S, [type(binary)]), read_bytes(S, Bytes), close(S).
read_bytes(S, Bytes) :-
    get_byte(S, B),
    ( B =:= -1 -> Bytes = [] ; Bytes = [B|T], read_bytes(S, T) ).
";

    private static readonly Lazy<X509Certificate2> Authority = new(MakeCertificate);

    private readonly string _dir = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), "shumway-tls-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string P(string name) => Path.Combine(_dir, name).Replace('\\', '/');

    private static X509Certificate2 MakeCertificate()
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using X509Certificate2 made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return Load(made.Export(X509ContentType.Pkcs12, Password), Password);
    }

    private static X509Certificate2 Load(byte[] pfx, string password)
    {
#if NET9_0_OR_GREATER
        return X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.Exportable);
#else
        return new X509Certificate2(pfx, password, X509KeyStorageFlags.Exportable);
#endif
    }

    private string PfxFile()
    {
        string file = P("identity.pfx");
        File.WriteAllBytes(file, Authority.Value.Export(X509ContentType.Pkcs12, Password));
        return file;
    }

    private string AuthorityFile()
    {
        string file = P("authority.cer");
        File.WriteAllBytes(file, Authority.Value.Export(X509ContentType.Cert));
        return file;
    }

    private static PrologEngine Engine()
    {
        var e = new PrologEngine();
        e.ConsultString(Helpers);
        return e;
    }

    private static T Finished<T>(Task<T> task, int seconds = 30)
    {
        Assert.True(task.Wait(TimeSpan.FromSeconds(seconds)), "it hung");
        return task.Result;
    }

    private static T WithinSeconds<T>(int seconds, Func<T> work) => Finished(Task.Run(work), seconds);

    private static void Joined(Task task) => Assert.True(task.Wait(TimeSpan.FromSeconds(10)), "it hung");

    /// <summary>A server in C# that takes one TLS connection, adds one to each byte it gets
    /// and closes at the end of input. Before the handshake it sends what <paramref name="greeting"/> says.</summary>
    private static (int Port, Task Done) TlsEchoServer(byte[]? greeting = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task done = Task.Run(() =>
        {
            try
            {
                using TcpClient client = listener.AcceptTcpClient();
                NetworkStream raw = client.GetStream();
                if (greeting is not null) raw.Write(greeting, 0, greeting.Length);
                using var tls = new SslStream(raw);
                tls.AuthenticateAsServer(Authority.Value, false, SslProtocols.None, false);
                int b;
                while ((b = tls.ReadByte()) >= 0)
                {
                    tls.WriteByte((byte)(b + 1));
                    tls.Flush();
                }
            }
            catch (Exception ex) when (ex is IOException or AuthenticationException or SocketException) { }
            finally { listener.Stop(); }
        });
        return (port, done);
    }

    // ---------- as a client

    [Fact]
    public void AClient_TalksToAServer_WithoutVerifying()
    {
        var (port, done) = TlsEchoServer();
        Assert.True(WithinSeconds(120, () => Engine().Query($@"
            socket_client_open('127.0.0.1':{port}, S0, []),
            tls_client_context(Ctx, [verify(false)]),
            tls_client_negotiate(Ctx, S0, S),
            put_byte(S, 7), flush_output(S),
            get_byte(S, 8),
            close(S).")).Success);
        Joined(done);
    }

    [Fact]
    public void AClient_RefusesAnUnknownAuthority_ByDefault()
    {
        var (port, _) = TlsEchoServer();
        Assert.True(WithinSeconds(120, () => Engine().Query($@"
            socket_client_open(localhost:{port}, S0, []),
            tls_client_context(Ctx, []),
            caught(tls_client_negotiate(Ctx, S0, _), system_error(_)),
            \+ is_stream(S0).")).Success);
    }

    [Fact]
    public void AClient_TrustsTheAuthorityItNames()
    {
        var (port, done) = TlsEchoServer();
        Assert.True(WithinSeconds(120, () => Engine().Query($@"
            socket_client_open(localhost:{port}, S0, []),
            tls_client_context(Ctx, [cacert_file('{AuthorityFile()}')]),
            tls_client_negotiate(Ctx, S0, S),
            put_byte(S, 1), flush_output(S),
            get_byte(S, 2),
            close(S).")).Success);
        Joined(done);
    }

    [Fact]
    public void AClient_RefusesAnAuthorityOtherThanTheOneNamed()
    {
        using X509Certificate2 other = MakeCertificate();
        File.WriteAllBytes(P("other.cer"), other.Export(X509ContentType.Cert));
        var (port, _) = TlsEchoServer();
        Assert.True(WithinSeconds(120, () => Engine().Query($@"
            socket_client_open(localhost:{port}, S0, []),
            tls_client_context(Ctx, [cacert_file('{P("other.cer")}')]),
            caught(tls_client_negotiate(Ctx, S0, _), system_error(_)).")).Success);
    }

    [Fact]
    public void AClient_RefusesAWrongName_EvenWithTheAuthority()
    {
        var (port, _) = TlsEchoServer();
        Assert.True(WithinSeconds(120, () => Engine().Query($@"
            socket_client_open(localhost:{port}, S0, []),
            tls_client_context(Ctx, [cacert_file('{AuthorityFile()}'), hostname('other.example')]),
            caught(tls_client_negotiate(Ctx, S0, _), system_error(_)).")).Success);
    }

    [Fact]
    public void AClient_TakesTheNameFromTheHostItConnectedTo()
    {
        var (port, _) = TlsEchoServer();
        // The certificate is for localhost: by address the name does not match.
        Assert.True(WithinSeconds(120, () => Engine().Query($@"
            socket_client_open('127.0.0.1':{port}, S0, []),
            tls_client_context(Ctx, [cacert_file('{AuthorityFile()}')]),
            caught(tls_client_negotiate(Ctx, S0, _), system_error(_)).")).Success);
        var (port2, done) = TlsEchoServer();
        Assert.True(WithinSeconds(120, () => Engine().Query($@"
            socket_client_open('127.0.0.1':{port2}, S0, []),
            tls_client_context(Ctx, [cacert_file('{AuthorityFile()}'), hostname(localhost)]),
            tls_client_negotiate(Ctx, S0, S),
            close(S).")).Success);
        Joined(done);
    }

    [Fact]
    public void Upgrading_AfterTalkingInTheClear()
    {
        var (port, done) = TlsEchoServer(new byte[] { 42 });
        Assert.True(WithinSeconds(120, () => Engine().Query($@"
            socket_client_open('127.0.0.1':{port}, S0, []),
            get_byte(S0, 42),
            tls_client_context(Ctx, [verify(false)]),
            tls_client_negotiate(Ctx, S0, S),
            put_byte(S, 7), flush_output(S),
            get_byte(S, 8),
            close(S).")).Success);
        Joined(done);
    }

    [Fact]
    public void Upgrading_WithBytesAlreadyRead_IsAnError()
    {
        var (port, _) = TlsEchoServer(new byte[] { 42, 43 });
        Assert.True(WithinSeconds(120, () => Engine().Query($@"
            socket_client_open('127.0.0.1':{port}, S0, []),
            get_byte(S0, 42),
            tls_client_context(Ctx, [verify(false)]),
            caught(tls_client_negotiate(Ctx, S0, _), permission_error(negotiate, stream, _)),
            is_stream(S0),
            close(S0).")).Success);
    }

    [Fact]
    public void ANegotiation_RefusesAReadLeftWaiting()
    {
        var (port, _) = TlsEchoServer();
        Assert.True(WithinSeconds(120, () => Engine().Query($@"
            socket_client_open('127.0.0.1':{port}, S0, []),
            socket_select([], [], [S0], [], [], [], 0),
            tls_client_context(Ctx, [verify(false)]),
            caught(tls_client_negotiate(Ctx, S0, _), permission_error(negotiate, stream, _)),
            close(S0).")).Success);
    }

    [Fact]
    public void ANegotiation_RefusesAPeekedByte()
    {
        var (port, _) = TlsEchoServer(new byte[] { 42 });
        Assert.True(WithinSeconds(120, () => Engine().Query($@"
            socket_client_open('127.0.0.1':{port}, S0, []),
            peek_byte(S0, 42),
            tls_client_context(Ctx, [verify(false)]),
            caught(tls_client_negotiate(Ctx, S0, _), permission_error(negotiate, stream, _)),
            close(S0).")).Success);
    }

    [Fact]
    public void TheNewStream_KeepsTheTimeout()
    {
        var (port, _) = TlsEchoServer();
        Assert.True(WithinSeconds(120, () => Engine().Query($@"
            socket_client_open('127.0.0.1':{port}, S0, [timeout(3)]),
            tls_client_context(Ctx, [verify(false)]),
            tls_client_negotiate(Ctx, S0, S),
            caught(get_byte(S, _), timeout_error(read)),
            close(S).")).Success);
    }

    [Fact]
    public void TheNewStream_KeepsTheBufferSize()
    {
        var (port, _) = TlsEchoServer();
        Assert.True(WithinSeconds(120, () => Engine().Query($@"
            socket_client_open('127.0.0.1':{port}, S0, [buffer_size(7)]),
            tls_client_context(Ctx, [verify(false)]),
            tls_client_negotiate(Ctx, S0, S),
            stream_property(S, buffer_size(7)),
            close(S).")).Success);
    }

    [Fact]
    public void TheNewStream_CanShutDown()
    {
        var (port, done) = TlsEchoServer();
        Assert.True(WithinSeconds(120, () => Engine().Query($@"
            socket_client_open('127.0.0.1':{port}, S0, []),
            tls_client_context(Ctx, [verify(false)]),
            tls_client_negotiate(Ctx, S0, S),
            socket_shutdown(S, write),
            close(S).")).Success);
        Joined(done);
    }

    [Fact]
    public void TheNewStream_KeepsWhatTheOldOneHad()
    {
        var (port, _) = TlsEchoServer();
        Assert.True(WithinSeconds(120, () => Engine().Query($@"
            socket_client_open('127.0.0.1':{port}, S0, [alias(conn), encoding('ISO-8859-1'), eof_action(eof_code)]),
            tls_client_context(Ctx, [verify(false)]),
            tls_client_negotiate(Ctx, S0, S),
            S \== S0, \+ is_stream(S0),
            stream_property(S, alias(conn)),
            stream_property(S, type(text)),
            stream_property(S, encoding('ISO-8859-1')),
            stream_property(S, eof_action(eof_code)),
            stream_property(S, mode(readwrite)),
            close(conn).")).Success);
    }

    [Fact]
    public void TheNewStream_InTextWithNoEncodingNamed_NeverRaises()
    {
        var (port, _) = TlsEchoServer();
        // 0x61 + 1 is b; 0x81 + 1 is 0x82, not valid UTF-8.
        Assert.True(WithinSeconds(120, () => Engine().Query($@"
            socket_client_open('127.0.0.1':{port}, S0, [type(text)]),
            tls_client_context(Ctx, [verify(false)]),
            tls_client_negotiate(Ctx, S0, S),
            put_char(S, a), flush_output(S), get_char(S, b),
            close(S).")).Success);
    }

    [Fact]
    public void ANegotiationWithATimeout_RaisesIt()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Assert.True(WithinSeconds(120, () => Engine().Query($@"
                socket_client_open('127.0.0.1':{port}, S0, [timeout(2)]),
                tls_client_context(Ctx, [verify(false)]),
                caught(tls_client_negotiate(Ctx, S0, _), timeout_error(tls)).")).Success);
        }
        finally { listener.Stop(); }
    }

    // ---------- as a server

    private (int Port, Task<int> Reply) TlsClientOf(int port, byte send)
        => (port, Task.Run(() =>
        {
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            using var tls = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
            tls.AuthenticateAsClient("localhost");
            tls.WriteByte(send);
            tls.Flush();
            return tls.ReadByte();
        }));

    private void ServeOnce(string contextOptions)
    {
        var e = Engine();
        var open = e.Query("socket_server_open('127.0.0.1':P, Srv).");
        long port = ((IntTerm)open["P"]!).Value;
        string srv = open["Srv"]!.ToString()!;
        var (_, reply) = TlsClientOf((int)port, 7);
        Assert.True(WithinSeconds(120, () => e.Query($@"
            tls_server_context(Ctx, {contextOptions}),
            socket_server_accept({Quoted(srv)}, _, S0, []),
            tls_server_negotiate(Ctx, S0, S),
            get_byte(S, B), B1 is B + 1, put_byte(S, B1), flush_output(S),
            close(S), socket_server_close({Quoted(srv)}).")).Success);
        Assert.Equal(8, Finished(reply));
    }

    private static string Quoted(string socketTerm)
        => "'$socket'(" + socketTerm.Substring(socketTerm.IndexOf('(') + 1);

    [Fact]
    public void AServer_TakesAPkcs12File()
        => ServeOnce($"[pkcs12_file('{PfxFile()}'), password('{Password}')]");

    [Fact]
    public void AServer_TakesPkcs12Contents()
    {
        var e = Engine();
        var open = e.Query("socket_server_open('127.0.0.1':P, Srv).");
        long port = ((IntTerm)open["P"]!).Value;
        string srv = Quoted(open["Srv"]!.ToString()!);
        var (_, reply) = TlsClientOf((int)port, 7);
        Assert.True(WithinSeconds(120, () => e.Query($@"
            bytes_of('{PfxFile()}', Bytes),
            tls_server_context(Ctx, [pkcs12(Bytes), password('{Password}')]),
            socket_server_accept({srv}, _, S0, []),
            tls_server_negotiate(Ctx, S0, S),
            get_byte(S, B), B1 is B + 1, put_byte(S, B1), flush_output(S),
            close(S), socket_server_close({srv}).")).Success);
        Assert.Equal(8, Finished(reply));
    }

#if !NETFRAMEWORK
    [Fact]
    public void AServer_TakesPemFiles()
    {
        File.WriteAllText(P("cert.pem"), Authority.Value.ExportCertificatePem());
        using RSA key = Authority.Value.GetRSAPrivateKey()!;
        byte[] exported = key.ExportPkcs8PrivateKey();
        File.WriteAllText(P("key.pem"), new string(PemEncoding.Write("PRIVATE KEY", exported)));
        ServeOnce($"[certificate_file('{P("cert.pem")}'), key_file('{P("key.pem")}')]");
    }
#else
    [Fact]
    public void PemFiles_AreAnError_OnFramework()
    {
        Assert.True(Engine().Query($@"
            caught(tls_server_context(_, [certificate_file('{P("cert.pem")}'), key_file('{P("key.pem")}')]), system_error(_)).").Success);
    }
#endif

    [Fact]
    public void AWrongPassword_IsAnError()
    {
        Assert.True(Engine().Query($@"
            caught(tls_server_context(_, [pkcs12_file('{PfxFile()}'), password(wrong)]), system_error(_)).").Success);
    }

    [Fact]
    public void AMissingFile_IsAnError()
    {
        Assert.True(Engine().Query($@"
            caught(tls_server_context(_, [pkcs12_file('{P("no.pfx")}')]), system_error(_)),
            caught(tls_client_context(_, [cacert_file('{P("no.cer")}')]), existence_error(source_sink, _)).").Success);
    }

    // ---------- bad arguments

    [Theory]
    [InlineData(@"caught(tls_client_context(_, [nosuch(1)]), domain_error(tls_option, nosuch(1)))")]
    [InlineData(@"caught(tls_client_context(_, [verify(maybe)]), domain_error(socket_option, verify(maybe)))")]
    [InlineData(@"caught(tls_client_context(_, [_]), instantiation_error)")]
    [InlineData(@"caught(tls_client_context(_, foo), type_error(list, foo))")]
    [InlineData(@"caught(tls_server_context(_, []), domain_error(tls_identity, []))")]
    [InlineData(@"caught(tls_server_context(_, [hostname(x)]), domain_error(tls_option, hostname(x)))")]
    [InlineData(@"caught(tls_client_negotiate(_, user_output, _), instantiation_error)")]
    [InlineData(@"caught(tls_client_negotiate(foo, user_output, _), domain_error(tls_context, foo))")]
    [InlineData(@"caught(tls_client_negotiate('$tls_context'(999), user_output, _), existence_error(tls_context, _))")]
    [InlineData(@"tls_client_context(C, [verify(false)]), caught(tls_server_negotiate(C, user_output, _), domain_error(tls_server_context, C))")]
    [InlineData(@"tls_client_context(C, [verify(false)]), caught(tls_client_negotiate(C, user_output, _), domain_error(socket_stream, user_output))")]
    public void BadArguments_AreErrors(string goal)
    {
        Assert.True(Engine().Query(goal + ".").Success, goal);
    }

    [Fact]
    public void ANegotiationOfAFile_IsAnError()
    {
        File.WriteAllText(P("f.txt"), "a");
        Assert.True(Engine().Query($@"
            open('{P("f.txt")}', read, F),
            tls_client_context(C, [verify(false)]),
            caught(tls_client_negotiate(C, F, _), domain_error(socket_stream, _)),
            close(F).").Success);
    }

    [Fact]
    public void AnError_NamesThePredicateOfTheLibrary()
    {
        Assert.True(Engine().Query(@"
            catch(tls_client_context(_, [nosuch(1)]), error(_, Context), true),
            Context == tls_client_context/2.").Success);
    }
}
