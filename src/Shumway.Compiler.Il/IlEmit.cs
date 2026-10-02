using System.Reflection;
using System.Reflection.Emit;

namespace Shumway.Compiler.Il;

// The emitter API Tier-1 compiles against (IlEmit, IlLabel, IlLocal), here
// over Sigil.
public sealed class IlLabel
{
    internal readonly Sigil.Label L;
    internal bool Marked;
    public string Name => L.Name;
    internal IlLabel(Sigil.Label l) => L = l;
    public override string ToString() => Name;
}

public sealed class IlLocal
{
    internal readonly Sigil.Local L;
    public string Name => L.Name;
    public Type LocalType => L.LocalType;
    internal IlLocal(Sigil.Local l) => L = l;
    public override string ToString() => Name;
}

public sealed class IlEmit
{
    private readonly Sigil.Emit<PredicateDelegate> _e;
    private readonly List<IlLabel> _labels = new();
    private static readonly PropertyInfo DynMethod =
        typeof(Sigil.Emit<PredicateDelegate>).GetProperty("DynMethod", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private IlEmit(Sigil.Emit<PredicateDelegate> e) => _e = e;

    public static IlEmit NewDynamicMethod(string name, bool record)
        => new(Sigil.Emit<PredicateDelegate>.NewDynamicMethod(name, doVerify: false));

    public static IlEmit BuildMethod(TypeBuilder type, string name, MethodAttributes attributes,
        CallingConventions callingConvention, bool record)
        => new(Sigil.Emit<PredicateDelegate>.BuildMethod(type, name, attributes, callingConvention,
            allowUnverifiableCode: true, doVerify: false));

    public string Instructions() => _e.Instructions();
    public IReadOnlyList<IlLabel> UnmarkedLabels => _labels.Where(l => !l.Marked).ToList();

    public IlLabel DefineLabel(string? name = null)
    {
        var l = new IlLabel(name is null ? _e.DefineLabel() : _e.DefineLabel(name));
        _labels.Add(l);
        return l;
    }
    public void MarkLabel(IlLabel label) { _e.MarkLabel(label.L); label.Marked = true; }
    public IlLocal DeclareLocal<T>(string? name = null) => new(name is null ? _e.DeclareLocal<T>() : _e.DeclareLocal<T>(name));
    public IlLocal DeclareLocal(Type type, string? name = null) => new(name is null ? _e.DeclareLocal(type) : _e.DeclareLocal(type, name));

    public void LoadArgument(int index) => _e.LoadArgument((ushort)index);
    public void StoreArgument(int index) => _e.StoreArgument((ushort)index);
    public void LoadLocal(IlLocal l) => _e.LoadLocal(l.L);
    public void StoreLocal(IlLocal l) => _e.StoreLocal(l.L);
    public void LoadLocalAddress(IlLocal l) => _e.LoadLocalAddress(l.L);
    public void LoadConstant(int v) => _e.LoadConstant(v);
    public void LoadConstant(uint v) => _e.LoadConstant(v);
    public void LoadConstant(bool v) => _e.LoadConstant(v);
    public void LoadConstant(long v) => _e.LoadConstant(v);
    public void LoadConstant(ulong v) => _e.LoadConstant(v);
    public void LoadConstant(double v) => _e.LoadConstant(v);
    public void LoadConstant(string v) => _e.LoadConstant(v);
    public void LoadField(FieldInfo f) => _e.LoadField(f);
    public void StoreField(FieldInfo f) => _e.StoreField(f);
    public void LoadElement<T>() => _e.LoadElement<T>();
    public void LoadElement(Type t) => _e.LoadElement(t);
    public void StoreElement<T>() => _e.StoreElement<T>();
    public void StoreElement(Type t) => _e.StoreElement(t);
    public void LoadLength<T>() => _e.LoadLength<T>();
    public void Convert<T>() where T : struct => _e.Convert<T>();
    public void Convert(Type t) => _e.Convert(t);
    public void Add() => _e.Add();
    public void Subtract() => _e.Subtract();
    public void Multiply() => _e.Multiply();
    public void Divide() => _e.Divide();
    public void Negate() => _e.Negate();
    public void And() => _e.And();
    public void Or() => _e.Or();
    public void Xor() => _e.Xor();
    public void ShiftLeft() => _e.ShiftLeft();
    public void ShiftRight() => _e.ShiftRight();
    public void UnsignedShiftRight() => _e.UnsignedShiftRight();
    public void CompareEqual() => _e.CompareEqual();
    public void Duplicate() => _e.Duplicate();
    public void Pop() => _e.Pop();
    public void Branch(IlLabel l) => _e.Branch(l.L);
    public void BranchIfTrue(IlLabel l) => _e.BranchIfTrue(l.L);
    public void BranchIfFalse(IlLabel l) => _e.BranchIfFalse(l.L);
    public void BranchIfEqual(IlLabel l) => _e.BranchIfEqual(l.L);
    public void BranchIfGreater(IlLabel l) => _e.BranchIfGreater(l.L);
    public void BranchIfGreaterOrEqual(IlLabel l) => _e.BranchIfGreaterOrEqual(l.L);
    public void BranchIfLess(IlLabel l) => _e.BranchIfLess(l.L);
    public void BranchIfLessOrEqual(IlLabel l) => _e.BranchIfLessOrEqual(l.L);
    public void UnsignedBranchIfNotEqual(IlLabel l) => _e.UnsignedBranchIfNotEqual(l.L);
    public void UnsignedBranchIfGreater(IlLabel l) => _e.UnsignedBranchIfGreater(l.L);
    public void UnsignedBranchIfGreaterOrEqual(IlLabel l) => _e.UnsignedBranchIfGreaterOrEqual(l.L);
    public void UnsignedBranchIfLess(IlLabel l) => _e.UnsignedBranchIfLess(l.L);
    public void UnsignedBranchIfLessOrEqual(IlLabel l) => _e.UnsignedBranchIfLessOrEqual(l.L);
    public void Switch(params IlLabel[] labels) => _e.Switch(labels.Select(l => l.L).ToArray());
    public void Call(MethodInfo m) => _e.Call(m);
    public void CallVirtual(MethodInfo m) => _e.CallVirtual(m);
    public void Call(IlEmit other) => _e.Call(other._e);
    public void CastClass(Type t) => _e.CastClass(t);
    public void NewObject(ConstructorInfo c) => _e.NewObject(c);
    public void Return() => _e.Return();
    public void Throw() => _e.Throw();

    public PredicateDelegate CreateDelegate(bool initLocals)
    {
        if (DynMethod.GetValue(_e) is DynamicMethod dm) dm.InitLocals = initLocals;
        return _e.CreateDelegate(Sigil.OptimizationOptions.None);
    }

    public MethodBuilder CreateMethod() => _e.CreateMethod(Sigil.OptimizationOptions.None);
}
