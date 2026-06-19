namespace Shumway.Compiler.NativeC;

/// <summary>
/// The AST of Shumway's Arity embedded-C subset (ADR-022). Two grammars share
/// these node types: a <c>:- c</c> region parses to a list of <see cref="CDecl"/>
/// (a module symbol table), and a <c>{ … }</c> native goal parses to a list of
/// <see cref="CStmt"/>. The subset is deliberately small — the corpus blocks are
/// linear statement sequences with no C control flow.
/// </summary>

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

/// <summary>A C type as it appears in a prototype, a global declaration, or a
/// <c>Var: type</c> binding. Qualifiers (<c>const</c>/<c>unsigned</c>/…) are
/// folded into <see cref="Name"/> (a normalised spelling, e.g. <c>"unsigned
/// long"</c>); pointer levels are counted in <see cref="PointerDepth"/> (so
/// <c>char*</c> is <c>("char", 1)</c>, <c>char**</c> is <c>("char", 2)</c>). A
/// typedef name (e.g. <c>pchar</c>, <c>preftype</c>) is carried verbatim as
/// <see cref="Name"/> and resolved later against the typedef table.</summary>
public sealed record CType(string Name, int PointerDepth = 0)
{
    public override string ToString() => Name + new string('*', PointerDepth);
}

// ---------------------------------------------------------------------------
// Declarations (from a `:- c` region)
// ---------------------------------------------------------------------------

public abstract record CDecl;

/// <summary>A global variable / buffer: <c>char lbuf[255];</c>,
/// <c>reftype pTranslateRef1;</c>. <see cref="ArrayLength"/> is the bracket size
/// for an array form, else null.</summary>
public sealed record CGlobalVar(string Name, CType Type, int? ArrayLength) : CDecl;

/// <summary>A function prototype: <c>int strcmp(const char*, const char*);</c>.
/// Parameter names are optional in C and usually absent here.</summary>
public sealed record CPrototype(string Name, CType ReturnType,
    IReadOnlyList<CParam> Params, bool IsVariadic = false) : CDecl;

public sealed record CParam(CType Type, string? Name);

/// <summary>A simple typedef: <c>typedef char *pchar;</c> →
/// <c>CTypedef("pchar", ("char", 1))</c>. Struct/union/function-pointer typedefs
/// are not modelled (skipped by the parser).</summary>
public sealed record CTypedef(string Alias, CType Underlying) : CDecl;

// ---------------------------------------------------------------------------
// Statements (from a `{ … }` block)
// ---------------------------------------------------------------------------

public abstract record CStmt;

/// <summary>A typed local declaration: <c>ret: long</c>, <c>PtrMsgCode: preftype</c>.
/// In Arity the name is usually a Prolog variable (Capitalised) bound on the way
/// in or out; mode/type inference (step 3) decides the direction.</summary>
public sealed record CVarDeclStmt(string Var, CType Type) : CStmt;

/// <summary>A Prolog binding: <c>MsgId is id</c>, <c>X is 'strcmp'(lbuf, rbuf)</c>.
/// Evaluates <see cref="Value"/> natively and unifies the result into the Prolog
/// variable <see cref="Var"/> (output).</summary>
public sealed record CBindStmt(string Var, CExpr Value) : CStmt;

/// <summary>A C assignment: <c>ret = 'get_next_literal'(…)</c>,
/// <c>pDomain = &amp;cDomain</c>. Pure native — no Prolog unification.</summary>
public sealed record CAssignStmt(CExpr Target, CExpr Value) : CStmt;

/// <summary>A bare native call used as a statement:
/// <c>'MakeCString'(buf, 10240, &amp;Str)</c>, <c>'freepar'(Ptr)</c>.</summary>
public sealed record CCallStmt(CExpr Call) : CStmt;

// ---------------------------------------------------------------------------
// Expressions
// ---------------------------------------------------------------------------

public abstract record CExpr;

/// <summary>A native function call <c>name(args)</c>. The name is the function's
/// C identifier (it may have been written Prolog-quoted as <c>'Name'</c> in the
/// source; the quotes are not part of the name).</summary>
public sealed record CCallExpr(string Name, IReadOnlyList<CExpr> Args) : CExpr;

/// <summary>A reference to a variable — a Prolog variable, a block-local, or a
/// `:- c` global. The parser does not classify it; that is step 3's job.</summary>
public sealed record CIdentExpr(string Name) : CExpr;

public sealed record CIntExpr(long Value) : CExpr;

public sealed record CStringExpr(string Value) : CExpr;

/// <summary><c>&amp;x</c> — address-of (an output parameter / global address).</summary>
public sealed record CAddrOfExpr(CExpr Operand) : CExpr;

/// <summary><c>*x</c> — pointer dereference.</summary>
public sealed record CDerefExpr(CExpr Operand) : CExpr;

/// <summary>A simple binary arithmetic expression — <c>Len - 1</c>, <c>A + B</c>.
/// <see cref="Op"/> is one of <c>+ - * /</c>.</summary>
public sealed record CBinaryExpr(char Op, CExpr Left, CExpr Right) : CExpr;

/// <summary>The <c>(void)</c> "no argument" marker, e.g.
/// <c>'get_exepath'(void)</c>.</summary>
public sealed record CVoidExpr : CExpr;
