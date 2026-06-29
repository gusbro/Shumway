using Shumway.Compiler.Ast;

namespace Shumway.Compiler.NativeC;

/// <summary>The .NET-mapped type of a marshalled Prolog variable (ADR-022, the
/// int/float/string tier). <see cref="Term"/> is the deferred whole-term tier.</summary>
public enum NativeKind { Int, Long, Float, Double, String, Term, Reftype }

/// <summary>The direction a native block uses a Prolog variable: read on entry
/// (<see cref="Input"/>) or unified on exit (<see cref="Output"/>).</summary>
public enum NativeMode { Input, Output }

/// <summary>An inferred binding for one Prolog variable named in a native block.</summary>
public sealed record NativeVar(string Name, NativeKind Kind, NativeMode Mode);

/// <summary>ADR-022 — a SCALAR <c>:- c</c> global referenced by a block (a plain
/// <c>int</c> / <c>long</c> / <c>float</c> / <c>double</c> global, as opposed to a
/// <c>char*</c>/<c>reftype</c> holder). It maps to per-engine persistent storage
/// (Arity static-storage semantics) — the block seeds its value on entry and writes
/// it through on every assignment. <see cref="IsFloat"/> picks the storage /
/// CLR-local type (<c>double</c> vs <c>long</c>).</summary>
public sealed record NativeScalarGlobal(string Name, bool IsFloat);

/// <summary>The result of analysing one <c>{ … }</c> block against its enclosing
/// clause and the <c>:- c</c> symbol table: the marshalled Prolog variables (with
/// inferred type+mode), the block-local C temporaries, the referenced
/// <c>:- c</c> globals, and any inference <see cref="Diagnostics"/> (a variable
/// whose mode/type could not be determined — a compile error per ADR-022).</summary>
public sealed record NativeBlockInfo(
    IReadOnlyList<NativeVar> PrologVars,
    IReadOnlyList<string> Locals,
    IReadOnlyList<string> Globals,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<NativeScalarGlobal> ScalarGlobals);

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
        Term clauseTerm, IReadOnlyList<CStmt> block, IReadOnlyList<CDecl> cDecls,
        IReadOnlyDictionary<string, CType>? clauseDeclHints = null)
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
        // ADR-024 — global C buffers (a `char*` / `char[]` or a reftype global) are
        // reusable HOLDERS (slots): a variable assigned from one (`Par1 is par1str`)
        // is a holder cursor, not a string value. (Resolved through typedefs, so
        // `pchar par1str` counts.)
        var holderGlobals = new HashSet<string>();
        foreach (var d in cDecls)
            if (d is CGlobalVar g)
            {
                var rt = ResolveTypedef(g.Type, typedefs);
                if ((rt.Name == "char" && (rt.PointerDepth >= 1 || g.ArrayLength is not null))
                    || rt.Name is "reftype" or "preftype" or "t_reftype")
                    holderGlobals.Add(g.Name);
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
        var lengthHint = new Dictionary<string, NativeKind>();  // MakeCString length arg → Int
        var holderVars = new HashSet<string>();            // Prolog var assigned from a holder global
        var unknownNames = new HashSet<string>();          // identifiers neither prolog-var nor declared global
        var bindIntermediates = new HashSet<string>();     // `Var is …` targets that are block-locals

        // Kinds of Prolog variables already determined as the statements are
        // scanned (seeded from the clause's type guards). A later `Var is …` whose
        // right side reads such a variable (e.g. `RetCode is Ret`, after
        // `Ret is 'i_product_revision'(buffer)` typed Ret from the prototype's
        // return type) picks the type up from here.
        var prologVarKind = new Dictionary<string, NativeKind>(typeGuard);

        foreach (var st in block)
        {
            switch (st)
            {
                case CVarDeclStmt v:
                    if (prologVars.Contains(v.Var))
                    {
                        declHint[v.Var] = v.Type;
                        if (MapType(v.Type, typedefs) is { } dk) prologVarKind[v.Var] = dk;
                    }
                    else localType[v.Var] = v.Type;
                    break;
                case CBindStmt b:
                    outputs.Add(b.Var);
                    // A `Var is …` target that is not a Prolog variable is a block-local
                    // intermediate — it is introduced here, so it is not an undeclared
                    // global even if a later statement reads it.
                    if (!prologVars.Contains(b.Var)) bindIntermediates.Add(b.Var);
                    // `Par1 is par1str` where par1str is a holder global → Par1 is a
                    // holder cursor (a slot), not a string value.
                    if (b.Value is CIdentExpr rhsId && holderGlobals.Contains(rhsId.Name))
                        holderVars.Add(b.Var);
                    if (InferExprKind(b.Value, localType, globalType, protoReturn, typedefs,
                            prologVarKind) is { } k)
                    {
                        rhsKind[b.Var] = k;
                        prologVarKind[b.Var] = k;   // visible to later statements
                    }
                    WalkExpr(b.Value, prologVars, referenced, globalType.Keys, globalsUsed,
                        intrinsicIn, intrinsicOut, unknownNames);
                    CollectLengthHints(b.Value, prologVars, lengthHint);
                    break;
                case CAssignStmt a:
                    WalkExpr(a.Target, prologVars, referenced, globalType.Keys, globalsUsed,
                        intrinsicIn, intrinsicOut, unknownNames);
                    WalkExpr(a.Value, prologVars, referenced, globalType.Keys, globalsUsed,
                        intrinsicIn, intrinsicOut, unknownNames);
                    CollectLengthHints(a.Target, prologVars, lengthHint);
                    CollectLengthHints(a.Value, prologVars, lengthHint);
                    break;
                case CCallStmt c:
                    WalkExpr(c.Call, prologVars, referenced, globalType.Keys, globalsUsed,
                        intrinsicIn, intrinsicOut, unknownNames);
                    CollectLengthHints(c.Call, prologVars, lengthHint);
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
            // A variable assigned from a holder global is a holder cursor; this
            // wins over its `: pchar` declaration (a holder, not a string value).
            if (holderVars.Contains(name)) kind = NativeKind.Reftype;
            if (kind is null && declHint.TryGetValue(name, out var dh)) kind = MapType(dh, typedefs);
            if (kind is null && rhsKind.TryGetValue(name, out var rk)) kind = rk;
            // A `Var: type` declaration in ANOTHER block of the same clause: a
            // variable declared in one block (e.g. `Par1: pchar`) and used in
            // another keeps that type.
            if (kind is null && clauseDeclHints is not null
                && clauseDeclHints.TryGetValue(name, out var ch))
                kind = MapType(ch, typedefs);
            // MakeCString(buffer, length, &Var): the length argument is an integer.
            // The intrinsic itself discards it, but when the same variable is used
            // elsewhere in the block (e.g. `buffer_len = Len - 1`) its type is known.
            if (kind is null && lengthHint.TryGetValue(name, out var lh)) kind = lh;
            if (kind is null && (intrinsicIn.Contains(name) || intrinsicOut.Contains(name)))
                kind = NativeKind.String;
            if (kind is null && typeGuard.TryGetValue(name, out var tg)) kind = tg;

            if (kind is null)
                diags.Add($"cannot infer the type of native variable '{name}' "
                    + "(add a type guard such as integer/1, atom/1 or float/1)");
            else
                vars.Add(new NativeVar(name, kind.Value, mode));
        }

        // Undeclared scalar globals: a lowercase identifier used in the block that
        // is neither a Prolog variable, a block-local (`Var: type` declaration or an
        // `is`-introduced intermediate), nor a declared `:- c` global is a typo or a
        // missing declaration — a consult error, never a silently zero-initialised
        // local. An `extern` declaration counts as declared (CParser folds `extern`
        // into a normal global), so a cross-module global is referenced by declaring
        // it `extern`; its per-engine storage is shared by name with the module that
        // defines it.
        foreach (var name in unknownNames.OrderBy(s => s))
            if (!localType.ContainsKey(name) && !bindIntermediates.Contains(name))
                diags.Add($"references undeclared native global '{name}' — declare it in a "
                    + "':- c' region (or as 'extern' if it is defined in another module)");

        // Scalar globals: a referenced `:- c` global that is NOT a holder
        // (char*/char[]/reftype) — a plain int/float scalar. Mapped to persistent
        // per-engine storage with Arity static-storage semantics.
        var scalarGlobals = new List<NativeScalarGlobal>();
        foreach (var name in globalsUsed.OrderBy(s => s))
        {
            if (holderGlobals.Contains(name)) continue;
            if (!globalType.TryGetValue(name, out var gt)) continue;   // undeclared → not a global
            var rt = ResolveTypedef(gt, typedefs);
            scalarGlobals.Add(new NativeScalarGlobal(name, rt.Name is "float" or "double"));
        }

        return new NativeBlockInfo(vars,
            localType.Keys.OrderBy(s => s).ToList(),
            globalsUsed.OrderBy(s => s).ToList(),
            diags,
            scalarGlobals);
    }

    // -----------------------------------------------------------------------

    private static void WalkExpr(CExpr e, HashSet<string> prologVars,
        HashSet<string> referenced, IEnumerable<string> globalNames,
        HashSet<string> globalsUsed, HashSet<string> intrinsicIn, HashSet<string> intrinsicOut,
        HashSet<string> unknownNames)
    {
        switch (e)
        {
            case CIdentExpr id:
                if (prologVars.Contains(id.Name)) referenced.Add(id.Name);
                else if (globalNames.Contains(id.Name)) globalsUsed.Add(id.Name);
                // A name that is neither a Prolog variable nor a declared `:- c`
                // global is a candidate undeclared global — flagged unless it turns
                // out to be a block-local (a `Var: type` decl or an `is`-introduced
                // intermediate), resolved by the caller.
                else unknownNames.Add(id.Name);
                break;
            case CAddrOfExpr a: WalkExpr(a.Operand, prologVars, referenced, globalNames, globalsUsed, intrinsicIn, intrinsicOut, unknownNames); break;
            case CDerefExpr d: WalkExpr(d.Operand, prologVars, referenced, globalNames, globalsUsed, intrinsicIn, intrinsicOut, unknownNames); break;
            case CBinaryExpr bin:
                WalkExpr(bin.Left, prologVars, referenced, globalNames, globalsUsed, intrinsicIn, intrinsicOut, unknownNames);
                WalkExpr(bin.Right, prologVars, referenced, globalNames, globalsUsed, intrinsicIn, intrinsicOut, unknownNames);
                break;
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
                    WalkExpr(arg, prologVars, referenced, globalNames, globalsUsed, intrinsicIn, intrinsicOut, unknownNames);
                break;
        }
    }

    /// <summary>Records the integer type of a <c>MakeCString(buffer, length,
    /// &amp;Var)</c> call's <em>length</em> argument (its second positional arg)
    /// when that argument is a Prolog variable. The intrinsic discards the length,
    /// but the type is genuinely known — so a variable that is also used elsewhere
    /// in the block (arithmetic, another call) doesn't fail type inference.</summary>
    private static void CollectLengthHints(CExpr e, HashSet<string> prologVars,
        Dictionary<string, NativeKind> hint)
    {
        switch (e)
        {
            case CCallExpr { Name: "MakeCString" } c:
                if (c.Args.Count >= 2 && c.Args[1] is CIdentExpr len && prologVars.Contains(len.Name))
                    hint[len.Name] = NativeKind.Int;
                foreach (var arg in c.Args) CollectLengthHints(arg, prologVars, hint);
                break;
            case CCallExpr c2:
                foreach (var arg in c2.Args) CollectLengthHints(arg, prologVars, hint);
                break;
            case CBinaryExpr b:
                CollectLengthHints(b.Left, prologVars, hint);
                CollectLengthHints(b.Right, prologVars, hint);
                break;
            case CAddrOfExpr a: CollectLengthHints(a.Operand, prologVars, hint); break;
            case CDerefExpr d: CollectLengthHints(d.Operand, prologVars, hint); break;
        }
    }

    private static NativeKind? InferExprKind(CExpr e,
        Dictionary<string, CType> localType, Dictionary<string, CType> globalType,
        Dictionary<string, CType> protoReturn, Dictionary<string, CType> typedefs,
        Dictionary<string, NativeKind> prologVarKind)
        => e switch
        {
            CIntExpr => NativeKind.Int,
            CStringExpr => NativeKind.String,
            // A block-local C temp, a `:- c` global, or — for a Prolog variable
            // (e.g. `RetCode is Ret`) — a Prolog variable already typed earlier in
            // the block (from the prototype return type, a guard, or a Var:type).
            CIdentExpr id =>
                localType.TryGetValue(id.Name, out var lt) ? MapType(lt, typedefs)
                : globalType.TryGetValue(id.Name, out var gt) ? MapType(gt, typedefs)
                : prologVarKind.TryGetValue(id.Name, out var pk) ? pk
                : null,
            CCallExpr c when protoReturn.TryGetValue(c.Name, out var rt) => MapType(rt, typedefs),
            CBinaryExpr b => CombineKind(
                InferExprKind(b.Left, localType, globalType, protoReturn, typedefs, prologVarKind),
                InferExprKind(b.Right, localType, globalType, protoReturn, typedefs, prologVarKind)),
            _ => null,
        };

    /// <summary>The result kind of a binary arithmetic expression: floating wins
    /// over integer, long over int. Null if either operand's kind is unknown.</summary>
    private static NativeKind? CombineKind(NativeKind? a, NativeKind? b)
    {
        if (a is null || b is null) return null;
        if (a is NativeKind.Double or NativeKind.Float || b is NativeKind.Double or NativeKind.Float)
            return NativeKind.Double;
        if (a == NativeKind.Long || b == NativeKind.Long) return NativeKind.Long;
        return NativeKind.Int;
    }

    /// <summary>Maps a (typedef-resolved) C type to a <see cref="NativeKind"/>, or
    /// null when it is outside the int/float/string tier (e.g. a reftype pointer —
    /// the deferred whole-term tier).</summary>
    private static NativeKind? MapType(CType t, Dictionary<string, CType> typedefs)
    {
        t = ResolveTypedef(t, typedefs);
        // ADR-024 — the Arity generic-term struct, in any pointer form, is a
        // reftype cursor (a TermSlot handle). reftype = struct t_reftype*,
        // preftype = reftype* — all map to the same handle in the cursor model.
        if (t.Name is "reftype" or "preftype" or "t_reftype")
            return NativeKind.Reftype;
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
        // Arity string-conversion predicates: both arguments are strings/atoms
        // (e.g. `make_prolog_string(Atom, Atom) :- atom(Atom), !.`). A variable a
        // block later marshals, bound by one of these in the clause body, is a
        // string — a type source even without an explicit atom/1 guard.
        if (c.Args.Length == 2 && c.Functor is "make_prolog_string" or "make_c_string")
        {
            foreach (var a in c.Args)
                if (a is VarTerm sv && !typeGuard.ContainsKey(sv.Name))
                    typeGuard[sv.Name] = NativeKind.String;
        }
        foreach (var a in c.Args) CollectGuards(a, hasVar, hasNonvar, typeGuard);
    }
}
