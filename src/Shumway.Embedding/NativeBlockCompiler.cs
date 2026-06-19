using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Shumway.Compiler.Ast;
using Shumway.Compiler.NativeC;
using Shumway.Core;

namespace Shumway.Embedding;

/// <summary>ADR-022 item 2 — compiles a native block to a delegate via Expression
/// trees (which JIT to IL), removing the interpreter's per-call dictionaries,
/// boxing and tree-walk. It runs in engine context, so interop functions are
/// resolved to concrete <see cref="MethodInfo"/>s and called directly.
///
/// <para>The value model has three CLR types — <c>long</c> (every integer kind),
/// <c>double</c> (every floating kind) and <c>string</c> — mirroring the
/// interpreter's int/float/string tier. A typing pass assigns each Prolog
/// variable and block-local one of these; everything else (the term/reftype tier,
/// C control flow, unusual return types) makes the compiler <see cref="Bail"/>,
/// and <see cref="TryCompile"/> returns null so the caller falls back to the
/// interpreter. Under Native AOT (no runtime IL generation) it returns null
/// immediately.</para>
///
/// <para>The typed analysis here is the shared core the build-time inline path
/// (item 2 stage C) will reuse against a Sigil emit target.</para></summary>
public static class NativeBlockCompiler
{
    /// <summary>Thrown internally to abandon compilation of an unsupported
    /// construct; <see cref="TryCompile"/> turns it into a null result.</summary>
    private sealed class Bail : Exception { }

    private static readonly MethodInfo ReadReg =
        typeof(RegisterMarshalling).GetMethod(nameof(RegisterMarshalling.ReadRegisterAsTerm))!;
    private static readonly MethodInfo UnifyReg =
        typeof(RegisterMarshalling).GetMethod(nameof(RegisterMarshalling.UnifyRegisterWithTerm))!;
    private static readonly MethodInfo FromTermGeneric =
        typeof(PrologEngine).GetMethod(nameof(PrologEngine.FromTerm))!;
    private static readonly MethodInfo ToTermGeneric =
        typeof(PrologEngine).GetMethod(nameof(PrologEngine.ToTerm))!;
    private static readonly PropertyInfo HostProp =
        typeof(Engine).GetProperty(nameof(Engine.Host))!;
    private static readonly ConstructorInfo AtomTermCtor =
        typeof(AtomTerm).GetConstructor(new[] { typeof(string) })!;

    /// <summary>Compiles the block to a <c>Func&lt;Engine,bool&gt;</c> whose Prolog
    /// variables live in argument registers <paramref name="regOffset"/>.. (in
    /// <paramref name="vars"/> order). Returns null — fall back to the interpreter
    /// — when the block uses an unsupported construct or runtime IL generation is
    /// unavailable.</summary>
    public static Func<Engine, bool>? TryCompile(IReadOnlyList<NativeVar> vars,
        IReadOnlyList<CStmt> stmts, int regOffset, Func<string, MethodInfo?> resolve)
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
            return null;
        try
        {
            return new Builder(vars, stmts, regOffset, resolve).Compile();
        }
        catch (Bail)
        {
            return null;
        }
    }

    private sealed class Builder
    {
        private readonly IReadOnlyList<NativeVar> _vars;
        private readonly IReadOnlyList<CStmt> _stmts;
        private readonly int _regOffset;
        private readonly Func<string, MethodInfo?> _resolve;
        private readonly Dictionary<string, int> _varIndex = new();
        private readonly Dictionary<string, Type> _types = new();
        private readonly Dictionary<string, ParameterExpression> _locals = new();
        private readonly HashSet<string> _toUnify = new();
        private ParameterExpression _engine = null!;
        private ParameterExpression _host = null!;
        private LabelTarget _ret = null!;

        public Builder(IReadOnlyList<NativeVar> vars, IReadOnlyList<CStmt> stmts,
            int regOffset, Func<string, MethodInfo?> resolve)
        {
            _vars = vars; _stmts = stmts; _regOffset = regOffset; _resolve = resolve;
            for (int i = 0; i < vars.Count; i++) _varIndex[vars[i].Name] = i;
        }

        public Func<Engine, bool> Compile()
        {
            ComputeTypes();

            _engine = Expression.Parameter(typeof(Engine), "engine");
            _host = Expression.Variable(typeof(PrologEngine), "host");
            _ret = Expression.Label(typeof(bool), "ret");

            var locals = new List<ParameterExpression> { _host };
            var body = new List<Expression>
            {
                // host = (PrologEngine)engine.Host
                Expression.Assign(_host,
                    Expression.Convert(Expression.Property(_engine, HostProp), typeof(PrologEngine))),
            };
            foreach (var (name, type) in _types)
            {
                var local = Expression.Variable(type, name);
                _locals[name] = local;
                locals.Add(local);
            }

            // Marshal inputs (Prolog → typed local).
            foreach (var v in _vars)
                if (v.Mode == NativeMode.Input)
                    body.Add(Expression.Assign(_locals[v.Name], ReadInput(v)));

            // Run the statements.
            foreach (var st in _stmts)
                EmitStmt(st, body);

            // Marshal outputs (typed local → Prolog) + unify; a failed unify
            // returns false from the whole block.
            foreach (var v in _vars)
                if (_toUnify.Contains(v.Name))
                    body.Add(UnifyOutput(v));

            body.Add(Expression.Label(_ret, Expression.Constant(true)));
            var block = Expression.Block(typeof(bool), locals, body);
            return Expression.Lambda<Func<Engine, bool>>(block, _engine).Compile();
        }

        // ----- typing pass -------------------------------------------------

        private void ComputeTypes()
        {
            foreach (var v in _vars) _types[v.Name] = ModelType(v.Kind);
            foreach (var st in _stmts)
            {
                switch (st)
                {
                    case CVarDeclStmt d:
                        _types[d.Var] = ModelType(d.Type);
                        break;
                    case CBindStmt b:
                        if (_varIndex.ContainsKey(b.Var)) _toUnify.Add(b.Var);
                        if (!_types.ContainsKey(b.Var)) _types[b.Var] = TypeOfExpr(b.Value);
                        break;
                    case CAssignStmt { Target: CIdentExpr id }:
                        if (!_types.ContainsKey(id.Name))
                            _types[id.Name] = TypeOfExpr(((CAssignStmt)st).Value);
                        break;
                    case CCallStmt { Call: CCallExpr call }:
                        TypeIntrinsic(call);
                        break;
                    default:
                        throw new Bail();
                }
            }
        }

        // The string intrinsics introduce / type a local. Non-intrinsic calls as a
        // statement have no typing effect (their result is discarded).
        private void TypeIntrinsic(CCallExpr call)
        {
            switch (call.Name)
            {
                case "MakeCString":
                {
                    string buf = call.Args.OfType<CIdentExpr>().FirstOrDefault()?.Name
                        ?? throw new Bail();
                    string str = StrArg(call) ?? throw new Bail();
                    if (!_types.ContainsKey(buf))
                        _types[buf] = _types.TryGetValue(str, out var t) ? t : typeof(string);
                    break;
                }
                case "MakePrologString":
                case "MakePrologStringEx":
                {
                    string outVar = StrArg(call) ?? throw new Bail();
                    if (_varIndex.ContainsKey(outVar)) _toUnify.Add(outVar);
                    if (!_types.ContainsKey(outVar))
                    {
                        var src = call.Args.FirstOrDefault(a => a is not CAddrOfExpr);
                        _types[outVar] = src is null ? typeof(string) : TypeOfExpr(src);
                    }
                    break;
                }
                // a non-intrinsic interop call as a statement — no typing effect.
            }
        }

        private Type TypeOfExpr(CExpr e) => e switch
        {
            CIntExpr => typeof(long),
            CStringExpr => typeof(string),
            CIdentExpr id => _types.TryGetValue(id.Name, out var t) ? t : throw new Bail(),
            CBinaryExpr b =>
                TypeOfExpr(b.Left) == typeof(string) || TypeOfExpr(b.Right) == typeof(string)
                    ? throw new Bail()
                    : (TypeOfExpr(b.Left) == typeof(double) || TypeOfExpr(b.Right) == typeof(double)
                        ? typeof(double) : typeof(long)),
            CCallExpr c when !IsIntrinsic(c.Name) => ModelOf(Resolve(c).ReturnType),
            _ => throw new Bail(),
        };

        // ----- statement / expression emit ---------------------------------

        private void EmitStmt(CStmt st, List<Expression> body)
        {
            switch (st)
            {
                case CVarDeclStmt:
                    break;   // local already declared from the typing pass
                case CBindStmt b:
                {
                    var local = _locals[b.Var];
                    body.Add(Expression.Assign(local, Coerce(EmitExpr(b.Value), local.Type)));
                    break;
                }
                case CAssignStmt { Target: CIdentExpr id } a:
                {
                    var local = _locals[id.Name];
                    body.Add(Expression.Assign(local, Coerce(EmitExpr(a.Value), local.Type)));
                    break;
                }
                case CCallStmt { Call: CCallExpr call }:
                    EmitCallStmt(call, body);
                    break;
                default:
                    throw new Bail();
            }
        }

        private void EmitCallStmt(CCallExpr call, List<Expression> body)
        {
            switch (call.Name)
            {
                case "MakeCString":
                {
                    string buf = call.Args.OfType<CIdentExpr>().FirstOrDefault()?.Name
                        ?? throw new Bail();
                    string str = StrArg(call) ?? throw new Bail();
                    var dst = _locals[buf];
                    body.Add(Expression.Assign(dst, Coerce(_locals[str], dst.Type)));
                    break;
                }
                case "MakePrologString":
                case "MakePrologStringEx":
                {
                    string outVar = StrArg(call) ?? throw new Bail();
                    var src = call.Args.FirstOrDefault(a => a is not CAddrOfExpr) ?? throw new Bail();
                    var dst = _locals[outVar];
                    body.Add(Expression.Assign(dst, Coerce(EmitExpr(src), dst.Type)));
                    break;
                }
                default:
                    // A non-intrinsic interop call as a statement — its result is
                    // discarded (the Block pops it).
                    body.Add(EmitInteropCall(call));
                    break;
            }
        }

        private Expression EmitExpr(CExpr e) => e switch
        {
            CIntExpr n => Expression.Constant(n.Value),
            CStringExpr s => Expression.Constant(s.Value, typeof(string)),
            CIdentExpr id => _locals.TryGetValue(id.Name, out var l) ? l : throw new Bail(),
            CBinaryExpr b => EmitBinary(b.Op, EmitExpr(b.Left), EmitExpr(b.Right)),
            CCallExpr c when !IsIntrinsic(c.Name) => NormalizeToModel(EmitInteropCall(c), Resolve(c).ReturnType),
            _ => throw new Bail(),
        };

        private Expression EmitBinary(char op, Expression l, Expression r)
        {
            if (l.Type == typeof(string) || r.Type == typeof(string)) throw new Bail();
            var t = l.Type == typeof(double) || r.Type == typeof(double) ? typeof(double) : typeof(long);
            l = Coerce(l, t); r = Coerce(r, t);
            return op switch
            {
                '+' => Expression.Add(l, r),
                '-' => Expression.Subtract(l, r),
                '*' => Expression.Multiply(l, r),
                '/' => Expression.Divide(l, r),
                _ => throw new Bail(),
            };
        }

        private Expression EmitInteropCall(CCallExpr c)
        {
            var m = Resolve(c);
            var ps = m.GetParameters();
            if (c.Args.Count != ps.Length) throw new Bail();
            var args = new Expression[ps.Length];
            for (int i = 0; i < ps.Length; i++)
                args[i] = Coerce(EmitExpr(c.Args[i]), ps[i].ParameterType);
            return Expression.Call(m, args);
        }

        // ----- marshalling -------------------------------------------------

        private Expression ReadInput(NativeVar v)
        {
            int reg = _regOffset + _varIndex[v.Name];
            var term = Expression.Call(ReadReg, _engine, Expression.Constant(reg));
            return Expression.Call(_host, FromTermGeneric.MakeGenericMethod(ModelType(v.Kind)), term);
        }

        private Expression UnifyOutput(NativeVar v)
        {
            int reg = _regOffset + _varIndex[v.Name];
            var local = _locals[v.Name];
            Expression term = v.Kind == NativeKind.String
                ? Expression.New(AtomTermCtor, Coerce(local, typeof(string)))
                : Expression.Call(_host, ToTermGeneric.MakeGenericMethod(ModelType(v.Kind)),
                    Coerce(local, ModelType(v.Kind)));
            var unify = Expression.Call(UnifyReg, _engine, Expression.Constant(reg), term);
            return Expression.IfThen(Expression.Not(unify),
                Expression.Return(_ret, Expression.Constant(false)));
        }

        // ----- helpers -----------------------------------------------------

        private MethodInfo Resolve(CCallExpr c) =>
            _resolve(c.Name) ?? throw new Bail();   // unresolved interop → interpreter (it raises the hard error)

        private static bool IsIntrinsic(string name) =>
            name is "MakeCString" or "MakePrologString" or "MakePrologStringEx";

        private static string? StrArg(CCallExpr c) =>
            c.Args.OfType<CAddrOfExpr>()
                .Select(a => (a.Operand as CIdentExpr)?.Name)
                .FirstOrDefault(n => n is not null);

        /// <summary>Coerce a model-typed expression (long/double/string) to a
        /// target CLR type (a local's type or an interop parameter type).</summary>
        private static Expression Coerce(Expression e, Type target)
        {
            if (e.Type == target) return e;
            if (target == typeof(string)) return e.Type == typeof(string) ? e : throw new Bail();
            if (IsNumeric(target) && IsNumeric(e.Type)) return Expression.Convert(e, target);
            throw new Bail();
        }

        /// <summary>Wrap an interop call so its result is one of the model types
        /// (long/double/string) for use as a value.</summary>
        private static Expression NormalizeToModel(Expression call, Type returnType)
        {
            if (returnType == typeof(void)) throw new Bail();   // void can't be a value
            var model = ModelOf(returnType);
            return call.Type == model ? call : Expression.Convert(call, model);
        }

        private static Type ModelType(NativeKind k) => k switch
        {
            NativeKind.Int or NativeKind.Long => typeof(long),
            NativeKind.Float or NativeKind.Double => typeof(double),
            NativeKind.String => typeof(string),
            _ => throw new Bail(),
        };

        private static Type ModelType(CType t)
        {
            if (t.PointerDepth > 0) return typeof(string);      // char* etc.
            if (t.Name is "float" or "double") return typeof(double);
            if (t.Name is "void") throw new Bail();
            return typeof(long);                                 // scalar integer (C ints)
        }

        private static Type ModelOf(Type t)
        {
            if (t == typeof(string)) return typeof(string);
            if (t == typeof(float) || t == typeof(double)) return typeof(double);
            if (IsIntegral(t)) return typeof(long);
            throw new Bail();
        }

        private static bool IsIntegral(Type t) =>
            t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort)
            || t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong)
            || t == typeof(char);

        private static bool IsNumeric(Type t) => IsIntegral(t) || t == typeof(float) || t == typeof(double);
    }
}
