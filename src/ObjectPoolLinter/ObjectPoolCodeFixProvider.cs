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

            if (node is BaseObjectCreationExpressionSyntax objectCreation)
            {
                context.RegisterCodeFix(
                    CodeAction.Create(
                        title: "Replace with object pool Get()",
                        createChangedDocument: c => ReplaceWithPoolGetAsync(context.Document, objectCreation, c),
                        equivalenceKey: "ObjectPoolLinterReplaceWithPoolGet"),
                    diagnostic);
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

            var typeSyntax = (objectCreation as ObjectCreationExpressionSyntax)?.Type;
            var typeSymbol = (typeSyntax != null 
                    ? semanticModel.GetSymbolInfo(typeSyntax, cancellationToken).Symbol as INamedTypeSymbol 
                    : null)
                ?? semanticModel.GetTypeInfo(objectCreation, cancellationToken).Type as INamedTypeSymbol;
            
            if (typeSymbol == null || typeSymbol.TypeKind == TypeKind.Error) return document;

            var poolIdentifier = SyntaxFactory.Identifier(typeSymbol.Name + "Pool");

            SimpleNameSyntax poolName;
            if (typeSyntax is GenericNameSyntax generic)
                poolName = SyntaxFactory.GenericName(poolIdentifier)
                            .WithTypeArgumentList(generic.TypeArgumentList);
            else if (typeSyntax is QualifiedNameSyntax { Right: GenericNameSyntax q })
                poolName = SyntaxFactory.GenericName(poolIdentifier)
                            .WithTypeArgumentList(q.TypeArgumentList);
            else if (typeSymbol.IsGenericType)
            {
                var typeArguments = typeSymbol.TypeArguments
                    .Select(t => SyntaxFactory.ParseTypeName(
                        t.ToMinimalDisplayString(semanticModel, objectCreation.SpanStart)))
                    .ToArray();

                if (typeArguments.Any(a => a.ContainsDiagnostics)) return document;

                poolName = SyntaxFactory.GenericName(poolIdentifier)
                    .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(
                        SyntaxFactory.SeparatedList<TypeSyntax>(typeArguments)));
            }
                
            else poolName = SyntaxFactory.IdentifierName(poolIdentifier);
            
            var poolGet = SyntaxFactory.InvocationExpression(
                            SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            poolName,
                            SyntaxFactory.IdentifierName("Get"))
            ).WithTriviaFrom(objectCreation);

            var newRoot = root.ReplaceNode(objectCreation, poolGet);
            return document.WithSyntaxRoot(newRoot);
        }

        private static async Task<Document> AddPoolingCommentAsync(
            Document document,
            SyntaxNode node,
            CancellationToken cancellationToken)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root == null) return document;

            var comment = SyntaxFactory.Comment("// TODO: use an object pool to avoid per-frame allocation");

            var newRoot = root.ReplaceNode(node, node.WithLeadingTrivia(node.GetLeadingTrivia().Insert(0, comment)));
            return document.WithSyntaxRoot(newRoot);
        }
    }
}