using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ObjectPoolLinter
{
    // Decides whether a syntax node executes as part of a hot path. Shared by every rule, so OPL001,
    // OPL002 and OPL003 agree on which methods are hot and honour the same .editorconfig options.
    // One instance per compilation.
    internal sealed class HotPathDetector
    {
        internal const string MonoBehaviourMetadataName = "UnityEngine.MonoBehaviour";

        private const string BurstCompileMetadataName = "Unity.Burst.BurstCompileAttribute";
        private const string BurstDiscardMetadataName = "Unity.Burst.BurstDiscardAttribute";

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

        private readonly INamedTypeSymbol _monoBehaviour;

        // Null when the compilation does not reference the Burst package.
        private readonly INamedTypeSymbol? _burstCompile;
        private readonly INamedTypeSymbol? _burstDiscard;

        private readonly ConcurrentDictionary<SyntaxTree, LinterOptions> _optionsByTree = new();
        private readonly ConcurrentDictionary<string, Regex?> _regexCache = new(System.StringComparer.Ordinal);

        private HotPathDetector(INamedTypeSymbol monoBehaviour, INamedTypeSymbol? burstCompile, INamedTypeSymbol? burstDiscard)
        {
            _monoBehaviour = monoBehaviour;
            _burstCompile = burstCompile;
            _burstDiscard = burstDiscard;
        }

        // Null when the compilation does not reference UnityEngine.MonoBehaviour; no rule runs then.
        internal static HotPathDetector? Create(Compilation compilation)
        {
            var monoBehaviour = compilation.GetTypeByMetadataName(MonoBehaviourMetadataName);
            if (monoBehaviour == null) return null;

            return new HotPathDetector(
                monoBehaviour,
                compilation.GetTypeByMetadataName(BurstCompileMetadataName),
                compilation.GetTypeByMetadataName(BurstDiscardMetadataName));
        }

        internal bool TryGetHotPathMethod(
            SyntaxNode node,
            SemanticModel semanticModel,
            AnalyzerOptions analyzerOptions,
            CancellationToken cancellationToken,
            out string methodName)
        {
            methodName = string.Empty;

            var method = GetExecutingMethod(node, semanticModel);
            if (method == null) return false;

            var methodSymbol = semanticModel.GetDeclaredSymbol(method, cancellationToken);
            if (methodSymbol?.ContainingType == null) return false;

            var options = GetOptions(node.SyntaxTree, analyzerOptions);
            if (options.IsExcludedType(methodSymbol.ContainingType)) return false;

            if (!options.IsAdditionalHotMethod(methodSymbol) && !IsUnityMessage(methodSymbol)) return false;

            if (IsBurstCompiled(methodSymbol)) return false;

            methodName = methodSymbol.Name;
            return true;
        }

        // Matches the namespace by its full name, so a user's own `Game.UnityEngine` does not count.
        internal static bool IsInUnityEngineNamespace(ISymbol symbol)
        {
            var ns = symbol.ContainingNamespace;
            if (ns == null || ns.IsGlobalNamespace) return false;

            var name = ns.ToDisplayString();
            return name.Equals("UnityEngine", System.StringComparison.Ordinal)
                || name.StartsWith("UnityEngine.", System.StringComparison.Ordinal);
        }

        // .editorconfig options can differ per file, so they are read per syntax tree and parsed once.
        internal LinterOptions GetOptions(SyntaxTree tree, AnalyzerOptions analyzerOptions)
        {
            return _optionsByTree.GetOrAdd(
                tree,
                t => LinterOptions.Parse(analyzerOptions.AnalyzerConfigOptionsProvider.GetOptions(t), _regexCache));
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

        // Burst compiles a job struct's methods, an ISystem struct's methods and static methods when the
        // method or a type containing it carries [BurstCompile], and it rejects managed allocations at
        // compile time, so nothing inside can put garbage on the heap. An instance method of a class,
        // such as a MonoBehaviour's Update, always runs as managed code whatever it is marked with, and so
        // does a [BurstDiscard] method.
        private bool IsBurstCompiled(IMethodSymbol method)
        {
            if (_burstCompile == null) return false;
            if (!method.IsStatic && method.ContainingType.TypeKind != TypeKind.Struct) return false;
            if (HasAttribute(method, _burstDiscard)) return false;

            if (HasAttribute(method, _burstCompile)) return true;

            for (var type = method.ContainingType; type != null; type = type.ContainingType)
            {
                if (HasAttribute(type, _burstCompile)) return true;
            }

            return false;
        }

        private static bool HasAttribute(ISymbol symbol, INamedTypeSymbol? attributeType)
        {
            if (attributeType == null) return false;

            foreach (var attribute in symbol.GetAttributes())
            {
                if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributeType)) return true;
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
}
