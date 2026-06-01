using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Shumway.SourceGen;

/// <summary>
/// Chunk 242 — Roslyn incremental source generator that emits a
/// <c>bool(Engine)</c> bridge method for every
/// <c>[Shumway.Embedding.PrologPredicate]</c>-decorated method whose
/// signature is <em>not</em> the raw
/// <c>bool Method(Shumway.Core.Engine)</c> form.
///
/// <para>The bridge name is
/// <c>_{originalName}_PrologBridge</c>; the runtime
/// <see cref="Shumway.Embedding.PrologEngine.RegisterPredicates"/>
/// detects typed methods and registers the matching bridge instead
/// of the user method. The bridge decodes each typed parameter
/// from its register via
/// <see cref="Shumway.Embedding.PrologEngine.FromTerm{T}"/>, calls
/// the user method, then either:</para>
/// <list type="bullet">
/// <item>returns <c>true</c> for a <c>void</c> return (the
///   predicate just succeeds);</item>
/// <item>returns the user method's <c>bool</c> result (the
///   predicate's truth value follows the C# bool);</item>
/// <item>encodes any other return type via
///   <see cref="Shumway.Embedding.PrologEngine.ToTerm{T}"/> and
///   unifies it with the next register — the predicate's arity is
///   one more than the number of typed C# parameters in that
///   case, which the <c>[PrologPredicate]</c> arity must reflect.</item>
/// </list>
///
/// <para>An <c>Engine</c> parameter (anywhere in the user method's
/// signature) is allowed and passed through verbatim — it doesn't
/// count toward the typed-parameter list. Useful for predicates
/// that want both ergonomic typed args <em>and</em> direct engine
/// access for cut barriers / heap manipulation.</para>
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class PrologPredicateGenerator : IIncrementalGenerator
{
    private const string AttributeFullName = "Shumway.Embedding.PrologPredicateAttribute";
    private const string EngineFullName = "Shumway.Core.Engine";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Group decorated methods by their containing type so each
        // partial type emits one bridges file.
        var perMethod = context.SyntaxProvider.ForAttributeWithMetadataName(
            AttributeFullName,
            predicate: static (node, _) => node is MethodDeclarationSyntax,
            transform: static (ctx, _) => BuildBridge(ctx))
            .Where(static b => b is not null)
            .Collect();

        context.RegisterSourceOutput(perMethod, static (spc, bridges) =>
        {
            // Group by (namespace + full type chain) and emit one
            // file per partial type — nested types get the full
            // `partial Outer { partial Inner { ... } }` shape.
            var byType = bridges
                .Where(b => b is not null)
                .GroupBy(b => (b!.Namespace,
                    Chain: string.Join(".", b.TypeChain.Select(r => r.Name))));
            foreach (var group in byType)
            {
                var first = group.First()!;
                spc.AddSource(
                    hintName: $"{(first.Namespace.Length == 0 ? "_global" : first.Namespace.Replace('.', '_'))}_{group.Key.Chain.Replace('.', '_')}.PrologPredicate.g.cs",
                    source: EmitGroup(first.Namespace, first.TypeChain,
                        group.Select(b => b!).ToList()));
            }
        });
    }

    private static BridgeModel? BuildBridge(GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not IMethodSymbol method) return null;
        var containing = method.ContainingType;
        if (containing is null) return null;

        // Chunk 244: NonDeterministic = true switches to the
        // iterator-driven bridge. Detect it first since the
        // signature-skip rule below would otherwise let an
        // accidentally-non-det signature pass through.
        bool nonDeterministic = false;
        foreach (var attr in ctx.Attributes)
        {
            foreach (var named in attr.NamedArguments)
            {
                if (named.Key == "NonDeterministic"
                    && named.Value.Value is bool b && b)
                {
                    nonDeterministic = true;
                }
            }
        }

        // Skip the raw bool(Engine) form — RegisterPredicates handles
        // it directly without a bridge. Only when not non-det; a
        // non-det attribute on a bool(Engine) is a misuse the
        // generator surfaces by still emitting a bridge that will
        // fail to compile (wrong return type).
        if (!nonDeterministic
            && method.ReturnType.SpecialType == SpecialType.System_Boolean
            && method.Parameters.Length == 1
            && method.Parameters[0].Type.ToDisplayString() == EngineFullName)
        {
            return null;
        }

        var typedParams = new List<TypedParam>();
        bool hasEngineParam = false;
        int engineParamIndex = -1;
        for (int i = 0; i < method.Parameters.Length; i++)
        {
            var p = method.Parameters[i];
            if (p.Type.ToDisplayString() == EngineFullName)
            {
                if (hasEngineParam) return null;  // only one allowed
                hasEngineParam = true;
                engineParamIndex = i;
                continue;
            }
            // Chunk 246 — mode from RefKind. Plain = + (input),
            // out = - (output), ref = ? (bidirectional, nullable
            // signals unbound).
            ParamMode mode = p.RefKind switch
            {
                RefKind.Out => ParamMode.Output,
                RefKind.Ref => ParamMode.Bidirectional,
                _ => ParamMode.Input,
            };
            // For ?-mode value-type params, the user must wrap in
            // Nullable<T>. The generator can't synthesise a sentinel
            // for `int` — null is the only safe "unbound" signal.
            // Reference-type params already carry null as a natural
            // unbound signal; the displayed name may or may not
            // have a NRT `?` suffix, irrelevant at runtime.
            string typeName = p.Type.ToDisplayString();
            string paramElementType = typeName;
            bool isValueTypeNullable = false;
            if (mode == ParamMode.Bidirectional && p.Type.IsValueType
                && typeName.EndsWith("?", System.StringComparison.Ordinal))
            {
                paramElementType = typeName.Substring(0, typeName.Length - 1);
                isValueTypeNullable = true;
            }
            typedParams.Add(new TypedParam(
                p.Name, typeName, paramElementType, i, mode, isValueTypeNullable));
        }

        // Chunk 246 — out/ref are incompatible with a non-bool /
        // non-void return: the return-as-output and an out/ref-as-
        // output would both want to bind a register, with no clear
        // contract for which is which. Reject the combo (the
        // generator emits nothing; RegisterPredicates' missing-
        // bridge diagnostic surfaces the mismatch).
        bool hasOutOrRef = typedParams.Any(p => p.Mode != ParamMode.Input);
        bool returnIsValueOutput =
            !method.ReturnsVoid
            && method.ReturnType.SpecialType != SpecialType.System_Boolean
            && !nonDeterministic;
        if (hasOutOrRef && returnIsValueOutput)
            return null;
        // Out/ref also don't combine with non-det in v1 — the
        // per-solution out-binding model needs more design.
        if (hasOutOrRef && nonDeterministic)
            return null;

        // Return shape:
        //   void                  -> always succeed, no output register
        //   bool                  -> success value, no output register
        //   T                     -> encode + unify with next register
        //   IEnumerable<T>        -> non-det generator (chunk 244)
        ReturnShape returnShape;
        string? returnTypeName = null;
        string? elementTypeName = null;
        if (nonDeterministic)
        {
            // Validate: must return IEnumerable<T>.
            if (method.ReturnType is INamedTypeSymbol named
                && named.IsGenericType
                && named.ConstructedFrom.ToDisplayString() == "System.Collections.Generic.IEnumerable<T>")
            {
                elementTypeName = named.TypeArguments[0].ToDisplayString();
            }
            else
            {
                // Fall through: emit nothing (the diagnostic
                // surfaces at the RegisterPredicates level when no
                // bridge is found, pointing the user at the
                // mismatch).
                return null;
            }
            returnShape = ReturnShape.NonDet;
        }
        else if (method.ReturnsVoid)
            returnShape = ReturnShape.Void;
        else if (method.ReturnType.SpecialType == SpecialType.System_Boolean)
            returnShape = ReturnShape.Bool;
        else
        {
            returnShape = ReturnShape.Encode;
            returnTypeName = method.ReturnType.ToDisplayString();
        }

        var ns = containing.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : containing.ContainingNamespace.ToDisplayString();

        // Walk up the containing-type chain so a nested type's
        // bridge emits the right `partial Outer { partial Inner
        // { ... } }` hierarchy. Outer-most type first.
        var typeChain = new List<TypeRing>();
        for (INamedTypeSymbol? t = containing; t is not null; t = t.ContainingType)
        {
            string kind = t switch
            {
                { IsRecord: true, TypeKind: TypeKind.Struct } => "record struct",
                { IsRecord: true } => "record",
                { TypeKind: TypeKind.Struct } => "struct",
                _ => "class",
            };
            typeChain.Insert(0, new TypeRing(t.Name, kind, t.IsStatic));
        }

        return new BridgeModel(
            Namespace: ns,
            TypeChain: typeChain,
            MethodName: method.Name,
            IsStatic: method.IsStatic,
            Parameters: method.Parameters.Select(p => (p.Name, p.Type.ToDisplayString())).ToList(),
            TypedParameters: typedParams,
            EngineParamIndex: engineParamIndex,
            ReturnShape: returnShape,
            ReturnTypeName: returnTypeName,
            ElementTypeName: elementTypeName);
    }

    private static string EmitGroup(
        string ns, IReadOnlyList<TypeRing> typeChain, IReadOnlyList<BridgeModel> bridges)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        if (!string.IsNullOrEmpty(ns))
        {
            sb.Append("namespace ").Append(ns).AppendLine(";");
            sb.AppendLine();
        }

        // Open the outer-class partials so the innermost type
        // (where the bridges live) is declared in its real
        // hierarchy. "partial" is repeated at every level — that's
        // how C# allows extending nested types without naming the
        // outer hierarchy with `[GeneratedCode]` etc.
        string indent = "";
        foreach (var ring in typeChain)
        {
            sb.Append(indent).Append("partial ").Append(ring.Kind).Append(' ')
              .Append(ring.Name).AppendLine();
            sb.Append(indent).AppendLine("{");
            indent += "    ";
        }

        foreach (var b in bridges)
        {
            EmitBridge(sb, indent, b);
            sb.AppendLine();
        }

        for (int i = typeChain.Count - 1; i >= 0; i--)
        {
            indent = new string(' ', i * 4);
            sb.Append(indent).AppendLine("}");
        }

        return sb.ToString();
    }

    private static void EmitBridge(StringBuilder sb, string indent, BridgeModel b)
    {
        string staticness = b.IsStatic ? "static " : "";
        string bridgeName = "_" + b.MethodName + "_PrologBridge";

        sb.Append(indent).Append("/// <summary>Chunk 242 generated bridge for ")
          .Append(b.MethodName)
          .AppendLine(". Decodes registers via FromTerm&lt;T&gt;, calls the user method,");
        sb.Append(indent).AppendLine("/// then unifies the encoded return value (when non-void / non-bool).</summary>");
        sb.Append(indent).Append("public ").Append(staticness)
          .Append("bool ").Append(bridgeName)
          .AppendLine("(global::Shumway.Core.Engine engine)");
        sb.Append(indent).AppendLine("{");
        sb.Append(indent).AppendLine("    var host = (global::Shumway.Embedding.PrologEngine)engine.Host!;");

        // Chunk 246 — per-parameter decode based on mode:
        //   Input (+) : FromTerm<T>(...) — fails with
        //     instantiation_error if the register is a var.
        //   Output (-) : just declare the local; the user method
        //     assigns it via the `out` parameter.
        //   Bidirectional (?) : read the register, decode if bound
        //     (FromTerm<T>(...)), else null. The local is
        //     Nullable<T>; the user can check .HasValue, mutate
        //     via `ref`. Post-call we unify if .HasValue.
        for (int i = 0; i < b.TypedParameters.Count; i++)
        {
            var p = b.TypedParameters[i];
            switch (p.Mode)
            {
                case ParamMode.Input:
                    // Chunk 246 — explicit instantiation_error for
                    // + mode lets the user see an ISO-shaped Prolog
                    // error rather than the InvalidCastException
                    // the converter would raise on a VarTerm.
                    sb.Append(indent).Append("    var __t").Append(i)
                      .Append(" = global::Shumway.Embedding.RegisterMarshalling.ReadRegisterAsTerm(engine, ")
                      .Append(i).AppendLine(");");
                    sb.Append(indent).Append("    if (__t").Append(i)
                      .AppendLine(" is global::Shumway.Compiler.Ast.VarTerm)");
                    sb.Append(indent).AppendLine("        throw new global::Shumway.Core.PrologRuntimeException(\"instantiation_error\");");
                    sb.Append(indent).Append("    var __arg")
                      .Append(i)
                      .Append(" = host.FromTerm<")
                      .Append(p.TypeName)
                      .Append(">(__t").Append(i).AppendLine(");");
                    break;
                case ParamMode.Output:
                    // The user method writes via `out`; just declare
                    // a default-initialised local (the C# `out`
                    // contract requires the callee to assign before
                    // return, so the initial value is unobservable).
                    sb.Append(indent).Append("    ").Append(p.TypeName)
                      .Append(" __arg").Append(i).AppendLine(" = default!;");
                    break;
                case ParamMode.Bidirectional:
                    // Detect bound-vs-unbound at the term level; pass
                    // a nullable to the user method through `ref`.
                    // The if/else form (rather than a `?:` ternary)
                    // is intentional: with a ternary, the type of
                    // the two branches gets unified at `int` (the
                    // FromTerm<int> branch) and `default` becomes
                    // 0, then boxes as Nullable<int>(0) at the
                    // assignment — making the param look "bound to
                    // 0" rather than unbound. Splitting forces
                    // each branch to assign at the nullable type.
                    sb.Append(indent).Append("    var __t").Append(i)
                      .Append(" = global::Shumway.Embedding.RegisterMarshalling.ReadRegisterAsTerm(engine, ")
                      .Append(i).AppendLine(");");
                    sb.Append(indent).Append("    ").Append(p.TypeName)
                      .Append(" __arg").Append(i).AppendLine(";");
                    sb.Append(indent).Append("    if (__t").Append(i)
                      .AppendLine(" is global::Shumway.Compiler.Ast.VarTerm)");
                    sb.Append(indent).Append("        __arg").Append(i).AppendLine(" = null;");
                    sb.Append(indent).AppendLine("    else");
                    sb.Append(indent).Append("        __arg").Append(i).Append(" = host.FromTerm<")
                      .Append(p.ElementTypeName).Append(">(__t").Append(i).AppendLine(");");
                    break;
            }
        }

        var callArgs = new List<string>();
        int typedIdx = 0;
        for (int i = 0; i < b.Parameters.Count; i++)
        {
            if (i == b.EngineParamIndex) { callArgs.Add("engine"); continue; }
            string prefix = b.TypedParameters[typedIdx].Mode switch
            {
                ParamMode.Output => "out ",
                ParamMode.Bidirectional => "ref ",
                _ => "",
            };
            callArgs.Add(prefix + "__arg" + typedIdx);
            typedIdx++;
        }

        // Static call qualifies through the *containing-type* name —
        // just the innermost ring is fine, because the bridge is
        // emitted inside the same type so the unqualified name
        // resolves correctly.
        string call = (b.IsStatic ? b.TypeChain[b.TypeChain.Count - 1].Name : "this")
            + "." + b.MethodName
            + "(" + string.Join(", ", callArgs) + ")";

        switch (b.ReturnShape)
        {
            case ReturnShape.Void:
                sb.Append(indent).Append("    ").Append(call).AppendLine(";");
                EmitOutRefUnify(sb, indent, b);
                sb.Append(indent).AppendLine("    return true;");
                break;
            case ReturnShape.Bool:
                sb.Append(indent).Append("    if (!(").Append(call).AppendLine(")) return false;");
                EmitOutRefUnify(sb, indent, b);
                sb.Append(indent).AppendLine("    return true;");
                break;
            case ReturnShape.Encode:
                sb.Append(indent).Append("    var __result = ").Append(call).AppendLine(";");
                sb.Append(indent).Append("    return global::Shumway.Embedding.RegisterMarshalling.UnifyRegisterWithTerm(")
                  .Append("engine, ")
                  .Append(b.TypedParameters.Count)
                  .Append(", host.ToTerm<")
                  .Append(b.ReturnTypeName)
                  .AppendLine(">(__result));");
                break;
            case ReturnShape.NonDet:
                // First call: build the iterator from the user method,
                // then hand off to the advance helper. The advance
                // helper handles both MoveNext+CP-push (for solutions)
                // and Dispose+fail (for exhaustion).
                sb.Append(indent).Append("    var __iter = ").Append(call).AppendLine("!.GetEnumerator();");
                sb.Append(indent).Append("    return ")
                  .Append(advanceHelperName(b))
                  .AppendLine("(engine, host, __iter, engine.BuiltinReturnPc);");
                sb.Append(indent).AppendLine("}");
                sb.AppendLine();
                EmitNonDetAdvance(sb, indent, b);
                return;
        }

        sb.Append(indent).AppendLine("}");
    }

    private static string advanceHelperName(BridgeModel b)
        => "_" + b.MethodName + "_PrologNonDetAdvance";

    /// <summary>Chunk 246 — after the user method returns, unify
    /// every out (-) and ref-with-bound-value (?) parameter back
    /// into its register. The pre-call decode for ? mode passed a
    /// Nullable; null means "user wants to leave the register
    /// alone", a value means "unify it" (which binds an unbound
    /// register or checks-equality against a bound one — the unify
    /// failure path is what makes a ?-mode "wrong-value" call
    /// fail the predicate).</summary>
    private static void EmitOutRefUnify(StringBuilder sb, string indent, BridgeModel b)
    {
        for (int i = 0; i < b.TypedParameters.Count; i++)
        {
            var p = b.TypedParameters[i];
            switch (p.Mode)
            {
                case ParamMode.Output:
                    sb.Append(indent).Append("    if (!global::Shumway.Embedding.RegisterMarshalling.UnifyRegisterWithTerm(")
                      .Append("engine, ").Append(i)
                      .Append(", host.ToTerm<").Append(p.TypeName).Append(">(__arg")
                      .Append(i).AppendLine("))) return false;");
                    break;
                case ParamMode.Bidirectional:
                    // Skip the unify when the user left the slot at
                    // null (no opinion). When the user set a value
                    // — including the same value the input carried
                    // — run the unify; if the register is unbound
                    // it binds, if bound it checks-equality.
                    // .Value access for Nullable<T>; the bare local
                    // for reference types (where null is the
                    // natural unbound signal).
                    string valueAccess = p.IsValueTypeNullable
                        ? $"__arg{i}.Value"
                        : $"__arg{i}";
                    sb.Append(indent).Append("    if (__arg").Append(i)
                      .Append(" is not null && !global::Shumway.Embedding.RegisterMarshalling.UnifyRegisterWithTerm(")
                      .Append("engine, ").Append(i)
                      .Append(", host.ToTerm<").Append(p.ElementTypeName).Append(">(")
                      .Append(valueAccess).AppendLine("))) return false;");
                    break;
            }
        }
    }

    /// <summary>Emits the chunk-244 non-det advance helper: one
    /// MoveNext step, push a CP that re-enters on backtrack, unify
    /// the current value with the next register. Returns false on
    /// exhaustion (the engine then continues backtracking past the
    /// foreign CP) or on a unify failure (same effect; the CP we
    /// just pushed will re-enter on the engine's backtrack pass).</summary>
    private static void EmitNonDetAdvance(StringBuilder sb, string indent, BridgeModel b)
    {
        sb.Append(indent).Append("private static bool ").Append(advanceHelperName(b))
          .Append("(global::Shumway.Core.Engine engine, global::Shumway.Embedding.PrologEngine host, ")
          .Append("global::System.Collections.Generic.IEnumerator<")
          .Append(b.ElementTypeName).AppendLine("> iter, int returnPc)");
        sb.Append(indent).AppendLine("{");
        sb.Append(indent).AppendLine("    if (!iter.MoveNext())");
        sb.Append(indent).AppendLine("    {");
        sb.Append(indent).AppendLine("        iter.Dispose();");
        sb.Append(indent).AppendLine("        return false;");
        sb.Append(indent).AppendLine("    }");
        // Push a re-arming CP that, on backtrack, advances the
        // iterator one more step. The CP captures the iterator and
        // the resume PC by closure. The onPrune callback (chunk
        // 245) Disposes the iterator if Prolog `!` cuts past this
        // CP without the engine backtracking through it — the
        // MoveNext-returns-false path of this helper already
        // handles the exhaustion case.
        sb.Append(indent).Append("    var __capturedIter = iter;");
        sb.AppendLine();
        sb.Append(indent).Append("    engine.PushBuiltinChoicePoint((e, _) =>");
        sb.AppendLine();
        sb.Append(indent).AppendLine("    {");
        sb.Append(indent).Append("        bool __ok = ")
          .Append(advanceHelperName(b)).AppendLine("(e, host, __capturedIter, returnPc);");
        sb.Append(indent).AppendLine("        if (__ok) e.ResumeAtReturnPc(returnPc);");
        sb.Append(indent).AppendLine("        return __ok;");
        sb.Append(indent).AppendLine("    }, arity: 0, onPrune: __capturedIter.Dispose);");
        // Unify the current value with the register right after the
        // typed input args.
        sb.Append(indent).Append("    return global::Shumway.Embedding.RegisterMarshalling.UnifyRegisterWithTerm(")
          .Append("engine, ").Append(b.TypedParameters.Count)
          .Append(", host.ToTerm<").Append(b.ElementTypeName).AppendLine(">(iter.Current));");
        sb.Append(indent).AppendLine("}");
    }

    private enum ReturnShape { Void, Bool, Encode, NonDet }

    private sealed record BridgeModel(
        string Namespace,
        List<TypeRing> TypeChain,
        string MethodName,
        bool IsStatic,
        List<(string Name, string TypeName)> Parameters,
        List<TypedParam> TypedParameters,
        int EngineParamIndex,
        ReturnShape ReturnShape,
        string? ReturnTypeName,
        string? ElementTypeName);

    private enum ParamMode { Input, Output, Bidirectional }

    private readonly record struct TypedParam(
        string Name,
        string TypeName,
        string ElementTypeName,
        int OriginalIndex,
        ParamMode Mode,
        bool IsValueTypeNullable);

    private readonly record struct TypeRing(string Name, string Kind, bool IsStatic);
}
