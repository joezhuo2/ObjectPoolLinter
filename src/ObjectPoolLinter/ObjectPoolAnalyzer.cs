using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace ObjectPoolLinter
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class ObjectPoolAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "OPL001";

        private const string Category = "Performance";
        private static readonly LocalizableString Title = "Object allocation in hot path";
        private static readonly LocalizableString MessageFormat = "'{0}' allocates inside the frequently-called method '{1}'. Consider using an object pool to avoid per-frame allocations.";
        private static readonly LocalizableString Description = "Allocating objects inside frequently-invoked Unity methods (such as Update) causes garbage collection pressure and frame hitches. Reuse instances via an object pool instead.";

        // Points at the default branch rather than a tag: a shipped analyzer keeps linking to the
        // current documentation for the rule, which is what a reader clicking the IDE lightbulb wants.
        internal const string HelpLinkUri = "https://github.com/joezhuo2/ObjectPoolLinter/blob/main/docs/rules/OPL001.md";

        private static readonly DiagnosticDescriptor Rule = new(
            DiagnosticId,
            Title,
            MessageFormat,
            Category,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: Description,
            helpLinkUri: HelpLinkUri
        );

        private const string UnityObjectMetadataName = "UnityEngine.Object";

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterCompilationStartAction(OnCompilationStart);
        }
        private static void OnCompilationStart(CompilationStartAnalysisContext context)
        {
            var hotPaths = HotPathDetector.Create(context.Compilation);
            if (hotPaths == null) return;

            var unityObject = context.Compilation.GetTypeByMetadataName(UnityObjectMetadataName);

            var analyzer = new CompilationAnalyzer(hotPaths, unityObject);

            context.RegisterSyntaxNodeAction(
                analyzer.AnalyzeAllocation,
                SyntaxKind.ObjectCreationExpression,
                SyntaxKind.ArrayCreationExpression,
                SyntaxKind.ImplicitArrayCreationExpression,
                SyntaxKind.ImplicitObjectCreationExpression
            );

            context.RegisterSyntaxNodeAction(analyzer.AnalyzeInvocation, SyntaxKind.InvocationExpression);
        }

        private sealed class CompilationAnalyzer
        {
            private readonly HotPathDetector _hotPaths;
            private readonly INamedTypeSymbol? _unityObject;

            internal CompilationAnalyzer(HotPathDetector hotPaths, INamedTypeSymbol? unityObject)
            {
                _hotPaths = hotPaths;
                _unityObject = unityObject;
            }

            internal void AnalyzeAllocation(SyntaxNodeAnalysisContext context)
            {
                var node = context.Node;

                var typeInfo = context.SemanticModel.GetTypeInfo(node, context.CancellationToken);
                var type = typeInfo.Type ?? typeInfo.ConvertedType;
                if (type == null) return;

                // A value type allocates only when the new instance is boxed on the spot.
                ITypeSymbol? boxedTo = null;
                if (type.IsValueType && !TryGetBoxingTarget(node, context.SemanticModel, context.CancellationToken, out boxedTo))
                    return;

                if (TryGetHotPathMethod(node, context, out var methodName))
                {
                    // Named from the symbol, not the syntax, so `new System.Collections.Generic.List<int>()`,
                    // `new List<int>()` and `new()` all read `new List<int>`, and `new int[10]` reads `new int[]`.
                    var allocation = "new " + type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                    if (boxedTo != null)
                        allocation += " boxed to " + boxedTo.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

                    var diagnostic = Diagnostic.Create(
                        Rule,
                        node.GetLocation(),
                        allocation,
                        methodName
                    );

                    context.ReportDiagnostic(diagnostic);
                }
            }

            internal void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
            {
                var invocation = (InvocationExpressionSyntax)context.Node;

                if (!IsInstantiateCall(invocation, context.SemanticModel)) return;

                if (TryGetHotPathMethod(invocation, context, out var methodName))
                {
                    var diagnostic = Diagnostic.Create(
                        Rule,
                        invocation.GetLocation(),
                        "Instantiate",
                        methodName);

                    context.ReportDiagnostic(diagnostic);
                }
            }

            // Covers the conversion applied directly to the creation expression: assignment or
            // initialization of an object or interface variable, a cast, an argument, a return value.
            // Nullable<T> is skipped because `new int?()` boxes to null and allocates nothing.
            private static bool TryGetBoxingTarget(
                SyntaxNode node,
                SemanticModel semanticModel,
                System.Threading.CancellationToken cancellationToken,
                out ITypeSymbol? boxedTo)
            {
                boxedTo = null;

                var operation = semanticModel.GetOperation(node, cancellationToken);
                if (operation?.Type is not { } type) return false;
                if (type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T) return false;

                if (operation.Parent is not IConversionOperation conversion) return false;
                if (!conversion.GetConversion().IsBoxing || conversion.Type == null) return false;

                boxedTo = conversion.Type;
                return true;
            }

            private bool IsInstantiateCall(InvocationExpressionSyntax invocation, SemanticModel semanticModel)
            {
                if (_unityObject == null) return false;

                var symbol = semanticModel.GetSymbolInfo(invocation).Symbol;

                if (symbol is not IMethodSymbol methodSymbol)
                    return false;

                if (!methodSymbol.Name.Equals("Instantiate", System.StringComparison.Ordinal))
                    return false;

                return methodSymbol.IsStatic &&
                       SymbolEqualityComparer.Default.Equals(methodSymbol.ContainingType?.OriginalDefinition, _unityObject);
            }

            private bool TryGetHotPathMethod(SyntaxNode node, SyntaxNodeAnalysisContext context, out string methodName)
            {
                return _hotPaths.TryGetHotPathMethod(node, context.SemanticModel, context.Options, context.CancellationToken, out methodName);
            }
        }
    }
}
