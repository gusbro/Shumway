using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Shumway.Compiler.Ast;
using Shumway.Compiler.NativeC;
using Shumway.Embedding;
using Xunit;
using Xunit.Abstractions;

// ADR-024 — the P/Invoke side of the materializer tier. These cover the marshalling
// machinery (signature from prototype + cdecl calli over a real native t_reftype)
// WITHOUT needing a C compiler: the "native" function is a C# method exposed as a
// cdecl function pointer, operating on the native struct exactly as real C would.
public class NativePInvokeTests
{
    private readonly ITestOutputHelper _output;
    public NativePInvokeTests(ITestOutputHelper output) => _output = output;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int BumpFn(IntPtr r);

    // int bump(t_reftype* r): if integer, r->crep.cint += 1; returns 1/0.
    private static int BumpCint(IntPtr r)
    {
        if (Marshal.ReadInt64(r, 0) != Reftype.Codes.Integer) return 0;
        Marshal.WriteInt32(r, 24, Marshal.ReadInt32(r, 24) + 1);   // crep.cint += 1
        return 1;
    }

    [Fact]
    public void Calli_OverNativeReftype_ModifiesInPlace_AndDematerializes()
    {
        var del = new BumpFn(BumpCint);
        IntPtr fn = Marshal.GetFunctionPointerForDelegate(del);

        // Derive the marshalling signature from a `:- c`-style prototype.
        var proto = new CPrototype("bump", new CType("int"),
            new[] { new CParam(new CType("reftype"), "r") });
        var sig = NativeCall.FromPrototype(proto, new Dictionary<string, CType>());
        Assert.Single(sig.ParamKinds);
        Assert.Equal(NativeCall.Kind.Reftype, sig.ParamKinds[0]);   // reftype param
        Assert.Equal(typeof(IntPtr), sig.ParamClrTypes[0]);         // passed as the struct pointer
        Assert.Equal(typeof(int), sig.ReturnType);

        IntPtr p = NativeReftype.Materialize(new IntTerm(10));
        try
        {
            object? ret = sig.Invoker(fn, new object?[] { p });
            Assert.Equal(1, ret);                           // calli return
            Assert.Equal(new IntTerm(11), NativeReftype.Dematerialize(p));  // C bumped it in place
        }
        finally
        {
            NativeReftype.Free(p);
            GC.KeepAlive(del);
        }
    }

    [Fact]
    public void FromPrototype_MapsScalarAndReftypeParams()
    {
        // int fn(int, short, long, double, reftype)
        var proto = new CPrototype("fn", new CType("int"), new[]
        {
            new CParam(new CType("int"), "a"),
            new CParam(new CType("short"), "b"),
            new CParam(new CType("long"), "c"),
            new CParam(new CType("double"), "d"),
            new CParam(new CType("reftype"), "r"),
        });
        var sig = NativeCall.FromPrototype(proto, new Dictionary<string, CType>());
        Assert.Equal(new[] { typeof(int), typeof(short), typeof(long), typeof(double), typeof(IntPtr) },
            sig.ParamClrTypes);
        Assert.Equal(
            new[] { NativeCall.Kind.Scalar, NativeCall.Kind.Scalar, NativeCall.Kind.Scalar,
                    NativeCall.Kind.Scalar, NativeCall.Kind.Reftype },
            sig.ParamKinds);
    }

    [Fact]
    public void FromPrototype_MapsOutScalarPointers()
    {
        // short* / int* (incl. via a typedef) are out-scalars — passed as a pointer,
        // the element type recorded for read-back.
        var typedefs = new Dictionary<string, CType> { ["pshort"] = new CType("short", 1) };
        var proto = new CPrototype("fn", new CType("int"), new[]
        {
            new CParam(new CType("int"), "v"),
            new CParam(new CType("pshort"), "a"),
            new CParam(new CType("int", 1), "b"),
        });
        var sig = NativeCall.FromPrototype(proto, typedefs);
        Assert.Equal(
            new[] { NativeCall.Kind.Scalar, NativeCall.Kind.OutScalar, NativeCall.Kind.OutScalar },
            sig.ParamKinds);
        Assert.Equal(typeof(short), sig.ParamElemTypes[1]);
        Assert.Equal(typeof(int), sig.ParamElemTypes[2]);
        Assert.Equal(new[] { typeof(int), typeof(IntPtr), typeof(IntPtr) }, sig.ParamClrTypes);
    }

    [Fact]
    public void FromPrototype_ResolvesPreftypeAndTypedefsToReftype()
    {
        // preftype (a reftype handle) and a typedef chain both map to the reftype param.
        var typedefs = new Dictionary<string, CType> { ["myref"] = new CType("reftype") };
        var proto = new CPrototype("fn", new CType("void"), new[]
        {
            new CParam(new CType("preftype"), "p"),
            new CParam(new CType("myref"), "q"),
        });
        var sig = NativeCall.FromPrototype(proto, typedefs);
        Assert.Equal(new[] { NativeCall.Kind.Reftype, NativeCall.Kind.Reftype }, sig.ParamKinds);
        Assert.Equal(typeof(void), sig.ReturnType);
    }

    [Fact]
    public void FromPrototype_RejectsUnsupportedPointerParam()
    {
        var proto = new CPrototype("fn", new CType("int"),
            new[] { new CParam(new CType("char", 1), "s") });   // char* — deferred
        Assert.Throws<InvalidOperationException>(
            () => NativeCall.FromPrototype(proto, new Dictionary<string, CType>()));
    }

    // ---- Full end-to-end through a REAL native DLL (compiled on demand; skips
    //      cleanly where no C toolchain is present — the calli mechanism above runs
    //      everywhere). ----

    private const string CSource = """
        typedef struct t_reftype {
            long long ntype; long long nelem; void* pars;
            union { char* cstr; int cint; double cflt; } crep;
        } t_reftype;
        __declspec(dllexport) int bump_native(t_reftype* r) {
            if (r->ntype != 1) return 0;        /* 1 = integer */
            r->crep.cint = r->crep.cint + 1;    /* modify in place */
            return 1;
        }
        """;

    private const string Program =
        ":- set_prolog_flag(arity_compat, true).\n" +
        ":- native bump_native/1.\n" +
        ":- c.\nreftype par1ref;\nint bump_native(reftype);\n:- prolog.\n" +
        "go(In, Out) :-\n" +
        "  { Ptr: preftype; Ptr is &par1ref },\n" +
        "  fill_par(In, Ptr),\n" +
        "  { ret: int; ret = 'bump_native'(par1ref); Ret is ret },\n" +
        "  Ret =:= 1,\n" +
        "  reftype_term(Out, Ptr).\n";

    [Fact]
    public void EndToEnd_RealNativeDll_PInvoke_ModifiesInPlace()
    {
        string? dll = NativeTestDll.TryBuild(CSource, "bumpnative", out string note);
        if (dll is null)
        {
            // No toolchain → warn + skip (the calli mechanism is covered above). The
            // compiler comes from $SHUMWAY_NATIVE_CC or the PATH — no hardcoded paths,
            // and it builds a .so/.dylib/.dll per platform.
            _output.WriteLine("SKIPPED real-DLL P/Invoke test: " + note
                + " — set SHUMWAY_NATIVE_CC to a C compiler (gcc/clang/cl) to run it.");
            return;
        }

        var e = new PrologEngine();
        e.UseNativeLibrary(dll);
        e.ConsultString(Program);
        // fill_par materializes In to a native t_reftype; bump_native (real C, via
        // P/Invoke) bumps crep.cint in place; reftype_term dematerializes it back.
        Assert.True(e.Query("go(10, Out), Out == 11.").Success);
        Assert.True(e.Query("go(41, Out), Out == 42.").Success);   // cached resolution on the 2nd call
    }

    // ---- "C builds a list": the native function ALLOCATES sub-nodes. Materialize
    //      and free go through the library's own reftype allocator (newreftype /
    //      freepar), so the mixed graph is freed safely. ----

    private const string ListCSource = """
        #include <stdlib.h>
        #include <string.h>
        #ifdef _MSC_VER
        #define EXPORT __declspec(dllexport)
        #else
        #define EXPORT __attribute__((visibility("default")))
        #endif
        typedef struct t_reftype {
            long long ntype; long long nelem; struct t_reftype** pars;
            union { char* cstr; int cint; double cflt; } crep;
        } t_reftype;
        EXPORT void newreftype(int u, int nelem, t_reftype** ref, int ntype, int val) {
            (void)u;
            t_reftype* r = (t_reftype*)calloc(1, sizeof(t_reftype));
            r->ntype = ntype;
            if (ntype == 1) r->crep.cint = val;
            else if (ntype == 5) { r->nelem = nelem; r->pars = (t_reftype**)calloc(nelem, sizeof(t_reftype*)); r->crep.cstr = (char*)calloc(val,1); }
            else if (ntype == 3 || ntype == 4) { r->nelem = val; r->crep.cstr = (char*)calloc(val,1); }
            *ref = r;
        }
        EXPORT void freepar(t_reftype** ref) {
            t_reftype* r = *ref; if (!r) return;
            if (r->ntype == 3 || r->ntype == 4 || r->ntype == 5) free(r->crep.cstr);
            if (r->ntype == 5) { long long i; for (i=0;i<r->nelem;i++){ t_reftype* c=r->pars[i]; freepar(&c);} free(r->pars); }
            free(r); *ref = 0;
        }
        EXPORT t_reftype** getargp(int n, t_reftype** ref) { return &((*ref)->pars[n-1]); }
        EXPORT int build_list(t_reftype* r, int n) {
            t_reftype* tail = (t_reftype*)calloc(1,sizeof(t_reftype));
            tail->ntype = 4; tail->nelem = 3; tail->crep.cstr = (char*)calloc(3,1); strcpy(tail->crep.cstr,"[]");
            int i;
            for (i=n;i>=1;i--){
                t_reftype* cons=(t_reftype*)calloc(1,sizeof(t_reftype));
                cons->ntype=5; cons->nelem=2; cons->crep.cstr=(char*)calloc(2,1); strcpy(cons->crep.cstr,".");
                cons->pars=(t_reftype**)calloc(2,sizeof(t_reftype*));
                t_reftype* head=(t_reftype*)calloc(1,sizeof(t_reftype)); head->ntype=1; head->crep.cint=i;
                cons->pars[0]=head; cons->pars[1]=tail; tail=cons;
            }
            *r = *tail; free(tail); return 1;
        }
        """;

    private const string ListProgram =
        ":- set_prolog_flag(arity_compat, true).\n" +
        ":- native build_list/2.\n" +
        ":- c.\nreftype par1ref;\nint build_list(reftype, int);\n:- prolog.\n" +
        "go(N, L) :-\n" +
        "  integer(N),\n" +
        "  { Ptr: preftype; Ptr is &par1ref },\n" +
        "  fill_par(_, Ptr),\n" +
        "  { ret: int; ret = 'build_list'(par1ref, N); Ret is ret },\n" +
        "  Ret =:= 1,\n" +
        "  reftype_term(L, Ptr).\n";

    [Fact]
    public void EndToEnd_NativeDll_BuildsAList_ViaLibraryAllocator()
    {
        string? dll = NativeTestDll.TryBuild(ListCSource, "buildlist", out string note);
        if (dll is null)
        {
            _output.WriteLine("SKIPPED build-a-list P/Invoke test: " + note
                + " — set SHUMWAY_NATIVE_CC to a C compiler to run it.");
            return;
        }
        var e = new PrologEngine();
        e.UseNativeLibrary(dll);   // exports newreftype/freepar → library-allocator mode
        e.ConsultString(ListProgram);
        // build_list (real C) allocates a cons list into par1ref via newreftype;
        // Shumway dematerializes the C-built graph and freepar's it.
        Assert.True(e.Query("go(3, L), L == [1,2,3].").Success);
        Assert.True(e.Query("go(1, L), L == [1].").Success);
        Assert.True(e.Query("go(5, L), length(L, 5), L = [1|_].").Success);
    }

    // ---- Out-scalar parameters: `fn(..., &local)` — a native function writes a
    //      scalar through a pointer, read back into the block-local. ----

    private const string OutCSource = """
        #ifdef _MSC_VER
        #define EXPORT __declspec(dllexport)
        #else
        #define EXPORT __attribute__((visibility("default")))
        #endif
        EXPORT int set_out(int v, int* out_i, short* out_s) {
            *out_i = v * 10;
            *out_s = (short)(v + 1);
            return 1;
        }
        """;

    private const string OutProgram =
        ":- set_prolog_flag(arity_compat, true).\n" +
        ":- native set_out/3.\n" +
        ":- c.\nint set_out(int, int*, short*);\n:- prolog.\n" +
        "calc(In, Ri, Rs) :-\n" +
        "  integer(In),\n" +
        "  { ret: int; oi: int; os: short;\n" +
        "    ret = 'set_out'(In, &oi, &os);\n" +
        "    Ri is oi; Rs is os; Ret is ret },\n" +
        "  Ret =:= 1.\n";

    [Fact]
    public void EndToEnd_NativeDll_OutScalarPointers()
    {
        string? dll = NativeTestDll.TryBuild(OutCSource, "outscalar", out string note);
        if (dll is null)
        {
            _output.WriteLine("SKIPPED out-scalar P/Invoke test: " + note
                + " — set SHUMWAY_NATIVE_CC to a C compiler to run it.");
            return;
        }
        var e = new PrologEngine();
        e.UseNativeLibrary(dll);
        e.ConsultString(OutProgram);
        // set_out writes *out_i = In*10 and *out_s = In+1 through the pointers;
        // Shumway reads them back into the block-locals oi/os.
        Assert.True(e.Query("calc(5, Ri, Rs), Ri == 50, Rs == 6.").Success);
        Assert.True(e.Query("calc(0, Ri, Rs), Ri == 0, Rs == 1.").Success);
    }
}

// Compiles a small C source to a shared library for the integration test. The
// compiler is taken from $SHUMWAY_NATIVE_CC or found on the PATH (cl / cc / gcc /
// clang) — NO hardcoded install paths, cross-platform. Returns null (with a note)
// when no compiler is available or the build fails.
internal static class NativeTestDll
{
    public static string? TryBuild(string cSource, string name, out string note)
    {
        string? cc = Environment.GetEnvironmentVariable("SHUMWAY_NATIVE_CC");
        var candidates = !string.IsNullOrWhiteSpace(cc)
            ? new[] { cc! }
            : OperatingSystem.IsWindows() ? new[] { "cl", "clang", "gcc" } : new[] { "cc", "clang", "gcc" };

        string ext = OperatingSystem.IsWindows() ? ".dll" : OperatingSystem.IsMacOS() ? ".dylib" : ".so";
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "shumway_nat_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        string src = name + ".c";
        string outName = name + ext;
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, src), cSource);
        string outPath = System.IO.Path.Combine(dir, outName);

        foreach (var compiler in candidates)
        {
            bool isCl = System.IO.Path.GetFileNameWithoutExtension(compiler)
                .Equals("cl", StringComparison.OrdinalIgnoreCase);
            string argv = isCl
                ? $"/nologo /LD /Fe:{outName} {src}"
                : $"-shared -fPIC -o {outName} {src}";
            var psi = new System.Diagnostics.ProcessStartInfo(compiler, argv)
            {
                WorkingDirectory = dir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            try
            {
                using var p = System.Diagnostics.Process.Start(psi);
                if (p is null) continue;
                if (!p.WaitForExit(60000)) { try { p.Kill(); } catch { } continue; }
                if (System.IO.File.Exists(outPath)) { note = "ok (" + compiler + ")"; return outPath; }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                continue;   // compiler not found on PATH — try the next candidate
            }
            catch { /* try next */ }
        }
        note = "no C compiler found on PATH (" + string.Join("/", candidates) + ")";
        return null;
    }
}
