using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Text;
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
    // Rewrites for OPL002. Each fix is offered only for the shapes it can rewrite without changing what
    // the code does; every other shape gets no fix, and docs/rules/OPL002.md lists the manual fix.
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(HiddenAllocationCodeFixProvider)), Shared]
    public sealed class HiddenAllocationCodeFixProvider : CodeFixProvider
    {
        internal const string CacheLambdaKey = "ObjectPoolLinterCacheLambda";
        internal const string CacheMethodGroupKey = "ObjectPoolLinterCacheMethodGroup";
        internal const string UseStringBuilderKey = "ObjectPoolLinterUseStringBuilder";
        internal const string LinqToLoopKey = "ObjectPoolLinterLinqToLoop";

        public override ImmutableArray<string> FixableDiagnosticIds =>
            ImmutableArray.Create(HiddenAllocationAnalyzer.DiagnosticId);

        // No fix-all: each fix picks a free field name from the document as it is, so two fixes applied in
        // one batch could pick the same name.
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
                var rewrite = CreateRewrite(node, root, semanticModel, context.CancellationToken);
                if (rewrite == null) continue;

                context.RegisterCodeFix(
                    CodeAction.Create(
                        title: rewrite.Title,
                        createChangedDocument: _ => Task.FromResult(rewrite.Apply(document, root)),
                        equivalenceKey: rewrite.EquivalenceKey),
                    diagnostic);
            }
        }

        private static Rewrite? CreateRewrite(SyntaxNode node, SyntaxNode root, SemanticModel model, CancellationToken cancellationToken)
        {
            var hot = HotMethod.Find(node, model, cancellationToken);
            if (hot == null) return null;

            var eol = GetEndOfLine(root);

            return node switch
            {
                AnonymousFunctionExpressionSyntax lambda => CacheLambda(lambda, hot, model, cancellationToken),
                InterpolatedStringExpressionSyntax interpolated => UseStringBuilder(interpolated, hot, model, eol, cancellationToken),
                InvocationExpressionSyntax invocation => LinqToLoop(invocation, hot, model, eol, cancellationToken),
                ExpressionSyntax expression => CacheMethodGroup(expression, hot, model, cancellationToken),
                _ => null,
            };
        }

        // F16. `Run(() => count + 1)` becomes `Run(_next)`, with `_next` assigned the lambda in Awake. The
        // locals the lambda captures become fields, so the lambda in Awake and the code left in the hot
        // method share them the way they shared the closure. Their value now survives between frames.
        private static Rewrite? CacheLambda(AnonymousFunctionExpressionSyntax lambda, HotMethod hot, SemanticModel model, CancellationToken cancellationToken)
        {
            if (!hot.CanAssignInAwake()) return null;

            // A lambda nested in another lambda or a local function can capture that function's variables,
            // which do not exist in Awake.
            if (lambda.Ancestors().TakeWhile(a => a != hot.Declaration).Any(a => a is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
                return null;

            if (model.GetOperation(lambda, cancellationToken) is not IAnonymousFunctionOperation operation) return null;
            if (model.GetTypeInfo(lambda, cancellationToken).ConvertedType is not INamedTypeSymbol { TypeKind: TypeKind.Delegate } delegateType ||
                !IsNameable(delegateType))
                return null;

            var owners = new HashSet<ISymbol>(SymbolEqualityComparer.Default) { operation.Symbol };
            foreach (var nested in operation.Body.Descendants())
            {
                if (nested is IAnonymousFunctionOperation nestedLambda) owners.Add(nestedLambda.Symbol);
                if (nested is ILocalFunctionOperation localFunction) owners.Add(localFunction.Symbol);
            }

            var captured = new List<ILocalSymbol>();
            foreach (var reference in operation.Body.Descendants())
            {
                switch (reference)
                {
                    case IParameterReferenceOperation parameter when !owners.Contains(parameter.Parameter.ContainingSymbol):
                        return null;
                    case IInvocationOperation { TargetMethod: { MethodKind: MethodKind.LocalFunction } called } when !owners.Contains(called):
                        return null;
                    case IMethodReferenceOperation { Method: { MethodKind: MethodKind.LocalFunction } referenced } when !owners.Contains(referenced):
                        return null;
                    case ILocalReferenceOperation local when !owners.Contains(local.Local.ContainingSymbol):
                        if (!captured.Contains(local.Local, SymbolEqualityComparer.Default)) captured.Add(local.Local);
                        break;
                }
            }

            var rewrite = new Rewrite(hot, "Cache the lambda in a field assigned in Awake()", CacheLambdaKey);
            var reserved = new HashSet<string>();

            foreach (var local in captured)
            {
                if (!TryPromoteLocal(local, lambda, hot, model, rewrite, cancellationToken)) return null;
                reserved.Add(local.Name);
            }

            var fieldName = hot.FreeMemberName(FieldName(NameHint(lambda, model, cancellationToken), "", "_callback"), reserved);
            rewrite.Fields.Add(hot.FieldModifiers(readOnly: false) + Display(delegateType, model, hot) + " " + fieldName + ";");
            rewrite.Replacements[lambda] = SyntaxFactory.IdentifierName(fieldName).WithTriviaFrom(lambda);

            var lambdaIndent = Indentation(lambda.FirstAncestorOrSelf<StatementSyntax>());
            rewrite.AwakeStatements.Add(indent => fieldName + " = " + Reindent(lambda.ToString(), lambdaIndent, indent) + ";");

            return rewrite;
        }

        // `var count = 3;` in the hot method becomes a field `count` and the statement `count = 3;`.
        private static bool TryPromoteLocal(ILocalSymbol local, AnonymousFunctionExpressionSyntax lambda, HotMethod hot, SemanticModel model, Rewrite rewrite, CancellationToken cancellationToken)
        {
            if (local.IsConst || local.IsRef || !IsNameable(local.Type)) return false;
            if (!SymbolEqualityComparer.Default.Equals(local.ContainingSymbol, hot.Symbol)) return false;
            if (local.DeclaringSyntaxReferences.Length != 1) return false;

            if (local.DeclaringSyntaxReferences[0].GetSyntax(cancellationToken) is not VariableDeclaratorSyntax { Initializer: { } initializer } declarator ||
                declarator.Parent is not VariableDeclarationSyntax { Variables: { Count: 1 } } declaration ||
                declaration.Parent is not LocalDeclarationStatementSyntax statement ||
                statement.Parent is not BlockSyntax ||
                statement.Modifiers.Count > 0 ||
                !statement.UsingKeyword.IsKind(SyntaxKind.None) ||
                statement.Span.Contains(lambda.Span))
                return false;

            // A local declared in a loop is a new variable on every iteration; a field is not.
            if (statement.Ancestors().TakeWhile(a => a != hot.Declaration)
                .Any(a => a is ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax))
                return false;

            // The field takes the local's name, so nothing else may already answer to it: no member, no
            // name outside the hot method that binds to anything but its own local, and no local in Awake,
            // which would hide the field from the moved lambda.
            if (hot.HasMember(local.Name)) return false;
            if (hot.FindAwake() is { } awake && IdentifierTexts(awake).Contains(local.Name)) return false;

            foreach (var identifier in hot.Class.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (identifier.Identifier.ValueText != local.Name || hot.Declaration.Span.Contains(identifier.Span)) continue;
                if (model.GetSymbolInfo(identifier, cancellationToken).Symbol is not (ILocalSymbol or IParameterSymbol or IRangeVariableSymbol))
                    return false;
            }

            var type = declaration.Type.IsVar ? Display(local.Type, model, hot) : declaration.Type.ToString();
            rewrite.Fields.Add(hot.FieldModifiers(readOnly: false) + type + " " + local.Name + ";");

            var assignment = SyntaxFactory.ParseStatement(local.Name + " = " + initializer.Value + ";")
                .WithLeadingTrivia(statement.GetLeadingTrivia())
                .WithTrailingTrivia(statement.GetTrailingTrivia());
            rewrite.Replacements[statement] = assignment;
            return true;
        }

        // F17. `Action callback = Spawn;` becomes `Action callback = _spawn;`, with `_spawn = Spawn;` in Awake.
        private static Rewrite? CacheMethodGroup(ExpressionSyntax node, HotMethod hot, SemanticModel model, CancellationToken cancellationToken)
        {
            var operation = model.GetOperation(node, cancellationToken);
            var reference = operation as IMethodReferenceOperation ?? (operation as IDelegateCreationOperation)?.Target as IMethodReferenceOperation;
            if (reference?.Parent is not IDelegateCreationOperation { Type: INamedTypeSymbol delegateType } || !IsNameable(delegateType)) return null;
            if (!hot.CanAssignInAwake()) return null;

            // `Spawn`, `this.Spawn` and `Helpers.Spawn` name the same method in Awake; `enemy.Die` binds to
            // whatever `enemy` is when the hot method runs, and a local function does not exist in Awake.
            if (reference.Instance is not (null or IInstanceReferenceOperation)) return null;
            if (reference.Method.MethodKind == MethodKind.LocalFunction) return null;

            var rewrite = new Rewrite(hot, "Cache the delegate in a field assigned in Awake()", CacheMethodGroupKey);
            var fieldName = hot.FreeMemberName(FieldName(reference.Method.Name, "", "_callback"));

            rewrite.Fields.Add(hot.FieldModifiers(readOnly: false) + Display(delegateType, model, hot) + " " + fieldName + ";");
            rewrite.Replacements[node] = SyntaxFactory.IdentifierName(fieldName).WithTriviaFrom(node);

            var methodGroup = node.ToString();
            rewrite.AwakeStatements.Add(_ => fieldName + " = " + methodGroup + ";");
            return rewrite;
        }

        // F18. `var text = $"hp: {hp}";` becomes
        //     _textBuilder.Clear().Append("hp: ").Append(hp);
        //     var text = _textBuilder.ToString();
        // with the builder in a field. The ToString() still allocates the result, which OPL002 keeps
        // reporting; the intermediate strings and boxed holes are gone.
        private static Rewrite? UseStringBuilder(InterpolatedStringExpressionSyntax interpolated, HotMethod hot, SemanticModel model, string eol, CancellationToken cancellationToken)
        {
            if (model.GetOperation(interpolated, cancellationToken) is not IInterpolatedStringOperation operation) return null;

            // An interpolated string converted to FormattableString or a handler type is not a string.
            if (model.GetTypeInfo(interpolated, cancellationToken).ConvertedType?.SpecialType != SpecialType.System_String) return null;

            // `{hp,5}` and `{hp:F2}` have no allocation-free StringBuilder equivalent.
            if (interpolated.Contents.OfType<InterpolationSyntax>().Any(i => i.AlignmentClause != null || i.FormatClause != null)) return null;

            if (!TryGetInsertionPoint(interpolated, hot, out var statement, out var block)) return null;

            var rewrite = new Rewrite(hot, "Build the string with a reused StringBuilder", UseStringBuilderKey);
            var stringBuilder = TypeName(model, hot, "System.Text", "StringBuilder", 0, rewrite);
            var builderName = hot.FreeMemberName(FieldName(NameHint(interpolated, model, cancellationToken), "Builder", "_builder"));

            rewrite.Fields.Add(hot.FieldModifiers(readOnly: true) + stringBuilder + " " + builderName + " = new " + stringBuilder + "();");

            var build = new StringBuilder(builderName).Append(".Clear()");
            foreach (var part in operation.Parts)
            {
                switch (part)
                {
                    case IInterpolatedStringTextOperation { Text: { ConstantValue: { HasValue: true, Value: string text } } } when text.Length > 0:
                        build.Append(".Append(").Append(SyntaxFactory.Literal(text).Text).Append(')');
                        break;
                    case IInterpolationOperation { Syntax: InterpolationSyntax hole }:
                        build.Append(".Append(").Append(AppendArgument(hole.Expression, model, cancellationToken)).Append(')');
                        break;
                }
            }

            var indent = Indentation(statement);
            var buildStatement = SyntaxFactory.ParseStatement(indent + build + ";" + eol);
            var replacement = SyntaxFactory.ParseExpression(builderName + ".ToString()").WithTriviaFrom(interpolated);

            rewrite.Replacements[block] = InsertBefore(block, statement, statement.ReplaceNode(interpolated, replacement), new[] { buildStatement });
            return rewrite;
        }

        // Append(string), Append(char), Append(bool) and the numeric overloads format a value the way
        // interpolation does. Other value types, arrays (Append(char[]) appends the characters) and
        // untyped holes go through Append(object), which calls ToString() like interpolation does.
        private static string AppendArgument(ExpressionSyntax expression, SemanticModel model, CancellationToken cancellationToken)
        {
            var type = model.GetTypeInfo(expression, cancellationToken).Type;

            var direct = type switch
            {
                null => false,
                { SpecialType: >= SpecialType.System_Boolean and <= SpecialType.System_String } => true,
                { TypeKind: TypeKind.Array or TypeKind.Dynamic or TypeKind.TypeParameter or TypeKind.Error } => false,
                _ => type.IsReferenceType,
            };

            if (direct) return expression.ToString();

            return expression is IdentifierNameSyntax or MemberAccessExpressionSyntax or ParenthesizedExpressionSyntax or LiteralExpressionSyntax
                ? "(object)" + expression
                : "(object)(" + expression + ")";
        }

        // F19. `var alive = hp.Where(h => h > 0).Select(h => h * 2).ToList();` over a List<T> or an array
        // becomes a for loop filling a List<T> held in a field, and `alive` refers to that list. The list
        // is cleared on the next run, so code that keeps the result across frames has to copy it.
        private static Rewrite? LinqToLoop(InvocationExpressionSyntax invocation, HotMethod hot, SemanticModel model, string eol, CancellationToken cancellationToken)
        {
            if (model.GetOperation(invocation, cancellationToken) is not IInvocationOperation toList || !IsEnumerableCall(toList, "ToList")) return null;
            if (toList.Type is not INamedTypeSymbol { TypeArguments: { Length: 1 } } listType || !IsNameable(listType)) return null;

            var receiver = Receiver(toList);
            LambdaExpressionSyntax? selector = null, predicate = null;
            IInvocationOperation innermost = toList;

            if (receiver is IInvocationOperation select && IsEnumerableCall(select, "Select"))
            {
                selector = SingleExpressionLambda(select);
                if (selector == null) return null;
                innermost = select;
                receiver = Receiver(select);
            }

            if (receiver is IInvocationOperation where && IsEnumerableCall(where, "Where"))
            {
                predicate = SingleExpressionLambda(where);
                if (predicate == null) return null;
                innermost = where;
                receiver = Receiver(where);
            }

            if (selector == null && predicate == null) return null;
            if (receiver == null || innermost.Syntax is not InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Expression: var sourceSyntax } }) return null;

            string countProperty;
            ITypeSymbol elementType;
            switch (receiver.Type)
            {
                case IArrayTypeSymbol { Rank: 1 } array:
                    countProperty = "Length";
                    elementType = array.ElementType;
                    break;
                case INamedTypeSymbol { TypeArguments: { Length: 1 } } sourceList when sourceList.OriginalDefinition.ToDisplayString() == "System.Collections.Generic.List<T>":
                    countProperty = "Count";
                    elementType = sourceList.TypeArguments[0];
                    break;
                default:
                    return null;
            }

            // The loop variable is typed as the element; a lambda typed as a base type could bind its body
            // to different members.
            var parameters = new List<IParameterSymbol>();
            foreach (var lambda in new[] { predicate, selector })
            {
                if (lambda == null) continue;
                if (model.GetSymbolInfo(lambda, cancellationToken).Symbol is not IMethodSymbol { Parameters: { Length: 1 } } lambdaSymbol) return null;
                if (!SymbolEqualityComparer.Default.Equals(lambdaSymbol.Parameters[0].Type, elementType)) return null;
                parameters.Add(lambdaSymbol.Parameters[0]);
            }

            if (!TryGetInsertionPoint(invocation, hot, out var statement, out var block)) return null;

            // Names already in use: every identifier in the hot method outside this chain, plus those in the
            // chain that do not name a lambda parameter (those are renamed to the loop variable).
            var used = new HashSet<string>(hot.Declaration.DescendantTokens()
                .Where(t => t.IsKind(SyntaxKind.IdentifierToken) && !invocation.Span.Contains(t.Span))
                .Select(t => t.ValueText));
            foreach (var identifier in invocation.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (!parameters.Contains(model.GetSymbolInfo(identifier, cancellationToken).Symbol, SymbolEqualityComparer.Default))
                    used.Add(identifier.Identifier.ValueText);
            }

            var rewrite = new Rewrite(hot, "Replace LINQ with a loop filling a reused List<T>", LinqToLoopKey);
            var list = TypeName(model, hot, "System.Collections.Generic", "List", 1, rewrite) + "<" + Display(listType.TypeArguments[0], model, hot) + ">";
            var bufferName = hot.FreeMemberName(FieldName(NameHint(invocation, model, cancellationToken), "Buffer", "_buffer"));
            rewrite.Fields.Add(hot.FieldModifiers(readOnly: true) + list + " " + bufferName + " = new " + list + "();");

            var itemName = FreeLocalName(parameters[0].Name, used);
            var indexName = FreeLocalName("i", used);

            var indent = Indentation(statement);
            var unit = IndentUnit(hot.Class);
            var lines = new List<string> { bufferName + ".Clear();" };

            var source = sourceSyntax.ToString();
            if (!IsStableReference(sourceSyntax, model, cancellationToken))
            {
                var sourceName = FreeLocalName("source", used);
                lines.Add("var " + sourceName + " = " + source + ";");
                source = sourceName;
            }

            lines.Add("for (int " + indexName + " = 0; " + indexName + " < " + source + "." + countProperty + "; " + indexName + "++)");
            lines.Add("{");
            lines.Add(unit + "var " + itemName + " = " + source + "[" + indexName + "];");

            var added = selector != null ? Inline(selector, parameters[parameters.Count - 1], itemName, model, cancellationToken) : itemName;
            if (predicate != null)
            {
                lines.Add(unit + "if (" + Inline(predicate, parameters[0], itemName, model, cancellationToken) + ")");
                lines.Add(unit + unit + bufferName + ".Add(" + added + ");");
            }
            else
            {
                lines.Add(unit + bufferName + ".Add(" + added + ");");
            }

            lines.Add("}");

            var loopText = string.Concat(lines.Select(line => indent + line + eol));
            var parsed = SyntaxFactory.ParseSyntaxTree("class C { void M() {" + eol + loopText + "} }", (CSharpParseOptions)hot.Class.SyntaxTree.Options);
            var inserted = parsed.GetRoot(cancellationToken).DescendantNodes().OfType<BlockSyntax>().First().Statements.ToList();

            var replacement = SyntaxFactory.IdentifierName(bufferName).WithTriviaFrom(invocation);
            rewrite.Replacements[block] = InsertBefore(block, statement, statement.ReplaceNode(invocation, replacement), inserted);
            return rewrite;
        }

        private static bool IsEnumerableCall(IInvocationOperation invocation, string name)
        {
            return !invocation.IsImplicit
                && invocation.TargetMethod.Name == name
                && invocation.TargetMethod.IsExtensionMethod
                && invocation.TargetMethod.ContainingType?.ToDisplayString() == "System.Linq.Enumerable";
        }

        private static IOperation? Receiver(IInvocationOperation invocation)
        {
            if (invocation.Arguments.Length == 0) return null;

            var value = invocation.Arguments[0].Value;
            while (value is IConversionOperation { IsImplicit: true } conversion)
                value = conversion.Operand;
            return value;
        }

        // The lambda of `Where(h => h > 0)` or `Select(h => h * 2)`: one parameter, an expression body,
        // not async. The index overloads take two parameters and are not matched.
        private static LambdaExpressionSyntax? SingleExpressionLambda(IInvocationOperation call)
        {
            if (call.Arguments.Length != 2) return null;

            var value = call.Arguments[1].Value;
            if (value is IDelegateCreationOperation { Target: IAnonymousFunctionOperation { Symbol: { Parameters: { Length: 1 } } } lambda } &&
                lambda.Syntax is LambdaExpressionSyntax { Body: ExpressionSyntax } syntax &&
                syntax.AsyncKeyword.IsKind(SyntaxKind.None))
                return syntax;

            return null;
        }

        // The lambda body with its parameter renamed to the loop variable.
        private static string Inline(LambdaExpressionSyntax lambda, IParameterSymbol parameter, string name, SemanticModel model, CancellationToken cancellationToken)
        {
            var body = (ExpressionSyntax)lambda.Body;
            var references = body.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                .Where(identifier => SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(identifier, cancellationToken).Symbol, parameter))
                .ToList();

            var renamed = body.ReplaceNodes(references, (original, _) => SyntaxFactory.IdentifierName(name).WithTriviaFrom(original));
            return renamed.ToString();
        }

        // A local, a parameter or a field, read once per loop condition. Anything else (a property, a call)
        // is evaluated once into a local, as LINQ evaluates its source once.
        private static bool IsStableReference(ExpressionSyntax expression, SemanticModel model, CancellationToken cancellationToken)
        {
            if (expression is not (IdentifierNameSyntax or MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax })) return false;
            return model.GetSymbolInfo(expression, cancellationToken).Symbol is ILocalSymbol or IParameterSymbol or IFieldSymbol;
        }
    }
}
