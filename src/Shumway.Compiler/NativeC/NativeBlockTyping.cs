using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Shumway.Compiler.NativeC;

/// <summary>Thrown to abandon native-block code generation on a construct outside
/// the supported int/float/string tier (the reftype tier, C control flow, exotic
/// types, an unresolved interop function). Both code generators catch it and fall
/// back to the interpreter.</summary>
public sealed class NativeBlockBailException : Exception { }

/// <summary>ADR-022 item 2 — the shared typed analysis of a native block, used by
/// both code generators: the Expression-tree delegate compiler
/// (<c>NativeBlockCompiler</c>, runtime) and the Sigil inline emitter
/// (<c>NativeBlockIlEmitter</c>, build-time IL). It assigns each Prolog variable
/// and block-local one of three model CLR types — <c>long</c> (every integer
/// kind), <c>double</c> (every floating kind), <c>string</c> — mirroring the
/// int/float/string tier, and records which outputs must be unified back. Any
/// construct outside the tier throws <see cref="NativeBlockBailException"/>.</summary>
public sealed class NativeBlockTyping
{
    /// <summary>Model CLR type (long/double/string) of each Prolog variable and
    /// block-local, by name.</summary>
    public Dictionary<string, Type> Types { get; } = new();

    /// <summary>Prolog variables that the block assigns and must therefore unify
    /// back to their register on exit.</summary>
    public HashSet<string> ToUnify { get; } = new();

    public static NativeBlockTyping Compute(IReadOnlyList<NativeVar> vars,
        IReadOnlyList<CStmt> stmts, Func<string, MethodInfo?> resolve)
    {
        var t = new NativeBlockTyping();
        var varNames = new HashSet<string>(vars.Select(v => v.Name));
        foreach (var v in vars) t.Types[v.Name] = ModelType(v.Kind);
        foreach (var st in stmts)
        {
            switch (st)
            {
                case CVarDeclStmt d:
                    t.Types[d.Var] = ModelType(d.Type);
                    break;
                case CBindStmt b:
                    if (varNames.Contains(b.Var)) t.ToUnify.Add(b.Var);
                    if (!t.Types.ContainsKey(b.Var)) t.Types[b.Var] = t.TypeOfExpr(b.Value, resolve);
                    break;
                case CAssignStmt { Target: CIdentExpr id } a:
                    if (!t.Types.ContainsKey(id.Name)) t.Types[id.Name] = t.TypeOfExpr(a.Value, resolve);
                    break;
                case CCallStmt { Call: CCallExpr call }:
                    t.TypeIntrinsic(call, varNames, resolve);
                    break;
                default:
                    throw new NativeBlockBailException();
            }
        }
        return t;
    }

    // The string intrinsics introduce / type a local. A non-intrinsic call as a
    // statement has no typing effect (its result is discarded).
    private void TypeIntrinsic(CCallExpr call, HashSet<string> varNames, Func<string, MethodInfo?> resolve)
    {
        switch (call.Name)
        {
            case "MakeCString":
            {
                string buf = call.Args.OfType<CIdentExpr>().FirstOrDefault()?.Name
                    ?? throw new NativeBlockBailException();
                string str = StrArg(call) ?? throw new NativeBlockBailException();
                if (!Types.ContainsKey(buf))
                    Types[buf] = Types.TryGetValue(str, out var t) ? t : typeof(string);
                break;
            }
            case "MakePrologString":
            case "MakePrologStringEx":
            {
                string outVar = StrArg(call) ?? throw new NativeBlockBailException();
                if (varNames.Contains(outVar)) ToUnify.Add(outVar);
                if (!Types.ContainsKey(outVar))
                {
                    var src = call.Args.FirstOrDefault(a => a is not CAddrOfExpr);
                    Types[outVar] = src is null ? typeof(string) : TypeOfExpr(src, resolve);
                }
                break;
            }
        }
    }

    public Type TypeOfExpr(CExpr e, Func<string, MethodInfo?> resolve) => e switch
    {
        CIntExpr => typeof(long),
        CStringExpr => typeof(string),
        CIdentExpr id => Types.TryGetValue(id.Name, out var t) ? t : throw new NativeBlockBailException(),
        CBinaryExpr b =>
            TypeOfExpr(b.Left, resolve) == typeof(string) || TypeOfExpr(b.Right, resolve) == typeof(string)
                ? throw new NativeBlockBailException()
                : (TypeOfExpr(b.Left, resolve) == typeof(double) || TypeOfExpr(b.Right, resolve) == typeof(double)
                    ? typeof(double) : typeof(long)),
        CCallExpr c when !IsIntrinsic(c.Name) => ModelOf(ResolveOrBail(resolve, c.Name).ReturnType),
        _ => throw new NativeBlockBailException(),
    };

    // ----- shared helpers ----------------------------------------------------

    public static MethodInfo ResolveOrBail(Func<string, MethodInfo?> resolve, string name) =>
        resolve(name) ?? throw new NativeBlockBailException();

    public static bool IsIntrinsic(string name) =>
        name is "MakeCString" or "MakePrologString" or "MakePrologStringEx";

    public static string? StrArg(CCallExpr c) =>
        c.Args.OfType<CAddrOfExpr>()
            .Select(a => (a.Operand as CIdentExpr)?.Name)
            .FirstOrDefault(n => n is not null);

    public static Type ModelType(NativeKind k) => k switch
    {
        NativeKind.Int or NativeKind.Long => typeof(long),
        NativeKind.Float or NativeKind.Double => typeof(double),
        NativeKind.String => typeof(string),
        _ => throw new NativeBlockBailException(),
    };

    public static Type ModelType(CType t)
    {
        if (t.PointerDepth > 0) return typeof(string);      // char* etc.
        if (t.Name is "float" or "double") return typeof(double);
        if (t.Name is "void") throw new NativeBlockBailException();
        return typeof(long);                                 // scalar integer (C ints)
    }

    public static Type ModelOf(Type t)
    {
        if (t == typeof(string)) return typeof(string);
        if (t == typeof(float) || t == typeof(double)) return typeof(double);
        if (IsIntegral(t)) return typeof(long);
        throw new NativeBlockBailException();
    }

    public static bool IsIntegral(Type t) =>
        t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort)
        || t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong)
        || t == typeof(char);

    public static bool IsNumeric(Type t) => IsIntegral(t) || t == typeof(float) || t == typeof(double);
}
