using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Shumway.Embedding.Debugging.Dap;

/// <summary>
/// ADR-036 — the Debug Adapter Protocol's wire framing: one JSON message behind an
/// ASCII <c>Content-Length</c> header, exactly as VS Code speaks it.
///
/// <para>Hand-rolled on <see cref="JsonDocument"/> / <see cref="Utf8JsonWriter"/> rather
/// than a serializer or Microsoft's DAP package: the protocol is ~20 small messages, the
/// engine's dependency policy is permissive-only, and the REPL publishes Native AOT —
/// DOM reads and direct writes are reflection-free by construction.</para>
/// </summary>
internal static class DapWire
{
    /// <summary>Reads one framed message. Null on a clean end of stream; throws on a
    /// torn one — the caller treats both as the client leaving.</summary>
    public static JsonDocument? ReadMessage(Stream stream)
    {
        byte[]? body = ReadMessageBytes(stream);
        return body is null ? null : JsonDocument.Parse(body);
    }

    /// <summary>The raw body of one framed message — what the ADR-036 proxy forwards
    /// verbatim, so a message crosses it byte-identical.</summary>
    public static byte[]? ReadMessageBytes(Stream stream)
    {
        int contentLength = -1;
        while (true)
        {
            string? line = ReadHeaderLine(stream);
            if (line is null) return null;                     // EOF between messages
            if (line.Length == 0) break;                       // blank line: body follows
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line.AsSpan("Content-Length:".Length).Trim(), out int n))
                contentLength = n;
        }
        if (contentLength < 0) return null;                    // header without a length

        byte[] body = new byte[contentLength];
        int read = 0;
        while (read < contentLength)
        {
            int n = stream.Read(body, read, contentLength - read);
            if (n <= 0) return null;                           // EOF mid-body
            read += n;
        }
        return body;
    }

    /// <summary>Frames and writes one message. The caller serializes writers — DAP
    /// events and responses interleave from different threads.</summary>
    public static void WriteMessage(Stream stream, byte[] jsonUtf8)
    {
        byte[] header = Encoding.ASCII.GetBytes(
            "Content-Length: " + jsonUtf8.Length + "\r\n\r\n");
        stream.Write(header, 0, header.Length);
        stream.Write(jsonUtf8, 0, jsonUtf8.Length);
        stream.Flush();
    }

    /// <summary>One CRLF-terminated ASCII header line, byte by byte — headers are tens of
    /// bytes and arrive once per message; buffering across them would just complicate the
    /// handoff to the exact-length body read.</summary>
    private static string? ReadHeaderLine(Stream stream)
    {
        var sb = new StringBuilder(32);
        while (true)
        {
            int b = stream.ReadByte();
            if (b < 0) return sb.Length == 0 ? null : sb.ToString();
            if (b == '\n')
            {
                if (sb.Length > 0 && sb[^1] == '\r') sb.Length--;
                return sb.ToString();
            }
            sb.Append((char)b);
        }
    }
}
