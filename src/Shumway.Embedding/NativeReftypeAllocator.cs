using System;
using System.Runtime.InteropServices;
using System.Text;
using Shumway.Compiler.Ast;

namespace Shumway.Embedding;

/// <summary>ADR-024 — the native library's own <c>t_reftype</c> allocator
/// (<c>newreftype</c> / <c>freepar</c> / <c>getargp</c> / <c>setcflt</c>, as in
/// Arity's <c>prlg_ifce</c>). When a native library exports these, the materializer
/// builds and frees reftype graphs <b>through them</b> instead of
/// <see cref="Marshal.AllocHGlobal(int)"/> — so a native C function that <b>allocates
/// new sub-nodes</b> (e.g. builds a list into the struct) does so in the same heap,
/// and <c>freepar</c> can release the whole mixed graph. Without this, a graph
/// part-allocated by Shumway and part-allocated by C cannot be freed safely.</summary>
internal sealed class NativeReftypeAllocator
{
    // void newreftype(int, int nelem, reftype* ref, int ntype, int val):
    //   allocates a t_reftype into *ref; for a functor allocates the pars array
    //   (nelem) and the cstr name buffer (val bytes); for a string the cstr buffer.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NewReftypeFn(int unused, int nelem, IntPtr refCell, int ntype, int val);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeparFn(IntPtr refCell);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetargpFn(int n, IntPtr refCell);   // → &(*ref)->pars[n-1]
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetcfltFn(double v, IntPtr refCell);

    private readonly NewReftypeFn _newreftype;
    private readonly FreeparFn _freepar;
    private readonly GetargpFn? _getargp;
    private readonly SetcfltFn? _setcflt;

    private const int OffNelem = 8, OffCrep = 24;

    private NativeReftypeAllocator(NewReftypeFn n, FreeparFn f, GetargpFn? g, SetcfltFn? s)
    { _newreftype = n; _freepar = f; _getargp = g; _setcflt = s; }

    /// <summary>Resolves the allocator API from a loaded native library, or null if
    /// the mandatory <c>newreftype</c>/<c>freepar</c> exports are absent (then the
    /// HGlobal in-place path is used).</summary>
    public static NativeReftypeAllocator? TryResolve(IntPtr lib)
    {
        if (!NativeLibrary.TryGetExport(lib, "newreftype", out var nr)) return null;
        if (!NativeLibrary.TryGetExport(lib, "freepar", out var fp)) return null;
        GetargpFn? g = NativeLibrary.TryGetExport(lib, "getargp", out var gp)
            ? Marshal.GetDelegateForFunctionPointer<GetargpFn>(gp) : null;
        SetcfltFn? s = NativeLibrary.TryGetExport(lib, "setcflt", out var sc)
            ? Marshal.GetDelegateForFunctionPointer<SetcfltFn>(sc) : null;
        return new NativeReftypeAllocator(
            Marshal.GetDelegateForFunctionPointer<NewReftypeFn>(nr),
            Marshal.GetDelegateForFunctionPointer<FreeparFn>(fp), g, s);
    }

    /// <summary>Builds <paramref name="term"/> into a native t_reftype graph using the
    /// library's allocator. Returns a <c>reftype*</c> cell (one pointer slot) that
    /// holds the graph; pass <c>*cell</c> to the native function, and release with
    /// <see cref="Free"/>. The cell itself is HGlobal (Shumway-owned); the t_reftype
    /// graph is the library's.</summary>
    public IntPtr Materialize(Term term, Encoding enc)
    {
        IntPtr cell = Marshal.AllocHGlobal(IntPtr.Size);
        Marshal.WriteIntPtr(cell, IntPtr.Zero);
        Fill(term, cell, enc);
        return cell;
    }

    private void Fill(Term term, IntPtr cell, Encoding enc)
    {
        switch (term)
        {
            case IntTerm i:
                _newreftype(0, 0, cell, (int)Reftype.Codes.Integer, unchecked((int)i.Value));
                break;
            case BigIntTerm b:
                _newreftype(0, 0, cell, (int)Reftype.Codes.Integer, unchecked((int)(long)(b.Value & ulong.MaxValue)));
                break;
            case FloatTerm f:
                if (_setcflt is null) throw new InvalidOperationException(
                    ":- native float materialization needs the library to export 'setcflt'.");
                _newreftype(0, 0, cell, (int)Reftype.Codes.Floating, 0);
                _setcflt(f.Value, cell);
                break;
            case AtomTerm a:
                WriteString(cell, (int)Reftype.Codes.Atom, a.Name, enc);
                break;
            case StringTerm s:
                WriteString(cell, (int)Reftype.Codes.String, s.Content, enc);
                break;
            case CompoundTerm c:
                if (_getargp is null) throw new InvalidOperationException(
                    ":- native functor materialization needs the library to export 'getargp'.");
                byte[] nb = enc.GetBytes(c.Functor);
                _newreftype(0, c.Args.Length, cell, (int)Reftype.Codes.Functor, nb.Length + 1);
                CopyCstr(cell, nb);
                for (int k = 0; k < c.Args.Length; k++)
                    Fill(c.Args[k], _getargp(k + 1, cell), enc);   // &pars[k]
                break;
            default:   // VarTerm / unknown → undef
                _newreftype(0, 0, cell, (int)Reftype.Codes.Undef, 0);
                break;
        }
    }

    private void WriteString(IntPtr cell, int ntype, string text, Encoding enc)
    {
        byte[] bytes = enc.GetBytes(text);
        _newreftype(0, 0, cell, ntype, bytes.Length + 1);   // newreftype allocates crep.cstr of this size
        CopyCstr(cell, bytes);
    }

    // Copies `bytes` + NUL into (*cell)->crep.cstr (allocated by newreftype).
    private static void CopyCstr(IntPtr cell, byte[] bytes)
    {
        IntPtr structPtr = Marshal.ReadIntPtr(cell);
        IntPtr cstr = Marshal.ReadIntPtr(structPtr, OffCrep);
        if (cstr == IntPtr.Zero) throw new InvalidOperationException(
            "newreftype did not allocate crep.cstr for a string/functor.");
        if (bytes.Length > 0) Marshal.Copy(bytes, 0, cstr, bytes.Length);
        Marshal.WriteByte(cstr, bytes.Length, 0);
    }

    /// <summary>The <c>t_reftype*</c> the native function receives — the struct
    /// pointer held in the cell.</summary>
    public static IntPtr StructPointer(IntPtr cell) => Marshal.ReadIntPtr(cell);

    /// <summary>Frees the graph via the library's <c>freepar</c>, then the cell.</summary>
    public void Free(IntPtr cell)
    {
        _freepar(cell);
        Marshal.FreeHGlobal(cell);
    }
}
