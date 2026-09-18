using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

using static ObjectPoolLinter.CodeFixSupport;

namespace ObjectPoolLinter
{
    // Rewrites for OPL003. As with OPL002, each fix is offered only for the shapes it can rewrite without
    // changing what the code does; docs/rules/OPL003.md lists the manual fix for everything else.
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(UnityApiAllocationCodeFixProvider)), Shared]
    public sealed class UnityApiAllocationCodeFixProvider : CodeFixProvider
    {
        internal const string UseCompareTagKey = "ObjectPoolLinterUseCompareTag";
        internal const string UseBufferOverloadKey = "ObjectPoolLinterUseBufferOverload";
        internal const string UseGetTouchKey = "ObjectPoolLinterUseGetTouch";

        public override ImmutableArray<string> FixableDiagnosticIds =>
            ImmutableArray.Create(UnityApiAllocationAnalyzer.DiagnosticId);

        // No fix-all: the buffer fix picks a free field name from the document as it is, so two fixes
        // applied in one batch could pick the same name.
        public override FixAllProvider? GetFixAllProvider() => null;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var document = context.Document;
            var root = await document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            var semanticModel = await document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
            if (root == null || semanticModel == null) return;

            foreach (var diagnostic in context.Diagnostics)
            {
                var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
                var action = CreateAction(document, root, node, semanticModel, context.CancellationToken);
                if (action != null) context.RegisterCodeFix(action, diagnostic);
            }
        }

        private static CodeAction? CreateAction(Document document, SyntaxNode root, SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
        {
            if (node is not ExpressionSyntax expression) return null;

            switch (model.GetOperation(expression, cancellationToken))
            {
                case IPropertyReferenceOperation { Property: { Name: "tag" } } reference:
                    return Replace(document, root, "Use CompareTag()", UseCompareTagKey, UseCompareTag(expression, reference, model, cancellationToken));

                case IPropertyReferenceOperation { Property: { Name: "touches", ContainingType: { Name: "Input" } } } reference:
                    return Replace(document, root, "Use Input.touchCount and Input.GetTouch()", UseGetTouchKey, UseGetTouch(expression, reference, root, model, cancellationToken));

                case IInvocationOperation invocation when expression is InvocationExpressionSyntax syntax:
                    var rewrite = UseBufferOverload(syntax, invocation, root, model, cancellationToken);
                    return rewrite == null
                        ? null
                        : CodeAction.Create(rewrite.Title, _ => Task.FromResult(rewrite.Apply(document, root)), rewrite.EquivalenceKey);

                default:
                    return null;
            }
        }

        private static CodeAction? Replace(Document document, SyntaxNode root, string title, string equivalenceKey, (SyntaxNode Old, SyntaxNode New)? edit)
        {
            if (edit is not { } found) return null;

            return CodeAction.Create(
                title,
                _ => Task.FromResult(document.WithSyntaxRoot(root.ReplaceNode(found.Old, found.New))),
                equivalenceKey);
        }

        // F20. `other.tag == "Player"` becomes `other.CompareTag("Player")`, and `!=` becomes
        // `!other.CompareTag("Player")`. CompareTag reads the tag without building a string. It also
        // logs an error for a tag that is not defined in the Tag Manager, where `==` quietly returns false.
        private static (SyntaxNode, SyntaxNode)? UseCompareTag(ExpressionSyntax node, IPropertyReferenceOperation reference, SemanticModel model, CancellationToken cancellationToken)
        {
            if (node.Parent is not BinaryExpressionSyntax binary) return null;

            var equals = binary.IsKind(SyntaxKind.EqualsExpression);
            if (!equals && !binary.IsKind(SyntaxKind.NotEqualsExpression)) return null;

            var other = binary.Left == node ? binary.Right : binary.Left;

            // `tag == null` and `tag == someObject` are not string comparisons CompareTag can make.
            if (model.GetTypeInfo(node, cancellationToken).ConvertedType?.SpecialType != SpecialType.System_String ||
                model.GetTypeInfo(other, cancellationToken).Type?.SpecialType != SpecialType.System_String)
                return null;
            if (model.GetConstantValue(other, cancellationToken) is { HasValue: true, Value: null }) return null;

            var hasCompareTag = reference.Property.ContainingType.GetMembers("CompareTag").OfType<IMethodSymbol>().Any(m =>
                !m.IsStatic &&
                m.ReturnType.SpecialType == SpecialType.System_Boolean &&
                m.Parameters.Length == 1 &&
                m.Parameters[0].Type.SpecialType == SpecialType.System_String);
            if (!hasCompareTag) return null;

            ExpressionSyntax target;
            switch (node)
            {
                case MemberAccessExpressionSyntax memberAccess:
                    // `"Player" == other.tag` evaluates the string first; the call evaluates `other` first.
                    if (binary.Right == node && !IsSideEffectFree(other) && !IsSideEffectFree(memberAccess.Expression)) return null;
                    target = memberAccess.WithName(SyntaxFactory.IdentifierName("CompareTag")).WithoutTrivia();
                    break;
                case IdentifierNameSyntax:
                    target = SyntaxFactory.IdentifierName("CompareTag");
                    break;
                default:
                    return null;
            }

            ExpressionSyntax call = SyntaxFactory.InvocationExpression(
                target,
                SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(other.WithoutTrivia()))));
            if (!equals) call = SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, call);

            return (binary, call.WithTriviaFrom(binary));
        }

        // F22. Three shapes, each reading the touches without copying them into a new array:
        //     Input.touches.Length                  becomes  Input.touchCount
        //     Input.touches[i]                      becomes  Input.GetTouch(i)
        //     foreach (var touch in Input.touches)  becomes  for (int i = 0; i < Input.touchCount; i++)
        //                                                    with `var touch = Input.GetTouch(i);` first in the body.
        private static (SyntaxNode, SyntaxNode)? UseGetTouch(ExpressionSyntax node, IPropertyReferenceOperation reference, SyntaxNode root, SemanticModel model, CancellationToken cancellationToken)
        {
            var input = reference.Property.ContainingType;
            if (reference.Property.Type is not IArrayTypeSymbol { ElementType: var touchType }) return null;

            var hasTouchCount = input.GetMembers("touchCount").OfType<IPropertySymbol>()
                .Any(p => p.IsStatic && p.GetMethod != null && p.Type.SpecialType == SpecialType.System_Int32);
            var hasGetTouch = input.GetMembers("GetTouch").OfType<IMethodSymbol>()
                .Any(m => m.IsStatic && m.Parameters.Length == 1 && m.Parameters[0].Type.SpecialType == SpecialType.System_Int32 &&
                          SymbolEqualityComparer.Default.Equals(m.ReturnType, touchType));

            switch (node.Parent)
            {
                case MemberAccessExpressionSyntax { Name: { Identifier: { ValueText: "Length" } } } length when length.Expression == node && hasTouchCount:
                    return (length, InputMember(node, "touchCount").WithTriviaFrom(length));

                case ElementAccessExpressionSyntax { ArgumentList: { Arguments: { Count: 1 } arguments } } element when element.Expression == node && hasGetTouch:
                    var index = arguments[0];
                    if (index.NameColon != null || !index.RefKindKeyword.IsKind(SyntaxKind.None)) return null;
                    if (model.GetTypeInfo(index.Expression, cancellationToken).ConvertedType?.SpecialType != SpecialType.System_Int32) return null;
                    if (IsWritten(element)) return null;
                    return (element, GetTouch(node, index.Expression.WithoutTrivia()).WithTriviaFrom(element));

                case ForEachStatementSyntax loop when loop.Expression == node && hasTouchCount && hasGetTouch:
                    return ForEachToFor(loop, node, touchType, root, model, cancellationToken);

                default:
                    return null;
            }
        }

        private static (SyntaxNode, SyntaxNode)? ForEachToFor(ForEachStatementSyntax loop, ExpressionSyntax touches, ITypeSymbol touchType, SyntaxNode root, SemanticModel model, CancellationToken cancellationToken)
        {
            if (!loop.AwaitKeyword.IsKind(SyntaxKind.None)) return null;

            // `foreach (object touch in ...)` would box each touch; only the element type itself carries over.
            if (model.GetDeclaredSymbol(loop, cancellationToken) is not ILocalSymbol local ||
                !SymbolEqualityComparer.Default.Equals(local.Type, touchType))
                return null;

            if (loop.FirstAncestorOrSelf<MemberDeclarationSyntax>() is not { } member) return null;
            var indexName = FreeLocalName("i", IdentifierTexts(member));

            var eol = GetEndOfLine(root);
            var unit = loop.FirstAncestorOrSelf<ClassDeclarationSyntax>() is { } @class ? IndentUnit(@class) : "    ";
            var header = "for (int " + indexName + " = 0; " + indexName + " < " + InputMember(touches, "touchCount") + "; " + indexName + "++)";
            var declaration = loop.Type + " " + loop.Identifier.ValueText + " = " + GetTouch(touches, SyntaxFactory.IdentifierName(indexName)) + ";";

            string text;
            if (loop.Statement is BlockSyntax block)
            {
                var indent = block.Statements.Count > 0 ? Indentation(block.Statements[0]) : Indentation(loop) + unit;
                var first = SyntaxFactory.ParseStatement(indent + declaration + eol);

                // `)` keeps its trailing trivia, so the brace stays on the line it was on.
                text = header + loop.CloseParenToken.TrailingTrivia.ToFullString() + block.WithStatements(block.Statements.Insert(0, first)).ToFullString();
            }
            else
            {
                var indent = Indentation(loop);
                var inner = indent + unit;
                var statement = Reindent(loop.Statement.WithoutTrivia().ToString(), Indentation(loop.Statement), inner);
                text = header + eol + indent + "{" + eol + inner + declaration + eol + inner + statement + eol + indent + "}" + eol;
            }

            var replacement = SyntaxFactory.ParseStatement(text)
                .WithLeadingTrivia(loop.GetLeadingTrivia())
                .WithTrailingTrivia(loop.GetTrailingTrivia());

            return (loop, replacement);
        }

        // `touches[0]` is a variable, `GetTouch(0)` a value: assigning to it, or to a field of it, does
        // not compile.
        private static bool IsWritten(ExpressionSyntax element)
        {
            SyntaxNode node = element;
            while (node.Parent is MemberAccessExpressionSyntax memberAccess && memberAccess.Expression == node)
                node = memberAccess;

            return node.Parent switch
            {
                AssignmentExpressionSyntax assignment => assignment.Left == node,
                PrefixUnaryExpressionSyntax unary => unary.IsKind(SyntaxKind.PreIncrementExpression) || unary.IsKind(SyntaxKind.PreDecrementExpression) ||
                                                     unary.IsKind(SyntaxKind.AddressOfExpression),
                PostfixUnaryExpressionSyntax => true,
                ArgumentSyntax argument => !argument.RefKindKeyword.IsKind(SyntaxKind.None),
                RefExpressionSyntax => true,
                _ => false,
            };
        }

        // `Input.touches` names the class the way the code does: `Input`, `UnityEngine.Input`, or nothing
        // under `using static UnityEngine.Input`.
        private static ExpressionSyntax InputMember(ExpressionSyntax touches, string name)
        {
            var member = SyntaxFactory.IdentifierName(name);
            return touches is MemberAccessExpressionSyntax memberAccess
                ? memberAccess.WithName(member).WithoutTrivia()
                : member;
        }

        private static ExpressionSyntax GetTouch(ExpressionSyntax touches, ExpressionSyntax index) =>
            SyntaxFactory.InvocationExpression(
                InputMember(touches, "GetTouch"),
                SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(index))));

        // F21. `var colliders = GetComponentsInChildren<Collider>();` becomes
        //     GetComponentsInChildren<Collider>(_collidersBuffer);
        //     var colliders = _collidersBuffer;
        // with the list in a field, and `colliders.Length` becomes `colliders.Count`. Unity clears the list
        // before filling it. `foreach (var c in GetComponentsInChildren<Collider>())` gets the same call in
        // front of the loop and iterates the list.
        private static Rewrite? UseBufferOverload(InvocationExpressionSyntax invocation, IInvocationOperation operation, SyntaxNode root, SemanticModel model, CancellationToken cancellationToken)
        {
            var method = operation.TargetMethod;
            if (method.IsStatic || method.TypeArguments.Length != 1 || method.ReturnType is not IArrayTypeSymbol { Rank: 1, ElementType: var elementType }) return null;
            if (!SymbolEqualityComparer.Default.Equals(elementType, method.TypeArguments[0]) || !elementType.IsReferenceType || !IsNameable(elementType)) return null;

            // The type argument is written out, so the new call binds to the generic overload.
            var name = invocation.Expression switch
            {
                MemberAccessExpressionSyntax memberAccess => memberAccess.Name,
                SimpleNameSyntax simple => simple,
                _ => null,
            };
            if (name is not GenericNameSyntax) return null;

            if (operation.Arguments.Any(a => a.ArgumentKind != ArgumentKind.Explicit) ||
                invocation.ArgumentList.Arguments.Any(a => a.NameColon != null || !a.RefKindKeyword.IsKind(SyntaxKind.None)))
                return null;

            if (!TryFindBufferOverload(method, out var prependFalse)) return null;

            var hot = HotMethod.Find(invocation, model, cancellationToken);
            if (hot == null) return null;

            StatementSyntax statement;
            ILocalSymbol? local = null;
            string hint;

            switch (invocation.Parent)
            {
                case ForEachStatementSyntax { Parent: BlockSyntax } forEach when forEach.Expression == invocation && forEach.AwaitKeyword.IsKind(SyntaxKind.None):
                    statement = forEach;
                    hint = elementType.Name;
                    break;

                case EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator }
                    when declarator.Parent is VariableDeclarationSyntax { Variables: { Count: 1 }, Parent: LocalDeclarationStatementSyntax { Parent: BlockSyntax } declaration } &&
                         declaration.Modifiers.Count == 0 && declaration.UsingKeyword.IsKind(SyntaxKind.None):
                    local = model.GetDeclaredSymbol(declarator, cancellationToken) as ILocalSymbol;
                    if (local == null) return null;
                    statement = declaration;
                    hint = local.Name;
                    break;

                default:
                    return null;
            }

            var block = (BlockSyntax)statement.Parent!;

            // Every use of the local has to work on a List<T> as it did on the array: reading or writing an
            // element, iterating it, or reading its length, which is renamed to Count. Anything else could
            // keep the list past the next refill.
            var lengths = new List<IdentifierNameSyntax>();
            if (local != null)
            {
                foreach (var identifier in block.DescendantNodes().OfType<IdentifierNameSyntax>())
                {
                    if (identifier.Identifier.ValueText != local.Name ||
                        !SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(identifier, cancellationToken).Symbol, local))
                        continue;

                    switch (identifier.Parent)
                    {
                        case ElementAccessExpressionSyntax element when element.Expression == identifier:
                        case ForEachStatementSyntax forEach when forEach.Expression == identifier:
                            break;
                        case MemberAccessExpressionSyntax { Name: IdentifierNameSyntax { Identifier: { ValueText: "Length" } } length } memberAccess
                            when memberAccess.Expression == identifier:
                            lengths.Add(length);
                            break;
                        default:
                            return null;
                    }
                }
            }

            var rewrite = new Rewrite(hot, "Fill a reused List<T> with the non-allocating overload", UseBufferOverloadKey);
            var list = TypeName(model, hot, "System.Collections.Generic", "List", 1, rewrite) + "<" + Display(elementType, model, hot) + ">";
            var bufferName = hot.FreeMemberName(FieldName(hint, "Buffer", "_buffer"));
            rewrite.Fields.Add(hot.FieldModifiers(readOnly: true) + list + " " + bufferName + " = new " + list + "();");

            var arguments = invocation.ArgumentList.Arguments.Select(a => a.ToString()).ToList();
            if (prependFalse) arguments.Add("false");
            arguments.Add(bufferName);

            var eol = GetEndOfLine(root);
            var fill = SyntaxFactory.ParseStatement(invocation.Expression + "(" + string.Join(", ", arguments) + ");" + eol);
            var buffer = SyntaxFactory.IdentifierName(bufferName).WithTriviaFrom(invocation);

            var index = block.Statements.IndexOf(statement);
            var renamed = block.ReplaceNodes(lengths, (original, _) => SyntaxFactory.IdentifierName("Count").WithTriviaFrom(original));
            var moved = renamed.Statements[index];

            StatementSyntax replacement = moved switch
            {
                ForEachStatementSyntax forEach => forEach.WithExpression(buffer),
                LocalDeclarationStatementSyntax declaration => declaration.WithDeclaration(declaration.Declaration
                    .WithType(SyntaxFactory.IdentifierName("var").WithTriviaFrom(declaration.Declaration.Type))
                    .WithVariables(SyntaxFactory.SingletonSeparatedList(declaration.Declaration.Variables[0]
                        .WithInitializer(declaration.Declaration.Variables[0].Initializer!.WithValue(buffer))))),
                _ => moved,
            };

            rewrite.Replacements[block] = InsertBefore(renamed, moved, replacement, new[] { fill });
            return rewrite;
        }

        // `GetComponentsInChildren<T>()` pairs with `GetComponentsInChildren<T>(List<T>)`: the same name
        // and parameters with a List<T> added last, returning void. `GetComponentsInParent<T>()` has only
        // `GetComponentsInParent<T>(bool, List<T>)`, which is called with `false`, the array overload's default.
        private static bool TryFindBufferOverload(IMethodSymbol method, out bool prependFalse)
        {
            prependFalse = false;
            var parameters = method.OriginalDefinition.Parameters;

            foreach (var candidate in method.ContainingType.GetMembers(method.Name).OfType<IMethodSymbol>())
            {
                if (candidate.IsStatic || !candidate.ReturnsVoid || candidate.TypeParameters.Length != 1 || candidate.Parameters.Length == 0) continue;
                if (!IsListOf(candidate.Parameters[candidate.Parameters.Length - 1].Type, candidate.TypeParameters[0])) continue;

                var leading = candidate.Parameters.Take(candidate.Parameters.Length - 1).Select(p => p.Type).ToList();
                if (leading.Count == parameters.Length &&
                    leading.Zip(parameters, (a, b) => SymbolEqualityComparer.Default.Equals(a, b.Type)).All(same => same))
                {
                    prependFalse = false;
                    return true;
                }

                if (parameters.Length == 0 && leading.Count == 1 && leading[0].SpecialType == SpecialType.System_Boolean)
                    prependFalse = true;
            }

            return prependFalse;
        }

        private static bool IsListOf(ITypeSymbol type, ITypeParameterSymbol element) =>
            type is INamedTypeSymbol { TypeArguments: { Length: 1 } arguments } named &&
            named.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.List<T>" &&
            SymbolEqualityComparer.Default.Equals(arguments[0], element);
    }
}
