using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Shumway.WebKitProbe;

/// <summary>Answers a call from the page, and does nothing else.</summary>
[SupportedOSPlatform("browser")]
public static partial class ThreadedPingApp
{
    public static void Main() { }

    [JSExport]
    internal static Task<string> Ping() => Task.FromResult("");
}
