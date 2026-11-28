using Shumway.Compiler.Ast;
using Shumway.Compiler.Parsing;
using Shumway.Core;

namespace Shumway.Embedding;

/// <summary>What a condition of <see cref="ConditionalCompilation"/> may ask of
/// the program being read, beyond the builtins and the engine's own libraries:
/// <paramref name="Flags"/> as they stand at the directive,
/// <paramref name="PredicateKnown"/> for a name and arity the program has, and
/// <paramref name="LibraryKnown"/> and <paramref name="FileExists"/> for an
/// <c>exists_source/1</c>. <paramref name="Unsupported"/> reports a condition
/// it cannot answer.</summary>
internal sealed record ConditionContext(
    PrologFlags Flags,
    Func<string, int, bool> PredicateKnown,
    Func<string, bool> LibraryKnown,
    Func<string, bool> FileExists,
    Action<Term> Unsupported);

/// <summary>Conditional compilation, <c>:- if(Cond)</c>, <c>:- elif(Cond)</c>,
/// <c>:- else</c> and <c>:- endif</c>, for the consult and for file-at-a-time
/// compilation alike, so both keep the same clauses of a source. A clause in
/// an inactive branch is dropped. A condition is answered without running a
/// query, which neither can do in the middle of reading a file: the forms
/// programs use (<see cref="Holds"/>); any other is reported and taken as
/// false.</summary>
internal sealed class ConditionalCompilation
{
    // One frame per open if: whether its branch is active, and whether a
    // branch of it was taken. A frame is active only when its parent is.
    private readonly List<(bool Active, bool AnyTaken)> _frames = new();
    private readonly ConditionContext _context;

    internal ConditionalCompilation(ConditionContext context) => _context = context;

    /// <summary>Whether the text being read is in an active branch.</summary>
    internal bool Including => _frames.Count == 0 || _frames[^1].Active;

    private bool OuterActive => _frames.Count <= 1 || _frames[^2].Active;

    /// <summary>Takes an <c>if</c>, <c>elif</c>, <c>else</c> or <c>endif</c>
    /// directive body and answers true; answers false for any other. A
    /// condition is evaluated only where its branch could be taken.</summary>
    internal bool TryConsume(Term body)
    {
        switch (body)
        {
            case CompoundTerm { Functor: "if", Args: [var condition] }:
            {
                bool on = Including && Holds(condition);
                _frames.Add((on, on));
                return true;
            }
            case CompoundTerm { Functor: "elif", Args: [var condition] }:
            {
                if (_frames.Count == 0) return true;
                var top = _frames[^1];
                bool on = OuterActive && !top.AnyTaken && Holds(condition);
                _frames[^1] = (on, top.AnyTaken || on);
                return true;
            }
            case AtomTerm { Name: "else" }:
            {
                if (_frames.Count == 0) return true;
                var top = _frames[^1];
                _frames[^1] = (OuterActive && !top.AnyTaken, true);
                return true;
            }
            case AtomTerm { Name: "endif" }:
                if (_frames.Count > 0) _frames.RemoveAt(_frames.Count - 1);
                return true;
            default:
                return false;
        }
    }

    private bool Holds(Term condition) => condition switch
    {
        AtomTerm { Name: "true" } => true,
        AtomTerm { Name: "fail" or "false" } => false,
        CompoundTerm { Functor: ",", Args: [var a, var b] } => Holds(a) && Holds(b),
        CompoundTerm { Functor: ";", Args: [var a, var b] } => Holds(a) || Holds(b),
        CompoundTerm { Functor: "\\+" or "not", Args: [var a] } => !Holds(a),
        CompoundTerm { Functor: "current_predicate", Args: [var indicator] } => PredicateExists(indicator),
        CompoundTerm { Functor: "exists_source", Args: [var spec] } => SourceExists(spec),
        CompoundTerm { Functor: "current_prolog_flag", Args: [AtomTerm flag, var value] } => FlagIs(flag.Name, value),
        _ => Unsupported(condition),
    };

    private bool PredicateExists(Term indicator)
    {
        if (indicator is not CompoundTerm { Functor: "/", Args: [AtomTerm name, IntTerm arity] }) return false;
        int fid = FunctorTable.Intern(AtomTable.Intern(name.Name, permanent: true).Id, (int)arity.Value);
        return Shumway.Builtins.BuiltinsRegistry.TryGetByFunctor(fid, out _)
            || _context.PredicateKnown(name.Name, (int)arity.Value);
    }

    private bool SourceExists(Term spec) => spec switch
    {
        CompoundTerm { Functor: "library", Args: [AtomTerm library] } =>
            _context.LibraryKnown(library.Name)
            || CompatLibraries.TryGet(library.Name, out _)
            || LibraryBundles.IsEngineLibrary(library.Name),
        AtomTerm file => _context.FileExists(file.Name) || _context.FileExists(file.Name + ".pl"),
        _ => false,
    };

    private bool FlagIs(string flag, Term value)
    {
        PrologFlags flags = _context.Flags;
        string? actual = flag switch
        {
            "bounded" => "false",
            "dialect" => "shumway",
            "double_quotes" => flags.DoubleQuotes.ToString().ToLowerInvariant(),
            "unknown" => flags.Unknown,
            "occurs_check" => flags.OccursCheck,
            "verbose" => flags.Verbose,
            _ => null,   // a flag this does not know: the condition cannot hold
        };
        return actual is not null && value is AtomTerm v && v.Name == actual;
    }

    private bool Unsupported(Term condition)
    {
        _context.Unsupported(condition);
        return false;
    }

    /// <summary>Keeps <paramref name="flags"/> as a <c>set_prolog_flag/2</c>
    /// directive leaves the ones a condition reads, for a reader that does not
    /// run the directive itself (file-at-a-time compilation).</summary>
    internal static void NoteFlag(PrologFlags flags, Term flag, Term value)
    {
        if (flag is not AtomTerm { Name: var name } || value is not AtomTerm { Name: var v }) return;
        switch (name)
        {
            case "double_quotes":
                flags.DoubleQuotes = v switch
                {
                    "codes" => DoubleQuotesMode.Codes,
                    "chars" => DoubleQuotesMode.Chars,
                    "atom" => DoubleQuotesMode.Atom,
                    "string" => DoubleQuotesMode.String,
                    _ => flags.DoubleQuotes,
                };
                break;
            case "unknown" when v is "error" or "fail" or "warning":
                flags.Unknown = v;
                break;
            case "occurs_check" when v is "true" or "false" or "error":
                flags.OccursCheck = v;
                break;
            case "verbose" when v is "normal" or "silent":
                flags.Verbose = v;
                break;
            case "arity_compat" when v == "true":
                // As the builtin: Arity's undefined predicates fail and its
                // double-quoted text is codes.
                flags.Unknown = "fail";
                flags.DoubleQuotes = DoubleQuotesMode.Codes;
                break;
        }
    }
}
