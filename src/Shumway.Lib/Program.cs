using System.Text;
using Shumway.Embedding;

namespace Shumway.Lib;

/// <summary>
/// <c>shumway-lib</c> CLI — the Shumway librarian. Assembles a runnable
/// <c>.shum</c> bundle out of chosen <c>.shmo</c> compiled-object files
/// (produced by <c>shumway-compile</c>) and performs the usual archive
/// operations on it, <em>without</em> the linker's reachability analysis or
/// dead-module pruning. Every object you put in is kept verbatim, so the
/// archive can be listed, and its objects extracted byte-for-byte.
///
/// <para>Use this when you want to package a specific set of modules — for
/// distribution, or as a runnable library — and keep all of them regardless
/// of what calls what. Use <c>shumway-link</c> instead when you want a
/// minimal bundle containing only the code reachable from an entry point.</para>
///
/// <para>Commands:</para>
/// <code>
///   shumway-lib create  &lt;archive.shum&gt; &lt;a.shmo b.shmo ...&gt;
///   shumway-lib list    &lt;archive.shum&gt;
///   shumway-lib add     &lt;archive.shum&gt; &lt;c.shmo ...&gt;
///   shumway-lib delete  &lt;archive.shum&gt; &lt;module ...&gt;
///   shumway-lib extract &lt;archive.shum&gt; [module ...] [-C dir]
/// </code>
///
/// <para>Exit codes: 0 on success, 1 on operation error, 3 on usage error.</para>
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitError = 1;
    private const int ExitUsageError = 3;

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return ExitUsageError;
        }

        string command = args[0];
        string[] rest = args[1..];
        try
        {
            return command switch
            {
                "create"            => CmdCreate(rest),
                "add" or "r"        => CmdAdd(rest),
                "list" or "t"       => CmdList(rest),
                "extract" or "x"    => CmdExtract(rest),
                "delete" or "d"     => CmdDelete(rest),
                "help" or "-h" or "--help" => PrintUsageOk(),
                _ => UnknownCommand(command),
            };
        }
        catch (LibrarianException ex)
        {
            Console.Error.WriteLine($"shumway-lib: {ex.Message}");
            return ExitError;
        }
        catch (Exception ex) when (ex is IOException
                                || ex is InvalidDataException
                                || ex is UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"shumway-lib: {ex.Message}");
            return ExitError;
        }
    }

    // ------------------------------------------------------------------------
    // create
    // ------------------------------------------------------------------------

    private static int CmdCreate(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine(
                "shumway-lib: create requires <archive.shum> and at least one .shmo.");
            return ExitUsageError;
        }
        string archivePath = args[0];
        if (!TryCollectMembers(args[1..], out var members)) return ExitUsageError;

        byte[] bytes = Librarian.CreateArchive(members);
        File.WriteAllBytes(archivePath, bytes);
        Console.Error.WriteLine(
            $"shumway-lib: created {archivePath} ({members.Count} module(s), {bytes.Length} bytes).");
        return ExitOk;
    }

    // ------------------------------------------------------------------------
    // add
    // ------------------------------------------------------------------------

    private static int CmdAdd(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine(
                "shumway-lib: add requires <archive.shum> and at least one .shmo.");
            return ExitUsageError;
        }
        string archivePath = args[0];
        if (!RequireExistingArchive(archivePath)) return ExitError;
        if (!TryCollectMembers(args[1..], out var members)) return ExitUsageError;

        byte[] existing = File.ReadAllBytes(archivePath);
        byte[] bytes = Librarian.AddMembers(existing, members);
        File.WriteAllBytes(archivePath, bytes);
        Console.Error.WriteLine(
            $"shumway-lib: added {members.Count} module(s) to {archivePath} ({bytes.Length} bytes).");
        return ExitOk;
    }

    // ------------------------------------------------------------------------
    // delete
    // ------------------------------------------------------------------------

    private static int CmdDelete(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine(
                "shumway-lib: delete requires <archive.shum> and at least one module name.");
            return ExitUsageError;
        }
        string archivePath = args[0];
        if (!RequireExistingArchive(archivePath)) return ExitError;
        string[] modules = args[1..];

        byte[] existing = File.ReadAllBytes(archivePath);
        byte[] bytes = Librarian.RemoveModules(
            existing, modules, out var removed, out var notFound);
        foreach (string n in notFound)
            Console.Error.WriteLine($"shumway-lib: warning: module '{n}' is not in the archive.");
        File.WriteAllBytes(archivePath, bytes);
        Console.Error.WriteLine(
            $"shumway-lib: removed {removed.Count} module(s) from {archivePath} ({bytes.Length} bytes).");
        return ExitOk;
    }

    // ------------------------------------------------------------------------
    // list
    // ------------------------------------------------------------------------

    private static int CmdList(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("shumway-lib: list requires exactly one <archive.shum>.");
            return ExitUsageError;
        }
        byte[] bytes = File.ReadAllBytes(args[0]);
        var bundle = BundleReader.FromBytes(bytes);
        if (bundle.ArchiveMembers.Count == 0)
        {
            if (bundle.Entries.Count > 0)
            {
                Console.Error.WriteLine(
                    $"shumway-lib: {args[0]} is a linked bundle (shumway-link), not a librarian "
                    + "archive. Its modules cannot be extracted; they are:");
                foreach (var e in bundle.Entries)
                    Console.Out.WriteLine($"  {e.ModuleName}");
                return ExitOk;
            }
            Console.Out.WriteLine("(empty archive)");
            return ExitOk;
        }

        var rows = new List<string[]>();
        rows.Add(new[] { "MODULE", "MODE", "ARITY", "PUB", "DYN", "LOC", "DYNCLAUSES", "SOURCE", "SIZE", "FILE" });
        foreach (var m in bundle.ArchiveMembers)
        {
            var o = ShmoReader.FromBytes(m.ShmoBytes);
            int pub = o.Defined.Count(d => d.Visibility == PredicateVisibility.Public);
            int dyn = o.Defined.Count(d => d.Visibility == PredicateVisibility.Dynamic);
            int loc = o.Defined.Count(d => d.Visibility == PredicateVisibility.Local);
            int dynClauses = o.DynamicSeeds.Sum(s => s.EncodedClauses.Count);
            rows.Add(new[]
            {
                o.ModuleName,
                o.BuildMode.ToString().ToLowerInvariant(),
                o.ArityCompat ? "yes" : "-",
                pub.ToString(), dyn.ToString(), loc.ToString(),
                dynClauses.ToString(),
                string.IsNullOrEmpty(o.Source) ? "stripped" : $"{o.Source.Length}c",
                m.ShmoBytes.Length.ToString(),
                m.FileName,
            });
        }
        PrintTable(rows);
        Console.Out.WriteLine(
            $"{bundle.ArchiveMembers.Count} module(s), {bytes.Length} bytes total.");
        return ExitOk;
    }

    // ------------------------------------------------------------------------
    // extract
    // ------------------------------------------------------------------------

    private static int CmdExtract(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("shumway-lib: extract requires <archive.shum>.");
            return ExitUsageError;
        }
        string? outDir = null;
        var wanted = new List<string>();
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            if (a is "-C" or "--output-dir")
            {
                if (++i >= args.Length)
                {
                    Console.Error.WriteLine($"shumway-lib: option '{a}' requires a directory.");
                    return ExitUsageError;
                }
                outDir = args[i];
            }
            else if (a.StartsWith('-'))
            {
                Console.Error.WriteLine($"shumway-lib: unknown option '{a}'.");
                return ExitUsageError;
            }
            else
            {
                wanted.Add(a);
            }
        }

        byte[] bytes = File.ReadAllBytes(args[0]);
        var members = Librarian.ReadArchive(bytes);
        if (members.Count == 0)
        {
            Console.Error.WriteLine(
                $"shumway-lib: {args[0]} is not a librarian archive (it has no .shmo members "
                + "to extract).");
            return ExitError;
        }
        if (outDir is not null) Directory.CreateDirectory(outDir);

        var byModule = new Dictionary<string, BundleArchiveMember>(StringComparer.Ordinal);
        foreach (var m in members) byModule[Librarian.ModuleNameOf(m)] = m;

        IEnumerable<BundleArchiveMember> toExtract;
        if (wanted.Count == 0)
        {
            toExtract = members;
        }
        else
        {
            var picked = new List<BundleArchiveMember>();
            int missing = 0;
            foreach (string name in wanted)
            {
                if (byModule.TryGetValue(name, out var m)) picked.Add(m);
                else { Console.Error.WriteLine($"shumway-lib: module '{name}' is not in the archive."); missing++; }
            }
            if (missing > 0) return ExitError;
            toExtract = picked;
        }

        int count = 0;
        foreach (var m in toExtract)
        {
            string name = string.IsNullOrEmpty(m.FileName)
                ? Librarian.ModuleNameOf(m) + ".shmo" : m.FileName;
            string path = outDir is null ? name : Path.Combine(outDir, name);
            File.WriteAllBytes(path, m.ShmoBytes);
            Console.Out.WriteLine($"extracted {path}");
            count++;
        }
        Console.Error.WriteLine($"shumway-lib: extracted {count} object(s).");
        return ExitOk;
    }

    // ------------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------------

    /// <summary>Reads each input path (wildcards expanded) as a <c>.shmo</c>,
    /// validating it parses, and builds the archive-member list keyed by file
    /// name. Returns false (after printing the error) on a bad path / file.</summary>
    private static bool TryCollectMembers(string[] inputs, out List<BundleArchiveMember> members)
    {
        members = new List<BundleArchiveMember>();
        var paths = new List<string>();
        foreach (string arg in inputs)
        {
            if (arg.IndexOfAny(WildcardChars) >= 0)
            {
                if (!TryExpandWildcard(arg, paths)) { members = null!; return false; }
            }
            else
            {
                paths.Add(arg);
            }
        }
        foreach (string path in paths)
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"shumway-lib: cannot read '{path}': {ex.Message}");
                return false;
            }
            try { ShmoReader.FromBytes(bytes); }
            catch (InvalidDataException ex)
            {
                Console.Error.WriteLine($"shumway-lib: '{path}' is not a valid .shmo: {ex.Message}");
                return false;
            }
            members.Add(new BundleArchiveMember(Path.GetFileName(path), bytes));
        }
        return true;
    }

    private static bool RequireExistingArchive(string path)
    {
        if (File.Exists(path)) return true;
        Console.Error.WriteLine(
            $"shumway-lib: archive '{path}' does not exist (use 'create' to make a new one).");
        return false;
    }

    private static readonly char[] WildcardChars = { '*', '?' };

    /// <summary>Expands a wildcard input argument against the file system,
    /// appending matches in case-insensitive sorted order (so the Windows
    /// shell's verbatim glob still works). Mirrors shumway-compile /
    /// shumway-link.</summary>
    private static bool TryExpandWildcard(string arg, List<string> into)
    {
        string dir = Path.GetDirectoryName(arg) is { Length: > 0 } d ? d : ".";
        string pattern = Path.GetFileName(arg);
        List<string> matches;
        try
        {
            matches = Directory.EnumerateFiles(dir, pattern).ToList();
        }
        catch (Exception ex) when (ex is IOException
                                || ex is DirectoryNotFoundException
                                || ex is ArgumentException
                                || ex is UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"shumway-lib: cannot expand '{arg}': {ex.Message}");
            return false;
        }
        if (matches.Count == 0)
        {
            Console.Error.WriteLine($"shumway-lib: no files match '{arg}'.");
            return false;
        }
        matches.Sort(StringComparer.OrdinalIgnoreCase);
        into.AddRange(matches);
        return true;
    }

    private static void PrintTable(List<string[]> rows)
    {
        int cols = rows[0].Length;
        var width = new int[cols];
        foreach (var r in rows)
            for (int c = 0; c < cols; c++)
                width[c] = Math.Max(width[c], r[c].Length);
        var sb = new StringBuilder();
        foreach (var r in rows)
        {
            sb.Clear();
            for (int c = 0; c < cols; c++)
            {
                sb.Append(r[c].PadRight(width[c]));
                if (c < cols - 1) sb.Append("  ");
            }
            Console.Out.WriteLine(sb.ToString());
        }
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"shumway-lib: unknown command '{command}'.");
        PrintUsage();
        return ExitUsageError;
    }

    private static int PrintUsageOk() { PrintUsage(); return ExitOk; }

    private static void PrintUsage()
    {
        Console.Error.WriteLine(
            "Usage: shumway-lib <command> <archive.shum> [args]\n"
            + "\n"
            + "The Shumway librarian. Packages compiled Prolog objects (.shmo, produced by\n"
            + "shumway-compile) into a runnable bundle (.shum), keeping every object you add —\n"
            + "no reachability analysis, no dead-module pruning. (Use shumway-link instead when\n"
            + "you want only the code reachable from an entry point.)\n"
            + "\n"
            + "Commands:\n"
            + "  create  <archive.shum> <a.shmo b.shmo ...>   Create an archive from objects.\n"
            + "  add     <archive.shum> <c.shmo ...>          Add objects to an archive (alias r).\n"
            + "  delete  <archive.shum> <module ...>          Remove modules by name (alias d).\n"
            + "  list    <archive.shum>                       Show the modules inside (alias t).\n"
            + "  extract <archive.shum> [module ...] [-C dir] Write objects back out as .shmo\n"
            + "                                               files (all, or the named modules;\n"
            + "                                               alias x). -C/--output-dir sets the\n"
            + "                                               destination directory.\n"
            + "  help                                         Show this message.\n"
            + "\n"
            + "Object inputs may use wildcards (e.g. *.shmo), expanded in sorted order so they\n"
            + "work from any shell. Modules are keyed by name; an archive cannot hold two\n"
            + "modules of the same name. Exit codes: 0 success, 1 error, 3 usage error.");
    }
}
