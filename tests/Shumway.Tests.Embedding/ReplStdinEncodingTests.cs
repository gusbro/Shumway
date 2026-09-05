using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace Shumway.Tests.Embedding;

/// <summary>What arrives on a redirected standard input is bytes, and by
/// convention those are UTF-8: it is what every peer engine emits and what a
/// consulted file already is. Read through the console's codepage instead, a
/// piped <c>X = "ä"</c> arrived as two characters, so a script fed to the top
/// level through a pipe read differently from the same text in a file.
///
/// <para>A terminal is left alone, since the encoding there has to match what
/// it actually sends, which is why the tests here pipe.</para></summary>
public sealed class ReplStdinEncodingTests
{
    private static string ReplExe
    {
        get
        {
            string suffix = OperatingSystem.IsWindows() ? ".exe" : "";
            string root = RepoRoot();
            foreach (string cfg in new[] { "Release", "Debug" })
            {
                string path = Path.Combine(root, "src", "Shumway.Repl", "bin", cfg,
                                           "net10.0", "shumway" + suffix);
                if (File.Exists(path)) return path;
            }
            throw new InvalidOperationException("shumway was not built.");
        }
    }

    private static string RepoRoot()
    {
        string current = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && current is not null; i++)
        {
            if (File.Exists(Path.Combine(current, "Shumway.slnx"))) return current;
            current = Path.GetDirectoryName(current)!;
        }
        throw new InvalidOperationException("Could not locate repository root.");
    }

    /// <summary>Feeds the top level UTF-8 BYTES and returns what it printed.
    /// Writing a C# string through the default encoder would prove nothing:
    /// the point is what the bytes on the pipe mean.</summary>
    private static string Piped(string queries)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ReplExe,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        using var proc = Process.Start(psi)!;
        byte[] utf8 = new UTF8Encoding(false).GetBytes(queries);
        proc.StandardInput.BaseStream.Write(utf8, 0, utf8.Length);
        proc.StandardInput.BaseStream.Flush();
        proc.StandardInput.Close();
        var outTask = proc.StandardOutput.ReadToEndAsync();
        if (!proc.WaitForExit(60_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* gone */ }
            Assert.Fail("the top level did not end after its input closed.");
        }
        return outTask.GetAwaiter().GetResult();
    }

    [Fact]
    public void APipedCharacterIsOneCharacter()
    {
        // Two bytes, one character. The old reading gave two.
        Assert.Contains("largo(1)", Piped("X = \"ä\", length(X, L), write(largo(L)), nl.\n"));
    }

    [Fact]
    public void APipedCharacterHasItsOwnCode()
    {
        Assert.Contains("[228]",
            Piped("atom_codes('ä', Cs), write(Cs), nl.\n"));
    }

    [Fact]
    public void APipedAstralCharacterIsOneCharacterToo()
    {
        // Four bytes, one character, above the basic plane.
        string output = Piped("atom_length('😀', L), atom_codes('😀', Cs), "
                              + "write(L-Cs), nl.\n");
        Assert.Contains("1-[128512]", output.Replace(" ", ""));
    }

    [Fact]
    public void AByteOrderMarkIsNotPartOfTheProgram()
    {
        // A file written by a Windows editor starts with one, and piping that
        // file must not make the first query a syntax error.
        Assert.Contains("[228]",
            Piped("﻿atom_codes('ä', Cs), write(Cs), nl.\n"));
    }

    [Fact]
    public void PlainTextIsUnaffected()
    {
        Assert.Contains("[97,98,99]",
            Piped("atom_codes(abc, Cs), write(Cs), nl.\n").Replace(" ", ""));
    }
}
