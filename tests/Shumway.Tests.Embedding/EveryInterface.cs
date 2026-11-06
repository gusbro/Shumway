using System;
using System.Runtime.InteropServices;

namespace Shumway.Tests.Embedding;

/// <summary>Listening on every interface makes a desktop Windows ask its user about the
/// firewall, once for each test host. Such a test runs where nobody gets asked. CI sets
/// the variable, and its Windows machines have nobody to ask.</summary>
internal static class EveryInterface
{
    public static bool MayListen =>
        !RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI"));
}
