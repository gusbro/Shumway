using System.Collections.Generic;
using System.Text;

namespace Shumway.Repl;

/// <summary>A top level saved with <c>shumway --save-toplevel</c>: the
/// bundles it loads at every start, the goals it runs then, the goal that
/// takes the interactive loop's place, and whether it starts quiet. The
/// executable <c>--save-toplevel</c> builds constructs one and calls
/// <see cref="Run"/>.</summary>
public sealed class SavedTopLevel
{
    public SavedTopLevel(IReadOnlyList<byte[]> bundles, IReadOnlyList<string> goals,
        string? topLevelGoal, bool quiet)
    {
        Bundles = bundles;
        Goals = goals;
        TopLevelGoal = topLevelGoal;
        Quiet = quiet;
    }

    /// <summary>The <c>.shum</c> bundles, loaded in this order.</summary>
    public IReadOnlyList<byte[]> Bundles { get; }

    /// <summary>The <c>-g</c> goals given when saving: run once each, silently,
    /// after the bundles load; one that fails or raises ends the start with
    /// status 1 or 2.</summary>
    public IReadOnlyList<string> Goals { get; }

    /// <summary>The <c>-t</c> goal given when saving, run in the interactive
    /// loop's place; a <c>-t</c> on the command line takes precedence.</summary>
    public string? TopLevelGoal { get; }

    /// <summary>Whether the top level starts as <c>-q</c> does.</summary>
    public bool Quiet { get; }

    /// <summary>Runs the top level: the bundles, the saved goals, then the
    /// command line read as <c>shumway</c> reads it (files, <c>-g</c>,
    /// <c>-t</c>, <c>-q</c>, <c>--</c>), then the top-level goal or the
    /// interactive loop. Returns the process exit status.</summary>
    public static int Run(string[] args, SavedTopLevel saved) => ReplTopLevel.Launch(args, saved);

    internal static string ResourceName(int index) => $"toplevel_{index:D3}.shum";

    /// <summary>The Program.cs of the executable that runs a saved top level:
    /// it reads the embedded bundles back and hands them, with the goals, to
    /// <see cref="Run"/>.</summary>
    internal static string GenerateProgram(int bundleCount, IReadOnlyList<string> goals,
        string? topLevelGoal, bool quiet)
    {
        static string Literal(string s) => "@\"" + s.Replace("\"", "\"\"") + "\"";
        var goalList = new StringBuilder();
        foreach (string g in goals)
        {
            if (goalList.Length > 0) goalList.Append(", ");
            goalList.Append(Literal(g));
        }
        string topLevel = topLevelGoal is null ? "null" : Literal(topLevelGoal);
        return $@"using System.Collections.Generic;
using System.IO;
using System.Reflection;

internal static class Program
{{
    public static int Main(string[] args)
    {{
        var bundles = new List<byte[]>();
        for (int i = 0; i < {bundleCount}; i++)
            bundles.Add(Embedded(""toplevel_"" + i.ToString(""D3"") + "".shum""));
        var saved = new Shumway.Repl.SavedTopLevel(bundles,
            new string[] {{ {goalList} }}, {topLevel}, {(quiet ? "true" : "false")});
        return Shumway.Repl.SavedTopLevel.Run(args, saved);
    }}

    private static byte[] Embedded(string name)
    {{
        var asm = Assembly.GetExecutingAssembly();
        foreach (string n in asm.GetManifestResourceNames())
            if (n.EndsWith(""."" + name) || n == name)
            {{
                using var stream = asm.GetManifestResourceStream(n)!;
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                return ms.ToArray();
            }}
        throw new System.InvalidOperationException(""saved top level: resource "" + name + "" not found."");
    }}
}}
";
    }
}
