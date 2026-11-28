using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Shumway.Tests.Core;

/// <summary>
/// ADR-073 D16: a clone builds and tests Shumway with the .NET SDK and
/// nothing else. The notebook site is built with Node and Python, apart from
/// the solution, in <c>jupyterlite/</c>. The CI runners have both installed,
/// so a project that came to need them would pass every lane: this reads the
/// build files instead.
/// </summary>
public class BuildNeedsOnlyDotNetTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Shumway.slnx")))
            dir = dir.Parent;
        if (dir is null)
            throw new InvalidOperationException("Could not locate the repository root (Shumway.slnx).");
        return dir.FullName;
    }

    private static readonly string[] Skipped = ["bin", "obj", "node_modules", ".git", "dist", "jupyterlite"];

    /// <summary>The project, props and targets files of the repository,
    /// the notebook site's own directory left out.</summary>
    private static IEnumerable<string> BuildFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            foreach (string sub in Directory.EnumerateDirectories(directory))
                if (!Skipped.Contains(Path.GetFileName(sub))) pending.Push(sub);
            foreach (string file in Directory.EnumerateFiles(directory))
                if (Path.GetExtension(file) is ".csproj" or ".props" or ".targets" or ".slnx") yield return file;
        }
    }

    // A tool of the notebook site's build, where a command names a program:
    // at its start, after what joins two commands, or at the end of a path.
    private static readonly Regex OutsideTool = new(
        @"(^|[\s""'&|;(\\/])(node|npm|npx|yarn|jlpm|python3?|py|pip3?|jupyter)(\.exe|\.cmd)?(?=[\s""']|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>What in a build file breaks the rule; empty when nothing
    /// does. <paramref name="commands"/> counts the commands it looked at.</summary>
    internal static List<string> Offences(string xml, out int commands)
    {
        var found = new List<string>();
        commands = 0;
        if (xml.IndexOf("jupyterlite", StringComparison.OrdinalIgnoreCase) >= 0)
            found.Add("refers to jupyterlite");
        foreach (XElement exec in XDocument.Parse(xml).Descendants().Where(e => e.Name.LocalName == "Exec"))
        {
            string command = (string?)exec.Attribute("Command") ?? "";
            commands++;
            Match tool = OutsideTool.Match(command);
            if (tool.Success) found.Add($"runs {tool.Groups[2].Value}: {command}");
        }
        return found;
    }

    [Fact]
    public void NoBuildFileRefersToTheNotebookSite_OrRunsNodeOrPython()
    {
        string root = RepoRoot();
        var offences = new List<string>();
        int files = 0, commands = 0;
        foreach (string file in BuildFiles(root))
        {
            files++;
            List<string> inFile = Offences(File.ReadAllText(file), out int here);
            foreach (string offence in inFile)
                offences.Add($"{file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar)} {offence}");
            commands += here;
        }

        // Not vacuous: the solution's projects were read, and the commands
        // the build does run (the library bake) were looked at.
        Assert.True(files >= 25, $"only {files} build files were found under {root}");
        Assert.True(commands >= 4, $"only {commands} build commands were looked at");
        Assert.True(offences.Count == 0, string.Join("\n", offences));
    }

    [Theory]
    [InlineData(@"<Project><Target Name=""T""><Exec Command=""npm ci"" /></Target></Project>", "runs npm")]
    [InlineData(@"<Project><Target Name=""T""><Exec Command=""cd x &amp;&amp; python -m jupyter lite build"" /></Target></Project>", "runs python")]
    [InlineData(@"<Project><Target Name=""T""><Exec Command=""&quot;C:\tools\node.exe&quot; build.mjs"" /></Target></Project>", "runs node")]
    [InlineData(@"<Project><ItemGroup><Content Include=""..\..\JupyterLite\extension\lib\**"" /></ItemGroup></Project>", "refers to jupyterlite")]
    public void WhatBreaksTheRule_IsFound(string xml, string expected)
    {
        Assert.Contains(Offences(xml, out _), offence => offence.StartsWith(expected, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(@"<Project><Target Name=""T""><Exec Command=""dotnet &quot;$(LibraryBakeDll)&quot; --prelude --il -o x.shum"" /></Target></Project>")]
    [InlineData(@"<Project><!-- one node of the tree, in Python's way --><PropertyGroup><NodeReuse>false</NodeReuse></PropertyGroup></Project>")]
    public void WhatTheBuildDoes_IsNotTakenForIt(string xml)
    {
        Assert.Empty(Offences(xml, out _));
    }
}
