using System.Reflection;
using System.Reflection.Emit;
using System.Text;

namespace Shumway.Compiler.Il;

/// <summary>A label of an <see cref="IlEmit"/> method.</summary>
public sealed class IlLabel
{
    internal readonly Label Label;
    internal bool Marked, Used;

    public string Name { get; }

    internal IlLabel(Label label, string name)
    {
        Label = label;
        Name = name;
    }

    public override string ToString() => Name;
}

/// <summary>A local of an <see cref="IlEmit"/> method.</summary>
public sealed class IlLocal
{
    internal readonly LocalBuilder Builder;

    public string Name { get; }
    public Type LocalType => Builder.LocalType;
    public int Index => Builder.LocalIndex;

    internal IlLocal(LocalBuilder builder, string name)
    {
        Builder = builder;
        Name = name;
    }

    public override string ToString() => Name;
}

/// <summary>An <see cref="IlEmit"/> check failed: the method's IL would be
/// invalid.</summary>
public sealed class IlEmitException : InvalidOperationException
{
    /// <summary>The instructions emitted so far; empty unless recorded.</summary>
    public string Instructions { get; }

    public IlEmitException(string message, string instructions) : base(message)
        => Instructions = instructions;
}

/// <summary>
/// ADR-062: Tier-1's IL emitter, over <see cref="ILGenerator"/>, for methods of
/// the <see cref="PredicateDelegate"/> signature. Each instruction goes to the
/// generator as it comes, but a call, which waits for the next one: a call that
/// the method's <c>ret</c> follows gets the <c>tail.</c> prefix when the
/// callee allows it (see <c>TailCallable</c>). It checks that a label is
/// marked at most once, that a label used is marked, and that no instruction
/// follows an unconditional transfer without a label; it does not track the
/// types on the stack.
/// </summary>
public sealed class IlEmit
{
    private static readonly Type ReturnType = typeof(bool);
    private static readonly Type[] ParameterTypes =
        typeof(PredicateDelegate).GetMethod("Invoke")!.GetParameters().Select(p => p.ParameterType).ToArray();

    // Dynamic methods live in a module of their own.
    private static readonly Module DynamicModule =
        AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Shumway.Il.DynamicMethods"), AssemblyBuilderAccess.Run)
            .DefineDynamicModule("DynamicMethods");

    private readonly ILGenerator _il;
    private readonly DynamicMethod? _dynamic;
    private readonly MethodBuilder? _method;
    private readonly StringBuilder? _text;
    private readonly List<IlLabel> _labels = new();
    private int _localSeq, _labelSeq;
    // After a br, ret or throw, until a label: nothing can reach what follows.
    private bool _unreachable;
    // The call that waits for the next instruction.
    private OpCode _pendingOp;
    private MethodInfo? _pendingMethod;
    // A method this emitter builds has the delegate's signature: it can be a
    // tail call, and a MethodBuilder answers no reflection before its type exists.
    private bool _pendingBuilt;
    private int _pendingTextAt;

    private IlEmit(ILGenerator il, DynamicMethod? dynamic, MethodBuilder? method, bool record)
    {
        _il = il;
        _dynamic = dynamic;
        _method = method;
        _text = record ? new StringBuilder() : null;
    }

    /// <summary>A method of its own (a <see cref="DynamicMethod"/>), which may
    /// reach any member.</summary>
    public static IlEmit NewDynamicMethod(string name, bool record)
    {
        var dm = new DynamicMethod(name, ReturnType, ParameterTypes, DynamicModule, skipVisibility: true);
        return new IlEmit(dm.GetILGenerator(), dm, null, record);
    }

    /// <summary>A static method of <paramref name="type"/>.</summary>
    public static IlEmit BuildMethod(TypeBuilder type, string name, MethodAttributes attributes,
        CallingConventions callingConvention, bool record)
    {
        var mb = type.DefineMethod(name, attributes, callingConvention, ReturnType, ParameterTypes);
        return new IlEmit(mb.GetILGenerator(), null, mb, record);
    }

    /// <summary>The method's instructions as text; empty unless recorded.</summary>
    public string Instructions() => _text?.ToString() ?? "";

    /// <summary>The IL bytes emitted so far, a pending call apart.</summary>
    public int Size => _il.ILOffset;

    /// <summary>The labels declared and not yet marked.</summary>
    public IReadOnlyList<IlLabel> UnmarkedLabels => _labels.Where(l => !l.Marked).ToList();

    // ---- labels and locals ----

    public IlLabel DefineLabel(string? name = null)
    {
        var l = new IlLabel(_il.DefineLabel(), name ?? $"_label{_labelSeq++}");
        _labels.Add(l);
        return l;
    }

    public void MarkLabel(IlLabel label)
    {
        Flush();
        if (label.Marked)
            throw Fail($"label [{label.Name}] has already been marked, and cannot be marked a second time");
        label.Marked = true;
        _il.MarkLabel(label.Label);
        _unreachable = false;
        _text?.Append(label.Name).Append(":\n");
    }

    public IlLocal DeclareLocal<T>(string? name = null) => DeclareLocal(typeof(T), name);

    public IlLocal DeclareLocal(Type type, string? name = null)
        => new(_il.DeclareLocal(type), name ?? $"_local{_localSeq++}");

    // ---- the instruction stream ----

    private void Begin()
    {
        Flush();
        if (_unreachable) throw Fail("Unreachable code detected");
    }

    private IlEmitException Fail(string message) => new(message, Instructions());

    private void Text(string op, string? operand = null)
    {
        if (_text is null) return;
        _text.Append(op);
        if (operand is not null) _text.Append(' ').Append(operand);
        _text.Append('\n');
    }

    private void Op(OpCode op)
    {
        Begin();
        _il.Emit(op);
        Text(op.Name!);
    }

    private void Flush()
    {
        if (_pendingMethod is null) return;
        _il.Emit(_pendingOp, _pendingMethod);
        _pendingMethod = null;
    }

    // ---- arguments, locals, constants ----

    public void LoadArgument(int index)
    {
        Begin();
        switch (index)
        {
            case 0: _il.Emit(OpCodes.Ldarg_0); Text("ldarg.0"); return;
            case 1: _il.Emit(OpCodes.Ldarg_1); Text("ldarg.1"); return;
            case 2: _il.Emit(OpCodes.Ldarg_2); Text("ldarg.2"); return;
            case 3: _il.Emit(OpCodes.Ldarg_3); Text("ldarg.3"); return;
        }
        if (index <= 255) { _il.Emit(OpCodes.Ldarg_S, (byte)index); Text("ldarg.s", index.ToString()); }
        else { _il.Emit(OpCodes.Ldarg, (short)index); Text("ldarg", index.ToString()); }
    }

    public void StoreArgument(int index)
    {
        Begin();
        if (index <= 255) { _il.Emit(OpCodes.Starg_S, (byte)index); Text("starg.s", index.ToString()); }
        else { _il.Emit(OpCodes.Starg, (short)index); Text("starg", index.ToString()); }
    }

    private static string LocalText(IlLocal l) => $"// {l.LocalType.FullName} {l.Name}";

    public void LoadLocal(IlLocal local)
    {
        Begin();
        int i = local.Index;
        switch (i)
        {
            case 0: _il.Emit(OpCodes.Ldloc_0); Text("ldloc.0", LocalText(local)); return;
            case 1: _il.Emit(OpCodes.Ldloc_1); Text("ldloc.1", LocalText(local)); return;
            case 2: _il.Emit(OpCodes.Ldloc_2); Text("ldloc.2", LocalText(local)); return;
            case 3: _il.Emit(OpCodes.Ldloc_3); Text("ldloc.3", LocalText(local)); return;
        }
        if (i <= 255) { _il.Emit(OpCodes.Ldloc_S, (byte)i); Text("ldloc.s", $"{i} {LocalText(local)}"); }
        else { _il.Emit(OpCodes.Ldloc, (short)i); Text("ldloc", $"{i} {LocalText(local)}"); }
    }

    public void StoreLocal(IlLocal local)
    {
        Begin();
        int i = local.Index;
        switch (i)
        {
            case 0: _il.Emit(OpCodes.Stloc_0); Text("stloc.0", LocalText(local)); return;
            case 1: _il.Emit(OpCodes.Stloc_1); Text("stloc.1", LocalText(local)); return;
            case 2: _il.Emit(OpCodes.Stloc_2); Text("stloc.2", LocalText(local)); return;
            case 3: _il.Emit(OpCodes.Stloc_3); Text("stloc.3", LocalText(local)); return;
        }
        if (i <= 255) { _il.Emit(OpCodes.Stloc_S, (byte)i); Text("stloc.s", $"{i} {LocalText(local)}"); }
        else { _il.Emit(OpCodes.Stloc, (short)i); Text("stloc", $"{i} {LocalText(local)}"); }
    }

    public void LoadLocalAddress(IlLocal local)
    {
        Begin();
        int i = local.Index;
        if (i <= 255) { _il.Emit(OpCodes.Ldloca_S, (byte)i); Text("ldloca.s", i.ToString()); }
        else { _il.Emit(OpCodes.Ldloca, (short)i); Text("ldloca", i.ToString()); }
    }

    public void LoadConstant(int value)
    {
        Begin();
        switch (value)
        {
            case -1: _il.Emit(OpCodes.Ldc_I4_M1); Text("ldc.i4.m1"); return;
            case 0: _il.Emit(OpCodes.Ldc_I4_0); Text("ldc.i4.0"); return;
            case 1: _il.Emit(OpCodes.Ldc_I4_1); Text("ldc.i4.1"); return;
            case 2: _il.Emit(OpCodes.Ldc_I4_2); Text("ldc.i4.2"); return;
            case 3: _il.Emit(OpCodes.Ldc_I4_3); Text("ldc.i4.3"); return;
            case 4: _il.Emit(OpCodes.Ldc_I4_4); Text("ldc.i4.4"); return;
            case 5: _il.Emit(OpCodes.Ldc_I4_5); Text("ldc.i4.5"); return;
            case 6: _il.Emit(OpCodes.Ldc_I4_6); Text("ldc.i4.6"); return;
            case 7: _il.Emit(OpCodes.Ldc_I4_7); Text("ldc.i4.7"); return;
            case 8: _il.Emit(OpCodes.Ldc_I4_8); Text("ldc.i4.8"); return;
        }
        if (value >= sbyte.MinValue && value <= sbyte.MaxValue)
        {
            _il.Emit(OpCodes.Ldc_I4_S, (sbyte)value);
            Text("ldc.i4.s", value.ToString());
        }
        else
        {
            _il.Emit(OpCodes.Ldc_I4, value);
            Text("ldc.i4", value.ToString());
        }
    }

    public void LoadConstant(uint value) => LoadConstant(unchecked((int)value));

    public void LoadConstant(bool value) => LoadConstant(value ? 1 : 0);

    public void LoadConstant(long value)
    {
        Begin();
        _il.Emit(OpCodes.Ldc_I8, value);
        Text("ldc.i8", value.ToString());
    }

    public void LoadConstant(ulong value) => LoadConstant(unchecked((long)value));

    public void LoadConstant(double value)
    {
        Begin();
        _il.Emit(OpCodes.Ldc_R8, value);
        Text("ldc.r8", value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public void LoadConstant(string value)
    {
        Begin();
        _il.Emit(OpCodes.Ldstr, value);
        Text("ldstr", $"'{value}'");
    }

    // ---- fields, elements, conversions ----

    public void LoadField(FieldInfo field)
    {
        Begin();
        var op = field.IsStatic ? OpCodes.Ldsfld : OpCodes.Ldfld;
        _il.Emit(op, field);
        Text(op.Name!, field.ToString());
    }

    public void StoreField(FieldInfo field)
    {
        Begin();
        var op = field.IsStatic ? OpCodes.Stsfld : OpCodes.Stfld;
        _il.Emit(op, field);
        Text(op.Name!, field.ToString());
    }

    public void LoadElement<T>() => LoadElement(typeof(T));

    public void LoadElement(Type t)
    {
        Begin();
        if (!t.IsValueType) { _il.Emit(OpCodes.Ldelem_Ref); Text("ldelem.ref"); }
        else if (t == typeof(int)) { _il.Emit(OpCodes.Ldelem_I4); Text("ldelem.i4"); }
        else if (t == typeof(long)) { _il.Emit(OpCodes.Ldelem_I8); Text("ldelem.i8"); }
        else if (t == typeof(byte)) { _il.Emit(OpCodes.Ldelem_U1); Text("ldelem.u1"); }
        else { _il.Emit(OpCodes.Ldelem, t); Text("ldelem", t.ToString()); }
    }

    public void StoreElement<T>() => StoreElement(typeof(T));

    public void StoreElement(Type t)
    {
        Begin();
        if (!t.IsValueType) { _il.Emit(OpCodes.Stelem_Ref); Text("stelem.ref"); }
        else if (t == typeof(int)) { _il.Emit(OpCodes.Stelem_I4); Text("stelem.i4"); }
        else if (t == typeof(long)) { _il.Emit(OpCodes.Stelem_I8); Text("stelem.i8"); }
        else if (t == typeof(byte)) { _il.Emit(OpCodes.Stelem_I1); Text("stelem.i1"); }
        else { _il.Emit(OpCodes.Stelem, t); Text("stelem", t.ToString()); }
    }

    public void LoadLength<T>() => Op(OpCodes.Ldlen);

    public void Convert<T>() => Convert(typeof(T));

    public void Convert(Type t)
    {
        Op(t == typeof(int) ? OpCodes.Conv_I4
            : t == typeof(long) ? OpCodes.Conv_I8
            : t == typeof(uint) ? OpCodes.Conv_U4
            : t == typeof(ulong) ? OpCodes.Conv_U8
            : t == typeof(short) ? OpCodes.Conv_I2
            : t == typeof(ushort) ? OpCodes.Conv_U2
            : t == typeof(sbyte) ? OpCodes.Conv_I1
            : t == typeof(byte) ? OpCodes.Conv_U1
            : t == typeof(double) ? OpCodes.Conv_R8
            : t == typeof(float) ? OpCodes.Conv_R4
            : t == typeof(nint) ? OpCodes.Conv_I
            : t == typeof(nuint) ? OpCodes.Conv_U
            : throw new NotSupportedException($"Convert<{t}>"));
    }

    // ---- arithmetic and the stack ----

    public void Add() => Op(OpCodes.Add);
    public void Subtract() => Op(OpCodes.Sub);
    public void Multiply() => Op(OpCodes.Mul);
    public void Divide() => Op(OpCodes.Div);
    public void Negate() => Op(OpCodes.Neg);
    public void And() => Op(OpCodes.And);
    public void Or() => Op(OpCodes.Or);
    public void Xor() => Op(OpCodes.Xor);
    public void ShiftLeft() => Op(OpCodes.Shl);
    public void ShiftRight() => Op(OpCodes.Shr);
    public void UnsignedShiftRight() => Op(OpCodes.Shr_Un);
    public void CompareEqual() => Op(OpCodes.Ceq);
    public void Duplicate() => Op(OpCodes.Dup);
    public void Pop() => Op(OpCodes.Pop);

    // ---- branches ----

    private void BranchOp(OpCode op, IlLabel label)
    {
        Begin();
        label.Used = true;
        _il.Emit(op, label.Label);
        Text(op.Name!, label.Name);
    }

    public void Branch(IlLabel label)
    {
        BranchOp(OpCodes.Br, label);
        _unreachable = true;
    }

    public void BranchIfTrue(IlLabel label) => BranchOp(OpCodes.Brtrue, label);
    public void BranchIfFalse(IlLabel label) => BranchOp(OpCodes.Brfalse, label);
    public void BranchIfEqual(IlLabel label) => BranchOp(OpCodes.Beq, label);
    public void BranchIfGreater(IlLabel label) => BranchOp(OpCodes.Bgt, label);
    public void BranchIfGreaterOrEqual(IlLabel label) => BranchOp(OpCodes.Bge, label);
    public void BranchIfLess(IlLabel label) => BranchOp(OpCodes.Blt, label);
    public void BranchIfLessOrEqual(IlLabel label) => BranchOp(OpCodes.Ble, label);
    public void UnsignedBranchIfNotEqual(IlLabel label) => BranchOp(OpCodes.Bne_Un, label);
    public void UnsignedBranchIfGreater(IlLabel label) => BranchOp(OpCodes.Bgt_Un, label);
    public void UnsignedBranchIfGreaterOrEqual(IlLabel label) => BranchOp(OpCodes.Bge_Un, label);
    public void UnsignedBranchIfLess(IlLabel label) => BranchOp(OpCodes.Blt_Un, label);
    public void UnsignedBranchIfLessOrEqual(IlLabel label) => BranchOp(OpCodes.Ble_Un, label);

    public void Switch(params IlLabel[] labels)
    {
        Begin();
        foreach (var l in labels) l.Used = true;
        _il.Emit(OpCodes.Switch, labels.Select(l => l.Label).ToArray());
        if (_text is not null) Text("switch", string.Join(", ", labels.Select(l => l.Name)));
    }

    // ---- calls and returns ----

    public void Call(MethodInfo method) => CallOp(OpCodes.Call, method);

    public void CallVirtual(MethodInfo method) => CallOp(OpCodes.Callvirt, method);

    /// <summary>A call to the method <paramref name="emit"/> builds.</summary>
    public void Call(IlEmit emit)
        => CallOp(OpCodes.Call, emit._method ?? throw new InvalidOperationException("only a built method can be called"), built: true);

    private void CallOp(OpCode op, MethodInfo method, bool built = false)
    {
        Begin();
        _pendingOp = op;
        _pendingMethod = method;
        _pendingBuilt = built;
        _pendingTextAt = _text?.Length ?? 0;
        Text(op.Name!, method is MethodBuilder ? method.Name : method.ToString());
    }

    public void CastClass(Type type)
    {
        Begin();
        _il.Emit(OpCodes.Castclass, type);
        Text("castclass", type.ToString());
    }

    public void NewObject(ConstructorInfo constructor)
    {
        Begin();
        _il.Emit(OpCodes.Newobj, constructor);
        Text("newobj", constructor.ToString());
    }

    /// <summary>No call of this method gets the <c>tail.</c> prefix. The JIT
    /// gives no first tier to a method with an explicit tail call: it
    /// compiles it with full optimization at its first compile.</summary>
    public bool NoTailCalls { get; set; }

    public void Return()
    {
        if (_unreachable) throw Fail("Unreachable code detected");
        if (!NoTailCalls && _pendingMethod is { } m && (_pendingBuilt || TailCallable(m)))
        {
            _il.Emit(OpCodes.Tailcall);
            _il.Emit(_pendingOp, m);
            _pendingMethod = null;
            _text?.Insert(_pendingTextAt, "tail.\n");
        }
        Flush();
        _il.Emit(OpCodes.Ret);
        Text("ret");
        _unreachable = true;
    }

    public void Throw()
    {
        Op(OpCodes.Throw);
        _unreachable = true;
    }

    /// <summary>The built method compiles with full optimization at its first
    /// compile and is not compiled again (AggressiveOptimization).</summary>
    public void OptimizeAtOnce()
        => (_method ?? throw new InvalidOperationException("not a built method"))
            .SetImplementationFlags((MethodImplAttributes)0x0200);

    /// <summary>Ends a method whose emission failed: what is there stays, every
    /// label is marked and the body ends in a throw. Nothing calls it; its
    /// type can be created.</summary>
    public void Abandon()
    {
        _pendingMethod = null;
        foreach (var l in _labels)
            if (!l.Marked)
            {
                l.Marked = true;
                _il.MarkLabel(l.Label);
            }
        _il.Emit(OpCodes.Ldnull);
        _il.Emit(OpCodes.Throw);
        _text?.Append("; abandoned\nldnull\nthrow\n");
        _unreachable = true;
    }

    // A call whose result is the method's, that passes no pointer or
    // reference (a value type's instance method passes its this as one).
    private static bool TailCallable(MethodInfo m)
    {
        if (m.ReturnType == typeof(void) || m.ReturnType.IsPointer || m.ReturnType == typeof(TypedReference)) return false;
        if (!m.IsStatic && m.DeclaringType!.IsValueType) return false;
        foreach (var p in m.GetParameters())
        {
            var t = p.ParameterType;
            if (t.IsPointer || t.IsByRef || t == typeof(TypedReference)) return false;
        }
        var r = Alias(m.ReturnType);
        var own = Alias(ReturnType);
        return r == own || (!r.IsValueType && own.IsAssignableFrom(r));
    }

    private static Type Alias(Type t)
    {
        if (t.IsEnum) t = Enum.GetUnderlyingType(t);
        if (t == typeof(bool) || t == typeof(sbyte) || t == typeof(byte) || t == typeof(short)
            || t == typeof(ushort) || t == typeof(char) || t == typeof(uint))
            return typeof(int);
        if (t == typeof(ulong)) return typeof(long);
        if (t == typeof(nuint)) return typeof(nint);
        return t;
    }

    // ---- finishing ----

    private void Finish()
    {
        Flush();
        // A label declared and never used may stay unmarked.
        foreach (var l in _labels)
            if (l.Used && !l.Marked)
                throw Fail($"label [{l.Name}] is used but never marked");
    }

    /// <summary>The dynamic method's delegate.</summary>
    public PredicateDelegate CreateDelegate(bool initLocals)
    {
        Finish();
        var dm = _dynamic ?? throw new InvalidOperationException("not a dynamic method");
        dm.InitLocals = initLocals;
        return (PredicateDelegate)dm.CreateDelegate(typeof(PredicateDelegate));
    }

    /// <summary>The built method, its body complete.</summary>
    public MethodBuilder CreateMethod()
    {
        Finish();
        return _method ?? throw new InvalidOperationException("not a built method");
    }
}
