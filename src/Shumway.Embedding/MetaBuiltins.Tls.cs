using System;
using System.IO;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Shumway.Builtins;
using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Embedding;

/// <summary>The internals of <c>library(tls)</c>. A negotiation turns an open stream into
/// an encrypted one, so a protocol can start in the clear and upgrade.</summary>
public static partial class MetaBuiltins
{
    private const string TlsContextFunctor = "$tls_context";

    internal sealed class TlsClientContext
    {
        public string? Hostname;
        public bool Verify = true;
        public string? CaCertificateFile;
        public X509Certificate2? Identity;
    }

    internal sealed class TlsServerContext
    {
        public X509Certificate2 Identity = null!;
    }

    private static string TextOption(Term value, Term option)
    {
        if (value is VarTerm) throw new ShumwayPrologException(IsoError.InstantiationError());
        return value.TryAsText(out string text) ? text
            : throw new ShumwayPrologException(IsoError.DomainError("tls_option", option));
    }

    private static byte[] BytesOption(Term value, Term option)
    {
        if (value is VarTerm) throw new ShumwayPrologException(IsoError.InstantiationError());
        if (!value.TryAsText(out string text))
            throw new ShumwayPrologException(IsoError.DomainError("tls_option", option));
        var bytes = new byte[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] > 255) throw new ShumwayPrologException(IsoError.DomainError("tls_option", option));
            bytes[i] = (byte)text[i];
        }
        return bytes;
    }

    private static X509Certificate2 LoadPkcs12(byte[] bytes, string? password)
    {
        try
        {
#if NET9_0_OR_GREATER
            return X509CertificateLoader.LoadPkcs12(bytes, password);
#else
            return new X509Certificate2(bytes, password);
#endif
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            throw new PrologRuntimeException("system_error", ex.Message);
        }
    }

    private static X509Certificate2 LoadPem(string certificateFile, string keyFile, string? password)
    {
#if NETFRAMEWORK
        throw new PrologRuntimeException("system_error", "PEM files need .NET 5 or later: use pkcs12_file/1");
#else
        try
        {
            using X509Certificate2 pem = password is null
                ? X509Certificate2.CreateFromPemFile(certificateFile, keyFile)
                : X509Certificate2.CreateFromEncryptedPemFile(certificateFile, password, keyFile);
            // SslStream on Windows needs a key it can reach, which a PEM load does not leave.
            return LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Security.Cryptography.CryptographicException)
        {
            throw new PrologRuntimeException("system_error", ex.Message);
        }
#endif
    }

    private static byte[] ReadFileOption(string path)
    {
        try { return File.ReadAllBytes(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PrologRuntimeException("system_error", ex.Message);
        }
    }

    /// <summary>A certificate and its key, from the options that name them.</summary>
    private sealed class IdentityOptions
    {
        public byte[]? Pkcs12;
        public string? CertificateFile;
        public string? KeyFile;
        public string? Password;

        public bool Take(CompoundTerm option, Term value)
        {
            switch (option.Functor)
            {
                case "pkcs12": Pkcs12 = BytesOption(value, option); return true;
                case "pkcs12_file": Pkcs12 = ReadFileOption(TextOption(value, option)); return true;
                case "certificate_file": CertificateFile = TextOption(value, option); return true;
                case "key_file": KeyFile = TextOption(value, option); return true;
                case "password": Password = TextOption(value, option); return true;
                default: return false;
            }
        }

        public X509Certificate2? Load()
        {
            if (Pkcs12 is not null) return LoadPkcs12(Pkcs12, Password);
            if (CertificateFile is not null || KeyFile is not null)
                return LoadPem(CertificateFile ?? KeyFile!, KeyFile ?? CertificateFile!, Password);
            return null;
        }
    }

    private static bool UnifyTlsContext(Activation engine, object context)
    {
        int id = RequireHost(engine, "library(tls)").AddNetObject(context);
        return engine.UnifyRegisterWithCell(0, Materializer.MaterializeAsCell(engine,
            new CompoundTerm(TlsContextFunctor, new Term[] { new IntTerm(id) })));
    }

    private static bool NewTlsClientContext(Activation engine)
    {
        var context = new TlsClientContext();
        var identity = new IdentityOptions();
        Term list = MaterializeRegister(engine, 1);
        foreach (Term option in ListItems(list, list))
        {
            if (option is VarTerm) throw new ShumwayPrologException(IsoError.InstantiationError());
            if (option is not CompoundTerm { Args: [var v] } c)
                throw new ShumwayPrologException(IsoError.DomainError("tls_option", option));
            if (identity.Take(c, v)) continue;
            switch (c.Functor)
            {
                case "hostname": context.Hostname = TextOption(v, option); break;
                case "verify": context.Verify = BoolOption(v, option); break;
                case "cacert_file": context.CaCertificateFile = TextOption(v, option); break;
                default: throw new ShumwayPrologException(IsoError.DomainError("tls_option", option));
            }
        }
        context.Identity = identity.Load();
        if (context.CaCertificateFile is not null && !File.Exists(context.CaCertificateFile))
            throw new ShumwayPrologException(IsoError.ExistenceError("source_sink", new AtomTerm(context.CaCertificateFile)));
        return UnifyTlsContext(engine, context);
    }

    /// <summary><c>'$tls_client_context'(-Context, +Options)</c></summary>
    public static bool TlsClientContextOpen(Activation engine) => NetCall(engine, NewTlsClientContext);

    private static bool NewTlsServerContext(Activation engine)
    {
        var identity = new IdentityOptions();
        Term list = MaterializeRegister(engine, 1);
        foreach (Term option in ListItems(list, list))
        {
            if (option is VarTerm) throw new ShumwayPrologException(IsoError.InstantiationError());
            if (option is not CompoundTerm { Args: [var v] } c || !identity.Take(c, v))
                throw new ShumwayPrologException(IsoError.DomainError("tls_option", option));
        }
        X509Certificate2 certificate = identity.Load()
            ?? throw new ShumwayPrologException(IsoError.DomainError("tls_identity", list));
        return UnifyTlsContext(engine, new TlsServerContext { Identity = certificate });
    }

    /// <summary><c>'$tls_server_context'(-Context, +Options)</c></summary>
    public static bool TlsServerContextOpen(Activation engine) => NetCall(engine, NewTlsServerContext);

    private static object RequireTlsContext(Activation engine, Term term)
    {
        if (term is VarTerm) throw new ShumwayPrologException(IsoError.InstantiationError());
        if (term is not CompoundTerm { Functor: TlsContextFunctor, Args: [IntTerm id] })
            throw new ShumwayPrologException(IsoError.DomainError("tls_context", term));
        return RequireHost(engine, "library(tls)").NetObject((int)id.Value)
            ?? throw new ShumwayPrologException(IsoError.ExistenceError("tls_context", term));
    }

    private static bool Trusted(TlsClientContext context, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (!context.Verify || errors == SslPolicyErrors.None) return true;
        // Only an unknown authority can be cured by the authority the program named.
        if (context.CaCertificateFile is null || errors != SslPolicyErrors.RemoteCertificateChainErrors
            || certificate is null || chain is null)
            return false;
        byte[] raw = File.ReadAllBytes(context.CaCertificateFile);
#if NET9_0_OR_GREATER
        using X509Certificate2 authority = LoadAuthority(raw);
        using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Clear();
        chain.ChainPolicy.CustomTrustStore.Add(authority);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(leaf);
#else
        using var authority = new X509Certificate2(raw);
        using var leaf = new X509Certificate2(certificate);
        chain.ChainPolicy.ExtraStore.Add(authority);
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        if (!chain.Build(leaf)) return false;
        // AllowUnknownCertificateAuthority takes any root: this one has to be the one named.
        return chain.ChainElements[chain.ChainElements.Count - 1].Certificate.Thumbprint == authority.Thumbprint;
#endif
    }

#if NET9_0_OR_GREATER
    private static X509Certificate2 LoadAuthority(byte[] raw)
    {
        const string pemStart = "-----BEGIN CERTIFICATE-----";
        string text = System.Text.Encoding.ASCII.GetString(raw);
        return text.Contains(pemStart)
            ? X509Certificate2.CreateFromPem(text)
            : X509CertificateLoader.LoadCertificate(raw);
    }
#endif

    /// <summary>Replaces a stream with an encrypted one over the same connection. The old
    /// stream's alias, end of stream action and timeout carry over, and the old stream is
    /// gone, whether the negotiation works or not: a failed one leaves the connection in no
    /// state to use.</summary>
    private static bool Negotiate(Activation engine, Func<SslStream, Task> handshake, Func<Stream, SslStream> over)
    {
        StreamRegistry registry = engine.Streams!;
        StreamHandle old = StreamBuiltins.ResolveStream(engine, engine.GetRegister(1));
        Term oldTerm = MaterializeRegister(engine, 1);
        if (old.LiveConnection is null || !old.IsReader || !old.IsWriter)
            throw new ShumwayPrologException(IsoError.DomainError("socket_stream", oldTerm));
        // The peer's first bytes must reach the handshake, not a buffer here.
        if (old.HasBufferedInput || !old.LiveConnection.IsIdle)
            throw new ShumwayPrologException(IsoError.PermissionError("negotiate", "stream", oldTerm, engine));
        if (old.IsBinary) old.BinaryStream!.Flush(); else old.Writer!.Flush();

        Stream connection = old.LiveConnection.Detach();
        SslStream tls = over(connection);
        int timeout = old.LiveConnection.TimeoutMs;
        string? alias = old.Alias;
        registry.Remove(old);
        try { registry.Wait(handshake(tls), timeout, "tls"); }
        catch
        {
            tls.Dispose();
            throw;
        }
        StreamHandle handle = StreamBuiltins.OpenLiveStream(registry, tls, "readwrite", old.IsBinary,
            old.NamedEncoding, alias, old.EofAction, translateNewlines: false);
        handle.Socket = old.Socket;
        handle.LiveConnection!.TimeoutMs = timeout;
        return engine.UnifyRegisterWithCell(2, StreamBuiltins.MakeStreamTerm(engine, handle));
    }

    private static bool NegotiateAsClient(Activation engine)
    {
        Term contextTerm = MaterializeRegister(engine, 0);
        if (RequireTlsContext(engine, contextTerm) is not TlsClientContext context)
            throw new ShumwayPrologException(IsoError.DomainError("tls_client_context", contextTerm));
        StreamHandle old = StreamBuiltins.ResolveStream(engine, engine.GetRegister(1));
        string host = context.Hostname ?? old.PeerHost ?? "";
        return Negotiate(engine,
            tls =>
            {
                var identities = new X509CertificateCollection();
                if (context.Identity is not null) identities.Add(context.Identity);
                return tls.AuthenticateAsClientAsync(host, identities, SslProtocols.None, checkCertificateRevocation: false);
            },
            connection => new SslStream(connection, leaveInnerStreamOpen: false,
                (_, certificate, chain, errors) => Trusted(context, certificate, chain, errors)));
    }

    /// <summary><c>'$tls_client_negotiate'(+Context, +Stream0, -Stream)</c></summary>
    public static bool TlsClientNegotiate(Activation engine) => NetCall(engine, NegotiateAsClient);

    private static bool NegotiateAsServer(Activation engine)
    {
        Term contextTerm = MaterializeRegister(engine, 0);
        if (RequireTlsContext(engine, contextTerm) is not TlsServerContext context)
            throw new ShumwayPrologException(IsoError.DomainError("tls_server_context", contextTerm));
        return Negotiate(engine,
            tls => tls.AuthenticateAsServerAsync(context.Identity, clientCertificateRequired: false,
                SslProtocols.None, checkCertificateRevocation: false),
            connection => new SslStream(connection, leaveInnerStreamOpen: false));
    }

    /// <summary><c>'$tls_server_negotiate'(+Context, +Stream0, -Stream)</c></summary>
    public static bool TlsServerNegotiate(Activation engine) => NetCall(engine, NegotiateAsServer);
}
