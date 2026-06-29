using System;
using System.Collections.Generic;
using System.Linq;
using Shumway.Compiler.NativeC;
using Shumway.Core;

namespace Shumway.Compiler.Il;

/// <summary>ADR-022 item 2 (stage C) — emits an embedded native block directly
/// into a predicate's IL at its <c>'$native_run'('$nb$…', regs)</c> call site,
/// instead of dispatching the builtin. The block's Prolog variables are in
/// argument registers 1.. (register 0 held the block-name atom). All emitted calls
/// are MemberRefs (interop functions + the marshalling handles supplied in the
/// <see cref="NativeInlineContext"/>), so a persisted-IL bundle needs no patch
/// entry — the CLR binds them at load.
///
/// <para>The typed analysis is shared with the runtime delegate compiler via
/// <see cref="NativeBlockTyping"/>. The walk runs twice: once with no emitter to
/// validate (any unsupported construct throws <see cref="NativeBlockBailException"/>
/// and the caller declines, leaving a normal builtin dispatch), then once for
/// real — so a half-emitted, corrupt method can never result.</para></summary>
internal static class NativeBlockInliner
{
    private static int _localSeq;

    public static bool TryEmit(Sigil.Emit<PredicateDelegate> emit, NativeInlineContext ctx,
        int regZeroAtom, Sigil.Label failLabel)
    {
        if (regZeroAtom < 0) return false;
        string? name = AtomTable.GetById(regZeroAtom)?.Name;
        if (name is null) return false;
        var block = ctx.BlockProvider(name);
        if (block is null) return false;

        NativeBlockTyping typing;
        try
        {
            typing = NativeBlockTyping.Compute(block.Vars, block.Stmts, ctx.InteropResolver);
            new Emitter(null, ctx, typing, block, failLabel, 0).Run();   // validate (no emit)
        }
        catch (NativeBlockBailException)
        {
            return false;
        }

        int salt = System.Threading.Interlocked.Increment(ref _localSeq);
        new Emitter(emit, ctx, typing, block, failLabel, salt).Run();    // emit for real
        return true;
    }

    /// <summary>ADR-024 fusion — <c>fill_par(Term, RefType)</c> inline: the slot is
    /// in register 1 (a Foreign cell), the term in register 0. Emits
    /// <c>ReadReftypeSlot(engine, 1).SetValue(ReadRegisterAsTerm(engine, 0))</c>.
    /// Returns false (decline) when the reftype handles aren't present.</summary>
    public static bool TryEmitFillPar(Sigil.Emit<PredicateDelegate> emit, NativeInlineContext ctx)
    {
        if (!ctx.HasReftype) return false;
        emit.LoadArgument(0);
        emit.LoadConstant(1);
        emit.Call(ctx.ReadReftypeSlot!);            // slot
        emit.LoadArgument(0);
        emit.LoadConstant(0);
        emit.Call(ctx.ReadRegisterAsTerm);          // term
        emit.Call(ctx.SlotSetValue!);               // slot.SetValue(term)
        return true;
    }

    /// <summary>ADR-024 fusion — <c>reftype_term(Term, RefType)</c> inline: emits
    /// <c>UnifyRegisterWithTerm(engine, 0, ReadReftypeSlot(engine, 1).Materialize())</c>,
    /// branching to <paramref name="failLabel"/> on a failed unify.</summary>
    public static bool TryEmitReftypeTerm(Sigil.Emit<PredicateDelegate> emit,
        NativeInlineContext ctx, Sigil.Label failLabel)
    {
        if (!ctx.HasReftype) return false;
        emit.LoadArgument(0);                       // engine (for Unify)
        emit.LoadConstant(0);                       // reg 0
        emit.LoadArgument(0);
        emit.LoadConstant(1);
        emit.Call(ctx.ReadReftypeSlot!);            // slot
        emit.Call(ctx.SlotMaterialize!);            // slot.Materialize() -> Term
        emit.Call(ctx.UnifyRegisterWithTerm);       // -> bool
        emit.BranchIfFalse(failLabel);
        return true;
    }

    private sealed class Emitter
    {
        private readonly Sigil.Emit<PredicateDelegate>? _emit;   // null = validate-only
        private readonly NativeInlineContext _ctx;
        private readonly NativeBlockTyping _typing;
        private readonly NativeBlockBody _block;
        private readonly Sigil.Label _fail;
        private readonly int _salt;
        private readonly Dictionary<string, int> _varIndex = new();
        private readonly Dictionary<string, Sigil.Local> _locals = new();
        private readonly Dictionary<string, bool> _scalarFloat = new();   // ADR-022 scalar globals

        public Emitter(Sigil.Emit<PredicateDelegate>? emit, NativeInlineContext ctx,
            NativeBlockTyping typing, NativeBlockBody block, Sigil.Label fail, int salt)
        {
            _emit = emit; _ctx = ctx; _typing = typing; _block = block; _fail = fail; _salt = salt;
            for (int i = 0; i < block.Vars.Length; i++) _varIndex[block.Vars[i].Name] = i;
            foreach (var g in block.ScalarGlobals) _scalarFloat[g.Name] = g.IsFloat;
        }

        // Prolog variable i is in argument register 1 + i (register 0 held the
        // block-name atom).
        private int Reg(string name) => 1 + _varIndex[name];

        public void Run()
        {
            // ADR-024 — reftype inlining needs the host-supplied handles; without
            // them, decline (the block runs via the delegate/interpreter).
            if (_typing.ReftypeVars.Count > 0 && !_ctx.HasReftype)
                throw new NativeBlockBailException();

            // ADR-022 — a scalar `:- c` global's local is typed from its declared C
            // kind, so seed/flush and the persistent storage agree.
            foreach (var g in _block.ScalarGlobals)
                _typing.Types[g.Name] = g.IsFloat ? typeof(double) : typeof(long);

            if (_emit is not null)
            {
                foreach (var (n, t) in _typing.Types)
                    _locals[n] = _emit.DeclareLocal(t, $"nb_{Sanitize(n)}_{_salt}");
                foreach (var n in _typing.ReftypeVars)
                    _locals[n] = _emit.DeclareLocal(_ctx.TermSlotType!, $"nb_{Sanitize(n)}_{_salt}");
            }

            foreach (var v in _block.Vars)
                if (v.Mode == NativeMode.Input)
                    EmitReadInput(v);

            // ADR-022 — seed each scalar global from per-engine persistent storage.
            foreach (var g in _block.ScalarGlobals)
                EmitSeedScalarGlobal(g);

            foreach (var st in _block.Stmts)
                EmitStmt(st);

            foreach (var v in _block.Vars)
                if (_typing.ToUnify.Contains(v.Name))
                    EmitUnifyOutput(v);
        }

        // ----- input / output marshalling ----------------------------------

        private void EmitReadInput(NativeVar v)
        {
            if (_emit is null) return;
            // ADR-024 — a reftype input is the slot handle (a Foreign cell).
            if (v.Kind == NativeKind.Reftype)
            {
                _emit.LoadArgument(0);
                _emit.LoadConstant(Reg(v.Name));
                _emit.Call(_ctx.ReadReftypeSlot!);
                _emit.StoreLocal(_locals[v.Name]);
                return;
            }
            var model = NativeBlockTyping.ModelType(v.Kind);
            // host = (PrologEngine)engine.Host
            _emit.LoadArgument(0);
            _emit.Call(_ctx.HostGetter);
            _emit.CastClass(_ctx.HostType);
            // term = RegisterMarshalling.ReadRegisterAsTerm(engine, reg)
            _emit.LoadArgument(0);
            _emit.LoadConstant(Reg(v.Name));
            _emit.Call(_ctx.ReadRegisterAsTerm);
            // local = host.FromTerm<model>(term)
            _emit.Call(_ctx.FromTermFor(model));
            _emit.StoreLocal(_locals[v.Name]);
        }

        private void EmitUnifyOutput(NativeVar v)
        {
            if (_emit is null) return;
            // ADR-024 — a reftype output binds the register to the slot's Foreign
            // cell: engine.UnifyRegisterWithCell(reg, engine.MakeForeign(slot)).
            if (v.Kind == NativeKind.Reftype)
            {
                _emit.LoadArgument(0);                       // engine (for Unify)
                _emit.LoadConstant(Reg(v.Name));
                _emit.LoadArgument(0);                       // engine (for MakeForeign)
                _emit.LoadLocal(_locals[v.Name]);            // slot (a TermSlot, an object)
                _emit.Call(_ctx.MakeForeign!);               // -> Cell
                _emit.Call(_ctx.UnifyRegisterWithCell!);     // -> bool
                _emit.BranchIfFalse(_fail);
                return;
            }
            var model = NativeBlockTyping.ModelType(v.Kind);
            // engine, reg, <term>  ->  RegisterMarshalling.UnifyRegisterWithTerm
            _emit.LoadArgument(0);
            _emit.LoadConstant(Reg(v.Name));
            if (v.Kind == NativeKind.String)
            {
                _emit.LoadLocal(_locals[v.Name]);
                _emit.NewObject(_ctx.AtomTermCtor);          // new AtomTerm(string)
            }
            else
            {
                _emit.LoadArgument(0);
                _emit.Call(_ctx.HostGetter);
                _emit.CastClass(_ctx.HostType);
                _emit.LoadLocal(_locals[v.Name]);
                _emit.Call(_ctx.ToTermFor(model));           // host.ToTerm<model>(value)
            }
            _emit.Call(_ctx.UnifyRegisterWithTerm);
            _emit.BranchIfFalse(_fail);
        }

        // ----- statements ---------------------------------------------------

        private void EmitStmt(CStmt st)
        {
            switch (st)
            {
                case CVarDeclStmt:
                    break;
                case CBindStmt b:
                    EmitAssign(b.Var, b.Value);
                    break;
                case CAssignStmt { Target: CIdentExpr id } a:
                    EmitAssign(id.Name, a.Value);
                    break;
                case CCallStmt { Call: CCallExpr call }:
                    EmitCallStmt(call);
                    break;
                default:
                    throw new NativeBlockBailException();
            }
        }

        private void EmitAssign(string target, CExpr value)
        {
            Type targetType = _typing.ReftypeVars.Contains(target)
                ? _ctx.TermSlotType! : _typing.Types[target];
            // ADR-024 — `H is buf` where H is a holder var and buf a holder global:
            // H = the global's slot.
            if (_typing.ReftypeVars.Contains(target) && value is CIdentExpr hg
                && !_typing.ReftypeVars.Contains(hg.Name) && !_typing.Types.ContainsKey(hg.Name))
            {
                EmitGetSlot(hg.Name);
                _emit?.StoreLocal(_locals[target]);
                return;
            }
            var srcType = EmitExpr(value);
            Coerce(srcType, targetType);
            _emit?.StoreLocal(_locals[target]);
            // ADR-022 — write-through to persistent storage for a scalar global.
            if (_scalarFloat.TryGetValue(target, out bool isFloat) && _emit is not null)
            {
                _emit.LoadArgument(0);
                _emit.Call(_ctx.HostGetter);
                _emit.CastClass(_ctx.HostType);
                _emit.LoadConstant(target);
                _emit.LoadLocal(_locals[target]);
                _emit.Call(isFloat ? _ctx.SetNativeGlobalFloat : _ctx.SetNativeGlobalInt);
            }
        }

        // ADR-022 — local = host.GetNativeGlobal{Int,Float}(name).
        private void EmitSeedScalarGlobal(NativeScalarGlobal g)
        {
            if (_emit is null) return;
            _emit.LoadArgument(0);
            _emit.Call(_ctx.HostGetter);
            _emit.CastClass(_ctx.HostType);
            _emit.LoadConstant(g.Name);
            _emit.Call(g.IsFloat ? _ctx.GetNativeGlobalFloat : _ctx.GetNativeGlobalInt);
            _emit.StoreLocal(_locals[g.Name]);
        }

        // host.GetOrCreateReftypeSlot(name) — leaves a TermSlot on the stack.
        private void EmitGetSlot(string name)
        {
            if (_emit is null) return;
            _emit.LoadArgument(0);
            _emit.Call(_ctx.HostGetter);
            _emit.CastClass(_ctx.HostType);
            _emit.LoadConstant(name);
            _emit.Call(_ctx.GetOrCreateReftypeSlot!);
        }

        private static string? ReftypeArgName(CExpr e) => e switch
        {
            CIdentExpr id => id.Name,
            CAddrOfExpr { Operand: CIdentExpr id } => id.Name,
            _ => null,
        };

        private void EmitCallStmt(CCallExpr call)
        {
            switch (call.Name)
            {
                case "MakeCString":
                {
                    string buf = call.Args.OfType<CIdentExpr>().FirstOrDefault()?.Name
                        ?? throw new NativeBlockBailException();
                    string str = NativeBlockTyping.StrArg(call) ?? throw new NativeBlockBailException();
                    if (!_typing.Types.ContainsKey(buf) || !_typing.Types.ContainsKey(str))
                        throw new NativeBlockBailException();
                    if (_emit is not null)
                    {
                        _emit.LoadLocal(_locals[str]);
                        _emit.StoreLocal(_locals[buf]);
                    }
                    break;
                }
                case "MakePrologString":
                case "MakePrologStringEx":
                {
                    string outVar = NativeBlockTyping.StrArg(call) ?? throw new NativeBlockBailException();
                    var src = call.Args.FirstOrDefault(a => a is not CAddrOfExpr)
                        ?? throw new NativeBlockBailException();
                    if (!_typing.Types.ContainsKey(outVar)) throw new NativeBlockBailException();
                    var srcType = EmitExpr(src);
                    Coerce(srcType, _typing.Types[outVar]);
                    _emit?.StoreLocal(_locals[outVar]);
                    break;
                }
                default:
                {
                    var ret = EmitInteropCall(call);          // statement: discard the result
                    if (_emit is not null && ret != typeof(void)) _emit.Pop();
                    break;
                }
            }
        }

        // ----- expressions (returns the model CLR type) ---------------------

        private Type EmitExpr(CExpr e)
        {
            switch (e)
            {
                case CIntExpr n:
                    _emit?.LoadConstant(n.Value);
                    return typeof(long);
                case CStringExpr s:
                    _emit?.LoadConstant(s.Value);
                    return typeof(string);
                // ADR-024 — `&name` is the reftype global's slot cursor.
                case CAddrOfExpr { Operand: CIdentExpr g }:
                    EmitGetSlot(g.Name);
                    return _ctx.TermSlotType!;
                case CIdentExpr id when _typing.ReftypeVars.Contains(id.Name):
                    _emit?.LoadLocal(_locals[id.Name]);
                    return _ctx.TermSlotType!;
                case CIdentExpr id:
                    if (!_typing.Types.TryGetValue(id.Name, out var t)) throw new NativeBlockBailException();
                    _emit?.LoadLocal(_locals[id.Name]);
                    return t;
                case CBinaryExpr b:
                    return EmitBinary(b);
                case CCallExpr c when !NativeBlockTyping.IsIntrinsic(c.Name):
                {
                    var ret = EmitInteropCall(c);
                    if (ret == typeof(void)) throw new NativeBlockBailException();
                    var model = NativeBlockTyping.ModelOf(ret);
                    if (_emit is not null && ret != model) _emit.Convert(model);
                    return model;
                }
                default:
                    throw new NativeBlockBailException();
            }
        }

        private Type EmitBinary(CBinaryExpr b)
        {
            var tL = _typing.TypeOfExpr(b.Left, _ctx.InteropResolver);
            var tR = _typing.TypeOfExpr(b.Right, _ctx.InteropResolver);
            if (tL == typeof(string) || tR == typeof(string)) throw new NativeBlockBailException();
            var result = tL == typeof(double) || tR == typeof(double) ? typeof(double) : typeof(long);

            Coerce(EmitExpr(b.Left), result);
            Coerce(EmitExpr(b.Right), result);
            if (_emit is not null)
                switch (b.Op)
                {
                    case '+': _emit.Add(); break;
                    case '-': _emit.Subtract(); break;
                    case '*': _emit.Multiply(); break;
                    case '/': _emit.Divide(); break;
                    default: throw new NativeBlockBailException();
                }
            else if (b.Op is not ('+' or '-' or '*' or '/'))
                throw new NativeBlockBailException();
            return result;
        }

        // Resolves + emits an interop call (args coerced to the parameter types);
        // returns the method's RAW return type (the caller normalizes / discards).
        private Type EmitInteropCall(CCallExpr c)
        {
            var m = NativeBlockTyping.ResolveOrBail(_ctx.InteropResolver, c.Name);
            var ps = m.GetParameters();
            if (c.Args.Count != ps.Length) throw new NativeBlockBailException();
            for (int i = 0; i < ps.Length; i++)
            {
                // ADR-024 — a TermSlot parameter receives a reftype variable (a
                // local) or a reftype global (resolved to its slot).
                if (_ctx.TermSlotType is not null && ps[i].ParameterType == _ctx.TermSlotType)
                {
                    if (c.Args[i] is CIdentExpr cid && _typing.ReftypeVars.Contains(cid.Name))
                        _emit?.LoadLocal(_locals[cid.Name]);
                    else if (ReftypeArgName(c.Args[i]) is { } rn)
                        EmitGetSlot(rn);
                    else
                        throw new NativeBlockBailException();
                    continue;
                }
                Coerce(EmitExpr(c.Args[i]), ps[i].ParameterType);
            }
            _emit?.Call(m);
            return m.ReturnType;
        }

        // Coerce the value on the stack from its model type to a target CLR type
        // (a local's type or an interop parameter type).
        private void Coerce(Type from, Type to)
        {
            if (from == to) return;
            if (to == typeof(string))
            {
                if (from != typeof(string)) throw new NativeBlockBailException();
                return;
            }
            if (NativeBlockTyping.IsNumeric(to) && NativeBlockTyping.IsNumeric(from))
            {
                _emit?.Convert(to);
                return;
            }
            throw new NativeBlockBailException();
        }

        private static string Sanitize(string name)
        {
            var chars = name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
            return new string(chars);
        }
    }
}
