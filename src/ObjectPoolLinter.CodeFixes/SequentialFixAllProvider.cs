using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;

namespace ObjectPoolLinter
{
    // Builds the code action a provider offers for the node a diagnostic sits on, or null when it offers none.
    internal delegate CodeAction? CreateCodeAction(Document document, SyntaxNode root, SyntaxNode node, SemanticModel model, CancellationToken cancellationToken);

    // Fix-all for the OPL002 and OPL003 fixes. Roslyn's batch fixer computes every fix against the
    // original document and merges the text changes, but these fixes add fields and Awake() statements to
    // the class they edit: two of them computed against the same document would pick the same free field
    // name and append to the same Awake(), and the merge would conflict or produce code that does not
    // compile. This fixer applies them one at a time instead, in source order, each against the document
    // the previous one produced, exactly as a user applying them by hand would.
    internal sealed class SequentialFixAllProvider : FixAllProvider
    {
        private readonly CreateCodeAction _createAction;

        internal SequentialFixAllProvider(CreateCodeAction createAction)
        {
            _createAction = createAction;
        }

        public override async Task<CodeAction?> GetFixAsync(FixAllContext fixAllContext)
        {
            var documents = fixAllContext.Scope switch
            {
                FixAllScope.Document when fixAllContext.Document != null => new[] { fixAllContext.Document },
                FixAllScope.Project => fixAllContext.Project.Documents.ToArray(),
                FixAllScope.Solution => fixAllContext.Solution.Projects.SelectMany(p => p.Documents).ToArray(),
                _ => new Document[0],
            };
            if (documents.Length == 0) return null;

            var title = fixAllContext.Scope switch
            {
                FixAllScope.Document => "Fix all occurrences in document",
                FixAllScope.Project => "Fix all occurrences in project",
                _ => "Fix all occurrences in solution",
            };

            var action = CodeAction.Create(
                title,
                cancellationToken => FixAllAsync(fixAllContext, documents, cancellationToken),
                fixAllContext.CodeActionEquivalenceKey);

            return await TelemetryCodeAction.WrapFixAllAsync(action, fixAllContext).ConfigureAwait(false);
        }

        private async Task<Solution> FixAllAsync(FixAllContext fixAllContext, IReadOnlyList<Document> documents, CancellationToken cancellationToken)
        {
            var solution = fixAllContext.Solution;

            // Documents are fixed one after another on the evolving solution, so a fix in one part of a
            // partial class sees the fields an earlier fix added to another part.
            foreach (var original in documents)
            {
                var diagnostics = await fixAllContext.GetDocumentDiagnosticsAsync(original).ConfigureAwait(false);
                if (diagnostics.IsEmpty) continue;

                var document = solution.GetDocument(original.Id);
                if (document == null) continue;

                document = await FixDocumentAsync(document, diagnostics, fixAllContext.CodeActionEquivalenceKey, cancellationToken).ConfigureAwait(false);
                solution = document.Project.Solution;
            }

            return solution;
        }

        private async Task<Document> FixDocumentAsync(Document document, ImmutableArray<Diagnostic> diagnostics, string? equivalenceKey, CancellationToken cancellationToken)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root == null) return document;

            // Each diagnostic's node is marked, so it can be found again after earlier fixes have moved it.
            var annotations = new List<SyntaxAnnotation>();
            var marked = new Dictionary<SyntaxNode, SyntaxAnnotation>();
            foreach (var diagnostic in diagnostics.OrderBy(d => d.Location.SourceSpan.Start))
            {
                var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
                if (marked.ContainsKey(node)) continue;

                var annotation = new SyntaxAnnotation();
                marked[node] = annotation;
                annotations.Add(annotation);
            }

            document = document.WithSyntaxRoot(root.ReplaceNodes(marked.Keys, (original, rewritten) => rewritten.WithAdditionalAnnotations(marked[original])));

            foreach (var annotation in annotations)
            {
                cancellationToken.ThrowIfCancellationRequested();

                root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                if (root == null || model == null) break;

                // A node an earlier fix replaced, such as one inside a lambda moved to Awake(), is gone.
                var node = root.GetAnnotatedNodes(annotation).FirstOrDefault();
                if (node == null) continue;

                var action = _createAction(document, root, node, model, cancellationToken);
                if (action == null || action.EquivalenceKey != equivalenceKey) continue;

                var operations = await action.GetOperationsAsync(cancellationToken).ConfigureAwait(false);
                var changed = operations.OfType<ApplyChangesOperation>().FirstOrDefault()?.ChangedSolution.GetDocument(document.Id);
                if (changed != null) document = changed;
            }

            return document;
        }
    }
}
