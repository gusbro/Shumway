using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>
/// The share-link encoding used by WebShumway. The web app is static files with
/// no server, so a shared program travels inside the URL fragment; these pin the
/// round trip and the two properties that matter for a link — it survives being
/// pasted anywhere, and a mangled one is rejected rather than half-decoded.
///
/// <para>The encoding is exercised here rather than through the browser because
/// it is ordinary .NET: the same Deflate the app runs. (Deflate, not Brotli —
/// browser-wasm has no Brotli codec.)</para>
/// </summary>
public class ShareEncodingTests
{
    // Mirrors WebShumwayApp.ShareEncode/ShareDecode, which live in the browser
    // app and cannot be referenced from here — the assertions below are about
    // the FORMAT, so a change to it that broke sharing would break these too.
    private static string Encode(string program, string query)
    {
        var payload = new MemoryStream();
        using (var w = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(program);
            w.Write(query);
        }
        var compressed = new MemoryStream();
        using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, true))
            deflate.Write(payload.GetBuffer(), 0, (int)payload.Length);
        return Convert.ToBase64String(compressed.ToArray())
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static (string Program, string Query)? Decode(string encoded)
    {
        try
        {
            string b64 = encoded.Replace('-', '+').Replace('_', '/');
            byte[] bytes = Convert.FromBase64String(
                b64.PadRight((b64.Length + 3) / 4 * 4, '='));
            var payload = new MemoryStream();
            using (var deflate = new DeflateStream(new MemoryStream(bytes), CompressionMode.Decompress))
                deflate.CopyTo(payload);
            payload.Position = 0;
            using var r = new BinaryReader(payload, Encoding.UTF8);
            return (r.ReadString(), r.ReadString());
        }
        catch { return null; }
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("p.", "p.")]
    [InlineData("anc(X,Y) :- par(X,Y).\npar(a,b).", "anc(a,X).")]
    [InlineData("% acentos y ñ, 日本語, emoji 🙂", "X = 'ñ'.")]
    public void RoundTrips(string program, string query)
    {
        var got = Decode(Encode(program, query));
        Assert.NotNull(got);
        Assert.Equal(program, got!.Value.Program);
        Assert.Equal(query, got.Value.Query);
    }

    [Fact]
    public void ASeparatorInTheProgramDoesNotConfuseTheDecoder()
    {
        // Length-prefixed rather than delimited, precisely so a program may
        // contain anything — including whatever delimiter we might have chosen.
        const string program = "p :- write('\\n---\\n|||'), nl.";
        var got = Decode(Encode(program, "p."));
        Assert.Equal(program, got!.Value.Program);
    }

    [Fact]
    public void TheEncodingIsUrlSafe()
    {
        string encoded = Encode(new string('x', 500) + "ñ日", "q.");
        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
        Assert.DoesNotContain('=', encoded);
        Assert.Equal(Uri.EscapeDataString(encoded), encoded);   // survives a paste
    }

    [Fact]
    public void CompressionEarnsItsPlace()
    {
        // A page of Prolog has to fit in a practical URL. Repetitive source is
        // exactly what a program is.
        string program = string.Concat(Enumerable.Repeat("fact(a, b, c).\n", 200));
        Assert.True(Encode(program, "fact(X,Y,Z).").Length < program.Length / 8,
            "expected the encoded form to be a fraction of the source");
    }

    [Theory]
    [InlineData("not base64 at all !!")]
    [InlineData("AAAA")]              // valid base64, not a deflate stream
    [InlineData("")]
    public void AMangledLinkIsRejectedRatherThanHalfDecoded(string encoded)
        => Assert.Null(Decode(encoded));
}
