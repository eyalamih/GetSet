using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace GetSet
{
    /// <summary>
    /// Represents a detected GetSet field declaration:
    ///
    ///     public int num = 5
    ///     {
    ///         get { return num; }
    ///         set { if (value >= 0) num = value; }
    ///     }
    ///
    /// The parser identifies these by finding a FieldDeclarationSyntax whose
    /// variable declarator is immediately followed by a block — which is not
    /// normally valid C# syntax, so Roslyn parses the block as part of an
    /// initializer expression error node.  We fish it out from the trivia /
    /// error-recovery tree.
    /// </summary>
    internal sealed class GetSetFieldInfo
    {
        public FieldDeclarationSyntax FieldDeclaration { get; }
        public string FieldName { get; }
        public string BackingFieldName { get; }
        public string PropertyType { get; }
        public string Modifiers { get; }        // "public", "public static", …
        public string? Initializer { get; }     // "= 5", or null
        public string GetBody { get; }
        public string SetBody { get; }          // empty string if getter-only
        public bool HasSetter { get; }

        public GetSetFieldInfo(
            FieldDeclarationSyntax fieldDeclaration,
            string fieldName,
            string propertyType,
            string modifiers,
            string? initializer,
            string getBody,
            string setBody)
        {
            FieldDeclaration = fieldDeclaration;
            FieldName = fieldName;
            BackingFieldName = "_" + char.ToLowerInvariant(fieldName[0]) + fieldName.Substring(1);
            PropertyType = propertyType;
            Modifiers = modifiers;
            Initializer = initializer;
            GetBody = getBody;
            SetBody = setBody;
            HasSetter = !string.IsNullOrWhiteSpace(setBody);
        }
    }

    /// <summary>
    /// Walks the syntax tree of a single file looking for the GetSet shorthand.
    ///
    /// Because the shorthand is not valid C#, Roslyn's error-recovery parser
    /// attaches the { get { … } set { … } } block as trailing trivia / skipped
    /// tokens on the field declaration.  We extract the raw source text for
    /// those tokens and re-parse just that fragment as a C# block so we can
    /// pull out the getter and setter bodies.
    /// </summary>
    internal sealed class GetSetSyntaxWalker : CSharpSyntaxWalker
    {
        private readonly string _sourceText;
        public List<GetSetFieldInfo> Found { get; } = new List<GetSetFieldInfo>();

        public GetSetSyntaxWalker(string sourceText) : base(SyntaxWalkerDepth.StructuredTrivia)
        {
            _sourceText = sourceText;
        }

        public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
        {
            TryExtract(node);
            base.VisitFieldDeclaration(node);
        }

        // ------------------------------------------------------------------ //
        //  Core extraction logic                                               //
        // ------------------------------------------------------------------ //

        private void TryExtract(FieldDeclarationSyntax field)
        {
            // We expect exactly ONE variable in the declaration (e.g. "int num = 5").
            if (field.Declaration.Variables.Count != 1)
                return;

            var variable = field.Declaration.Variables[0];

            // Collect skipped-token trivia that Roslyn parked after the field.
            // This is where the { get { … } set { … } } block ends up.
            string skipped = CollectSkippedTrivia(field);
            if (string.IsNullOrWhiteSpace(skipped))
                return;

            skipped = skipped.Trim();
            if (!skipped.StartsWith("{"))
                return;

            // Re-parse the skipped fragment as a C# block statement so we can
            // navigate it properly with Roslyn APIs.
            string wrappedBlock = "class __Dummy__ { void __M__() " + skipped + " }";
            SyntaxTree tempTree = CSharpSyntaxTree.ParseText(wrappedBlock);
            var root = tempTree.GetRoot();

            // Pull the first Block inside the dummy method.
            BlockSyntax? outerBlock = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .Select(m => m.Body)
                .FirstOrDefault(b => b != null);

            if (outerBlock == null || outerBlock.Statements.Count == 0)
                return;

            // We expect the outer block to have a single statement that is
            // itself a block — or, in the case of nested get/set blocks, we
            // look for ExpressionStatements whose leading identifier is "get"
            // or "set".
            string? getBody = null;
            string? setBody = null;

            foreach (var stmt in outerBlock.Statements)
            {
                string stmtText = stmt.ToFullString().Trim();

                if (stmtText.StartsWith("get", StringComparison.OrdinalIgnoreCase))
                    getBody = ExtractBraceBody(stmtText, "get");
                else if (stmtText.StartsWith("set", StringComparison.OrdinalIgnoreCase))
                    setBody = ExtractBraceBody(stmtText, "set");
            }

            // Fallback: if Roslyn collapsed everything into one statement,
            // do a manual text scan.
            if (getBody == null)
            {
                getBody = ExtractAccessorByKeyword(skipped, "get");
                setBody = ExtractAccessorByKeyword(skipped, "set");
            }

            if (getBody == null)
                return;   // No getter found — not a valid GetSet block.

            // Build modifiers string (strip any field-only keywords that don't
            // apply to properties, such as "readonly").
            string modifiers = BuildModifiers(field.Modifiers);

            // Grab the type text.
            string typeName = field.Declaration.Type.ToFullString().Trim();

            // Grab the initializer text (the "= 5" part), if present.
            string? initializer = variable.Initializer?.ToFullString().Trim();

            string fieldName = variable.Identifier.Text;

            Found.Add(new GetSetFieldInfo(
                field,
                fieldName,
                typeName,
                modifiers,
                initializer,
                getBody,
                setBody ?? string.Empty));
        }

        // ------------------------------------------------------------------ //
        //  Helpers                                                             //
        // ------------------------------------------------------------------ //

        private static string CollectSkippedTrivia(SyntaxNode node)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (var trivia in node.GetTrailingTrivia())
            {
                if (trivia.IsKind(SyntaxKind.SkippedTokensTrivia) ||
                    trivia.IsKind(SyntaxKind.DisabledTextTrivia))
                {
                    sb.Append(trivia.ToFullString());
                }
            }
            // Also check the next sibling's leading trivia — Roslyn sometimes
            // parks the skipped block there instead.
            var parent = node.Parent;
            if (parent != null)
            {
                bool foundSelf = false;
                foreach (var child in parent.ChildNodes())
                {
                    if (foundSelf)
                    {
                        foreach (var t in child.GetLeadingTrivia())
                        {
                            if (t.IsKind(SyntaxKind.SkippedTokensTrivia))
                                sb.Append(t.ToFullString());
                        }
                        break;
                    }
                    if (child == node) foundSelf = true;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Given a fragment like:
        ///   get { return _num; }
        /// or
        ///   get\r\n{\r\n    return _num;\r\n}
        /// extracts everything between the outermost braces.
        /// </summary>
        private static string ExtractBraceBody(string text, string keyword)
        {
            int keywordIdx = text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
            if (keywordIdx < 0) return "";
            int braceStart = text.IndexOf('{', keywordIdx + keyword.Length);
            if (braceStart < 0) return "";
            int depth = 0;
            for (int i = braceStart; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                        return text.Substring(braceStart, i - braceStart + 1).Trim();
                }
            }
            return "";
        }

        /// <summary>
        /// Scans the outer block text for the get/set accessor by keyword.
        /// </summary>
        private static string? ExtractAccessorByKeyword(string block, string keyword)
        {
            // Strip the very outer { }
            int firstBrace = block.IndexOf('{');
            int lastBrace = block.LastIndexOf('}');
            if (firstBrace < 0 || lastBrace <= firstBrace)
                return null;

            string inner = block.Substring(firstBrace + 1, lastBrace - firstBrace - 1);
            int kw = inner.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
            if (kw < 0) return null;

            int braceStart = inner.IndexOf('{', kw + keyword.Length);
            if (braceStart < 0) return null;

            int depth = 0;
            for (int i = braceStart; i < inner.Length; i++)
            {
                if (inner[i] == '{') depth++;
                else if (inner[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                        return inner.Substring(braceStart, i - braceStart + 1).Trim();
                }
            }
            return null;
        }

        private static string BuildModifiers(SyntaxTokenList tokens)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (var t in tokens)
            {
                // Skip field-only modifiers that are invalid on properties.
                if (t.IsKind(SyntaxKind.ReadOnlyKeyword) ||
                    t.IsKind(SyntaxKind.VolatileKeyword))
                    continue;

                if (sb.Length > 0) sb.Append(' ');
                sb.Append(t.Text);
            }
            return sb.ToString();
        }
    }
}
