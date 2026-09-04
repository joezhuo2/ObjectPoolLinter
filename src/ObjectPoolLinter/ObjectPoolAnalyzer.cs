using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ObjectPoolLinter
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class ObjectPoolAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "OPL001";

        private const string Category = "Performance";
        private static readonly LocalizableString Title = "Object allocation in hot path";
        private static readonly LocalizableString MessageFormat = "'{1}' is allocated inside the frequently-called method '{0}'. Consider using an object pool to avoid per-frame allocations.";
        private static readonly LocalizableString Description = "Allocating objects inside frequently-invoked Unity methods (such as Update) causes garbage collection pressure and frame hitches. Reuse instances via an object pool instead.";

        private static readonly DiagnosticDescriptor Rule = new(
            DiagnosticId,
            Title,
            MessageFormat,
            Category,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: Description
        );

        private static readonly ImmutableHashSet<string> HotPathMethodNames =
            ImmutableHashSet.Create(
                "Update",
                "FixedUpdate",
                "LateUpdate",
                "OnGUI",
                "OnTriggerStay",
                "OnTriggerStay2D",
                "OnCollisionStay",
                "OnCollisionStay2D",
                "OnMouseOver",
                "OnMouseDrag",
                "OnAnimatorMove",
                "OnAnimatorIK",
                "OnRenderObject",
                "OnWillRenderObject",
                "OnPreRender",
                "OnPostRender",
                "OnDrawGizmos",
                "OnDrawGizmosSelected"
            );

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterSyntaxNodeAction(
                AnalyzeAllocation, 
                SyntaxKind.ObjectCreationExpression,
                SyntaxKind.ArrayCreationExpression,
                SyntaxKind.ImplicitArrayCreationExpression,
                SyntaxKind.ImplicitObjectCreationExpression
            );
            
            context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        }

        private static void AnalyzeAllocation(SyntaxNodeAnalysisContext context)
        {
            var node = context.Node;

            var typeInfo = context.SemanticModel.GetTypeInfo(node, context.CancellationToken);
            var type = typeInfo.Type ?? typeInfo.ConvertedType;
            if (type == null) return;

            if (type.IsValueType && type is not IArrayTypeSymbol) return;

            string allocatedTypeName = node switch
            {
                ObjectCreationExpressionSyntax obj => obj.Type.ToString(),
                ArrayCreationExpressionSyntax arr => arr.Type.ToString(),
                _ => type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
            };

            if (TryGetHotPathMethod(node, context.SemanticModel, out var methodName))
            {
                var diagnostic = Diagnostic.Create(
                    Rule,
                    node.GetLocation(),
                    methodName,
                    allocatedTypeName
                );

                context.ReportDiagnostic(diagnostic);
            }
        }

        private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
        {
            var invocation = (InvocationExpressionSyntax)context.Node;

            if (!IsInstantiateCall(invocation, context.SemanticModel)) return;

            if (TryGetHotPathMethod(invocation, context.SemanticModel, out var methodName))
            {
                var diagnostic = Diagnostic.Create(
                    Rule,
                    invocation.GetLocation(),
                    methodName,
                    "Instantiate");

                context.ReportDiagnostic(diagnostic);
            }
        }

        private static bool IsInstantiateCall(InvocationExpressionSyntax invocation, SemanticModel semanticModel)
        {
            var symbol = semanticModel.GetSymbolInfo(invocation).Symbol;

            if (symbol is not IMethodSymbol methodSymbol)
                return false;

            if (!methodSymbol.Name.Equals("Instantiate", System.StringComparison.Ordinal))
                return false;

            return methodSymbol.IsStatic &&
                   methodSymbol.ContainingType != null &&
                   methodSymbol.ContainingType.Name.Equals("Object", System.StringComparison.Ordinal) &&
                   methodSymbol.ContainingType.ContainingNamespace?.Name.Equals("UnityEngine", System.StringComparison.Ordinal) == true;
        }

        private static bool TryGetHotPathMethod(SyntaxNode node, SemanticModel semanticModel, out string methodName)
        {
            methodName = string.Empty;

            var method = GetExecutingMethod(node, semanticModel);
            if (method == null) return false;

            var methodSymbol = semanticModel.GetDeclaredSymbol(method);
            if (methodSymbol == null) return false;

            if (!IsUnityMessage(methodSymbol)) return false;

            methodName = methodSymbol.Name;
            return true;
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

        private static bool IsUnityMessage(IMethodSymbol methodSymbol)
        {
            if (!HotPathMethodNames.Contains(methodSymbol.Name)) return false;

            var containingType = methodSymbol.ContainingType;
            while (containingType != null)
            {
                if (containingType.Name.Equals("MonoBehaviour", System.StringComparison.Ordinal) &&
                    containingType.ContainingNamespace?.Name.Equals("UnityEngine", System.StringComparison.Ordinal) == true)
                {
                    return true;
                }

                containingType = containingType.BaseType;
            }

            return false;
        }
    }
}
