using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Shumway.Compiler.Ast;
using Shumway.Compiler.Lexer;

namespace Shumway.Compiler.Parsing;

/// <summary>
/// ISO 7.4.2.7 <c>:- include(File)</c> — textual inclusion, shared by the
/// engine consult path (<c>PrologEngine.ConsultString</c>) and the separate
/// compiler (<c>ShmoCompiler</c>).
///
/// <para>Expansion is depth-first in source order, and each included file is
/// parsed AT EXPANSION TIME against the caller's live operator table — so an
/// <c>:- op/3</c> executed by an earlier include is in force when a later
/// sibling parses (the SWI loader-file pattern: a first include defines
/// operators, subsequent includes use them). Approximation vs. strict ISO
/// streaming: the INCLUDING file is parsed in full before expansion, so its
/// own text cannot use operators an included file defines — loader files
/// (only directives) are unaffected.</para>
///
/// <para>Cycles (a file transitively including itself) are an error; the same
/// file included twice on separate branches is legal textual duplication.
/// Relative paths resolve against the including file's directory when known,
/// else the process CWD; a missing extension retries with <c>.pl</c>.</para>
/// </summary>
public static class IncludeExpander
{
    /// <summary>True when <paramref name="clauses"/> contains at least one
    /// <c>:- include/1</c> directive.</summary>
    public static bool HasInclude(IReadOnlyList<Clause> clauses)
        => clauses.Any(c => TryReadIncludeDirective(c, out _));

    /// <summary>Expands every <c>:- include/1</c> in <paramref name="clauses"/>
    /// (recursively). Returns the SAME list instance when there is nothing to
    /// expand, so callers sharing a cached parse are unaffected.</summary>
    public static List<Clause> Expand(List<Clause> clauses, string? baseDir,
        OperatorTable operators, PrologFlags flags)
        => Expand(clauses, baseDir, operators, flags, chain: null);

    private static List<Clause> Expand(List<Clause> clauses, string? baseDir,
        OperatorTable operators, PrologFlags flags, HashSet<string>? chain)
    {
        if (!HasInclude(clauses)) return clauses;

        var result = new List<Clause>(clauses.Count + 64);
        foreach (var c in clauses)
        {
            if (!TryReadIncludeDirective(c, out string? fileRef))
            {
                result.Add(c);
                continue;
            }
            string resolved = Path.IsPathRooted(fileRef!)
                ? fileRef!
                : Path.Combine(baseDir ?? Directory.GetCurrentDirectory(), fileRef!);
            if (!File.Exists(resolved) && !Path.HasExtension(resolved))
                resolved += ".pl";
            if (!File.Exists(resolved))
                throw new FileNotFoundException(
                    $":- include({fileRef}): file not found (resolved to '{resolved}').", resolved);
            string full = Path.GetFullPath(resolved);
            chain ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!chain.Add(full))
                throw new InvalidOperationException(
                    $":- include({fileRef}): cyclic inclusion of '{full}'.");
            List<Clause> sub;
            try
            {
                sub = new ClauseReader(
                    new Lexer.Lexer(
                        Shumway.Core.TextFile.ReadAllText(full, flags.DefaultTextEncoding),
                        flags.CharConversionEnabled ? flags.CharConversion : null),
                    operators, flags).ReadAll().ToList();
            }
            catch (ParseException pe)
            {
                // Re-frame so the caller's file:line diagnostics aren't
                // silently attributed to the INCLUDING file.
                throw new ParseException(
                    $"in included file '{full}': {pe.Message}", pe.Position);
            }
            result.AddRange(Expand(sub, Path.GetDirectoryName(full), operators, flags, chain));
            chain.Remove(full);
        }
        return result;
    }

    private static bool TryReadIncludeDirective(Clause clause, out string? fileRef)
    {
        fileRef = null;
        if (clause.Kind != ClauseKind.Directive) return false;
        if (clause.Term is not CompoundTerm { Functor: ":-", Args.Length: 1 } wrap) return false;
        if (wrap.Args[0] is not CompoundTerm { Functor: "include", Args.Length: 1 } inc) return false;
        fileRef = inc.Args[0] switch
        {
            AtomTerm a => a.Name,
            StringTerm s => s.Content,
            _ => null,
        };
        return fileRef is not null;
    }
}
