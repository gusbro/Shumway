using Shumway.Compiler.Ast;

namespace Shumway.Compiler.NativeC;

/// <summary>The .NET-mapped type of a marshalled Prolog variable (ADR-022, the
/// int/float/string tier). <see cref="Term"/> is the deferred whole-term tier.</summary>
public enum NativeKind { Int, Long, Float, Double, String, Term }

/// <summary>The direction a native block uses a Prolog variable: read on entry
/// (<see cref="Input"/>) or unified on exit (<see cref="Output"/>).</summary>
public enum NativeMode { Input, Output }

/// <summary>An inferred binding for one Prolog variable named in a native block.</summary>
public sealed record NativeVar(string Name, NativeKind Kind, NativeMode Mode);

/// <summary>The result of analysing one <c>{ … }</c> block against its enclosing
/// clause and the <c>:- c</c> symbol table: the marshalled Prolog variables (with
/// inferred type+mode), the block-local C temporaries, the referenced
/// <c>:- c</c> globals, and any inference <see cref="Diagnostics"/> (a variable
/// whose mode/type could not be determined — a compile error per ADR-022).</summary>
public sealed record NativeBlockInfo(
    IReadOnlyList<NativeVar> PrologVars,
    IReadOnlyList<string> Locals,
    IReadOnlyList<string> Globals,
    IReadOnlyList<string> Diagnostics);

/// <summary>ADR-022 step 3 — infers the type and mode of each Prolog variable a
/// native block marshals. Sources, most specific first: a block-local
/// <c>Var: type</c> declaration; the C type of an <c>is</c> right-hand side (a
/// local, a <c>:- c</c> prototype's return type, or a literal); the
/// <c>MakeCString</c> / <c>MakePrologString</c> string intrinsics; and the
/// surrounding Prolog mode/type guards (<c>var</c>/<c>nonvar</c>/<c>integer</c>/
/// <c>float</c>/<c>atom</c>/<c>string</c>/<c>term</c>). A variable that none of
/// these determine is reported as a diagnostic.</summary>
public static class NativeInference
{
    private static readonly HashSet<string> TypeGuards =
        new() { "integer", "float", "atom", "string", "term" };

    public static NativeBlockInfo Analyze(
        Term clauseTerm, IReadOnlyList<CStmt> block, IReadOnlyList<CDecl> cDecls)
    {
        // ----- clause context: Prolog variable names + the mode/type guards -----
        var prologVars = new HashSet<string>();
        CollectVarNames(clauseTerm, prologVars);
        var hasVar = new HashSet<string>();
        var hasNonvar = new HashSet<string>();
        var typeGuard = new Dictionary<string, NativeKind>();
        CollectGuards(clauseTerm, hasVar, hasNonvar, typeGuard);

        // ----- `:- c` symbol table: typedefs, globals, prototype return types -----
        var typedefs = new Dictionary<string, CType>();
        var globalType = new Dictionary<string, CType>();
        var protoReturn = new Dictionary<string, CType>();
        foreach (var d in cDecls)
        {
            switch (d)
            {
                case CTypedef td: typedefs[td.Alias] = td.Underlying; break;
                case CGlobalVar g: globalType[g.Name] = g.Type; break;
                case CPrototype p: protoReturn[p.Name] = p.ReturnType; break;
            }
        }

        // ----- block scan -----
        var localType = new Dictionary<string, CType>();   // block-local C temps
        var declHint = new Dictionary<string, CType>();    // `Var: type` for a Prolog var
        var outputs = new HashSet<string>();               // bound by `Var is …`
        var rhsKind = new Dictionary<string, NativeKind>();
        var intrinsicIn = new HashSet<string>();           // &Var to MakeCString
        var intrinsicOut = new HashSet<string>();          // &Var to MakePrologString
        var referenced = new HashSet<string>();            // any Prolog var the block reads
        var globalsUsed = new HashSet<string>();

        foreach (var st in block)
        {
            switch (st)
            {
                case CVarDeclStmt v:
                    if (prologVars.Contains(v.Var)) declHint[v.Var] = v.Type;
                    else localType[v.Var] = v.Type;
                    break;
                case CBindStmt b:
                    outputs.Add(b.Var);
                    if (InferExprKind(b.Value, localType, globalType, protoReturn, typedefs) is { } k)
                        rhsKind[b.Var] = k;
                    WalkExpr(b.Value, prologVars, referenced, globalType.Keys, globalsUsed,
                        intrinsicIn, intrinsicOut);
                    break;
                case CAssignStmt a:
                    WalkExpr(a.Target, prologVars, referenced, globalType.Keys, globalsUsed,
                        intrinsicIn, intrinsicOut);
                    WalkExpr(a.Value, prologVars, referenced, globalType.Keys, globalsUsed,
                        intrinsicIn, intrinsicOut);
                    break;
                case CCallStmt c:
                    WalkExpr(c.Call, prologVars, referenced, globalType.Keys, globalsUsed,
                        intrinsicIn, intrinsicOut);
                    break;
            }
        }

        // The full set of Prolog variables this block marshals.
        var touched = new SortedSet<string>(referenced);
        touched.UnionWith(outputs);
        touched.UnionWith(intrinsicIn);
        touched.UnionWith(intrinsicOut);
        touched.RemoveWhere(v => !prologVars.Contains(v));

        var vars = new List<NativeVar>();
        var diags = new List<string>();
        foreach (var name in touched)
        {
            // mode
            NativeMode mode =
                hasVar.Contains(name) ? NativeMode.Output
                : hasNonvar.Contains(name) ? NativeMode.Input
                : outputs.Contains(name) || intrinsicOut.Contains(name) ? NativeMode.Output
                : NativeMode.Input;

            // type — most specific first
            NativeKind? kind = null;
            if (declHint.TryGetValue(name, out var dh)) kind = MapType(dh, typedefs);
            if (kind is null && rhsKind.TryGetValue(name, out var rk)) kind = rk;
            if (kind is null && (intrinsicIn.Contains(name) || intrinsicOut.Contains(name)))
                kind = NativeKind.String;
            if (kind is null && typeGuard.TryGetValue(name, out var tg)) kind = tg;

            if (kind is null)
                diags.Add($"cannot infer the type of native variable '{name}' "
                    + "(add a type guard such as integer/1, atom/1 or float/1)");
            else
                vars.Add(new NativeVar(name, kind.Value, mode));
        }

        return new NativeBlockInfo(vars,
            localType.Keys.OrderBy(s => s).ToList(),
            globalsUsed.OrderBy(s => s).ToList(),
            diags);
    }

    // -----------------------------------------------------------------------

    private static void WalkExpr(CExpr e, HashSet<string> prologVars,
        HashSet<string> referenced, IEnumerable<string> globalNames,
        HashSet<string> globalsUsed, HashSet<string> intrinsicIn, HashSet<string> intrinsicOut)
    {
        switch (e)
        {
            case CIdentExpr id:
                if (prologVars.Contains(id.Name)) referenced.Add(id.Name);
                else if (globalNames.Contains(id.Name)) globalsUsed.Add(id.Name);
                break;
            case CAddrOfExpr a: WalkExpr(a.Operand, prologVars, referenced, globalNames, globalsUsed, intrinsicIn, intrinsicOut); break;
            case CDerefExpr d: WalkExpr(d.Operand, prologVars, referenced, globalNames, globalsUsed, intrinsicIn, intrinsicOut); break;
            case CCallExpr c:
                bool inStr = c.Name == "MakeCString";
                bool outStr = c.Name is "MakePrologString" or "MakePrologStringEx";
                if (inStr || outStr)
                {
                    // A string intrinsic marshals exactly its `&Var` argument (the
                    // Prolog string). Its other args — the C buffer and, for
                    // MakeCString, the length — are consumed/discarded in the .NET
                    // lowering (an atom IS a .NET string, there is no buffer), so
                    // they are NOT generic-walked: a length that happens to be a
                    // Prolog variable must not become a marshalled input.
                    foreach (var arg in c.Args)
                    {
                        if (arg is CAddrOfExpr { Operand: CIdentExpr av } && prologVars.Contains(av.Name))
                            (inStr ? intrinsicIn : intrinsicOut).Add(av.Name);
                        else if (arg is CIdentExpr g && globalNames.Contains(g.Name))
                            globalsUsed.Add(g.Name);
                    }
                    return;
                }
                foreach (var arg in c.Args)
                    WalkExpr(arg, prologVars, referenced, globalNames, globalsUsed, intrinsicIn, intrinsicOut);
                break;
        }
    }

    private static NativeKind? InferExprKind(CExpr e,
        Dictionary<string, CType> localType, Dictionary<string, CType> globalType,
        Dictionary<string, CType> protoReturn, Dictionary<string, CType> typedefs)
        => e switch
        {
            CIntExpr => NativeKind.Int,
            CStringExpr => NativeKind.String,
            CIdentExpr id =>
                localType.TryGetValue(id.Name, out var lt) ? MapType(lt, typedefs)
                : globalType.TryGetValue(id.Name, out var gt) ? MapType(gt, typedefs)
                : null,
            CCallExpr c when protoReturn.TryGetValue(c.Name, out var rt) => MapType(rt, typedefs),
            _ => null,
        };

    /// <summary>Maps a (typedef-resolved) C type to a <see cref="NativeKind"/>, or
    /// null when it is outside the int/float/string tier (e.g. a reftype pointer —
    /// the deferred whole-term tier).</summary>
    private static NativeKind? MapType(CType t, Dictionary<string, CType> typedefs)
    {
        t = ResolveTypedef(t, typedefs);
        if (t.PointerDepth >= 1)
            return t.Name == "char" ? NativeKind.String : null;   // char* → string; others deferred
        return t.Name switch
        {
            "int" or "short" or "unsigned" or "unsigned int" or "char" => NativeKind.Int,
            "long" or "unsigned long" or "int64_t" => NativeKind.Long,
            "float" => NativeKind.Float,
            "double" => NativeKind.Double,
            _ => null,
        };
    }

    private static CType ResolveTypedef(CType t, Dictionary<string, CType> typedefs)
    {
        for (int guard = 0; guard < 16 && typedefs.TryGetValue(t.Name, out var u); guard++)
            t = new CType(u.Name, u.PointerDepth + t.PointerDepth);
        return t;
    }

    private static void CollectVarNames(Term t, HashSet<string> into)
    {
        switch (t)
        {
            case VarTerm v: into.Add(v.Name); break;
            case CompoundTerm c: foreach (var a in c.Args) CollectVarNames(a, into); break;
        }
    }

    private static void CollectGuards(Term t, HashSet<string> hasVar,
        HashSet<string> hasNonvar, Dictionary<string, NativeKind> typeGuard)
    {
        if (t is not CompoundTerm c) return;
        if (c.Args.Length == 1 && c.Args[0] is VarTerm v)
        {
            switch (c.Functor)
            {
                case "var": hasVar.Add(v.Name); break;
                case "nonvar": hasNonvar.Add(v.Name); break;
                case "integer": typeGuard[v.Name] = NativeKind.Int; break;
                case "float": typeGuard[v.Name] = NativeKind.Float; break;
                case "atom" or "string": typeGuard[v.Name] = NativeKind.String; break;
                case "term": typeGuard[v.Name] = NativeKind.Term; break;
            }
        }
        foreach (var a in c.Args) CollectGuards(a, hasVar, hasNonvar, typeGuard);
    }
}
