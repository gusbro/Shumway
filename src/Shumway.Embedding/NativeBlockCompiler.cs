using System;
using System.Collections.Generic;
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
/// <para>The value model and typing pass are shared with the build-time Sigil
/// inline emitter via <see cref="NativeBlockTyping"/> — three CLR types
/// (<c>long</c>/<c>double</c>/<c>string</c>, the int/float/string tier). Any
/// construct outside the tier makes the typing or emit <see cref="NativeBlockBailException">bail</see>,
/// and <see cref="TryCompile"/> returns null so the caller falls back to the
/// interpreter. Under Native AOT (no runtime IL generation) it returns null
/// immediately.</para></summary>
public static class NativeBlockCompiler
{
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
    // ADR-024 reftype tier:
    private static readonly MethodInfo GetOrCreateSlot =
        typeof(PrologEngine).GetMethod(nameof(PrologEngine.GetOrCreateReftypeSlot),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
    private static readonly MethodInfo MakeForeignM =
        typeof(Engine).GetMethod(nameof(Engine.MakeForeign))!;
    private static readonly MethodInfo UnifyRegCell =
        typeof(Engine).GetMethod(nameof(Engine.UnifyRegisterWithCell))!;
    private static readonly MethodInfo ReadSlotM =
        typeof(NativeBlockCompiler).GetMethod(nameof(ReadReftypeSlot))!;

    /// <summary>ADR-024 — the <see cref="TermSlot"/> a register holds (a Foreign
    /// cell, read as <c>'$foreign'(Id)</c>), or null. Shared by the
    /// Expression-compiled blocks (called from emitted IL).</summary>
    public static TermSlot? ReadReftypeSlot(Engine engine, int reg)
    {
        var t = RegisterMarshalling.ReadRegisterAsTerm(engine, reg);
        return t is CompoundTerm { Functor: "$foreign", Args.Length: 1 } ct
            && ct.Args[0] is IntTerm id
            ? engine.AsForeign<TermSlot>(Shumway.Core.Cell.Foreign((int)id.Value))
            : null;
    }

    /// <summary>Compiles the block to a <c>Func&lt;Engine,bool&gt;</c> whose Prolog
    /// variables live in argument registers <paramref name="regOffset"/>.. (in
    /// <paramref name="vars"/> order). Returns null — fall back to the interpreter
    /// — when the block uses an unsupported construct or runtime IL generation is
    /// unavailable.</summary>
    /// <summary>Number of native blocks compiled to a delegate (vs fell back to the
    /// interpreter). Test/diagnostic observability.</summary>
    public static int CompiledCount;

    /// <summary>Bench/diagnostic only: when true, never compile — force the
    /// interpreter fallback. Lets a microbench compare the two paths.</summary>
    public static bool ForceInterpreter;

    public static Func<Engine, bool>? TryCompile(IReadOnlyList<NativeVar> vars,
        IReadOnlyList<CStmt> stmts, int regOffset, Func<string, MethodInfo?> resolve)
    {
        if (ForceInterpreter) return null;
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
            return null;
        try
        {
            var del = new Builder(vars, stmts, regOffset, resolve).Compile();
            System.Threading.Interlocked.Increment(ref CompiledCount);
            return del;
        }
        catch (NativeBlockBailException)
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
        private readonly Dictionary<string, ParameterExpression> _locals = new();
        private Dictionary<string, Type> _types = null!;
        private HashSet<string> _toUnify = null!;
        private HashSet<string> _reftypeVars = null!;
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
            var typing = NativeBlockTyping.Compute(_vars, _stmts, _resolve);
            _types = typing.Types;
            _toUnify = typing.ToUnify;
            _reftypeVars = typing.ReftypeVars;

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
            // ADR-024 — reftype locals are TermSlot cursors.
            foreach (var name in _reftypeVars)
            {
                var local = Expression.Variable(typeof(TermSlot), name);
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
                    // ADR-024 — `H is buf` where H is a holder var and buf is a
                    // holder global: H = the global's slot.
                    if (_reftypeVars.Contains(b.Var) && b.Value is CIdentExpr hg
                        && !_locals.ContainsKey(hg.Name))
                    {
                        body.Add(Expression.Assign(local, EmitGetSlot(hg.Name)));
                        break;
                    }
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
                    throw new NativeBlockBailException();
            }
        }

        private void EmitCallStmt(CCallExpr call, List<Expression> body)
        {
            switch (call.Name)
            {
                case "MakeCString":
                {
                    string buf = call.Args.OfType<CIdentExpr>().FirstOrDefault()?.Name
                        ?? throw new NativeBlockBailException();
                    string str = NativeBlockTyping.StrArg(call) ?? throw new NativeBlockBailException();
                    var dst = _locals[buf];
                    body.Add(Expression.Assign(dst, Coerce(_locals[str], dst.Type)));
                    break;
                }
                case "MakePrologString":
                case "MakePrologStringEx":
                {
                    string outVar = NativeBlockTyping.StrArg(call) ?? throw new NativeBlockBailException();
                    var src = call.Args.FirstOrDefault(a => a is not CAddrOfExpr) ?? throw new NativeBlockBailException();
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
            // ADR-024 — `&name` is the reftype global's slot cursor.
            CAddrOfExpr { Operand: CIdentExpr g } => EmitGetSlot(g.Name),
            CIdentExpr id => _locals.TryGetValue(id.Name, out var l) ? l : throw new NativeBlockBailException(),
            CBinaryExpr b => EmitBinary(b.Op, EmitExpr(b.Left), EmitExpr(b.Right)),
            CCallExpr c when !NativeBlockTyping.IsIntrinsic(c.Name) =>
                NormalizeToModel(EmitInteropCall(c), NativeBlockTyping.ResolveOrBail(_resolve, c.Name).ReturnType),
            _ => throw new NativeBlockBailException(),
        };

        // host.GetOrCreateReftypeSlot(name) — the slot for a reftype global.
        private Expression EmitGetSlot(string name)
            => Expression.Call(_host, GetOrCreateSlot, Expression.Constant(name));

        private Expression EmitBinary(char op, Expression l, Expression r)
        {
            if (l.Type == typeof(string) || r.Type == typeof(string)) throw new NativeBlockBailException();
            var t = l.Type == typeof(double) || r.Type == typeof(double) ? typeof(double) : typeof(long);
            l = Coerce(l, t); r = Coerce(r, t);
            return op switch
            {
                '+' => Expression.Add(l, r),
                '-' => Expression.Subtract(l, r),
                '*' => Expression.Multiply(l, r),
                '/' => Expression.Divide(l, r),
                _ => throw new NativeBlockBailException(),
            };
        }

        private Expression EmitInteropCall(CCallExpr c)
        {
            var m = NativeBlockTyping.ResolveOrBail(_resolve, c.Name);
            var ps = m.GetParameters();
            if (c.Args.Count != ps.Length) throw new NativeBlockBailException();
            var args = new Expression[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                // ADR-024 — a TermSlot parameter receives a reftype global
                // (`'fn'(par1ref)` → its slot) or a reftype variable (a TermSlot
                // local).
                if (ps[i].ParameterType == typeof(TermSlot))
                {
                    if (c.Args[i] is CIdentExpr cid && _locals.TryGetValue(cid.Name, out var rl))
                        args[i] = rl;
                    else if (ReftypeArgName(c.Args[i]) is { } rn)
                        args[i] = EmitGetSlot(rn);
                    else
                        throw new NativeBlockBailException();
                    continue;
                }
                args[i] = Coerce(EmitExpr(c.Args[i]), ps[i].ParameterType);
            }
            return Expression.Call(m, args);
        }

        // ----- marshalling -------------------------------------------------

        private Expression ReadInput(NativeVar v)
        {
            int reg = _regOffset + _varIndex[v.Name];
            // ADR-024 — a reftype input is the slot handle (a Foreign cell).
            if (v.Kind == NativeKind.Reftype)
                return Expression.Call(ReadSlotM, _engine, Expression.Constant(reg));
            var term = Expression.Call(ReadReg, _engine, Expression.Constant(reg));
            return Expression.Call(_host,
                FromTermGeneric.MakeGenericMethod(NativeBlockTyping.ModelType(v.Kind)), term);
        }

        private Expression UnifyOutput(NativeVar v)
        {
            int reg = _regOffset + _varIndex[v.Name];
            var local = _locals[v.Name];
            // ADR-024 — a reftype output binds the register to the slot's Foreign
            // cell: engine.UnifyRegisterWithCell(reg, engine.MakeForeign(slot)).
            if (v.Kind == NativeKind.Reftype)
            {
                var foreign = Expression.Call(_engine, MakeForeignM,
                    Expression.Convert(local, typeof(object)));
                var unifyCell = Expression.Call(_engine, UnifyRegCell, Expression.Constant(reg), foreign);
                return Expression.IfThen(Expression.Not(unifyCell),
                    Expression.Return(_ret, Expression.Constant(false)));
            }
            Expression term = v.Kind == NativeKind.String
                ? Expression.New(AtomTermCtor, Coerce(local, typeof(string)))
                : Expression.Call(_host, ToTermGeneric.MakeGenericMethod(NativeBlockTyping.ModelType(v.Kind)),
                    Coerce(local, NativeBlockTyping.ModelType(v.Kind)));
            var unify = Expression.Call(UnifyReg, _engine, Expression.Constant(reg), term);
            return Expression.IfThen(Expression.Not(unify),
                Expression.Return(_ret, Expression.Constant(false)));
        }

        /// <summary>The reftype-global name an interop argument names (`par1ref` or
        /// `&amp;par1ref`), or null.</summary>
        private static string? ReftypeArgName(CExpr e) => e switch
        {
            CIdentExpr id => id.Name,
            CAddrOfExpr { Operand: CIdentExpr id } => id.Name,
            _ => null,
        };

        /// <summary>Coerce a model-typed expression (long/double/string) to a
        /// target CLR type (a local's type or an interop parameter type).</summary>
        private static Expression Coerce(Expression e, Type target)
        {
            if (e.Type == target) return e;
            if (target == typeof(string)) return e.Type == typeof(string) ? e : throw new NativeBlockBailException();
            if (NativeBlockTyping.IsNumeric(target) && NativeBlockTyping.IsNumeric(e.Type))
                return Expression.Convert(e, target);
            throw new NativeBlockBailException();
        }

        /// <summary>Wrap an interop call so its result is one of the model types
        /// (long/double/string) for use as a value.</summary>
        private static Expression NormalizeToModel(Expression call, Type returnType)
        {
            if (returnType == typeof(void)) throw new NativeBlockBailException();   // void can't be a value
            var model = NativeBlockTyping.ModelOf(returnType);
            return call.Type == model ? call : Expression.Convert(call, model);
        }
    }
}
