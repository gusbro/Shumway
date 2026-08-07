using System.IO.Compression;
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using System.Text.Json;

namespace Shumway.Web;

/// <summary>
/// Putting a program in a link. There is no server to store it on — the whole
/// app is static files — so the program travels IN the URL, compressed and
/// base64url-encoded in the fragment. The fragment never reaches a server,
/// which is the property that makes this acceptable: sharing a link does not
/// hand anyone's code to a third party.
///
/// <para>A link carries EITHER one file or a whole workspace, because a program
/// that spans files is not shareable one file at a time. The payload says which,
/// so the readable label the URL starts with (<c>#queens.pl~…</c>) is decoration
/// for the person reading the link — a hand-edited label cannot mislead the
/// loader.</para>
///
/// <para>Deflate rather than Brotli: browser-wasm has no Brotli codec (the same
/// limitation that shapes bundle loading). Deflate is present and enough — a
/// program is small, and it is the compression that keeps a page of Prolog
/// inside a practical URL length.</para>
/// </summary>
internal static partial class WebShumwayApp
{
    private const byte ShareFile = 1;
    private const byte ShareWorkspace = 2;

    /// <summary>Packs one file and a query.</summary>
    [JSExport]
    internal static Task<string> ShareEncodeFile(string name, string program, string query)
        => Task.FromResult(Pack(ShareFile, name, query,
                                new[] { (name, program ?? "") }));

    /// <summary>Packs the whole active workspace and a query.</summary>
    [JSExport]
    internal static Task<string> ShareEncodeWorkspace(string query)
        => OnEngine(() =>
        {
            EnsureWorkspace();
            var files = Directory.GetFiles(ActiveWorkspaceDir)
                .OrderBy(p => p, StringComparer.Ordinal)
                .Select(p => (Path.GetFileName(p), File.ReadAllText(p)))
                .ToArray();
            return Pack(ShareWorkspace, _activeWorkspace, query, files);
        });

    private static string Pack(
        byte kind, string label, string query, (string Name, string Text)[] files)
    {
        // Length-prefixed rather than delimited: a program may contain anything,
        // including whatever separator we might have picked.
        var payload = new MemoryStream();
        using (var w = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(kind);
            w.Write(label ?? "");
            w.Write(query ?? "");
            w.Write(files.Length);
            foreach (var (name, text) in files)
            {
                w.Write(name ?? "");
                w.Write(text ?? "");
            }
        }

        var compressed = new MemoryStream();
        using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, true))
            deflate.Write(payload.GetBuffer(), 0, (int)payload.Length);

        return ToBase64Url(compressed.ToArray());
    }

    /// <summary>Unpacks a share into JSON: <c>{kind, label, query, files:[{name,
    /// text}]}</c>. Returns null when the text is not a valid share — a
    /// hand-edited URL should land the user on an empty page, not an error.
    ///
    /// <para>Written with <see cref="Utf8JsonWriter"/> rather than serialized
    /// from an object: no reflection, so nothing here depends on what the
    /// trimmer decided to keep.</para></summary>
    [JSExport]
    internal static Task<string?> ShareDecode(string encoded)
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
            byte kind = r.ReadByte();
            if (kind is not (ShareFile or ShareWorkspace)) return Task.FromResult((string?)null);
            string label = r.ReadString();
            string query = r.ReadString();
            int count = r.ReadInt32();
            if (count < 0 || count > 4096) return Task.FromResult((string?)null);

            var json = new MemoryStream();
            using (var w = new Utf8JsonWriter(json))
            {
                w.WriteStartObject();
                w.WriteString("kind", kind == ShareFile ? "file" : "workspace");
                w.WriteString("label", label);
                w.WriteString("query", query);
                w.WriteStartArray("files");
                for (int i = 0; i < count; i++)
                {
                    w.WriteStartObject();
                    w.WriteString("name", r.ReadString());
                    w.WriteString("text", r.ReadString());
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            return Task.FromResult<string?>(Encoding.UTF8.GetString(json.ToArray()));
        }
        catch
        {
            return Task.FromResult((string?)null);
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
}
