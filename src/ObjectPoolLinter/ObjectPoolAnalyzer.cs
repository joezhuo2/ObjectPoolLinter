using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
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

        private const string MonoBehaviourMetadataName = "UnityEngine.MonoBehaviour";
        private const string UnityObjectMetadataName = "UnityEngine.Object";

        // .editorconfig keys. Both take a comma-separated list; see docs/rules/OPL001.md#configuration.
        internal const string AdditionalHotMethodsOption = "object_pool_linter.additional_hot_methods";
        internal const string ExcludedTypesOption = "object_pool_linter.excluded_types";

        private const string NoParameters = "";

        private static readonly ImmutableDictionary<string, string> HotPathMessageSignatures =
            new Dictionary<string, string>(System.StringComparer.Ordinal)
            {
                ["Update"] = NoParameters,
                ["FixedUpdate"] = NoParameters,
                ["LateUpdate"] = NoParameters,
                ["OnGUI"] = NoParameters,
                ["OnTriggerStay"] = "UnityEngine.Collider",
                ["OnTriggerStay2D"] = "UnityEngine.Collider2D",
                ["OnCollisionStay"] = "UnityEngine.Collision",
                ["OnCollisionStay2D"] = "UnityEngine.Collision2D",
                ["OnMouseOver"] = NoParameters,
                ["OnMouseDrag"] = NoParameters,
                ["OnAnimatorMove"] = NoParameters,
                ["OnAnimatorIK"] = "int",
                ["OnRenderObject"] = NoParameters,
                ["OnWillRenderObject"] = NoParameters,
                ["OnPreRender"] = NoParameters,
                ["OnPostRender"] = NoParameters,
                ["OnDrawGizmos"] = NoParameters,
                ["OnDrawGizmosSelected"] = NoParameters,
            }.ToImmutableDictionary(System.StringComparer.Ordinal);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterCompilationStartAction(OnCompilationStart);
        }
        private static void OnCompilationStart(CompilationStartAnalysisContext context)
        {
            var monoBehaviour = context.Compilation.GetTypeByMetadataName(MonoBehaviourMetadataName);
            if (monoBehaviour == null) return;

            var unityObject = context.Compilation.GetTypeByMetadataName(UnityObjectMetadataName);

            var analyzer = new CompilationAnalyzer(monoBehaviour, unityObject);

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
            private readonly INamedTypeSymbol _monoBehaviour;
            private readonly INamedTypeSymbol? _unityObject;
            private readonly ConcurrentDictionary<SyntaxTree, HotPathOptions> _optionsByTree = new();

            internal CompilationAnalyzer(INamedTypeSymbol monoBehaviour, INamedTypeSymbol? unityObject)
            {
                _monoBehaviour = monoBehaviour;
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
                methodName = string.Empty;

                var semanticModel = context.SemanticModel;
                var method = GetExecutingMethod(node, semanticModel);
                if (method == null) return false;

                var methodSymbol = semanticModel.GetDeclaredSymbol(method, context.CancellationToken);
                if (methodSymbol?.ContainingType == null) return false;

                var options = GetOptions(node.SyntaxTree, context.Options);
                if (options.IsExcludedType(methodSymbol.ContainingType)) return false;

                if (!options.IsAdditionalHotMethod(methodSymbol) && !IsUnityMessage(methodSymbol)) return false;

                methodName = methodSymbol.Name;
                return true;
            }

            // .editorconfig options can differ per file, so they are read per syntax tree and parsed once.
            private HotPathOptions GetOptions(SyntaxTree tree, AnalyzerOptions analyzerOptions)
            {
                return _optionsByTree.GetOrAdd(
                    tree,
                    t => HotPathOptions.Parse(analyzerOptions.AnalyzerConfigOptionsProvider.GetOptions(t)));
            }

            private bool IsUnityMessage(IMethodSymbol methodSymbol)
            {
                if (!HotPathMessageSignatures.TryGetValue(methodSymbol.Name, out var expectedParameterType))
                    return false;

                if (methodSymbol.IsStatic || methodSymbol.IsGenericMethod) return false;

                if (!HasExpectedParameters(methodSymbol, expectedParameterType)) return false;

                var containingType = methodSymbol.ContainingType;
                while (containingType != null)
                {
                    if (SymbolEqualityComparer.Default.Equals(containingType.OriginalDefinition, _monoBehaviour))
                    {
                        return true;
                    }

                    containingType = containingType.BaseType;
                }

                return false;
            }

            private static MethodDeclarationSyntax? GetExecutingMethod(SyntaxNode node, SemanticModel semanticModel)
            {
                for (var current = node.Parent; current != null; current = current.Parent)
                {
                    switch (current)
                    {
                        case MethodDeclarationSyntax method: return method;

                        case AnonymousFunctionExpressionSyntax lambda:
                            if (!IsInvokedInPlace(lambda)) return null;
                            break;

                        case LocalFunctionStatementSyntax localFunction:
                            if (!IsCalledByDeclaringBody(localFunction, semanticModel)) return null;
                            break;

                        case BaseMethodDeclarationSyntax:
                        case AccessorDeclarationSyntax:
                        case BasePropertyDeclarationSyntax:
                        case BaseFieldDeclarationSyntax:
                        case BaseTypeDeclarationSyntax:
                            return null;
                    }
                }

                return null;
            }

            private static bool IsInvokedInPlace(AnonymousFunctionExpressionSyntax lambda)
            {
                SyntaxNode current = lambda;
                while (current.Parent is ParenthesizedExpressionSyntax or CastExpressionSyntax)
                    current = current.Parent;

                return current.Parent is InvocationExpressionSyntax invocation && invocation.Expression == current;
            }

            private static bool IsCalledByDeclaringBody(LocalFunctionStatementSyntax localFunction, SemanticModel semanticModel)
            {
                var localFunctionSymbol = semanticModel.GetDeclaredSymbol(localFunction);
                if (localFunctionSymbol == null) return false;

                var declaringBody = localFunction.Ancestors()
                    .FirstOrDefault(ancestor => ancestor is BaseMethodDeclarationSyntax
                                             or AccessorDeclarationSyntax
                                             or LocalFunctionStatementSyntax
                                             or AnonymousFunctionExpressionSyntax);
                if (declaringBody == null) return false;

                foreach (var invocation in declaringBody.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (localFunction.Span.Contains(invocation.Span)) continue;

                    var invokedSymbol = semanticModel.GetSymbolInfo(invocation).Symbol;
                    if (SymbolEqualityComparer.Default.Equals(invokedSymbol, localFunctionSymbol)) return true;
                }

                return false;
            }

            private static bool HasExpectedParameters(IMethodSymbol methodSymbol, string expectedParameterType)
            {
                var parameters = methodSymbol.Parameters;

                if (expectedParameterType.Length == 0)
                    return parameters.Length == 0;

                if (parameters.Length != 1) return false;

                var parameter = parameters[0];
                if (parameter.RefKind != RefKind.None) return false;

                if (expectedParameterType.Equals("int", System.StringComparison.Ordinal))
                    return parameter.Type.SpecialType == SpecialType.System_Int32;

                return parameter.Type.ToDisplayString().Equals(expectedParameterType, System.StringComparison.Ordinal);
            }
        }

        // User configuration from .editorconfig. Every name is matched ordinally, so `tick` does not
        // match `Tick`. A type name is its simple name (`Enemy`) or its namespace-qualified name with
        // nested types separated by dots (`Game.AI.Enemy.Brain`); generic type parameters are left out.
        private sealed class HotPathOptions
        {
            private static readonly HotPathOptions Empty = new(ImmutableArray<string>.Empty, ImmutableArray<string>.Empty);

            private static readonly char[] Separators = { ',' };

            // Entries are either a bare method name (`Tick`) or a type-qualified one (`Enemy.Tick`).
            private readonly ImmutableArray<string> _additionalHotMethods;
            private readonly ImmutableArray<string> _excludedTypes;

            private HotPathOptions(ImmutableArray<string> additionalHotMethods, ImmutableArray<string> excludedTypes)
            {
                _additionalHotMethods = additionalHotMethods;
                _excludedTypes = excludedTypes;
            }

            internal static HotPathOptions Parse(AnalyzerConfigOptions options)
            {
                var additionalHotMethods = ParseList(options, AdditionalHotMethodsOption);
                var excludedTypes = ParseList(options, ExcludedTypesOption);

                if (additionalHotMethods.IsEmpty && excludedTypes.IsEmpty) return Empty;
                return new HotPathOptions(additionalHotMethods, excludedTypes);
            }

            internal bool IsExcludedType(INamedTypeSymbol type)
            {
                foreach (var entry in _excludedTypes)
                {
                    if (TypeNameMatches(type, entry)) return true;
                }

                return false;
            }

            // Any signature, static or instance, on any type: a custom update loop is often a plain
            // class driven by a manager, and its tick method usually takes a delta time.
            internal bool IsAdditionalHotMethod(IMethodSymbol method)
            {
                foreach (var entry in _additionalHotMethods)
                {
                    var lastDot = entry.LastIndexOf('.');
                    var methodName = lastDot < 0 ? entry : entry.Substring(lastDot + 1);
                    if (!method.Name.Equals(methodName, System.StringComparison.Ordinal)) continue;

                    if (lastDot < 0 || TypeNameMatches(method.ContainingType, entry.Substring(0, lastDot))) return true;
                }

                return false;
            }

            private static ImmutableArray<string> ParseList(AnalyzerConfigOptions options, string key)
            {
                if (!options.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                    return ImmutableArray<string>.Empty;

                var builder = ImmutableArray.CreateBuilder<string>();
                foreach (var part in value.Split(Separators))
                {
                    var trimmed = part.Trim();
                    if (trimmed.StartsWith("global::", System.StringComparison.Ordinal))
                        trimmed = trimmed.Substring("global::".Length);

                    if (trimmed.Length > 0) builder.Add(trimmed);
                }

                return builder.ToImmutable();
            }

            private static bool TypeNameMatches(INamedTypeSymbol type, string name)
            {
                if (type.Name.Equals(name, System.StringComparison.Ordinal)) return true;
                if (name.IndexOf('.') < 0) return false;

                return GetQualifiedName(type).Equals(name, System.StringComparison.Ordinal);
            }

            private static string GetQualifiedName(INamedTypeSymbol type)
            {
                var name = type.Name;
                for (var containing = type.ContainingType; containing != null; containing = containing.ContainingType)
                    name = containing.Name + "." + name;

                var ns = type.ContainingNamespace;
                return ns == null || ns.IsGlobalNamespace ? name : ns.ToDisplayString() + "." + name;
            }
        }
    }
}
