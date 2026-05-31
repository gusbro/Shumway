using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Shumway.SourceGen;

/// <summary>
/// Chunk 241 — Roslyn incremental source generator for
/// <c>[Shumway.Embedding.PrologTerm]</c>. For every type declared
/// with the attribute, emits a <c>partial</c> extension with:
///
/// <list type="bullet">
/// <item><c>public Shumway.Compiler.Ast.Term ToPrologTerm(Shumway.Embedding.PrologEngine engine)</c>
///   — encodes the instance as a compound term whose functor is the
///   attribute's <c>Functor</c> (default: the type name as written)
///   and whose arguments are the declared <c>FieldOrProperty</c>
///   members converted via <see cref="Shumway.Embedding.PrologEngine.ToTerm{T}"/>.</item>
/// <item><c>public static T FromPrologTerm(Shumway.Compiler.Ast.Term term)</c>
///   — decodes a matching compound term back into an instance,
///   dispatching each argument through
///   <see cref="Shumway.Embedding.PrologEngine.FromTerm{T}"/>. Requires
///   the type either expose a matching positional constructor (the
///   record / primary-constructor case) or a parameterless
///   constructor plus settable members.</item>
/// </list>
///
/// <para>The runtime <see cref="Shumway.Embedding.PrologEngine.ToTerm{T}"/>
/// dispatcher discovers these methods via convention (one
/// reflection probe per type, cached) — no module-initializer
/// registration step is required, so a generator-produced type
/// works the same whether built ahead of time or hot-loaded.</para>
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class PrologTermGenerator : IIncrementalGenerator
{
    private const string AttributeNamespace = "Shumway.Embedding";
    private const string AttributeName = "PrologTermAttribute";
    private const string FullAttributeName = AttributeNamespace + "." + AttributeName;

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var candidates = context.SyntaxProvider.ForAttributeWithMetadataName(
            FullAttributeName,
            predicate: static (node, _) =>
                node is ClassDeclarationSyntax
                    or StructDeclarationSyntax
                    or RecordDeclarationSyntax,
            transform: static (ctx, _) => BuildModel(ctx))
            .Where(static m => m is not null);

        context.RegisterSourceOutput(candidates,
            static (spc, model) => spc.AddSource(
                hintName: $"{model!.NamespaceSlug}_{model.ChainSlug}.PrologTerm.g.cs",
                source: Emit(model)));
    }

    private static TermModel? BuildModel(GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol type) return null;

        // Functor: explicit ctor argument wins; otherwise the type
        // name as declared in the source (no lowercasing — atoms
        // are case-sensitive and the user picks the convention).
        string functor = type.Name;
        foreach (var attr in ctx.Attributes)
        {
            if (attr.ConstructorArguments.Length == 1
                && attr.ConstructorArguments[0].Value is string explicitFunctor
                && !string.IsNullOrEmpty(explicitFunctor))
            {
                functor = explicitFunctor;
            }
        }

        // Members in declaration order. Records gain their primary-
        // constructor parameters as auto-properties, which show up
        // here; that's the intended common case.
        var members = type.GetMembers()
            .Where(m =>
                (m is IPropertySymbol { IsStatic: false, IsIndexer: false, DeclaredAccessibility: Accessibility.Public })
                || (m is IFieldSymbol { IsStatic: false, IsConst: false, DeclaredAccessibility: Accessibility.Public,
                    AssociatedSymbol: null }))
            .Select(m => m switch
            {
                IPropertySymbol p => new TermMember(p.Name, p.Type.ToDisplayString(), p.IsReadOnly),
                IFieldSymbol f => new TermMember(f.Name, f.Type.ToDisplayString(), f.IsReadOnly),
                _ => default!,
            })
            .ToArray();

        var ns = type.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : type.ContainingNamespace.ToDisplayString();

        bool isRecord = type.IsRecord;

        // Find a positional ctor whose parameter names line up
        // case-insensitively with the chosen members — that's the
        // "record" / "primary-constructor" decoding path. If none
        // is found, the decoder falls back to a parameterless ctor
        // + member assignment.
        bool hasPositionalCtor = type.InstanceConstructors
            .Any(c => c.Parameters.Length == members.Length
                && c.Parameters.Select(p => p.Name)
                    .SequenceEqual(members.Select(m => m.Name),
                        System.StringComparer.OrdinalIgnoreCase));

        // Walk up containing types so a nested [PrologTerm] type
        // emits its `partial Outer { partial Inner { ... } }`
        // hierarchy correctly.
        var typeChain = new System.Collections.Generic.List<TypeRing>();
        for (INamedTypeSymbol? cur = type; cur is not null; cur = cur.ContainingType)
        {
            string ringKind = cur switch
            {
                { IsRecord: true, TypeKind: TypeKind.Struct } => "record struct",
                { IsRecord: true } => "record",
                { TypeKind: TypeKind.Struct } => "struct",
                _ => "class",
            };
            typeChain.Insert(0, new TypeRing(cur.Name, ringKind));
        }
        string chainSlug = string.Join("_", typeChain.Select(r => r.Name));

        return new TermModel(
            Namespace: ns,
            NamespaceSlug: string.IsNullOrEmpty(ns) ? "_global" : ns.Replace('.', '_'),
            TypeName: type.Name,
            TypeChain: typeChain,
            ChainSlug: chainSlug,
            Functor: functor,
            Members: members,
            HasPositionalCtor: hasPositionalCtor);
    }

    private static string Emit(TermModel model)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        if (!string.IsNullOrEmpty(model.Namespace))
        {
            sb.Append("namespace ").Append(model.Namespace).AppendLine(";");
            sb.AppendLine();
        }

        // Open outer-class partials so a nested type's emit lands
        // in its real hierarchy. `i` is the per-level indent; the
        // body emits at `i` (one level past the innermost partial's
        // opening brace).
        string i = "";
        foreach (var ring in model.TypeChain)
        {
            sb.Append(i).Append("partial ").Append(ring.Kind).Append(' ')
              .Append(ring.Name).AppendLine();
            sb.Append(i).AppendLine("{");
            i += "    ";
        }

        EmitBody(sb, model, i);

        for (int k = model.TypeChain.Count - 1; k >= 0; k--)
        {
            sb.Append(new string(' ', k * 4)).AppendLine("}");
        }

        return sb.ToString();
    }

    private static void EmitBody(StringBuilder sb, TermModel model, string i)
    {
        string i1 = i + "    ";
        string i2 = i + "        ";
        string i3 = i + "            ";

        // -------- ToPrologTerm --------
        sb.Append(i).AppendLine("/// <summary>Chunk 241 generated — encodes this instance as a Prolog");
        sb.Append(i).AppendLine("/// compound term whose functor is the [PrologTerm] attribute value");
        sb.Append(i).AppendLine("/// (default: the C# type name as declared).</summary>");
        sb.Append(i).AppendLine("public global::Shumway.Compiler.Ast.Term ToPrologTerm(global::Shumway.Embedding.PrologEngine engine)");
        sb.Append(i).AppendLine("{");
        if (model.Members.Length == 0)
        {
            sb.Append(i1).Append("return new global::Shumway.Compiler.Ast.AtomTerm(\"")
              .Append(EscapeCs(model.Functor)).AppendLine("\");");
        }
        else
        {
            sb.Append(i1).AppendLine("var args = new global::Shumway.Compiler.Ast.Term[]");
            sb.Append(i1).AppendLine("{");
            foreach (var m in model.Members)
            {
                sb.Append(i2).Append("engine.ToTerm<").Append(m.TypeName).Append(">(this.")
                  .Append(m.Name).AppendLine("),");
            }
            sb.Append(i1).AppendLine("};");
            sb.Append(i1).Append("return new global::Shumway.Compiler.Ast.CompoundTerm(\"")
              .Append(EscapeCs(model.Functor)).AppendLine("\", args);");
        }
        sb.Append(i).AppendLine("}");
        sb.AppendLine();

        // -------- FromPrologTerm(Term) --------
        sb.Append(i).AppendLine("/// <summary>Chunk 241 generated — decodes a matching Prolog compound");
        sb.Append(i).AppendLine("/// term back into a fresh instance. The Term-only overload is for");
        sb.Append(i).AppendLine("/// engine-free nullary types; the 2-arg overload below carries the");
        sb.Append(i).AppendLine("/// engine for member recursion.</summary>");
        sb.Append(i).Append("public static ").Append(model.TypeName).AppendLine(" FromPrologTerm(global::Shumway.Compiler.Ast.Term term)");
        sb.Append(i).AppendLine("{");
        if (model.Members.Length == 0)
        {
            sb.Append(i1).Append("if (term is global::Shumway.Compiler.Ast.AtomTerm a && a.Name == \"")
              .Append(EscapeCs(model.Functor)).AppendLine("\")");
            sb.Append(i2).Append("return new ").Append(model.TypeName).AppendLine("();");
            sb.Append(i1).Append("throw new global::System.InvalidCastException($\"Expected atom '")
              .Append(EscapeCs(model.Functor)).AppendLine("', got {term}.\");");
        }
        else
        {
            sb.Append(i1).AppendLine("throw new global::System.InvalidOperationException(\"FromPrologTerm(Term) requires an engine for argument decoding; call FromPrologTerm(engine, term).\");");
        }
        sb.Append(i).AppendLine("}");
        sb.AppendLine();

        // -------- FromPrologTerm(PrologEngine, Term) --------
        sb.Append(i).Append("public static ").Append(model.TypeName).AppendLine(" FromPrologTerm(global::Shumway.Embedding.PrologEngine engine, global::Shumway.Compiler.Ast.Term term)");
        sb.Append(i).AppendLine("{");
        if (model.Members.Length == 0)
        {
            sb.Append(i1).Append("if (term is global::Shumway.Compiler.Ast.AtomTerm a && a.Name == \"")
              .Append(EscapeCs(model.Functor)).AppendLine("\")");
            sb.Append(i2).Append("return new ").Append(model.TypeName).AppendLine("();");
            sb.Append(i1).Append("throw new global::System.InvalidCastException($\"Expected atom '")
              .Append(EscapeCs(model.Functor)).AppendLine("', got {term}.\");");
        }
        else
        {
            sb.Append(i1).Append("if (term is not global::Shumway.Compiler.Ast.CompoundTerm c || c.Functor != \"")
              .Append(EscapeCs(model.Functor)).Append("\" || c.Args.Length != ")
              .Append(model.Members.Length).AppendLine(")");
            sb.Append(i2).Append("throw new global::System.InvalidCastException($\"Expected compound '")
              .Append(EscapeCs(model.Functor)).Append("/").Append(model.Members.Length)
              .AppendLine("', got {term}.\");");

            if (model.HasPositionalCtor)
            {
                sb.Append(i1).Append("return new ").Append(model.TypeName).AppendLine("(");
                for (int k = 0; k < model.Members.Length; k++)
                {
                    sb.Append(i3).Append("engine.FromTerm<").Append(model.Members[k].TypeName)
                      .Append(">(c.Args[").Append(k).Append("])")
                      .AppendLine(k == model.Members.Length - 1 ? ");" : ",");
                }
            }
            else
            {
                sb.Append(i1).Append("var instance = new ").Append(model.TypeName).AppendLine("();");
                for (int k = 0; k < model.Members.Length; k++)
                {
                    sb.Append(i1).Append("instance.").Append(model.Members[k].Name)
                      .Append(" = engine.FromTerm<").Append(model.Members[k].TypeName)
                      .Append(">(c.Args[").Append(k).AppendLine("]);");
                }
                sb.Append(i1).AppendLine("return instance;");
            }
        }
        sb.Append(i).AppendLine("}");
    }

    private static string EscapeCs(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private sealed record TermModel(
        string Namespace,
        string NamespaceSlug,
        string TypeName,
        System.Collections.Generic.List<TypeRing> TypeChain,
        string ChainSlug,
        string Functor,
        TermMember[] Members,
        bool HasPositionalCtor);

    private readonly record struct TermMember(string Name, string TypeName, bool IsReadOnly);

    private readonly record struct TypeRing(string Name, string Kind);
}
