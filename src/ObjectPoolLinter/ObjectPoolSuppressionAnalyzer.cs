using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ObjectPoolLinter
{
    /// <summary>
    /// Suppresses OPL001, OPL002 and OPL003 where the allocation is known not to run every frame:
    /// behind a first-frame guard, inside <c>#if UNITY_EDITOR</c>, behind a static boolean latch the
    /// guarded code sets, or assigned straight into a field, which is the caching the rules ask for.
    /// Each pattern can be switched off with <c>object_pool_linter.suppressions</c>.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class ObjectPoolSuppressionAnalyzer : DiagnosticSuppressor
    {
        internal const string FirstFrameId = "OPLS001";
        internal const string EditorOnlyId = "OPLS002";
        internal const string StaticLatchId = "OPLS003";
        internal const string CachedFieldId = "OPLS004";

        private const string TimeTypeName = "Time";
        private const string UnityEngineNamespace = "UnityEngine";
        private const string EditorSymbolPrefix = "UNITY_EDITOR";

        private static readonly ImmutableArray<string> SuppressedDiagnosticIds = ImmutableArray.Create(
            ObjectPoolAnalyzer.DiagnosticId,
            HiddenAllocationAnalyzer.DiagnosticId,
            UnityApiAllocationAnalyzer.DiagnosticId);

        private static readonly ImmutableArray<(SuppressionKind Kind, string Id, string Justification)> Patterns =
            ImmutableArray.Create(
                (SuppressionKind.EditorOnly, EditorOnlyId,
                    "The allocation is inside #if UNITY_EDITOR, so it is not in a player build."),
                (SuppressionKind.FirstFrame, FirstFrameId,
                    "The allocation is guarded by Time.frameCount == 0, so it runs on the first frame only."),
                (SuppressionKind.StaticLatch, StaticLatchId,
                    "The allocation is guarded by a static boolean latch the guarded code sets, so it runs once."),
                (SuppressionKind.CachedField, CachedFieldId,
                    "The allocated object is assigned to a field, which is the caching the rule asks for."));

        // One descriptor per pattern and suppressed rule: a descriptor names exactly one diagnostic id.
        private static readonly ImmutableDictionary<(SuppressionKind Kind, string DiagnosticId), SuppressionDescriptor> Descriptors =
            CreateDescriptors();

        public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions => Descriptors.Values.ToImmutableArray();

        public override void ReportSuppressions(SuppressionAnalysisContext context)
        {
            foreach (var diagnostic in context.ReportedDiagnostics)
            {
                var tree = diagnostic.Location.SourceTree;
                if (tree == null) continue;

                var root = tree.GetRoot(context.CancellationToken);
                var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);

                var options = LinterOptions.Parse(context.Options.AnalyzerConfigOptionsProvider.GetOptions(tree));

                SemanticModel? semanticModel = null;

                foreach (var (kind, _, _) in Patterns)
                {
                    if (!options.IsSuppressionEnabled(kind)) continue;
                    if (!Descriptors.TryGetValue((kind, diagnostic.Id), out var descriptor)) continue;

                    // The editor-only check is the only syntactic one, so the semantic model is asked
                    // for lazily: most diagnostics match no pattern at all.
                    if (kind != SuppressionKind.EditorOnly)
                        semanticModel ??= context.GetSemanticModel(tree);

                    if (!Matches(kind, node, semanticModel, context.CancellationToken)) continue;

                    context.ReportSuppression(Suppression.Create(descriptor, diagnostic));
                    break;
                }
            }
        }

        private static ImmutableDictionary<(SuppressionKind, string), SuppressionDescriptor> CreateDescriptors()
        {
            var builder = ImmutableDictionary.CreateBuilder<(SuppressionKind, string), SuppressionDescriptor>();

            foreach (var (kind, id, justification) in Patterns)
            {
                foreach (var diagnosticId in SuppressedDiagnosticIds)
                    builder[(kind, diagnosticId)] = new SuppressionDescriptor(id, diagnosticId, justification);
            }

            return builder.ToImmutable();
        }

        private static bool Matches(SuppressionKind kind, SyntaxNode node, SemanticModel? semanticModel, CancellationToken cancellationToken)
        {
            switch (kind)
            {
                case SuppressionKind.EditorOnly:
                    return IsEditorOnly(node, cancellationToken);
                case SuppressionKind.FirstFrame:
                    return semanticModel != null && IsFirstFrameGuarded(node, semanticModel, cancellationToken);
                case SuppressionKind.StaticLatch:
                    return semanticModel != null && IsLatchGuarded(node, semanticModel, cancellationToken);
                case SuppressionKind.CachedField:
                    return semanticModel != null && IsCachedInField(node, semanticModel, cancellationToken);
                default:
                    return false;
            }
        }

        // Inside the taken branch of an `#if` that requires UNITY_EDITOR (or a variant such as
        // UNITY_EDITOR_WIN). A branch reached only when the symbol is *not* defined does not qualify.
        private static bool IsEditorOnly(SyntaxNode node, CancellationToken cancellationToken)
        {
            var root = node.SyntaxTree.GetRoot(cancellationToken);

            for (var directive = root.GetFirstDirective(); directive != null; directive = directive.GetNextDirective())
            {
                if (directive is not IfDirectiveTriviaSyntax ifDirective) continue;
                if (!RequiresEditorSymbol(ifDirective.Condition)) continue;

                var related = ifDirective.GetRelatedDirectives();
                var index = related.IndexOf(ifDirective);
                if (index < 0 || index + 1 >= related.Count) continue;

                if (node.SpanStart >= ifDirective.FullSpan.End && node.Span.End <= related[index + 1].FullSpan.Start)
                    return true;
            }

            return false;
        }

        private static bool RequiresEditorSymbol(ExpressionSyntax condition)
        {
            switch (condition)
            {
                case IdentifierNameSyntax name:
                    return name.Identifier.ValueText.StartsWith(EditorSymbolPrefix, StringComparison.Ordinal);
                case ParenthesizedExpressionSyntax parenthesized:
                    return RequiresEditorSymbol(parenthesized.Expression);
                case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalAndExpression):
                    return RequiresEditorSymbol(binary.Left) || RequiresEditorSymbol(binary.Right);
                default:
                    return false;
            }
        }

        private static bool IsFirstFrameGuarded(SyntaxNode node, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            foreach (var ifStatement in GuardingIfStatements(node))
            {
                if (TestsFirstFrame(ifStatement.Condition, semanticModel, cancellationToken)) return true;
            }

            return false;
        }

        // The `if` statements whose taken branch the node sits in. The else branch is not one of them:
        // it is what runs on every other frame.
        private static IEnumerable<IfStatementSyntax> GuardingIfStatements(SyntaxNode node)
        {
            for (var ancestor = node.Parent; ancestor != null; ancestor = ancestor.Parent)
            {
                if (ancestor is IfStatementSyntax ifStatement && ifStatement.Statement.FullSpan.Contains(node.Span))
                    yield return ifStatement;
            }
        }

        private static bool TestsFirstFrame(ExpressionSyntax condition, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            switch (condition)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    return TestsFirstFrame(parenthesized.Expression, semanticModel, cancellationToken);
                case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalAndExpression):
                    return TestsFirstFrame(binary.Left, semanticModel, cancellationToken) ||
                           TestsFirstFrame(binary.Right, semanticModel, cancellationToken);
                case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.EqualsExpression):
                    return (IsFrameCount(binary.Left, semanticModel, cancellationToken) && IsZero(binary.Right)) ||
                           (IsFrameCount(binary.Right, semanticModel, cancellationToken) && IsZero(binary.Left));
                default:
                    return false;
            }
        }

        private static bool IsFrameCount(ExpressionSyntax expression, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            return semanticModel.GetSymbolInfo(expression, cancellationToken).Symbol is IPropertySymbol { IsStatic: true } property &&
                   property.Name == "frameCount" &&
                   property.ContainingType?.Name == TimeTypeName &&
                   property.ContainingType.ContainingNamespace?.ToDisplayString() == UnityEngineNamespace;
        }

        private static bool IsZero(ExpressionSyntax expression)
        {
            return expression is LiteralExpressionSyntax literal &&
                   literal.IsKind(SyntaxKind.NumericLiteralExpression) &&
                   literal.Token.Value is int value && value == 0;
        }

        // `if (!s_spawned) { ... s_spawned = true; }` and the shapes around it: the condition reads a
        // static bool field, and the guarded branch writes it, so the branch runs once.
        private static bool IsLatchGuarded(SyntaxNode node, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            foreach (var ifStatement in GuardingIfStatements(node))
            {
                foreach (var field in LatchFields(ifStatement.Condition, semanticModel, cancellationToken))
                {
                    if (AssignsField(ifStatement.Statement, field, semanticModel, cancellationToken)) return true;
                }
            }

            return false;
        }

        private static IEnumerable<IFieldSymbol> LatchFields(ExpressionSyntax condition, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            switch (condition)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    foreach (var field in LatchFields(parenthesized.Expression, semanticModel, cancellationToken))
                        yield return field;
                    break;
                case PrefixUnaryExpressionSyntax unary when unary.IsKind(SyntaxKind.LogicalNotExpression):
                    foreach (var field in LatchFields(unary.Operand, semanticModel, cancellationToken))
                        yield return field;
                    break;
                case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalAndExpression) ||
                                                        binary.IsKind(SyntaxKind.EqualsExpression) ||
                                                        binary.IsKind(SyntaxKind.NotEqualsExpression):
                    foreach (var field in LatchFields(binary.Left, semanticModel, cancellationToken))
                        yield return field;
                    foreach (var field in LatchFields(binary.Right, semanticModel, cancellationToken))
                        yield return field;
                    break;
                case IdentifierNameSyntax or MemberAccessExpressionSyntax:
                    if (semanticModel.GetSymbolInfo(condition, cancellationToken).Symbol is IFieldSymbol
                        { IsStatic: true, Type.SpecialType: SpecialType.System_Boolean } latch)
                        yield return latch;
                    break;
            }
        }

        private static bool AssignsField(SyntaxNode scope, IFieldSymbol field, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            foreach (var assignment in scope.DescendantNodesAndSelf().OfType<AssignmentExpressionSyntax>())
            {
                if (SymbolEqualityComparer.Default.Equals(
                        semanticModel.GetSymbolInfo(assignment.Left, cancellationToken).Symbol, field))
                    return true;
            }

            return false;
        }

        // `_buffer = new List<int>();`, `_buffer ??= new List<int>();`, `_buffer = _buffer ?? new List<int>();`
        // - the allocated object outlives the frame, which is what the rules are asking for.
        private static bool IsCachedInField(SyntaxNode node, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            if (node is not ExpressionSyntax expression) return false;

            SyntaxNode current = expression;
            while (current.Parent is ParenthesizedExpressionSyntax or CastExpressionSyntax or ConditionalExpressionSyntax ||
                   (current.Parent is BinaryExpressionSyntax binary && binary.IsKind(SyntaxKind.CoalesceExpression)))
            {
                current = current.Parent;
            }

            if (current.Parent is not AssignmentExpressionSyntax assignment || assignment.Right != current) return false;

            return semanticModel.GetSymbolInfo(assignment.Left, cancellationToken).Symbol is IFieldSymbol;
        }
    }
}
