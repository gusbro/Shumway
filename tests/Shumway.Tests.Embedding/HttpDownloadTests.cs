using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary><c>http_download/2</c> — the conformity toolchain's page fetcher.
/// Served from a loopback TcpListener (port 0 = ephemeral, no URL-ACL
/// friction), so the test is CI-safe and network-free.</summary>
public sealed class HttpDownloadTests
{
    [Fact]
    public async Task HttpDownload_WritesTheResponseBytesToTheFile()
    {
        // Body includes a non-ASCII byte to pin the RAW-BYTES contract (the
        // Neumerkel pages are ISO-8859-1; text decoding would corrupt them).
        byte[] body = Encoding.Latin1.GetBytes("<html>café & <tr>!</html>");
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(() =>
        {
            using TcpClient client = listener.AcceptTcpClient();
            using NetworkStream s = client.GetStream();
            // Drain the request head (ends at the blank line).
            var buf = new byte[4096];
            int seen = 0, n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0)
            {
                seen += n;
                string head = Encoding.ASCII.GetString(buf, 0, n);
                if (head.Contains("\r\n\r\n") || seen > buf.Length) break;
            }
            string header = "HTTP/1.1 200 OK\r\nContent-Length: " + body.Length
                + "\r\nConnection: close\r\n\r\n";
            byte[] hb = Encoding.ASCII.GetBytes(header);
            s.Write(hb, 0, hb.Length);
            s.Write(body, 0, body.Length);
        });

        string file = Path.Combine(Path.GetTempPath(),
            "shumway_httpdl_" + Guid.NewGuid().ToString("N") + ".html");
        try
        {
            var e = new PrologEngine();
            string fileAtom = file.Replace("\\", "\\\\");
            Assert.True(e.Query(
                $"http_download('http://127.0.0.1:{port}/page', '{fileAtom}').").Success);
            await server.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(body, File.ReadAllBytes(file));
        }
        finally
        {
            listener.Stop();
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public void HttpDownload_Failure_IsACatchableExistenceError()
    {
        // A loopback port with no listener refuses instantly — no timeout.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int deadPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var e = new PrologEngine();
        var sol = e.Query(
            $"catch(http_download('http://127.0.0.1:{deadPort}/x', 'nowhere.tmp'), "
            + "error(existence_error(url, U), _), true), atom(U).");
        Assert.True(sol.Success);
    }
}
