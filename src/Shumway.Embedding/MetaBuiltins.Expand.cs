using Shumway.Builtins;
using Shumway.Compiler.Ast;
using Shumway.Core;

namespace Shumway.Embedding;

public static partial class MetaBuiltins
{
    // ============================================================================
    // expand_term/2 (DCG expansion exposed).
    // ============================================================================

    public static bool ExpandTerm2(Activation engine)
    {
        Term input = MaterializeRegister(engine, 0);
        Term result;
        if (input is CompoundTerm { Functor: "-->", Args.Length: 2 })
        {
            // Wrap as a DcgRule clause, run the same transform consult
            // uses, take the expanded clause's term back. The resulting
            // term is shaped as `:- (Head', Body')` for the user.
            var clause = new Shumway.Compiler.Ast.Clause(
                Shumway.Compiler.Ast.ClauseKind.DcgRule, input,
                new Shumway.Compiler.Lexer.SourcePosition(0, 0, 0));
            var transformed = Shumway.Compiler.Parsing.DcgTransform.Apply(new[] { clause });
            result = transformed[0].Term;
        }
        else
        {
            result = input;
        }
        Cell cell = Materializer.MaterializeAsCell(engine, result);
        return engine.UnifyRegisterWithCell(1, cell);
    }

    // ============================================================================
    // file_list/1,2 (Arity-Prolog database dump).
    // ============================================================================

    public static bool FileList1(Activation engine)
    {
        PrologEngine host = RequireHost(engine, "file_list/1");
        string path = RequireAtomPath(engine, register: 0, builtin: "file_list/1");
        var fids = host.ListablePredicates().Select(p => p.FunctorId).ToList();
        WritePredicatesToFile(host, path, fids);
        return true;
    }

    public static bool FileList2(Activation engine)
    {
        PrologEngine host = RequireHost(engine, "file_list/2");
        string path = RequireAtomPath(engine, register: 0, builtin: "file_list/2");
        Term spec = MaterializeRegister(engine, 1);
        var fids = ResolveFileListSpec(host, spec);
        WritePredicatesToFile(host, path, fids);
        return true;
    }

    private static List<int> ResolveFileListSpec(PrologEngine host, Term spec)
    {
        var requested = new List<(string Name, int Arity)>();
        // Accept Name/Arity directly, or a [..] list of them.
        if (spec is CompoundTerm { Functor: "/", Args.Length: 2 } single)
        {
            requested.Add(ParsePredicateIndicator(single));
        }
        else if (spec is CompoundTerm { Functor: ".", Args.Length: 2 } || spec is AtomTerm { Name: "[]" })
        {
            Term cursor = spec;
            while (cursor is CompoundTerm { Functor: ".", Args.Length: 2 } cons)
            {
                if (cons.Args[0] is not CompoundTerm { Functor: "/", Args.Length: 2 } pi)
                    throw new ShumwayPrologException(
                        IsoError.TypeError("predicate_indicator", cons.Args[0]));
                requested.Add(ParsePredicateIndicator(pi));
                cursor = cons.Args[1];
            }
            if (cursor is not AtomTerm { Name: "[]" })
                throw new ShumwayPrologException(
                    IsoError.TypeError("list", spec));
        }
        else
        {
            throw new ShumwayPrologException(
                IsoError.TypeError("predicate_indicator_or_list", spec));
        }

        // Map (Name, Arity) → matching fids (across modules; a local pred
        // is stored as <module>$<name> so demangle when comparing).
        var fids = new List<int>();
        foreach (var (name, arity) in requested)
        {
            foreach (var (fid, _) in host.ListablePredicates())
            {
                var (atomId, fidArity) = FunctorTable.Lookup(fid);
                if (fidArity != arity) continue;
                string mangled = AtomTable.GetById(atomId)?.Name ?? "";
                if (mangled == name || PrologEngine.DemangleLocalName(mangled) == name)
                    fids.Add(fid);
            }
        }
        return fids;
    }

    private static (string Name, int Arity) ParsePredicateIndicator(CompoundTerm pi)
    {
        if (pi.Args[0] is not AtomTerm nameAtom)
            throw new ShumwayPrologException(
                IsoError.TypeError("predicate_indicator", pi));
        if (pi.Args[1] is not IntTerm arityInt)
            throw new ShumwayPrologException(
                IsoError.TypeError("predicate_indicator", pi));
        return (nameAtom.Name, (int)arityInt.Value);
    }

    private static void WritePredicatesToFile(PrologEngine host, string path, IList<int> fids)
    {
        using var sw = new System.IO.StreamWriter(path, append: false);
        // Emit `:- dynamic Name/Arity.` for any dynamic predicate in the
        // list so a re-consult preserves the declaration (under
        // implicit_dynamic=true the directive isn't strictly required,
        // but it documents intent and works regardless of the flag).
        var dynamicFids = new HashSet<int>();
        foreach (int fid in fids)
        {
            if (host.IsDynamic(fid)) dynamicFids.Add(fid);
        }
        foreach (int fid in dynamicFids)
        {
            var (atomId, arity) = FunctorTable.Lookup(fid);
            string name = PrologEngine.DemangleLocalName(
                AtomTable.GetById(atomId)?.Name ?? "");
            sw.WriteLine($":- dynamic {name}/{arity}.");
        }
        if (dynamicFids.Count > 0) sw.WriteLine();
        foreach (int fid in fids)
        {
            foreach (var clause in host.ClausesForListing(fid))
                ClausePortrayer.Print(sw, clause.Term);
        }
    }

    public static bool Directory6(Activation engine)
    {
        string path = RequireAtomPath(engine, register: 0, builtin: "directory/6");
        if (!System.IO.Directory.Exists(path))
            throw new ShumwayPrologException(
                IsoError.ExistenceError("directory", new AtomTerm(path)));
        var entries = new System.IO.DirectoryInfo(path)
            .EnumerateFileSystemInfos()
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ToList();
        if (entries.Count == 0) return false;
        int returnPc = engine.BuiltinReturnPc;
        return IndexEnumCursor.Start(engine, entries.Count, 6, returnPc,  // arity 6 (directory/6)
            (e, i) => Directory6Unify(e, entries, i));
    }

    private static bool Directory6Unify(
        Activation engine, List<System.IO.FileSystemInfo> entries, int index)
    {
        var info = entries[index];
        // Arity-style mode bits: ReadOnly=1, Hidden=2, System=4,
        // Directory=16, Archive=32 — .NET FileAttributes uses the
        // same numeric values so a masked cast works directly.
        const int ModeMask = 1 | 2 | 4 | 16 | 32;
        int mode = (int)info.Attributes & ModeMask;
        var t = info.LastWriteTime;
        long size = info is System.IO.FileInfo f ? f.Length : 0L;
        int nameAid = AtomTable.Intern(info.Name, permanent: false).Id;
        int timeAid = AtomTable.Intern(
            $"{t.Hour:D2}:{t.Minute:D2}:{t.Second:D2}", permanent: false).Id;
        int dateAid = AtomTable.Intern(
            $"{t.Year:D4}-{t.Month:D2}-{t.Day:D2}", permanent: false).Id;

        if (!engine.UnifyRegisterWithCell(1, Cell.Atom(nameAid))) return false;
        if (!engine.UnifyRegisterWithCell(2, Cell.Int(mode))) return false;
        if (!engine.UnifyRegisterWithCell(3, Cell.Atom(timeAid))) return false;
        if (!engine.UnifyRegisterWithCell(4, Cell.Atom(dateAid))) return false;
        if (!engine.UnifyRegisterWithCell(5, Cell.Int(size))) return false;
        return true;
    }
}
