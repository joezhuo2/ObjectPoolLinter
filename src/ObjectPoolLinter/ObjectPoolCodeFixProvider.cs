using System.Collections.Immutable;
using System.Linq;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ObjectPoolLinter
{
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ObjectPoolCodeFixProvider)), Shared]
    public sealed class ObjectPoolCodeFixProvider : CodeFixProvider
    {
        public sealed override ImmutableArray<string> FixableDiagnosticIds =>
            ImmutableArray.Create(ObjectPoolAnalyzer.DiagnosticId);

        public sealed override FixAllProvider GetFixAllProvider() =>
            WellKnownFixAllProviders.BatchFixer;

        public sealed override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            if (root == null) return;

            var diagnostic = context.Diagnostics[0];
            var diagnosticSpan = diagnostic.Location.SourceSpan;

            var node = root.FindNode(diagnosticSpan);

            if (node is BaseObjectCreationExpressionSyntax { Initializer: null } objectCreation)
            {
                var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);

                if (semanticModel != null &&
                    TryGetPoolName(semanticModel, objectCreation, context.CancellationToken, out _))
                {
                    context.RegisterCodeFix(
                        CodeAction.Create(
                            title: "Replace with object pool Get()",
                            createChangedDocument: c => ReplaceWithPoolGetAsync(context.Document, objectCreation, c),
                            equivalenceKey: "ObjectPoolLinterReplaceWithPoolGet"),
                        diagnostic);
                }
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    title: "Add pooling TODO comment",
                    createChangedDocument: c => AddPoolingCommentAsync(context.Document, node, c),
                    equivalenceKey: "ObjectPoolLinterAddPoolingComment"),
                diagnostic);
        }

        private static async Task<Document> ReplaceWithPoolGetAsync(
            Document document,
            BaseObjectCreationExpressionSyntax objectCreation,
            CancellationToken cancellationToken)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root == null) return document;

            var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (semanticModel == null) return document;

            if (!TryGetPoolName(semanticModel, objectCreation, cancellationToken, out var poolName) || poolName == null)
                return document;

            var argumentList = objectCreation.ArgumentList ?? SyntaxFactory.ArgumentList();

            var poolGet = SyntaxFactory.InvocationExpression(
                            SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            poolName,
                            SyntaxFactory.IdentifierName("Get")),
                            argumentList.WithoutTrivia()
            ).WithTriviaFrom(objectCreation);

            var newRoot = root.ReplaceNode(objectCreation, poolGet);
            return document.WithSyntaxRoot(newRoot);
        }

        private static bool TryGetPoolName(
            SemanticModel semanticModel,
            BaseObjectCreationExpressionSyntax objectCreation,
            CancellationToken cancellationToken,
            out SimpleNameSyntax? poolName)
        {
            poolName = null;

            var typeSyntax = (objectCreation as ObjectCreationExpressionSyntax)?.Type;
            var typeSymbol = (typeSyntax != null
                    ? semanticModel.GetSymbolInfo(typeSyntax, cancellationToken).Symbol as INamedTypeSymbol
                    : null)
                ?? semanticModel.GetTypeInfo(objectCreation, cancellationToken).Type as INamedTypeSymbol;

            if (typeSymbol == null || typeSymbol.TypeKind == TypeKind.Error) return false;

            var poolIdentifier = SyntaxFactory.Identifier(typeSymbol.Name + "Pool");

            SimpleNameSyntax candidate;
            if (typeSyntax is GenericNameSyntax generic)
                candidate = SyntaxFactory.GenericName(poolIdentifier)
                            .WithTypeArgumentList(generic.TypeArgumentList);
            else if (typeSyntax is QualifiedNameSyntax { Right: GenericNameSyntax q })
                candidate = SyntaxFactory.GenericName(poolIdentifier)
                            .WithTypeArgumentList(q.TypeArgumentList);
            else if (typeSymbol.IsGenericType)
            {
                var typeArguments = typeSymbol.TypeArguments
                    .Select(t => SyntaxFactory.ParseTypeName(
                        t.ToMinimalDisplayString(semanticModel, objectCreation.SpanStart)))
                    .ToArray();

                if (typeArguments.Any(a => a.ContainsDiagnostics)) return false;

                candidate = SyntaxFactory.GenericName(poolIdentifier)
                    .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(
                        SyntaxFactory.SeparatedList<TypeSyntax>(typeArguments)));
            }

            else candidate = SyntaxFactory.IdentifierName(poolIdentifier);

            var arity = candidate is GenericNameSyntax poolGeneric
                ? poolGeneric.TypeArgumentList.Arguments.Count
                : 0;

            var argumentCount = objectCreation.ArgumentList?.Arguments.Count ?? 0;

            if (!PoolTypeIsUsable(semanticModel, objectCreation.SpanStart, poolIdentifier.ValueText, arity, argumentCount))
                return false;

            poolName = candidate;
            return true;
        }

        private static bool PoolTypeIsUsable(
            SemanticModel semanticModel,
            int position,
            string poolTypeName,
            int arity,
            int argumentCount)
        {
            foreach (var symbol in semanticModel.LookupNamespacesAndTypes(position, name: poolTypeName))
            {
                if (symbol is not INamedTypeSymbol poolType || poolType.Arity != arity) continue;

                foreach (var member in poolType.GetMembers("Get"))
                {
                    if (member is not IMethodSymbol getMethod || !getMethod.IsStatic) continue;
                    if (!semanticModel.IsAccessible(position, getMethod)) continue;
                    if (!AcceptsArgumentCount(getMethod, argumentCount)) continue;

                    return true;
                }
            }

            return false;
        }

        private static bool AcceptsArgumentCount(IMethodSymbol method, int argumentCount)
        {
            var parameters = method.Parameters;

            if (parameters.Length > 0 && parameters[parameters.Length - 1].IsParams)
                return argumentCount >= parameters.Length - 1;

            if (argumentCount > parameters.Length) return false;

            return argumentCount >= parameters.Count(p => !p.IsOptional);
        }

        private static async Task<Document> AddPoolingCommentAsync(
            Document document,
            SyntaxNode node,
            CancellationToken cancellationToken)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root == null) return document;

            var statement = node.FirstAncestorOrSelf<StatementSyntax>();
            if (statement == null) return document;

            var leadingTrivia = statement.GetLeadingTrivia();
            var indentation = leadingTrivia.LastOrDefault(t => t.IsKind(SyntaxKind.WhitespaceTrivia));

            var commentTrivia = SyntaxFactory.TriviaList(
                SyntaxFactory.Comment("// TODO: use an object pool to avoid per-frame allocation"),
                GetEndOfLine(root));

            if (indentation.IsKind(SyntaxKind.WhitespaceTrivia)) commentTrivia = commentTrivia.Add(indentation);

            var newStatement = statement.WithLeadingTrivia(leadingTrivia.AddRange(commentTrivia));
            var newRoot = root.ReplaceNode(statement, newStatement);
            return document.WithSyntaxRoot(newRoot);
        }

        private static SyntaxTrivia GetEndOfLine(SyntaxNode root)
        {
            var existing = root.DescendantTrivia(descendIntoTrivia: true)
                .FirstOrDefault(t => t.IsKind(SyntaxKind.EndOfLineTrivia));

            return existing.IsKind(SyntaxKind.EndOfLineTrivia)
                ? SyntaxFactory.EndOfLine(existing.ToFullString())
                : SyntaxFactory.CarriageReturnLineFeed;
        }
    }
}
