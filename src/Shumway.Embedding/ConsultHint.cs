using Shumway.Compiler.Ast;
using Shumway.Compiler.Lexer;
using Shumway.Compiler.Parsing;

namespace Shumway.Embedding;

/// <summary>Why the consult would read or run a source otherwise than a
/// file-at-a-time compile of it, for the hint that names <c>--consult</c>.
/// A library the source loads can change it in two ways: it defines
/// <c>term_expansion</c> or <c>goal_expansion</c>, which expand the files that
/// load it, or it has goal directives, which the consult runs and a link that
/// compiles it one file at a time leaves out. When the compile failed, the
/// failure itself can be what the consult reads: a declaration that names a
/// predicate with its module (<c>user:portray/1</c>), or an operator a library
/// the source loads defines and the source is written with. A hook or a goal
/// directive of the source itself is not a reason here: the first refuses the
/// compile (<see cref="ShmoCompiler.WhatFileAtATimeLoses"/>), the second is a
/// warning that names <c>--consult</c>.</summary>
public static class ConsultHint
{
    /// <summary>The reason, a sentence, or null when the consult would read
    /// <paramref name="path"/> as the compile did. <paramref name="text"/> is
    /// the source when it is not the file at <paramref name="path"/>. A library
    /// is looked for as the linker resolves it: among the engine's own, in
    /// <paramref name="libraryDirs"/>, beside the source, then among the
    /// dialect libraries.</summary>
    public static string? Reason(string path, ShmoCompileResult result,
        IReadOnlyList<string> libraryDirs, string? text = null)
    {
        string file = Path.GetFileName(path);
        // A failed compile has no object: what it loads, what it declares and
        // the atoms it is written with come from its text.
        string? source = result.Object is null ? text ?? ReadOrEmpty(path) : null;
        if (source is not null && QualifiedDeclarationInError(source, result.Errors) is { } declared)
            return $"{file} declares {declared}, a predicate named with its module. "
                + "Only --consult reads that declaration.";

        IEnumerable<string> loaded = result.Object is { } obj
            ? obj.LibraryDeps.Select(d => d.LibName)
            : LibrariesLoadedBy(source!);
        HashSet<string>? atoms = null;
        string? besideSource = DirectoryOf(path);
        string? operatorReason = null;
        foreach (string name in loaded.Distinct())
        {
            if (Inspect(name, libraryDirs, besideSource) is not { } library) continue;
            if (library.Hooks.Count > 0)
                return $"{file} loads library '{name}', which changes the code of the files "
                    + $"that load it ({Joined(library.Hooks)}). That only happens with --consult.";
            if (library.GoalsLeftOut.Count > 0)
                return $"{file} loads library '{name}', which "
                    + (library.GoalsLeftOut.Count == 1
                        ? "has a directive that runs a goal"
                        : "has directives that run goals")
                    + $" ({Joined(library.GoalsLeftOut)}). "
                    + (library.GoalsLeftOut.Count == 1 ? "That goal runs" : "Those goals run")
                    + " only with --consult.";
            if (source is null || operatorReason is not null || library.Operators.Count == 0)
                continue;
            atoms ??= AtomsOf(source);
            var used = library.Operators.Where(op => atoms is null || atoms.Contains(op)).ToList();
            if (used.Count > 0)
                operatorReason = $"{file} uses "
                    + (used.Count == 1 ? "the operator " : "the operators ") + Joined(used)
                    + $" of library '{name}'. Only --consult knows "
                    + (used.Count == 1 ? "that operator" : "those operators")
                    + " while it reads the file.";
        }
        return operatorReason;
    }

    private sealed record Library(List<string> Hooks, List<string> GoalsLeftOut, List<string> Operators);

    private static readonly OperatorTable Defaults = OperatorTable.Default();

    // The declarations a file-at-a-time compile reads as Name/Arity lists
    // (ShmoCompiler.ProcessDirective); one that names a predicate with its
    // module is an error there.
    private static readonly HashSet<string> Declarations = new()
    {
        "native", "public", "dynamic", "visible", "multifile", "ensure_linked", "table",
    };

    // The first M:Name/Arity of a declaration the compile reported an error at.
    private static string? QualifiedDeclarationInError(string text, IReadOnlyList<ShmoCompileError> errors)
    {
        if (errors.Count == 0) return null;
        foreach (var clause in Read(text))
        {
            if (clause.Kind != ClauseKind.Directive
                || clause.Term is not CompoundTerm { Functor: ":-", Args: [CompoundTerm d] }
                || d.Args.Length != 1 || !Declarations.Contains(d.Functor))
                continue;
            if (!errors.Any(e => e.Line == clause.Position.Line && e.Column == clause.Position.Column))
                continue;
            foreach (Term item in Items(d.Args[0]))
                if (item is CompoundTerm { Functor: ":", Args: [_, CompoundTerm { Functor: "/" or "//", Args.Length: 2 }] })
                    return AstTermRenderer.RenderQuoted(item);
        }
        return null;
    }

    // The items of a declaration: a list, a comma sequence, or one.
    private static IEnumerable<Term> Items(Term spec)
    {
        var pending = new Stack<Term>();
        pending.Push(spec);
        while (pending.Count > 0)
        {
            Term t = pending.Pop();
            if (t is CompoundTerm { Functor: "." or ",", Args: [var first, var rest] })
            {
                pending.Push(rest);
                pending.Push(first);
            }
            else if (t is not AtomTerm { Name: "[]" })
                yield return t;
        }
    }

    // The libraries a source's directives load, as the file-at-a-time compile
    // records them: use_module/1,2 of library(X) or of a file, and
    // ensure_loaded/1 of library(X).
    private static List<string> LibrariesLoadedBy(string text)
    {
        var names = new List<string>();
        foreach (var clause in Read(text))
        {
            if (clause.Kind != ClauseKind.Directive
                || clause.Term is not CompoundTerm { Functor: ":-", Args: [CompoundTerm d] })
                continue;
            Term? spec = d switch
            {
                { Functor: "use_module", Args.Length: 1 or 2 } => d.Args[0],
                { Functor: "ensure_loaded", Args: [CompoundTerm { Functor: "library" } lib] } => lib,
                _ => null,
            };
            if (spec is CompoundTerm { Functor: "library", Args: [AtomTerm name] })
                names.Add(name.Name);
            else if (spec is AtomTerm fileName)
                names.Add(fileName.Name);
        }
        return names;
    }

    private static Library? Inspect(string name, IReadOnlyList<string> libraryDirs, string? besideSource)
    {
        try
        {
            // The engine's own libraries load as bundles: nothing of them is
            // left out.
            if (LibraryBundles.IsEngineLibrary(name))
                return FromSource(LibraryBundles.SourceOf(name), compiled: null);
            var dirs = new List<string>();
            foreach (string entry in libraryDirs) dirs.Add(LibrarySearchPath.DirectoryOf(entry));
            if (besideSource is not null) dirs.Add(besideSource);
            foreach (string dir in dirs)
            {
                string shmo = Path.Combine(dir, name + ".shmo");
                if (File.Exists(shmo)) return FromObject(ShmoReader.ReadFromFile(shmo));
                string pl = Path.Combine(dir, name + ".pl");
                if (File.Exists(pl))
                {
                    string text = Shumway.Core.TextFile.ReadAllText(pl);
                    return FromSource(text, ShmoCompiler.TryCompileSource(text, name, libraryDirs: libraryDirs));
                }
            }
            if (DialectRegistry.TryResolve(null, name, out string source, out _, out _)
                && source.Length > 0)
                return FromSource(source, ShmoCompiler.TryCompileSource(source, name, libraryDirs: libraryDirs));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException
            or ArgumentException or NotSupportedException or UnauthorizedAccessException)
        {
            // A library that cannot be read gives no reason; the link reports it.
        }
        return null;
    }

    // A library's hooks, the goal directives the link's compile of it leaves
    // out (none when it is not compiled by the link), and the operators it
    // gives the files that load it: a module's exported ones, or every :- op
    // of a file that is no module.
    private static Library FromSource(string text, ShmoCompileResult? compiled)
    {
        var hooks = new List<string>();
        var exported = new List<ShmoOperatorDef>();
        var global = new List<ShmoOperatorDef>();
        bool isModule = false;
        foreach (var clause in Read(text))
        {
            if (clause.Kind == ClauseKind.Directive)
            {
                if (clause.Term is not CompoundTerm { Functor: ":-", Args: [CompoundTerm d] }) continue;
                if (d is { Functor: "module", Args: [_, var exports] })
                {
                    isModule = true;
                    foreach (Term item in Items(exports))
                        if (item is CompoundTerm { Functor: "op", Args.Length: 3 } op)
                            AddOperators(exported, op);
                }
                else if (d is { Functor: "op", Args.Length: 3 })
                    AddOperators(global, d);
                continue;
            }
            Term head = clause.Term is CompoundTerm { Functor: ":-", Args: [var h, _] } ? h : clause.Term;
            while (head is CompoundTerm { Functor: ":", Args: [_, var inner] }) head = inner;
            if (head is CompoundTerm { Functor: "term_expansion" or "goal_expansion" } hook)
                AddOnce(hooks, $"{hook.Functor}/{hook.Args.Length}");
        }
        var goals = new List<string>();
        if (compiled is not null)
            foreach (string goal in compiled.GoalsLeftOut) AddOnce(goals, goal);
        return new Library(hooks, goals, NewOperators(isModule ? exported : global));
    }

    private static Library FromObject(ShmoObject obj)
    {
        var hooks = new List<string>();
        foreach (var d in obj.Defined)
        {
            string name = d.Indicator.Name;
            int cut = name.LastIndexOf('$');
            if (cut >= 0) name = name.Substring(cut + 1);
            if (name is "term_expansion" or "goal_expansion" && ShmoCompiler.HasClauses(obj, d))
                AddOnce(hooks, $"{name}/{d.Indicator.Arity}");
        }
        // ADR-046: an exported operator carries a '*' after its type.
        var given = new List<ShmoOperatorDef>();
        foreach (var op in obj.Operators)
        {
            bool exported = op.Type.EndsWith("*", StringComparison.Ordinal);
            if (exported == obj.IsExportQualified)
                given.Add(op with { Type = op.Type.TrimEnd('*') });
        }
        // A compiled object's directives were reported when it was compiled.
        return new Library(hooks, new List<string>(), NewOperators(given));
    }

    private static void AddOperators(List<ShmoOperatorDef> into, CompoundTerm op)
    {
        if (op.Args[0] is not IntTerm priority || op.Args[1] is not AtomTerm type) return;
        foreach (Term item in Items(op.Args[2]))
            if (item is AtomTerm each)
                into.Add(new ShmoOperatorDef((int)priority.Value, type.Name, each.Name));
    }

    // The operators a file-at-a-time reader lacks: not in the default table
    // with the same priority and type.
    private static List<string> NewOperators(List<ShmoOperatorDef> operators)
    {
        var names = new List<string>();
        foreach (var op in operators)
        {
            if (op.Priority <= 0 || !Enum.TryParse(op.Type, ignoreCase: true, out OperatorType type))
                continue;
            int priority;
            OperatorType known;
            bool have = type is OperatorType.Fx or OperatorType.Fy
                    ? Defaults.TryGetPrefix(op.Name, out priority, out known)
                : type is OperatorType.Xf or OperatorType.Yf
                    ? Defaults.TryGetPostfix(op.Name, out priority, out known)
                : Defaults.TryGetInfix(op.Name, out priority, out known);
            if (!have || priority != op.Priority || known != type) AddOnce(names, op.Name);
        }
        return names;
    }

    // The atoms a source is written with, an operator among them; null when
    // the text cannot be read into tokens, and then any operator may be used.
    private static HashSet<string>? AtomsOf(string text)
    {
        var atoms = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var token in new Lexer(text).Tokenize())
                if (token.Kind == TokenKind.Atom) atoms.Add(token.Text);
        }
        catch (LexerException) { return null; }
        return atoms;
    }

    private static IEnumerable<Clause> Read(string text)
    {
        foreach (var entry in new ClauseReader(new Lexer(text), OperatorTable.Default(), new PrologFlags())
                     .ReadAllCollectingErrors(int.MaxValue))
            if (entry.Clause is not null) yield return entry.Clause;
    }

    private static string? DirectoryOf(string path)
    {
        try { return Path.GetDirectoryName(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return null; }
    }

    private static string ReadOrEmpty(string path)
    {
        try { return File.Exists(path) ? Shumway.Core.TextFile.ReadAllText(path) : ""; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    private static void AddOnce(List<string> list, string item)
    {
        if (!list.Contains(item)) list.Add(item);
    }

    private static string Joined(List<string> items) => items.Count switch
    {
        1 => items[0],
        2 => $"{items[0]} and {items[1]}",
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[items.Count - 1],
    };
}
