using System;
using System.IO;
using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

/// <summary>
/// ADR-035 — a file is a FILE, not the string somebody happened to name it with.
///
/// <para>The engine learns a file's name from a command line and the debugger learns it from
/// an editor, and they never agree: <c>c:\temp\Blint.pl</c> against <c>C:\temp\Blint.pl</c>,
/// a relative path against the absolute one Visual Studio always uses. When those interned as
/// two different files, a breakpoint bound against the one with no code in it — and it did
/// not fail, it simply never hit. The program ran clean through every breakpoint in it and
/// the debugger looked broken.</para>
/// </summary>
public class DebugSiteTableTests
{
    [Fact]
    public void TheSameFileNamedTwoWays_IsOneFile()
    {
        string full = Path.Combine(Path.GetTempPath(), "shumway-sites-test.pl");
        int id = DebugSiteTable.InternFile(full);

        // The relative path a consult may be given, against the absolute one an IDE always
        // uses. Same file.
        string relative = Path.Combine(
            Path.GetTempPath(), "sub", "..", "shumway-sites-test.pl");
        Assert.Equal(id, DebugSiteTable.InternFile(relative));

        if (OperatingSystem.IsWindows())
        {
            // And the drive letter's case, which is how the user's Blint breakpoints were
            // lost: `shumway --debug c:\temp\Blint.pl` in the console, `C:\temp\Blint.pl` in
            // the editor.
            Assert.Equal(id, DebugSiteTable.InternFile(full.ToUpperInvariant()));
            Assert.Equal(id, DebugSiteTable.InternFile(full.ToLowerInvariant()));
        }

        // And the name it gives back is the one spelling, whichever way it was asked.
        Assert.Equal(full, DebugSiteTable.FileName(id), ignoreCase: OperatingSystem.IsWindows());
    }

    [Fact]
    public void ASyntheticNameIsNotAPath_AndIsLeftAlone()
    {
        // ConsultString has no file. `<string>` must survive the trip unchanged — it is what
        // every in-memory program's sites are keyed by, and Path.GetFullPath would turn it
        // into a name under the current directory.
        int id = DebugSiteTable.InternFile("<string>");
        Assert.Equal("<string>", DebugSiteTable.FileName(id));
        Assert.Equal(id, DebugSiteTable.InternFile("<string>"));
    }
}
