using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Embedding;

public static partial class MetaBuiltins
{
    // One client for the process: HttpClient is designed to be shared, and a
    // per-call instance leaks sockets under churn.
    private static System.Net.Http.HttpClient? _httpClient;
    private static System.Net.Http.HttpClient HttpClientInstance
        => _httpClient ??= new System.Net.Http.HttpClient();

    /// <summary><c>http_download(+URL, +File)</c> — downloads URL's RAW BYTES
    /// to File. Byte-fidelity is the contract: no text decoding happens, so a
    /// page in any charset round-trips exactly. Synchronous (the engine's
    /// builtins run within the interpreter thread); a network/HTTP failure is
    /// a catchable <c>existence_error(url, URL)</c>.</summary>
    public static bool HttpDownload(Activation engine)
    {
        if (!TryGetStringArg(engine, 0, out string url))
            throw new ShumwayPrologException(IsoError.InstantiationError());
        if (!TryGetStringArg(engine, 1, out string file))
            throw new ShumwayPrologException(IsoError.InstantiationError());
        try
        {
            byte[] data = HttpClientInstance.GetByteArrayAsync(url)
                .GetAwaiter().GetResult();
            System.IO.File.WriteAllBytes(file, data);
        }
        catch (System.Exception ex) when (ex is System.Net.Http.HttpRequestException
            or System.Threading.Tasks.TaskCanceledException
            or System.UriFormatException
            or System.InvalidOperationException
            or System.IO.IOException
            or System.UnauthorizedAccessException
            or System.AggregateException)
        {
            throw new ShumwayPrologException(
                IsoError.ExistenceError("url", new AtomTerm(url)));
        }
        return true;
    }
}
