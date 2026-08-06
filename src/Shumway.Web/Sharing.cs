using System.IO.Compression;
using System.Runtime.InteropServices.JavaScript;
using System.Text;

namespace Shumway.Web;

/// <summary>
/// Putting a program in a link. There is no server to store it on — the whole
/// app is static files — so the program travels IN the URL, compressed and
/// base64url-encoded in the fragment. The fragment never reaches a server,
/// which is the property that makes this acceptable: sharing a link does not
/// hand anyone's code to a third party.
///
/// <para>Deflate rather than Brotli: browser-wasm has no Brotli codec (the same
/// limitation that shapes bundle loading). Deflate is present and enough — a
/// program is small, and it is the compression that keeps a page of Prolog
/// inside a practical URL length.</para>
/// </summary>
internal static partial class WebShumwayApp
{
    /// <summary>Packs a program and a query into a fragment-safe string.</summary>
    [JSExport]
    internal static string ShareEncode(string program, string query)
    {
        // Length-prefixed rather than delimited: a program may contain anything,
        // including whatever separator we might have picked.
        var payload = new MemoryStream();
        using (var w = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(program ?? "");
            w.Write(query ?? "");
        }

        var compressed = new MemoryStream();
        using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, true))
            deflate.Write(payload.GetBuffer(), 0, (int)payload.Length);

        return ToBase64Url(compressed.ToArray());
    }

    /// <summary>Unpacks what <see cref="ShareEncode"/> produced: the program and
    /// the query, newline-separated after a first line holding the program's
    /// length in characters. Returns null when the text is not a valid share —
    /// a hand-edited URL should land the user on an empty page, not an error.</summary>
    [JSExport]
    internal static string? ShareDecode(string encoded)
    {
        try
        {
            byte[] compressed = FromBase64Url(encoded);
            var payload = new MemoryStream();
            using (var deflate = new DeflateStream(
                       new MemoryStream(compressed), CompressionMode.Decompress))
                deflate.CopyTo(payload);
            payload.Position = 0;

            using var r = new BinaryReader(payload, Encoding.UTF8);
            string program = r.ReadString();
            string query = r.ReadString();
            return program.Length + "\n" + program + query;
        }
        catch
        {
            return null;
        }
    }

    // base64url: '+' and '/' are not safe in a fragment, and '=' padding is
    // noise in a URL.
    private static string ToBase64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        string b64 = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b64.PadRight((b64.Length + 3) / 4 * 4, '='));
    }

    /// <summary>Loads the CLP(FD) library, so a program can post constraints.
    /// Opt-in because it is a library, not part of the core engine.</summary>
    [JSExport]
    internal static string? UseClpfd()
    {
        try { _session!.Engine.UseClpfd(); return null; }
        catch (Exception ex) { return Describe(ex); }
    }
}
