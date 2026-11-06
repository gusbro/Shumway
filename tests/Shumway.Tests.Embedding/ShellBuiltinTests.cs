using System.Diagnostics;
using System.Runtime.InteropServices;
using Shumway.Compiler.Ast;
using Shumway.Embedding;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>What <c>shell/1,2</c> hands to the platform's shell.</summary>
public sealed class ShellBuiltinTests
{
    private static bool Windows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    [Theory]
    [InlineData(@"test ""a\""b"" = 'a""b'", 0)]
    [InlineData(@"test ""a\""b"" = 'a""c'", 1)]
    [InlineData(@"test 'a\b' = 'a\b'", 0)]
    [InlineData(@"test 'a\\b' = 'a\b'", 1)]
    [InlineData(@"test 'a\' = 'a\'", 0)]
    [InlineData(@"X=""a b"" && test ""x$X\""y"" = 'xa b""y'", 0)]
    public void OnUnix_TheCommandReachesTheShellAsItIs(string command, int status)
    {
        if (Windows) return;
        string atom = command.Replace(@"\", @"\\").Replace("'", @"\'");
        var sol = new PrologEngine().Query($"shell('{atom}', S).");
        Assert.Equal(status, ((IntTerm)sol["S"]!).Value);
    }

#if !NETFRAMEWORK
    [Fact]
    public void OnUnix_TheCommandIsOneArgument()
    {
        const string command = @"test ""a\""b"" = x";
        ProcessStartInfo psi = MetaBuiltins.ShellStartInfo(command, windows: false);
        Assert.Equal("/bin/sh", psi.FileName);
        Assert.Equal(new[] { "-c", command }, psi.ArgumentList);
        Assert.Empty(psi.Arguments);
    }
#endif

    [Fact]
    public void OnWindows_TheShellParsesTheCommand()
    {
        ProcessStartInfo psi = MetaBuiltins.ShellStartInfo("echo a", windows: true);
        Assert.Equal("cmd.exe", psi.FileName);
        Assert.Equal("/C echo a", psi.Arguments);
    }
}
