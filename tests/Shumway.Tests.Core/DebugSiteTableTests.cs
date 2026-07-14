using System;
using System.IO;
using Shumway.Core;
using Xunit;

namespace Shumway.Tests.Core;

/// <summary>
/// ADR-035 — a source file is identified by its NAME, not by the path somebody reached it
/// through, and not by its case.
///
/// <para>That is the identity Shumway already uses everywhere else: a source file IS a
/// module, and a module takes its name from the file's name with the directory dropped. The
/// debug site table now agrees with the rest of the engine.</para>
///
/// <para>Keying by the string as given was a real bug, and a silent one. The engine was
/// started with <c>shumway --debug c:\temp\Blint.pl</c>; the editor opened
/// <c>C:\temp\Blint.pl</c>; those were two different files here, so every breakpoint bound
/// against the one with no code in it and NEVER HIT. The program ran clean through them and
/// the debugger looked broken.</para>
/// </summary>
public class DebugSiteTableTests
{
    [Fact]
    public void AFileIsIdentifiedByItsName_NotByThePathItWasReachedThrough()
    {
        int id = DebugSiteTable.InternFile(@"C:\temp\shumway-blint-test.pl");

        // The drive letter's case, which is how the user's breakpoints were lost.
        Assert.Equal(id, DebugSiteTable.InternFile(@"c:\temp\shumway-blint-test.pl"));

        // The relative name a consult may be given, against the absolute one an IDE always
        // uses. And the same file reached through another route entirely — a mapped drive, a
        // share, a copy in a build directory. Canonicalising the path would have fixed the
        // first spelling and left all of these.
        Assert.Equal(id, DebugSiteTable.InternFile("shumway-blint-test.pl"));
        Assert.Equal(id, DebugSiteTable.InternFile(@"\\build\out\SHUMWAY-BLINT-TEST.PL"));

        // Different names are still different files.
        Assert.NotEqual(id, DebugSiteTable.InternFile(@"C:\temp\shumway-other-test.pl"));
    }

    [Fact]
    public void TheNameItGivesBack_IsOneYouCanOpen()
    {
        // The key identifies the file; the name NAVIGATES to it. A debugger handed
        // "blint.pl" cannot open anything, so the fullest name anyone has offered wins —
        // whichever side offered it first.
        DebugSiteTable.InternFile("shumway-nav-test.pl");
        int id = DebugSiteTable.InternFile(@"C:\temp\shumway-nav-test.pl");
        Assert.Equal(@"C:\temp\shumway-nav-test.pl", DebugSiteTable.FileName(id));

        // And a barer name later does not take that away.
        DebugSiteTable.InternFile("shumway-nav-test.pl");
        Assert.Equal(@"C:\temp\shumway-nav-test.pl", DebugSiteTable.FileName(id));
    }

    [Fact]
    public void ASyntheticNameIsNotAPath_AndIsLeftAlone()
    {
        // ConsultString has no file. `<string>` is what every in-memory program's sites are
        // keyed by, and it must survive the trip unchanged.
        int id = DebugSiteTable.InternFile("<string>");
        Assert.Equal("<string>", DebugSiteTable.FileName(id));
        Assert.Equal(id, DebugSiteTable.InternFile("<string>"));
    }
}
