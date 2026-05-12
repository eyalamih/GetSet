using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace GetSet
{
    /// <summary>
    /// Roslyn Incremental Source Generator for the GetSet shorthand syntax.
    ///
    /// Usage — instead of the normal C# property boilerplate:
    ///
    ///     private int _num = 5;
    ///     public int num
    ///     {
    ///         get { return _num; }
    ///         set { if (value >= 0) _num = value; }
    ///     }
    ///
    /// You can write:
    ///
    ///     public int num = 5
    ///     {
    ///         get { return num; }
    ///         set { if (value >= 0) num = value; }
    ///     }
    ///
    /// The generator detects this pattern and emits a partial class containing:
    ///   - A private backing field  (_num)
    ///   - The full public property (num) with your getter/setter bodies,
    ///     with all references to "num" rewritten to "_num" inside the bodies.
    ///
    /// IMPORTANT — the consuming class must be declared "partial".
    /// </summary>
    [Generator]
    public sealed class GetSetGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // 1. Inject the marker attribute into the user's compilation so
            //    they can optionally annotate their class, though it's not
            //    required — the generator also activates on any class that
            //    contains the shorthand syntax.
            context.RegisterPostInitializationOutput(ctx =>
            {
                ctx.AddSource("GetSetAttribute.g.cs", SourceText.From(AttributeSource, Encoding.UTF8));
            });

            // 2. Collect every syntax tree that has at least one field with
            //    an attached block (the shorthand).  We use a SyntaxProvider
            //    that fires per-file so we only re-run on changed files.
            var fieldsProvider = context.SyntaxProvider
                .CreateSyntaxProvider(
                    predicate: static (node, _) => node is FieldDeclarationSyntax,
                    transform: static (ctx, _) => GetFieldIfShorthand(ctx))
                .Where(static info => info != null)
                .Select(static (info, _) => info!);

            // 3. Group by class so we emit one partial class per type.
            var grouped = fieldsProvider.Collect();

            context.RegisterSourceOutput(grouped, static (ctx, fields) =>
            {
                var byClass = new Dictionary<string, List<GetSetFieldInfo>>();

                foreach (var field in fields)
                {
                    // Walk up to find the containing class / struct.
                    var containingType = field.FieldDeclaration
                        .Ancestors()
                        .OfType<TypeDeclarationSyntax>()
                        .FirstOrDefault();

                    if (containingType == null) continue;

                    // We need the fully-qualified class key so that two classes
                    // with the same name in different namespaces don't collide.
                    string key = BuildTypeKey(containingType);
                    if (!byClass.ContainsKey(key))
                        byClass[key] = new List<GetSetFieldInfo>();
                    byClass[key].Add(field);
                }

                foreach (var kvp in byClass)
                {
                    string source = EmitPartialClass(kvp.Value);
                    string hintName = kvp.Key.Replace('.', '_').Replace('<', '_').Replace('>', '_')
                                     + ".GetSet.g.cs";
                    ctx.AddSource(hintName, SourceText.From(source, Encoding.UTF8));
                }
            });
        }

        // ------------------------------------------------------------------ //
        //  Detection                                                           //
        // ------------------------------------------------------------------ //

        private static GetSetFieldInfo? GetFieldIfShorthand(GeneratorSyntaxContext ctx)
        {
            var field = (FieldDeclarationSyntax)ctx.Node;

            // Quick check: any skipped-token trivia after the field?
            bool hasSkipped = field.GetTrailingTrivia()
                .Any(t => t.IsKind(SyntaxKind.SkippedTokensTrivia));

            if (!hasSkipped)
            {
                // Also check next sibling's leading trivia.
                var parent = field.Parent;
                if (parent != null)
                {
                    bool foundSelf = false;
                    foreach (var child in parent.ChildNodes())
                    {
                        if (foundSelf)
                        {
                            hasSkipped = child.GetLeadingTrivia()
                                .Any(t => t.IsKind(SyntaxKind.SkippedTokensTrivia));
                            break;
                        }
                        if (child == field) foundSelf = true;
                    }
                }
            }

            if (!hasSkipped) return null;

            // Run the full walker on just this file's source text.
            string source = field.SyntaxTree.ToString();
            var walker = new GetSetSyntaxWalker(source);
            walker.Visit(field);

            return walker.Found.Count > 0 ? walker.Found[0] : null;
        }

        // ------------------------------------------------------------------ //
        //  Code emission                                                       //
        // ------------------------------------------------------------------ //

        private static string EmitPartialClass(List<GetSetFieldInfo> fields)
        {
            // All fields share the same containing type — grab it from the first.
            var firstField = fields[0].FieldDeclaration;
            var typeDecl = firstField.Ancestors().OfType<TypeDeclarationSyntax>().First();

            string typeKind = typeDecl is StructDeclarationSyntax ? "struct" : "class";
            string typeName = typeDecl.Identifier.Text;
            if (typeDecl.TypeParameterList != null)
                typeName += typeDecl.TypeParameterList.ToFullString().Trim();

            // Collect namespace (may be file-scoped or block-scoped).
            string? ns = GetNamespace(typeDecl);

            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("// Generated by GetSet source generator.");
            sb.AppendLine("// Do not edit this file manually.");
            sb.AppendLine();
            sb.AppendLine("using System;");
            sb.AppendLine();

            bool hasNamespace = !string.IsNullOrWhiteSpace(ns);
            if (hasNamespace)
            {
                sb.AppendLine($"namespace {ns}");
                sb.AppendLine("{");
            }

            // Outer accessibility modifiers on the type.
            string typeModifiers = BuildTypeModifiers(typeDecl.Modifiers);
            sb.AppendLine($"    {typeModifiers} partial {typeKind} {typeName}");
            sb.AppendLine("    {");

            foreach (var info in fields)
            {
                EmitProperty(sb, info);
            }

            sb.AppendLine("    }");

            if (hasNamespace)
                sb.AppendLine("}");

            return sb.ToString();
        }

        private static void EmitProperty(StringBuilder sb, GetSetFieldInfo info)
        {
            string backingField = info.BackingFieldName;
            string propName = info.FieldName;

            // Private backing field.
            string init = info.Initializer != null ? " " + info.Initializer : "";
            sb.AppendLine($"        private {info.PropertyType} {backingField}{init};");
            sb.AppendLine();

            // Rewrite the accessor bodies: replace bare references to the
            // property name with the backing field name.
            string getBody = RewriteFieldRefs(info.GetBody, propName, backingField);
            string setBody = info.HasSetter
                ? RewriteFieldRefs(info.SetBody, propName, backingField)
                : string.Empty;

            // Property declaration.
            sb.AppendLine($"        {info.Modifiers} {info.PropertyType} {propName}");
            sb.AppendLine("        {");
            sb.AppendLine($"            get {getBody}");
            if (info.HasSetter)
                sb.AppendLine($"            set {setBody}");
            sb.AppendLine("        }");
            sb.AppendLine();
        }

        // ------------------------------------------------------------------ //
        //  Backing-field reference rewriter                                   //
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Replaces references to <paramref name="propName"/> with
        /// <paramref name="backingField"/> inside an accessor body,
        /// using a word-boundary-aware replacement so "numeral" is not
        /// accidentally changed to "_numeral".
        /// </summary>
        private static string RewriteFieldRefs(string body, string propName, string backingField)
        {
            // Simple but robust: walk character-by-character and replace
            // whole-word occurrences only.
            var sb = new StringBuilder(body.Length);
            int i = 0;
            while (i < body.Length)
            {
                // Check if we're at a word boundary match.
                if (i + propName.Length <= body.Length &&
                    string.Compare(body, i, propName, 0, propName.Length, StringComparison.Ordinal) == 0)
                {
                    bool prevOk  = i == 0 || !IsIdentChar(body[i - 1]);
                    bool nextOk  = (i + propName.Length) >= body.Length ||
                                   !IsIdentChar(body[i + propName.Length]);

                    if (prevOk && nextOk)
                    {
                        sb.Append(backingField);
                        i += propName.Length;
                        continue;
                    }
                }
                sb.Append(body[i]);
                i++;
            }
            return sb.ToString();
        }

        private static bool IsIdentChar(char c) =>
            char.IsLetterOrDigit(c) || c == '_';

        // ------------------------------------------------------------------ //
        //  Namespace / type helpers                                            //
        // ------------------------------------------------------------------ //

        private static string? GetNamespace(SyntaxNode node)
        {
            // Handle both block-scoped and file-scoped namespaces.
            var ns = node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
            return ns?.Name.ToFullString().Trim();
        }

        private static string BuildTypeKey(TypeDeclarationSyntax typeDecl)
        {
            string? ns = GetNamespace(typeDecl);
            string name = typeDecl.Identifier.Text;
            return string.IsNullOrWhiteSpace(ns) ? name : ns + "." + name;
        }

        private static string BuildTypeModifiers(SyntaxTokenList tokens)
        {
            var parts = new List<string>();
            foreach (var t in tokens)
            {
                // "partial" will be added by the generator; skip if already there.
                if (t.IsKind(SyntaxKind.PartialKeyword)) continue;
                parts.Add(t.Text);
            }
            return string.Join(" ", parts);
        }

        // ------------------------------------------------------------------ //
        //  Injected attribute source                                           //
        // ------------------------------------------------------------------ //

        private const string AttributeSource = @"
// <auto-generated/>
namespace GetSet
{
    /// <summary>
    /// Optional marker attribute.  Apply to a partial class to explicitly
    /// signal that it uses GetSet shorthand syntax.  The source generator
    /// will activate regardless of whether this attribute is present.
    /// </summary>
    [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Struct, AllowMultiple = false)]
    public sealed class UseGetSetAttribute : System.Attribute { }
}
";
    }
}
