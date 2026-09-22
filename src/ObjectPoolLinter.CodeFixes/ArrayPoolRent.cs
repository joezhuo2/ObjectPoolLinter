using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using static ObjectPoolLinter.CodeFixSupport;

namespace ObjectPoolLinter
{
    /// <summary>
    /// One array allocation the <c>ArrayPool&lt;T&gt;.Shared</c> fix can rewrite, together with
    /// everything the rewrite needs: the statements it wraps in <c>try</c>/<c>finally</c>, and the
    /// <c>Length</c> reads it has to redirect, because a rented array is allowed to be longer than
    /// the length that was asked for.
    /// </summary>
    internal sealed class ArrayPoolPlan
    {
        internal ArrayPoolPlan(
            ArrayCreationExpressionSyntax creation,
            ExpressionSyntax size,
            LocalDeclarationStatementSyntax declaration,
            BlockSyntax block,
            string elementType,
            string bufferName,
            string? lengthName,
            IReadOnlyList<MemberAccessExpressionSyntax> lengthReads,
            bool clearOnReturn)
        {
            Creation = creation;
            Size = size;
            Declaration = declaration;
            Block = block;
            ElementType = elementType;
            BufferName = bufferName;
            LengthName = lengthName;
            LengthReads = lengthReads;
            ClearOnReturn = clearOnReturn;
        }

        internal ArrayCreationExpressionSyntax Creation { get; }

        internal ExpressionSyntax Size { get; }

        internal LocalDeclarationStatementSyntax Declaration { get; }

        internal BlockSyntax Block { get; }

        internal string ElementType { get; }

        internal string BufferName { get; }

        // The local holding the length that was asked for, or null when nothing reads Length.
        internal string? LengthName { get; }

        internal IReadOnlyList<MemberAccessExpressionSyntax> LengthReads { get; }

        // Reference elements are cleared on the way back, or the pool keeps them alive for as long as
        // it holds the buffer.
        internal bool ClearOnReturn { get; }
    }

    /// <summary>
    /// Plans and applies the "Rent the array from ArrayPool&lt;T&gt;.Shared" fix for OPL001 array
    /// allocations. The rewrite is offered only where the buffer provably does not outlive the
    /// block - it is indexed and its <c>Length</c> is read, and nothing else - because a buffer that
    /// escapes would be read after it has been returned to the pool.
    /// </summary>
    internal static class ArrayPoolRewrite
    {
        private const string ArrayPoolMetadataName = "System.Buffers.ArrayPool`1";
        private const string ArrayPool = "System.Buffers.ArrayPool";

        internal static ArrayPoolPlan? TryPlan(
            SemanticModel semanticModel,
            SyntaxNode node,
            CancellationToken cancellationToken)
        {
            if (node is not ArrayCreationExpressionSyntax { Initializer: null } creation) return null;
            if (semanticModel.Compilation.GetTypeByMetadataName(ArrayPoolMetadataName) == null) return null;

            if (creation.Type.RankSpecifiers.Count != 1) return null;
            var sizes = creation.Type.RankSpecifiers[0].Sizes;
            if (sizes.Count != 1 || sizes[0] is OmittedArraySizeExpressionSyntax) return null;

            if (semanticModel.GetTypeInfo(creation, cancellationToken).Type is not IArrayTypeSymbol { Rank: 1 } arrayType) return null;
            if (!IsNameable(arrayType.ElementType)) return null;

            if (creation.Parent is not EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator }) return null;
            if (declarator.Parent?.Parent is not LocalDeclarationStatementSyntax declaration) return null;
            if (declaration.Declaration.Variables.Count != 1) return null;
            if (!declaration.UsingKeyword.IsKind(SyntaxKind.None) || declaration.IsConst) return null;
            if (declaration.Parent is not BlockSyntax block) return null;

            if (semanticModel.GetDeclaredSymbol(declarator, cancellationToken) is not ILocalSymbol local) return null;

            // A `yield return` may not sit in a try block with a finally clause.
            var index = block.Statements.IndexOf(declaration);
            var moved = block.Statements.Skip(index + 1).ToList();
            if (moved.Any(s => s.DescendantNodesAndSelf().Any(n => n is YieldStatementSyntax))) return null;

            var lengthReads = new List<MemberAccessExpressionSyntax>();
            var uses = 0;

            foreach (var reference in block.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (reference.Identifier.ValueText != local.Name) continue;
                if (!SymbolEqualityComparer.Default.Equals(semanticModel.GetSymbolInfo(reference, cancellationToken).Symbol, local)) continue;

                // Read inside a lambda or a local function the buffer can outlive the block through.
                if (reference.Ancestors().TakeWhile(a => a != block)
                        .Any(a => a is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
                    return null;

                uses++;

                switch (reference.Parent)
                {
                    case ElementAccessExpressionSyntax elementAccess when elementAccess.Expression == reference:
                        break;
                    case MemberAccessExpressionSyntax memberAccess when memberAccess.Expression == reference &&
                                                                       memberAccess.Name.Identifier.ValueText == "Length":
                        lengthReads.Add(memberAccess);
                        break;
                    default:
                        // Passed on, assigned, returned, enumerated: the buffer may be kept.
                        return null;
                }
            }

            // Nothing reads the buffer, so there is nothing to wrap and nothing to gain.
            if (uses == 0) return null;

            var bufferName = declarator.Identifier.ValueText;
            var lengthName = lengthReads.Count == 0
                ? null
                : FreeLocalName(bufferName + "Length", IdentifierTexts((SyntaxNode?)block.FirstAncestorOrSelf<MemberDeclarationSyntax>() ?? block));

            return new ArrayPoolPlan(
                creation,
                sizes[0],
                declaration,
                block,
                arrayType.ElementType.ToMinimalDisplayString(semanticModel, creation.SpanStart),
                bufferName,
                lengthName,
                lengthReads,
                !arrayType.ElementType.IsValueType);
        }

        internal static Document Apply(Document document, SyntaxNode root, ArrayPoolPlan plan)
        {
            var eol = GetEndOfLine(root);
            var indent = Indentation(plan.Declaration);
            var unit = plan.Block.FirstAncestorOrSelf<ClassDeclarationSyntax>() is { } @class ? IndentUnit(@class) : "    ";

            var rent = ArrayPool + "<" + plan.ElementType + ">.Shared.Rent(" +
                       (plan.LengthName ?? plan.Size.ToString()) + ")";

            var replacements = new Dictionary<SyntaxNode, SyntaxNode>
            {
                [plan.Creation] = SyntaxFactory.ParseExpression(rent).WithTriviaFrom(plan.Creation),
            };

            if (plan.LengthName != null)
            {
                foreach (var read in plan.LengthReads)
                    replacements[read] = SyntaxFactory.IdentifierName(plan.LengthName).WithTriviaFrom(read);
            }

            var index = plan.Block.Statements.IndexOf(plan.Declaration);
            var rewritten = plan.Block.ReplaceNodes(replacements.Keys, (original, _) => replacements[original]);

            var statements = rewritten.Statements;
            var declaration = statements[index];
            var moved = statements.Skip(index + 1).ToList();

            var newStatements = statements.Take(index).ToList();

            if (plan.LengthName != null)
            {
                // The length is read before it is rented, so a size expression with a side effect runs
                // exactly once, and the first new statement keeps whatever stood above the declaration.
                newStatements.Add(SyntaxFactory.ParseStatement(indent + "int " + plan.LengthName + " = " + plan.Size + ";" + eol)
                    .WithLeadingTrivia(declaration.GetLeadingTrivia()));
                declaration = declaration.WithLeadingTrivia(SyntaxFactory.Whitespace(indent));
            }

            newStatements.Add(declaration);
            newStatements.Add(WrapInTryFinally(plan, moved, indent, unit, eol));

            var newRoot = root.ReplaceNode(plan.Block, rewritten.WithStatements(SyntaxFactory.List(newStatements)));
            return document.WithSyntaxRoot(newRoot);
        }

        private static StatementSyntax WrapInTryFinally(
            ArrayPoolPlan plan,
            IReadOnlyList<StatementSyntax> moved,
            string indent,
            string unit,
            string eol)
        {
            var body = string.Concat(moved.Select(s => s.ToFullString()));
            body = Indent(body, unit, eol);
            if (body.Length > 0 && !body.EndsWith(eol, StringComparison.Ordinal)) body += eol;

            var returnCall = ArrayPool + "<" + plan.ElementType + ">.Shared.Return(" + plan.BufferName +
                             (plan.ClearOnReturn ? ", clearArray: true" : string.Empty) + ");";

            var text = indent + "try" + eol +
                       indent + "{" + eol +
                       body +
                       indent + "}" + eol +
                       indent + "finally" + eol +
                       indent + "{" + eol +
                       indent + unit + returnCall + eol +
                       indent + "}" + eol;

            return SyntaxFactory.ParseStatement(text);
        }

        private static string Indent(string text, string unit, string eol)
        {
            var lines = text.Split(new[] { eol }, StringSplitOptions.None);

            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Length > 0) lines[i] = unit + lines[i];
            }

            return string.Join(eol, lines);
        }
    }
}
