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

        private static readonly LocalizableString Title =
            "Object allocation in hot path";

        private static readonly LocalizableString MessageFormat =
            "'{1}' is allocated inside the frequently-called method '{0}'. Consider using an object pool to avoid per-frame allocations.";

        private static readonly LocalizableString Description =
            "Allocating objects inside frequently-invoked Unity methods (such as Update) causes garbage collection pressure and frame hitches. Reuse instances via an object pool instead.";

        private static readonly DiagnosticDescriptor Rule = new(
            DiagnosticId,
            Title,
            MessageFormat,
            Category,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: Description);

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
                "OnDrawGizmosSelected");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
            ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterSyntaxNodeAction(AnalyzeObjectCreation, SyntaxKind.ObjectCreationExpression);

            context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        }

        private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext context)
        {
            var objectCreation = (ObjectCreationExpressionSyntax)context.Node;

            var typeInfo = context.SemanticModel.GetTypeInfo(objectCreation, context.CancellationToken);
            if (typeInfo.Type != null && typeInfo.Type.IsValueType)
            {
                return;
            }

            if (TryGetHotPathMethod(objectCreation, context.SemanticModel, out var methodName))
            {
                var diagnostic = Diagnostic.Create(
                    Rule,
                    objectCreation.GetLocation(),
                    methodName,
                    objectCreation.Type.ToString());

                context.ReportDiagnostic(diagnostic);
            }
        }

        private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
        {
            var invocation = (InvocationExpressionSyntax)context.Node;

            if (!IsInstantiateCall(invocation, context.SemanticModel))
            {
                return;
            }

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

            var method = node.Ancestors()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault();

            if (method == null)
                return false;

            var methodSymbol = semanticModel.GetDeclaredSymbol(method);
            if (methodSymbol == null)
                return false;

            if (!IsUnityMessage(methodSymbol))
                return false;

            methodName = methodSymbol.Name;
            return true;
        }

        private static bool IsUnityMessage(IMethodSymbol methodSymbol)
        {
            if (!HotPathMethodNames.Contains(methodSymbol.Name))
                return false;

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
