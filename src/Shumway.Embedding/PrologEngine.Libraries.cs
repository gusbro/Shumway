using System.Collections.Immutable;
using Shumway.Compiler.Ast;
using Shumway.Compiler.Lexer;
using Shumway.Compiler.Parsing;
using Shumway.Compiler.Wam;
using Shumway.Core;
using Shumway.Interpreter;

namespace Shumway.Embedding;

public sealed partial class PrologEngine
{
    /// <summary>Loads the CLP(FD) constraint library into this
    /// engine, making the finite-domain constraints — <c>#=</c>, <c>#\=</c>,
    /// <c>#&lt;</c>, <c>#&gt;</c>, <c>#=&lt;</c>, <c>#&gt;=</c>, <c>in</c>,
    /// <c>ins</c> — and their operators available to subsequently consulted
    /// source and queries. CLP(FD) is opt-in: an engine that never calls
    /// this carries none of the library's weight.</summary>
    public void UseClpfd()
    {
        ConsultString(Clpfd.Source);
        MarkModuleNonDebuggable(Clpfd.ModuleName);   // ADR-035 — a library, not the user's code
    }

    /// <summary>Loads the CLP(R) constraint library into this
    /// engine, making linear-equality constraints over the reals available
    /// through the <c>{Constraint}</c> wrapper. CLP(R) is opt-in: an engine
    /// that never calls this carries none of the library's weight.
    ///
    /// <para>CLP(R) and CLP(FD) both define a <c>verify_attributes/4</c>
    /// hook as a public predicate, so for now only one of the two may be
    /// loaded into a given engine.</para></summary>
    public void UseClpr()
    {
        ConsultString(Clpr.Source);
        MarkModuleNonDebuggable(Clpr.ModuleName);   // ADR-035 — a library, not the user's code
    }

    // Compatibility libraries loaded on demand by use_module(library(Name)),
    // tracked so a repeated import (or a program that imports the same library
    // as one of its dependencies) does not re-consult and trip the
    // public-predicate uniqueness check.
    private readonly HashSet<string> _loadedCompatLibraries = new();

    /// <summary>Loads a built-in Scryer/Trealla compatibility library by name
    /// (see <see cref="CompatLibraries"/>), idempotently. Returns <c>true</c>
    /// if <paramref name="name"/> is a known compatibility library (whether it
    /// carries Prolog source or is a prelude-covered no-op), <c>false</c> for
    /// an unknown library name.</summary>
    internal bool UseCompatLibrary(string name)
    {
        if (!CompatLibraries.TryGet(name, out string source))
            return false;
        // recordInHistory:false — the importing program's own source (which
        // carries the use_module directive) is what SaveState replays; the
        // directive re-loads the library on restore, so recording the library
        // body too would double-consult it (and trip public uniqueness).
        if (_loadedCompatLibraries.Add(name) && source.Length > 0)
            ConsultStringInner(source, recordInHistory: false);
        return true;
    }

    /// <summary>Executes a <c>use_module/1</c> directive body at consult time.
    /// <c>library(Name)</c> loads a constraint or compatibility library;
    /// a plain atom names a file to consult. An unknown library is reported as
    /// a warning and skipped (the program may only need predicates Shumway
    /// already provides, or it will surface a clearer per-predicate
    /// existence_error later) rather than aborting the whole consult.</summary>
    internal void ExecuteUseModuleDirective(Term spec)
    {
        if (spec is CompoundTerm { Functor: "library", Args: [AtomTerm lib] })
        {
            switch (lib.Name)
            {
                case "clpfd": UseClpfd(); return;
                case "clpr":  UseClpr();  return;
                default:
                    if (UseCompatLibrary(lib.Name)) return;
                    Console.Error.WriteLine(
                        $"warning: unknown library '{lib.Name}' in use_module/1 — ignored");
                    return;
            }
        }
        if (spec is AtomTerm fileAtom)
        {
            // Already-loaded module (e.g. consulted directly on the command
            // line, or imported earlier) — importing it again is a no-op.
            if (_modules.ContainsKey(fileAtom.Name)) return;
            string path = fileAtom.Name;
            if (_consultBaseDir is not null && !System.IO.Path.IsPathRooted(path))
                path = System.IO.Path.Combine(_consultBaseDir, path);
            if (!System.IO.Path.HasExtension(path) && System.IO.File.Exists(path + ".pl"))
                path += ".pl";
            // Idempotent: a file already consulted (on the command line or via
            // an earlier import) is not re-consulted — re-loading it would
            // double its clauses.
            try
            {
                if (System.IO.File.Exists(path)
                    && _consultedPaths.Contains(System.IO.Path.GetFullPath(path)))
                    return;
            }
            catch { /* fall through to the normal load */ }
            if (!System.IO.File.Exists(path))
            {
                Console.Error.WriteLine(
                    $"warning: use_module/1 target '{fileAtom.Name}' not found — ignored");
                return;
            }
            // A failing import must not abort the importing consult — warn and
            // continue; any predicate genuinely needed surfaces a clearer
            // existence_error at the call site.
            try { ConsultFile(path); }
            catch (System.Exception ex)
            {
                Console.Error.WriteLine(
                    $"warning: use_module('{fileAtom.Name}') failed: {ex.Message}");
            }
        }
    }

    internal int _nativeBlockConsultSeq;
    // the engine's monotonic synthesized-helper sequence: every
    // consult/assert transform on this engine draws unique helper ids, so a
    // second consult's `$disj_N` can never collide with the first's in the same
    // module. Per-engine (not global) so the atom space stays bounded across
    // engines/processes; the query stub uses the reserved `$q` prefix instead.
    private int _metaHelperSeq;
    internal int NextMetaHelperId() => ++_metaHelperSeq;


    /// <summary>ADR-025 — enables the inline if-then-else lowering: an eligible
    /// plain-goal <c>(C -&gt; T ; E)</c> / <c>(A ; B)</c> compiles INSIDE the host
    /// clause (get_level; try_me_else; cut; jump) instead of a synthesized
    /// 2-clause helper reached by a Call. STATIC consult paths only — the
    /// runtime assert path always uses the helper form. Default OFF (stage (c)
    /// of the ADR-025 rollout): a predicate with an inline ITE is not yet
    /// Tier-1-promotable (the IL compiler rejects the shape gracefully and it
    /// stays on Tier-0), so flipping this on trades Tier-1 eligibility for the
    /// Tier-0 win until the ADR's stage (b) lands. Set before consulting.</summary>
    public bool EnableInlineIte { get; set; }
}
